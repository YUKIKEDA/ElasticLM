using System.Net;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// Borrowing end to end, the borrow record across restarts, the usage log, the administration API, and the license reload
/// (design §12.7.4, §13.1, §13.4, §13.7, §18.1).
/// </summary>
public sealed class BorrowAndAdminTests : IAsyncDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// A borrowed lease is saved next to the installation key and found by a new client with the server gone; a changed file
    /// is not used; an expired borrow is deleted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server; a client that borrows pro for one day; the server stopped.
    ///
    /// Steps:
    /// - A new client on the same data folder reads its borrows, and so do a client of another server URL and a client that
    ///   expects server license lic-other; change one character of the saved token and read again; pad the file to exactly
    ///   256 KiB and read, then to one byte more and read; restore it, advance past the expiry, and read again.
    ///
    /// Expected:
    /// - The borrow, held and not renewed (none for the other server, none for the client that expects lic-other); none
    ///   from the changed file (the file stays); the borrow at 256 KiB and none beyond; none after the expiry and the file
    ///   is gone.
    /// </remarks>
    [Fact]
    public async Task Borrow_IsKeptForUseWithoutTheServer()
    {
        ElmServer server = await StartOpenServerAsync();
        string data = _deployment.NewFolder();
        LicenseClient first = _deployment.Client(server.PublicUrls[0], data);
        Lease borrowed = Acquired(await first.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(borrowed.IsBorrow);
        await first.DisposeAsync();
        await server.DisposeAsync();

        LicenseClient offline = _deployment.Client(server.PublicUrls[0], data);
        Lease found = Assert.Single(offline.GetBorrowedLeases());
        var otherServer = new UriBuilder(server.PublicUrls[0]) { Host = "localhost" }.Uri;
        Assert.Empty(_deployment.Client(otherServer, data).GetBorrowedLeases());
        LicenseClient expectsOther = _deployment.Client(
            server.PublicUrls[0],
            data,
            o => new LicenseClientOptions
            {
                ProductId = o.ProductId,
                ServerUrl = o.ServerUrl,
                Trust = o.Trust,
                DataDirectory = o.DataDirectory,
                ClockGuard = o.ClockGuard,
                TimeProvider = o.TimeProvider,
                ExpectedServerLicenseId = "lic-other",
            }
        );
        Assert.Empty(expectsOther.GetBorrowedLeases());
        Assert.Equal((borrowed.Id, LeaseState.Held, true), (found.Id, found.State, found.IsBorrow));

        string file = Assert.Single(Directory.GetFiles(Path.Combine(data, "borrows")));
        string saved = File.ReadAllText(file);
        int at = saved.IndexOf("\"lease\":\"", StringComparison.Ordinal) + 30;
        File.WriteAllText(file, saved[..at] + (saved[at] == 'A' ? 'B' : 'A') + saved[(at + 1)..]);
        Assert.Empty(_deployment.Client(server.PublicUrls[0], data).GetBorrowedLeases());
        Assert.True(File.Exists(file));

        // The saved file may be at most 256 KiB: padded with JSON whitespace to exactly that it is read, one byte more it is not.
        File.WriteAllText(file, saved + new string(' ', (256 * 1024) - System.Text.Encoding.UTF8.GetByteCount(saved)));
        Assert.Single(_deployment.Client(server.PublicUrls[0], data).GetBorrowedLeases());
        File.AppendAllText(file, " ");
        Assert.Empty(_deployment.Client(server.PublicUrls[0], data).GetBorrowedLeases());

        File.WriteAllText(file, saved);
        _deployment.Time.Advance(Day + TimeSpan.FromSeconds(1));
        Assert.Empty(_deployment.Client(server.PublicUrls[0], data).GetBorrowedLeases());
        Assert.False(File.Exists(file));
    }

    /// <summary>
    /// A borrow survives a server restart through the signed record, keeps its seats there, and can be returned early by a
    /// client that reads it from the disk; the early-return limit holds across the restart.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with 2 seats and 1 early return in 30 days; a client that borrows 1 seat, returns it early, and borrows 1
    ///   seat again; the server restarted on the same port.
    ///
    /// Steps:
    /// - Read the status of the new server; a new client reads its borrows and returns the borrow early.
    ///
    /// Expected:
    /// - 1 seat borrowed after the restart; the return is ELM_RETURN_NOT_ALLOWED (the one early return was used), the
    ///   borrow stays held and on the disk.
    /// </remarks>
    [Fact]
    public async Task Borrow_SurvivesARestartWithItsReturnLimit()
    {
        string serverData = _deployment.NewFolder();
        _deployment.WriteServerLicense(serverData, s => s with { BorrowReturnLimit = 1 });
        ElmServer before = await StartOpenServerAsync(serverData);
        int port = before.PublicUrls[0].Port;
        string data = _deployment.NewFolder();
        LicenseClient first = _deployment.Client(before.PublicUrls[0], data);
        Lease returned = Acquired(await first.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await first.ReleaseLeaseAsync(returned, TestContext.Current.CancellationToken));
        Acquired(await first.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken));
        await first.DisposeAsync();
        await before.DisposeAsync();

        ElmServer after = await _deployment.StartServerAsync(serverData, s => With(s, port));
        Assert.Equal(1, Assert.Single(after.Engine.Usage()).Borrowed);

        LicenseClient second = _deployment.Client(after.PublicUrls[0], data);
        Lease kept = Assert.Single(second.GetBorrowedLeases());
        LicenseError? refused = await second.ReleaseLeaseAsync(kept, TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.ReturnNotAllowed, refused?.Code);
        Assert.Equal(LeaseState.Held, kept.State);
        Assert.Single(Directory.GetFiles(Path.Combine(data, "borrows")));
    }

    /// <summary>
    /// A client holding a lease and a borrow reclaims only the lease after a restart; the borrow is not sent and stays held.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client with a lease and a borrow; the server restarted on the same port.
    ///
    /// Steps:
    /// - Advance 200 s so the lease renews (and is reclaimed).
    ///
    /// Expected:
    /// - The lease is held with sequence 2; the borrow is still held; the new server counts both seats.
    /// </remarks>
    [Fact]
    public async Task Restart_ReclaimsLeasesButNotBorrows()
    {
        string serverData = _deployment.NewFolder();
        ElmServer before = await StartOpenServerAsync(serverData);
        int port = before.PublicUrls[0].Port;
        LicenseClient client = _deployment.Client(before.PublicUrls[0]);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Lease borrow = Acquired(await client.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken));
        await before.DisposeAsync();

        ElmServer after = await _deployment.StartServerAsync(serverData, s => With(s, port));
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));

        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
        Assert.Equal(LeaseState.Held, borrow.State);
        Assert.Equal(2, after.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A borrow restored from the record can be returned early by its key, although the restarted server never saw the key:
    /// the proof carries it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client that borrowed 2 seats; the server restarted on the same port.
    ///
    /// Steps:
    /// - A new client reads its borrows and returns the borrow early.
    ///
    /// Expected:
    /// - The return succeeds; the server counts no seat; the saved file is gone.
    /// </remarks>
    [Fact]
    public async Task RestoredBorrow_CanBeReturnedEarly()
    {
        string serverData = _deployment.NewFolder();
        ElmServer before = await StartOpenServerAsync(serverData);
        int port = before.PublicUrls[0].Port;
        string data = _deployment.NewFolder();
        LicenseClient first = _deployment.Client(before.PublicUrls[0], data);
        Acquired(await first.BorrowLeaseAsync("pro", Day, count: 2, cancellationToken: TestContext.Current.CancellationToken));
        await first.DisposeAsync();
        await before.DisposeAsync();

        ElmServer after = await _deployment.StartServerAsync(serverData, s => With(s, port));
        LicenseClient second = _deployment.Client(after.PublicUrls[0], data);
        Lease borrowed = Assert.Single(second.GetBorrowedLeases());

        Assert.Null(await second.ReleaseLeaseAsync(borrowed, TestContext.Current.CancellationToken));
        Assert.Equal(0, after.Engine.SeatsInUse("pro"));
        Assert.Empty(Directory.GetFiles(Path.Combine(data, "borrows")));
    }

    /// <summary>
    /// A broken borrow record stops the start; with --set-aside-broken-records the server moves it aside, starts without its
    /// borrows, and writes that to the usage log.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that recorded a borrow and stopped; a character of the borrow line changed.
    ///
    /// Steps:
    /// - Start; start with the set-aside option; verify the new usage log file.
    ///
    /// Expected:
    /// - ServerStartException naming the option; then a running server with no borrowed seat, a borrows-broken file, and
    ///   a records.set_aside line in the usage log.
    /// </remarks>
    [Fact]
    public async Task BrokenRecord_StopsTheStartUnlessSetAside()
    {
        string serverData = _deployment.NewFolder();
        ElmServer before = await StartOpenServerAsync(serverData);
        Acquired(
            await _deployment.Client(before.PublicUrls[0]).BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken)
        );
        await before.DisposeAsync();
        string record = SignedLog.FindFiles(serverData, "borrows")[^1];
        string[] lines = File.ReadAllLines(record);
        int borrowLine = Array.FindIndex(lines, l => l.Contains("borrow.started", StringComparison.Ordinal));
        lines[borrowLine] = lines[borrowLine].Replace("\"seats\":2", "\"seats\":9", StringComparison.Ordinal);
        File.WriteAllText(record, string.Join('\n', lines) + "\n");

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(serverData));
        Assert.Contains("--set-aside-broken-records", refused.Message, StringComparison.Ordinal);

        ElmServer after = await _deployment.StartServerAsync(serverData, setAside: true);
        Assert.Equal(0, Assert.Single(after.Engine.Usage()).Borrowed);
        Assert.Single(Directory.GetFiles(serverData, "borrows-broken-*.jsonl"));
        await after.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(serverData, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        Assert.Contains(usage.Entries, e => e.Type == "records.set_aside");
    }

    /// <summary>
    /// The guard takes a path for an administration path without regard to case, as routing does: on the TLS listener
    /// "/Admin/…" and "/ADMIN/…" are not served even with the right token, and nothing is recorded for them; on the socket a
    /// lease path in another case is not served either (design §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running server and its administrator token.
    ///
    /// Steps:
    /// - On the https listener, with the token: GET Admin/v1/status and ADMIN/v1/status. On the socket, with the token:
    ///   GET V1/nonce, then GET Admin/v1/status. Stop the server and verify the usage log.
    ///
    /// Expected:
    /// - 404 for the two requests on the https listener and for V1/nonce on the socket; 200 for Admin/v1/status on the
    ///   socket, which routing matches.
    /// - The usage log is intact, holds one admin.action line (the socket's status) and no admin.denied line: the guard's
    ///   404 records nothing.
    /// </remarks>
    [Fact]
    public async Task AdminApi_PathInAnotherCase_IsServedOnTheSocketOnly()
    {
        ElmServer server = await StartOpenServerAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using HttpClient admin = Deployment.AdminHttp(server);

        using (HttpClient https = Deployment.RawHttp(server.PublicUrls[0]))
        {
            https.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("Admin/v1/status", cancel)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("ADMIN/v1/status", cancel)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("V1/nonce", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("Admin/v1/status", cancel)).StatusCode);

        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(server.DataDirectory, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        Assert.Single(usage.Entries, e => e.Type == "admin.action");
        Assert.DoesNotContain(usage.Entries, e => e.Type == "admin.denied");
    }

    /// <summary>
    /// The administration API answers only with the administrator token, only on the listener of its socket file, records
    /// every authorized operation in the usage log, and counts the unauthorized requests on one line (design §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with one lease.
    ///
    /// Steps:
    /// - Get the status without a token and with a wrong one; get the status, the leases; force-release an unknown lease and
    ///   the lease; seal the log; ask the https listener for /admin/v1/status with the right token, and the socket for the
    ///   lease paths /v1/nonce and /v1/server/license; stop and verify the usage log.
    ///
    /// Expected:
    /// - 401 ELM_UNAUTHORIZED twice; the status shows the license and 1 seat in use; the lease list has the lease; 404
    ///   ELM_UNKNOWN_LEASE, then 204 and no seat in use; 204; 404 for each path on the wrong listener; the usage log is intact, has an
    ///   admin.action line for each authorized operation, and one admin.denied line with count 2.
    /// </remarks>
    [Fact]
    public async Task AdminApi_NeedsTheTokenAndIsRecorded()
    {
        ElmServer server = await StartOpenServerAsync();
        Lease lease = Acquired(
            await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using (HttpClient anonymous = Deployment.AdminHttp(server, authenticate: false))
        using (HttpClient wrong = Deployment.AdminHttp(server, token: "AAAA"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("admin/v1/status", cancel)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("admin/v1/status", cancel)).StatusCode);
        }

        using HttpClient admin = Deployment.AdminHttp(server);
        using (JsonDocument status = JsonDocument.Parse(await admin.GetStringAsync("admin/v1/status", cancel)))
        {
            Assert.Equal("lic-srv", status.RootElement.GetProperty("license").GetString());
            Assert.Equal(1, status.RootElement.GetProperty("features")[0].GetProperty("inUse").GetInt64());
        }

        using (JsonDocument leases = JsonDocument.Parse(await admin.GetStringAsync("admin/v1/leases", cancel)))
        {
            Assert.Equal(lease.Id, leases.RootElement.GetProperty("leases")[0].GetProperty("id").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("admin/v1/leases/unknown", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"admin/v1/leases/{lease.Id}", cancel)).StatusCode);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("admin/v1/log/seal", null, cancel)).StatusCode);

        using (HttpClient https = Deployment.RawHttp(server.PublicUrls[0]))
        {
            https.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("admin/v1/status", cancel)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("v1/nonce", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("v1/server/license", cancel)).StatusCode);

        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(server.DataDirectory, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        string[] actions =
        [
            .. usage
                .Entries.Where(e => e.Type == "admin.action")
                .Select(e => e.Value.GetProperty("action").GetString() + ":" + e.Value.GetProperty("result").GetString()),
        ];
        Assert.Equal(["status:ELM_OK", "leases:ELM_OK", "release:ELM_UNKNOWN_LEASE", "release:ELM_OK", "seal:ELM_OK"], actions);
        LogEntry denied = Assert.Single(usage.Entries, e => e.Type == "admin.denied");
        Assert.Equal(2, denied.Value.GetProperty("count").GetInt64());
        Assert.Equal("ELM_UNAUTHORIZED", denied.Value.GetProperty("code").GetString());
        Assert.Contains(usage.Entries, e => e.Type == "lease.acquired");
        Assert.Contains(usage.Entries, e => e.Type == "server.stopped");
    }

    /// <summary>
    /// A reload puts a new server license of this server in place (clients fetch it, the usage log records the change signed
    /// by the old key); a license of another key is refused and the old one stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server and a lease; a new license file for the same key with 3 seats.
    ///
    /// Steps:
    /// - Reload; advance 200 s so the client renews; write a license bound to another key and reload; stop and verify the
    ///   usage log.
    ///
    /// Expected:
    /// - 200 with the new license's jti; the lease's next token names the new license; 409 with code ELM_WRONG_INSTALLATION
    ///   and detail "The server license does not verify: The license is bound to another installation key. This server's
    ///   installation key is {thumbprint}." (no code in it), with the status still on the new license; the usage log is
    ///   intact with one key.changed line.
    /// </remarks>
    [Fact]
    public async Task Reload_ReplacesTheLicenseOfThisServerOnly()
    {
        string serverData = _deployment.NewFolder();
        ElmServer server = await StartOpenServerAsync(serverData);
        Lease lease = Acquired(
            await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        CancellationToken cancel = TestContext.Current.CancellationToken;
        _deployment.WriteServerLicense(serverData, s => s with { Features = [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 3)] });
        using HttpClient admin = Deployment.AdminHttp(server);

        string newToken;
        using (HttpResponseMessage reloaded = await admin.PostAsync("admin/v1/license/reload", null, cancel))
        {
            Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
            using JsonDocument answer = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync(cancel));
            newToken = answer.RootElement.GetProperty("licenseToken").GetString()!;
        }

        Assert.Equal(newToken, server.Engine.Server.License.TokenId);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.ServerLicenseTokenId == newToken);

        string otherKey = Deployment.ServerKeyId(_deployment.NewFolder());
        _deployment.WriteServerLicense(serverData, s => s with { InstallationKeyThumbprint = otherKey });
        using (HttpResponseMessage refused = await admin.PostAsync("admin/v1/license/reload", null, cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(cancel));
            Assert.Equal("ELM_WRONG_INSTALLATION", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "The server license does not verify: The license is bound to another installation key. This server's installation "
                    + $"key is {Deployment.ServerKeyId(serverData)}.",
                problem.RootElement.GetProperty("detail").GetString()
            );
        }

        Assert.Equal(newToken, server.Engine.Server.License.TokenId);
        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(serverData, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.Empty(usage.Issues);
    }

    /// <summary>
    /// The borrow record's lines are read only in their documented shape; a signed line of another shape is skipped.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server data folder; a borrow record signed with the server's key whose lines are: a borrow without seats, a
    ///   borrow with seats as text, a borrow with a negative borrow limit, a return without a time, a return at a time past
    ///   year 9999, and one valid return with fingerprints that are not an array.
    ///
    /// Steps:
    /// - Load the record.
    ///
    /// Expected:
    /// - No borrow and exactly the one valid return, with no fingerprint.
    /// </remarks>
    [Fact]
    public async Task BorrowRecord_LinesOfAnotherShape_AreSkipped()
    {
        string serverData = _deployment.NewFolder();
        await (await StartOpenServerAsync(serverData)).DisposeAsync();
        using (ServerRecord record = ServerRecord.Open(serverData, _deployment.Time))
        {
            SignedLog log = record.Log;
            log.Append("borrow.started", w => w.WriteString("lease", "a.b.c"));
            log.Append(
                "borrow.started",
                w =>
                {
                    w.WriteString("lease", "a.b.c");
                    w.WriteString("lic", "l");
                    w.WriteString("seats", "2");
                    w.WriteNumber("borrow", 1);
                }
            );
            log.Append(
                "borrow.started",
                w =>
                {
                    w.WriteString("lease", "a.b.c");
                    w.WriteString("lic", "l");
                    w.WriteNumber("seats", 2);
                    w.WriteNumber("borrow", -1);
                }
            );
            log.Append(
                "borrow.returned",
                w =>
                {
                    w.WriteString("id", "x");
                    w.WriteString("key", "k");
                }
            );
            log.Append(
                "borrow.returned",
                w =>
                {
                    w.WriteString("id", "x");
                    w.WriteString("key", "k");
                    w.WriteNumber("at", 253_402_300_800);
                }
            );
            log.Append(
                "borrow.returned",
                w =>
                {
                    w.WriteString("id", "y");
                    w.WriteString("key", "k");
                    w.WriteString("fp", "aa");
                    w.WriteNumber("at", 2);
                }
            );
        }

        using ServerIdentity server = ServerIdentityOf(serverData);
        using BorrowJournal journal = BorrowJournal.Load(serverData, _deployment.Trust, server, setAsideBroken: false, _deployment.Time);
        Assert.Empty(journal.Borrows);
        BorrowReturn returned = Assert.Single(journal.Returns);
        Assert.Equal("y", returned.LeaseId);
        Assert.Empty(returned.Fingerprints);
    }

    /// <summary>
    /// An early return written after the last signature (a crash before its signature reached the disk, or a line added by
    /// someone who can write the folder) is not read, while a borrow written so is; the client was never told the return
    /// succeeded, and keeping the borrow is the safe side.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that lent a borrow and stopped; two unsigned lines appended to the record: the return of that borrow and
    ///   another borrow line copied from the record.
    ///
    /// Steps:
    /// - Load the record.
    ///
    /// Expected:
    /// - Two borrows (the signed one and the unsigned copy) and no return.
    /// </remarks>
    [Fact]
    public async Task BorrowRecord_UnsignedReturn_IsDropped()
    {
        string serverData = _deployment.NewFolder();
        ElmServer server = await StartOpenServerAsync(serverData);
        Lease borrow = Acquired(
            await _deployment.Client(server.PublicUrls[0]).BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken)
        );
        await server.DisposeAsync();
        string file = SignedLog.FindFiles(serverData, "borrows")[^1];
        string started = File.ReadAllLines(file).Last(l => l.Contains("borrow.started", StringComparison.Ordinal));
        File.AppendAllText(file, $"{{\"type\":\"borrow.returned\",\"ts\":1,\"id\":\"{borrow.Id}\",\"key\":\"k\",\"fp\":[],\"at\":1}}\n{started}\n");

        using ServerIdentity identity = ServerIdentityOf(serverData);
        using BorrowJournal journal = BorrowJournal.Load(serverData, _deployment.Trust, identity, setAsideBroken: false, _deployment.Time);
        Assert.Equal(2, journal.Borrows.Count);
        Assert.Empty(journal.Returns);
    }

    /// <summary>
    /// A borrow record signed by the key of another server license of the same vendor and product stops the start: another
    /// server cannot hand its record to this one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server data folder with a license (lic-srv); a record in it written with the key and license of another server
    ///   (lic-other).
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - ServerStartException that names lic-other.
    /// </remarks>
    [Fact]
    public async Task BorrowRecord_OfAnotherServer_StopsTheStart()
    {
        string serverData = _deployment.NewFolder();
        _deployment.WriteServerLicense(serverData);
        string otherData = _deployment.NewFolder();
        _deployment.WriteServerLicense(otherData, s => s with { LicenseId = "lic-other" });
        using (ServerRecord record = ServerRecord.Open(otherData, _deployment.Time, serverData))
        {
            record.Log.Append("server.note", _ => { });
        }

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(serverData));
        Assert.Contains("lic-other", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A borrow record of this server's key is read after a restart with a new server license ID (the vendor reissued the
    /// license to the same machine).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that lent a borrow under lic-srv and stopped; its license replaced by lic-new for the same key.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - It starts and counts the borrowed seat.
    /// </remarks>
    [Fact]
    public async Task BorrowRecord_OfThisKeyUnderANewLicense_IsRead()
    {
        string serverData = _deployment.NewFolder();
        ElmServer before = await StartOpenServerAsync(serverData);
        Acquired(
            await _deployment.Client(before.PublicUrls[0]).BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken)
        );
        await before.DisposeAsync();
        _deployment.WriteServerLicense(serverData, s => s with { LicenseId = "lic-new" });

        ElmServer after = await _deployment.StartServerAsync(serverData);
        Assert.Equal(1, Assert.Single(after.Engine.Usage()).Borrowed);
    }

    /// <summary>
    /// A newest borrow record that is empty or cut inside its first line (a crash while the file was started, or a file
    /// emptied by hand) is a broken record: the start stops with the reason, and --set-aside-broken-records moves it aside.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that ran and stopped; a newer borrow record file of 0 bytes, or holding only {"type":"chain.st.
    ///
    /// Steps:
    /// - Start; start with the set-aside option.
    ///
    /// Expected:
    /// - ServerStartException naming --set-aside-broken-records; then a running server and a borrows-broken file.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("{\"type\":\"chain.st")]
    public async Task EmptyRecord_IsABrokenRecord(string content)
    {
        string serverData = _deployment.NewFolder();
        await (await StartOpenServerAsync(serverData)).DisposeAsync();
        File.WriteAllText(Path.Combine(serverData, "borrows-29991231T000000000Z.jsonl"), content);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(serverData));
        Assert.Contains("--set-aside-broken-records", refused.Message, StringComparison.Ordinal);

        await _deployment.StartServerAsync(serverData, setAside: true);
        Assert.Single(Directory.GetFiles(serverData, "borrows-broken-*.jsonl"));
    }

    /// <summary>
    /// The usage log names each release by its kind and records an overage with the seats in use, from what the engine
    /// returns for the request.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license of pro with 2 seats and 50 % overage (3 allowed).
    ///
    /// Steps:
    /// - Acquire 3 leases of 1 seat; release two of them; borrow 1 seat (2 in use, no overage) and return it; stop and verify
    ///   the usage log.
    ///
    /// Expected:
    /// - One overage line, for the third lease, with inUse 3; two lease.released lines and one borrow.returned line.
    /// </remarks>
    [Fact]
    public async Task UsageLog_NamesReleasesAndOverage()
    {
        string serverData = _deployment.NewFolder();
        _deployment.WriteServerLicense(serverData, s => s with { OveragePercent = 50 });
        ElmServer server = await StartOpenServerAsync(serverData);
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Lease[] leases =
        [
            Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel)),
            Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel)),
            Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel)),
        ];
        Assert.Null(await client.ReleaseLeaseAsync(leases[0], cancel));
        Assert.Null(await client.ReleaseLeaseAsync(leases[1], cancel));
        Lease borrow = Acquired(await client.BorrowLeaseAsync("pro", Day, cancellationToken: cancel));
        Assert.Null(await client.ReleaseLeaseAsync(borrow, cancel));

        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(serverData, "usage"), _deployment.Trust, "acme-cad", "usage");
        LogEntry overage = Assert.Single(usage.Entries, e => e.Type == "overage");
        Assert.Equal((leases[2].Id, 3L), (overage.Value.GetProperty("lease").GetString(), overage.Value.GetProperty("inUse").GetInt64()));
        Assert.Equal(
            [leases[0].Id, leases[1].Id],
            usage.Entries.Where(e => e.Type == "lease.released").Select(e => e.Value.GetProperty("lease").GetString())
        );
        Assert.Equal(borrow.Id, Assert.Single(usage.Entries, e => e.Type == "borrow.returned").Value.GetProperty("lease").GetString());
    }

    /// <summary>
    /// Refused requests are written as counts per feature and code each minute, with at most 100 kinds: refusals beyond them
    /// are counted without their feature, so many feature names cannot grow the log line by line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window.
    ///
    /// Steps:
    /// - Acquire 105 features the license does not grant, f-0 to f-104, and f-0 again; stop and verify the usage log.
    ///
    /// Expected:
    /// - 101 lease.denied lines with ELM_FEATURE_NOT_LICENSED: f-0 with count 2, 99 more with a feature and count 1, and
    ///   one without a feature with count 5.
    /// </remarks>
    [Fact]
    public async Task Denials_AreCountedWithBoundedKinds()
    {
        ElmServer server = await StartOpenServerAsync();
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        foreach (int i in Enumerable.Range(0, 105).Append(0))
        {
            Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync($"f-{i}", cancellationToken: TestContext.Current.CancellationToken));
        }

        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(server.DataDirectory, "usage"), _deployment.Trust, "acme-cad", "usage");
        LogEntry[] denied = [.. usage.Entries.Where(e => e.Type == "lease.denied")];
        Assert.Equal(101, denied.Length);
        Assert.All(denied, e => Assert.Equal("ELM_FEATURE_NOT_LICENSED", e.Value.GetProperty("code").GetString()));
        long Count(string? feature) =>
            denied
                .Single(e => (e.Value.TryGetProperty("feat", out JsonElement f) ? f.GetString() : null) == feature)
                .Value.GetProperty("count")
                .GetInt64();
        Assert.Equal(2, Count("f-0"));
        Assert.Equal(1, Count("f-99"));
        Assert.Equal(5, Count(null));
    }

    /// <summary>
    /// The client keeps at most 1,000 saved borrows: the 1,000th is saved, a borrow beyond them is refused with
    /// ELM_INTERNAL_ERROR.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client data folder whose borrows folder holds 999 files.
    ///
    /// Steps:
    /// - Borrow; borrow again.
    ///
    /// Expected:
    /// - The first is saved (1,000 files); the second is ELM_INTERNAL_ERROR naming the 1,000 borrows, and no file is added.
    /// </remarks>
    [Fact]
    public async Task Borrow_BeyondTheSavedLimit_IsRefused()
    {
        ElmServer server = await StartOpenServerAsync();
        string data = _deployment.NewFolder();
        LicenseClient client = _deployment.Client(server.PublicUrls[0], data);
        string folder = Path.Combine(data, "borrows");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 999; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"old-{i}.json"), "{}");
        }

        Acquired(await client.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1_000, Directory.GetFiles(folder, "*.json").Length);
        LeaseResult result = await client.BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken);
        LicenseError error = Assert.IsType<LeaseResult.Failed>(result).Error;
        Assert.Equal(ErrorCode.InternalError, error.Code);
        Assert.Contains("1000 borrows", error.Message, StringComparison.Ordinal);
        Assert.Equal(1_000, Directory.GetFiles(folder, "*.json").Length);
    }

    /// <summary>
    /// The administrator cannot release a borrow (design §12.7.4): the API answers 409 ELM_RETURN_NOT_ALLOWED and the seat
    /// stays in use.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with a borrow.
    ///
    /// Steps:
    /// - DELETE /admin/v1/leases/{borrow ID}.
    ///
    /// Expected:
    /// - 409 with ELM_RETURN_NOT_ALLOWED; 1 seat borrowed.
    /// </remarks>
    [Fact]
    public async Task AdminApi_DoesNotReleaseABorrow()
    {
        ElmServer server = await StartOpenServerAsync();
        Lease borrow = Acquired(
            await _deployment.Client(server.PublicUrls[0]).BorrowLeaseAsync("pro", Day, cancellationToken: TestContext.Current.CancellationToken)
        );
        using HttpClient admin = Deployment.AdminHttp(server);

        using HttpResponseMessage refused = await admin.DeleteAsync($"admin/v1/leases/{borrow.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(
            "ELM_RETURN_NOT_ALLOWED",
            await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
        Assert.Equal(1, Assert.Single(server.Engine.Usage()).Borrowed);
    }

    /// <summary>
    /// An administration operation that ends with an exception is written to the usage log as admin.action with the
    /// result ELM_INTERNAL_ERROR, and the caller gets no answer of the operation (design §13.7, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server on a clock whose next read the test can make throw an InvalidOperationException. No setting of a deployed
    ///   server makes an operation of the administration API throw, so the exception comes from the host's clock.
    ///
    /// Steps:
    /// - Make the next read throw; get the status, which reads the clock; get the status again; stop and verify the usage
    ///   log.
    ///
    /// Expected:
    /// - 500 without a body, then 200. The usage log is intact, and its admin.action lines are status with
    ///   ELM_INTERNAL_ERROR and status with ELM_OK, in this order.
    /// </remarks>
    [Fact]
    public async Task AdminApi_OperationThrows_IsRecordedAsInternalError()
    {
        using var clock = new HeldClock(_deployment.Time);
        await using ElmServer server = await StartOnClockAsync(clock, new RecordingLoggerProvider());
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using HttpClient admin = Deployment.AdminHttp(server);

        clock.FailNextRead();
        using (HttpResponseMessage failed = await admin.GetAsync("admin/v1/status", cancel))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Empty(await failed.Content.ReadAsByteArrayAsync(cancel));
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("admin/v1/status", cancel)).StatusCode);

        await server.DisposeAsync();
        Assert.Equal(["status:ELM_INTERNAL_ERROR", "status:ELM_OK"], AdminActions(server));
    }

    /// <summary>
    /// An administration operation whose answer cannot be written because the caller is gone is written to the usage log
    /// as admin.action with the result ELM_NETWORK_ERROR (design §13.7, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server on a clock that can hold its next read, with a log the test reads.
    ///
    /// Steps:
    /// - Hold the next read of the clock, which the status operation makes after the token is checked.
    /// - On a connection of its own to the socket file, GET /admin/v1/status with the administrator token. Once the server
    ///   waits in the held read, end the caller's side and read until the server has closed the connection; let the held
    ///   read go on; wait for one more admin.action entry in the server's log.
    /// - Kestrel cancels the request's token on the thread pool after it has closed the connection. A run in which the
    ///   answer was written before that leaves a line with ELM_OK (no run of 15 on Windows 11, measured with the loop cut
    ///   to one run; 1 of 15 in the same test of elm-service), and the steps are run again, twenty times at most.
    /// - Stop and verify the usage log.
    ///
    /// Expected:
    /// - Each run adds exactly one admin.action entry. The usage log is intact; its last admin.action line is status with
    ///   ELM_NETWORK_ERROR, and every line before it is status with ELM_OK.
    /// </remarks>
    [Fact]
    public async Task AdminApi_CallerGoneBeforeTheAnswer_IsRecordedAsNetworkError()
    {
        var log = new RecordingLoggerProvider();
        using var clock = new HeldClock(_deployment.Time);
        await using ElmServer server = await StartOnClockAsync(clock, log);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        byte[] request = System.Text.Encoding.ASCII.GetBytes(
            $"GET /admin/v1/status HTTP/1.1\r\nHost: elm-server.invalid\r\nAuthorization: Bearer {AdminToken.Read(server.DataDirectory)}\r\n\r\n"
        );
        string[] Logged() => [.. log.Entries.Select(e => e.Message).Where(m => m.StartsWith("admin.action: status ", StringComparison.Ordinal))];

        bool cut = false;
        for (int run = 0; run < 20 && !cut; run++)
        {
            int before = Logged().Length;
            Task held = clock.HoldNextReadAsync();
            using var caller = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified
            );
            await caller.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(server.AdminSocketPath), cancel);
            await caller.SendAsync(request, cancel);
            await held.WaitAsync(TimeSpan.FromSeconds(10), cancel);
            await LeaveAsync(caller);
            clock.Release();
            await Deployment.WaitUntilAsync(() => Logged().Length > before);
            string[] entries = Logged();
            Assert.Equal(before + 1, entries.Length);
            cut = entries[^1].EndsWith("-> ELM_NETWORK_ERROR", StringComparison.Ordinal);
        }

        await server.DisposeAsync();
        string[] actions = AdminActions(server);
        Assert.Equal("status:ELM_NETWORK_ERROR", actions[^1]);
        Assert.All(actions[..^1], a => Assert.Equal("status:ELM_OK", a));
    }

    private static Lease Acquired(LeaseResult result) => Assert.IsType<LeaseResult.Acquired>(result).Lease;

    // A server with a license in a new data folder, on the given clock, whose log goes to the given provider too.
    private async Task<ElmServer> StartOnClockAsync(TimeProvider clock, RecordingLoggerProvider log)
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        return await ElmServer.StartAsync(
            _deployment.Settings(data),
            new ServerHostOptions
            {
                Time = clock,
                Environment = _ => null,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Information,
                LoggerProvider = log,
            },
            TestContext.Current.CancellationToken
        );
    }

    // The admin.action lines of a stopped server's usage log, which must verify, as "action:result".
    private string[] AdminActions(ElmServer server)
    {
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(server.DataDirectory, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        return
        [
            .. usage
                .Entries.Where(e => e.Type == "admin.action")
                .Select(e => e.Value.GetProperty("action").GetString() + ":" + e.Value.GetProperty("result").GetString()),
        ];
    }

    // The caller goes: it ends its side of the connection and reads until the server has closed it too, which Kestrel does
    // when it has taken the request as aborted. A connection that the server resets counts as closed.
    private static async Task LeaveAsync(System.Net.Sockets.Socket caller)
    {
        caller.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] buffer = new byte[1_024];
        try
        {
            while (await caller.ReceiveAsync(buffer, limit.Token) > 0) { }
        }
        catch (System.Net.Sockets.SocketException) { }
    }

    // The deployment's fake clock for a server, whose next read of the time the test can hold until it lets it go on, or
    // make throw. The status operation reads the time, so a held read keeps a request inside that operation.
    private sealed class HeldClock(TimeProvider inner) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(true);
        private readonly Lock _lock = new();
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hold;
        private bool _fail;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        // Holds the next read of the time and returns a task that ends once that read waits.
        public Task HoldNextReadAsync()
        {
            lock (_lock)
            {
                _release.Reset();
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _hold = true;
                return _held.Task;
            }
        }

        public void Release() => _release.Set();

        public void FailNextRead()
        {
            lock (_lock)
            {
                _fail = true;
            }
        }

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);

        public override DateTimeOffset GetUtcNow()
        {
            bool hold;
            bool fail;
            lock (_lock)
            {
                (hold, fail) = (_hold, _fail);
                (_hold, _fail) = (false, false);
                if (hold)
                {
                    _held.SetResult();
                }
            }

            if (fail)
            {
                throw new InvalidOperationException("The test's clock cannot be read.");
            }

            if (hold)
            {
                // The limit only ends a test that never lets the read go on.
                _release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            return inner.GetUtcNow();
        }

        public void Dispose() => _release.Dispose();
    }

    // The server identity of a data folder: its license verified for its installation key.
    private ServerIdentity ServerIdentityOf(string serverData)
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, serverData);
        Assert.True(store.TryLoad(out InstallationKey? key, out _));
        using (key)
        {
            var verifier = new LicenseVerifier(_deployment.Trust, _deployment.Time, StatelessClockGuard.Instance);
            Assert.True(
                ServerIdentity.TryVerify(
                    verifier,
                    File.ReadAllBytes(Path.Combine(serverData, "server.elmlic")),
                    key!.WithPrivateKey(KeyFiles.ExportPublicJwk),
                    "acme-cad",
                    out ServerIdentity? identity,
                    out _
                )
            );
            return identity!;
        }
    }

    private static ServerSettings With(ServerSettings settings, int port) =>
        new()
        {
            Product = settings.Product,
            Listen = new Uri($"https://127.0.0.1:{port}"),
            LicensePath = settings.LicensePath,
            TrustRootKeyFiles = settings.TrustRootKeyFiles,
            DataDirectory = settings.DataDirectory,
            RateLimitPerIp = settings.RateLimitPerIp,
        };

    private async Task<ElmServer> StartOpenServerAsync(string? data = null)
    {
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        return server;
    }
}
