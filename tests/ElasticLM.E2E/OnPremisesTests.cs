using System.Globalization;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.E2E;

/// <summary>
/// The on-premises license server's flows against the published elm-server, with ElasticLM.Client as the app and the
/// published elm as the administrator's tool (design §12.7, §13, §20.7).
/// </summary>
public sealed class OnPremisesTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Leases on a server with 15-second leases: taken after the start's reclaim time, renewed on their own, listed and released
    /// by the administrator, borrowed and returned, held again after the server restarts, and logged in a usage log that
    /// verifies.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with a server license issued by elm (pro, 3 seats, 15-second leases, borrows of
    ///   a day) bound to the key the server made on its first run.
    ///
    /// Steps:
    /// - Start the server. Acquire pro, waiting each Retry-After while the server answers ELM_SERVER_RECOVERING. Wait for the
    ///   lease's expiry to move (its renewal at a third of the lease). elm server status and leases. Acquire a second lease and
    ///   release it with elm server release. Borrow pro for an hour and return it. Kill the server and start it again; acquire
    ///   once; wait for the first lease to be held again with a later expiry. Release it. elm server seal; elm log verify the
    ///   data folder --product acme-cad.
    ///
    /// Expected:
    /// - The lease is Held with 1 seat; its expiry moves; status and leases exit 0 and leases names the lease; release exits 0 and
    ///   the second lease is gone from leases; the borrow is a borrow and is gone after its return. After the restart a new
    ///   acquisition is ELM_SERVER_RECOVERING, and the first lease comes back to Held with a later expiry. The log verifies with
    ///   exit 0.
    /// </remarks>
    [Fact]
    public async Task Leases_RenewBorrowReleaseAndSurviveARestart()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "server", "acme-cad", license);
        string serverKey = await server.InstallationKeyAsync();
        string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        await vendor.ElmOkAsync(
            "license",
            "issue",
            "--signing-key",
            vendor.SigningKey,
            "--certificate",
            vendor.Certificate,
            "--product",
            "acme-cad",
            "--license-id",
            "e2e-server",
            "--kind",
            "server",
            "--feature",
            "pro;seats=3",
            "--expires",
            expires,
            "--bind-key",
            serverKey,
            "--lease-minutes",
            "0.25",
            "--borrow-days",
            "1",
            "--trust",
            vendor.RootPublicKey,
            "--out",
            license
        );
        await server.StartAsync();

        var options = new LicenseClientOptions
        {
            ProductId = "acme-cad",
            ServerUrl = server.Url,
            Trust = TrustStore.Create([await File.ReadAllTextAsync(vendor.RootPublicKey, Cancel)], []),
            DataDirectory = vendor.File("client"),
        };
        Assert.True(LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error), error?.Message);
        await using LicenseClient client = created!;

        Lease lease = await AcquireAfterRecoveryAsync(client);
        Assert.Equal((LeaseState.Held, 1), (lease.State, lease.Seats));
        DateTimeOffset first = lease.ExpiresAt;
        await RealClock.WaitUntilAsync("the lease's renewal", () => lease.ExpiresAt > first, Bound);

        Assert.Equal(0, (await server.ElmServerAsync(vendor, "status")).Code);
        (int code, string leases, string listError) = await server.ElmServerAsync(vendor, "leases");
        Assert.True(code == 0, listError);
        Assert.Contains(lease.Id, leases, StringComparison.Ordinal);

        Lease second = Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: Cancel)).Lease;
        (code, _, string releaseError) = await server.ElmServerAsync(vendor, "release", second.Id);
        Assert.True(code == 0, releaseError);
        Assert.DoesNotContain(second.Id, (await server.ElmServerAsync(vendor, "leases")).Out, StringComparison.Ordinal);

        Lease borrowed = Assert
            .IsType<LeaseResult.Acquired>(await client.BorrowLeaseAsync("pro", TimeSpan.FromHours(1), cancellationToken: Cancel))
            .Lease;
        Assert.True(borrowed.IsBorrow);
        Assert.Contains(client.GetBorrowedLeases(), l => l.Id == borrowed.Id);
        Assert.Null(await client.ReleaseLeaseAsync(borrowed, Cancel));
        Assert.DoesNotContain(client.GetBorrowedLeases(), l => l.Id == borrowed.Id);

        // design §13.4: after a crash the server waits a lease's length; a client's renewal gets ELM_UNKNOWN_LEASE and takes its
        // seat back, while a new acquisition waits.
        // Read after the kill: a renewal between the read and the kill would move the expiry, and the wait below would hold
        // before the server took the seat back (PR #179's review).
        await server.KillAsync();
        DateTimeOffset beforeRestart = lease.ExpiresAt;
        await server.StartAsync();
        LeaseResult.Failed waiting = Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync("pro", cancellationToken: Cancel));
        Assert.Equal(ErrorCode.ServerRecovering, waiting.Error.Code);
        await RealClock.WaitUntilAsync(
            "the lease held again after the restart",
            () => lease.State == LeaseState.Held && lease.ExpiresAt > beforeRestart,
            Bound
        );
        Assert.Null(await client.ReleaseLeaseAsync(lease, Cancel));

        (code, _, string sealError) = await server.ElmServerAsync(vendor, "seal");
        Assert.True(code == 0, sealError);
        (code, string verified, string verifyError) = await vendor.ElmAsync(
            "log",
            "verify",
            server.DataDirectory,
            "--product",
            "acme-cad",
            "--trust",
            vendor.RootPublicKey
        );
        Assert.True(code == 0, verified + verifyError);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A server just started refuses acquisitions for a lease's length (design §13.4), and says how long with Retry-After.
    private static async Task<Lease> AcquireAfterRecoveryAsync(LicenseClient client)
    {
        for (int i = 0; i < 10; i++)
        {
            LeaseResult result = await client.AcquireLeaseAsync("pro", cancellationToken: Cancel);
            if (result is LeaseResult.Acquired acquired)
            {
                return acquired.Lease;
            }

            var failed = (LeaseResult.Failed)result;
            Assert.True(failed.Error.Code == ErrorCode.ServerRecovering, failed.Error.Message);
            await RealClock.DelayAsync("the server's reclaim time", failed.RetryAfter ?? TimeSpan.FromSeconds(1));
        }

        Assert.Fail("The server did not give a lease after its reclaim time.");
        return null!;
    }
}
