using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;
using ElasticLM.Server.Tests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The audit rows of administration calls that end early (design §14.5, §18.2, V1d9a): refusals made during authentication
/// and at the admin UI's sign-in, request bodies that Kestrel cannot read, and commits that a caller who leaves no longer
/// cancels.
/// </summary>
public sealed class ElmServiceEndedEarlyTests
{
    /// <summary>
    /// A refusal made while the caller is authenticated, whose answer cannot be written because the caller is gone, is
    /// audited as a refusal after authentication is (design §18.2, V1d9a): the row has the result ELM_NETWORK_ERROR and the
    /// refusal's actor and detail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake identity provider and signs in to the admin UI, on a clock that can hold one read of
    ///   the time; an owner key.
    /// - csrf: a session of alice (owner) from a sign-in; the request is POST /v1/admin/audit/verify with her session cookie
    ///   and no X-ELM-CSRF header. token: the request is GET /v1/admin/admins with dave's access token, whose group maps to
    ///   no role.
    ///
    /// Steps:
    /// - Hold the read of the time that the session's or the token's check makes itself: it comes after the check's last
    ///   read of the database and before the refusal's answer.
    /// - On a socket of its own, send the request. Once the service waits in the held read, end the caller's side and read
    ///   until the service has closed the connection; let the held read go on; wait for one more row of the action.
    /// - Run these steps 50 times: Kestrel cancels the request's token on the thread pool after it has closed the
    ///   connection, and until then it drops the answer's write without an exception (design §18.2, V1d9).
    /// - Check the chain with the owner key.
    ///
    /// Expected:
    /// - Each run adds exactly one row: csrf, audit.verify by alice's ID with the detail {"reason":"csrf"}; token,
    ///   admin.list with no actor ID and the detail of dave's issuer and subject. Each has the result ELM_NETWORK_ERROR and
    ///   no target. The chain is intact.
    /// </remarks>
    [Theory]
    [InlineData("csrf")]
    [InlineData("token")]
    public async Task AuthenticationRefusal_CallerGoneBeforeItsAnswer_IsAuditedAsNetworkError(string refusal)
    {
        await using TestService test = TestService.Open("sqlite");
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        using var clock = new HeldClock(test.Time);
        await using ElmService service = await StartWebAsync(test, idp, clock);
        string head = $"Host: {service.Url.Authority}\r\n";
        string request,
            action,
            method;
        string? actorId;
        (string, string)[] detail;
        if (refusal == "csrf")
        {
            using var browser = new WebBrowser();
            string session = await SignInAsync(browser, service);
            SessionHolder holder = (await service.Database.FindSessionAsync(ApiKeys.HashOf(session), TestContext.Current.CancellationToken))!;
            request = $"POST /v1/admin/audit/verify HTTP/1.1\r\n{head}Cookie: {ElmService.SessionCookie}={session}\r\nContent-Length: 0\r\n\r\n";
            (action, method, actorId, detail) = ("audit.verify", "AuthenticateSessionAsync", holder.AdminId.ToString("D"), [("reason", "csrf")]);
        }
        else
        {
            request = $"GET /v1/admin/admins HTTP/1.1\r\n{head}Authorization: Bearer {idp.Token("dave", ["other"])}\r\n\r\n";
            (action, method, actorId, detail) = ("admin.list", "AuthenticateOidcAsync", null, [("issuer", idp.Issuer), ("subject", "dave")]);
        }

        for (int run = 0; run < 50; run++)
        {
            await LeaveWhileHeldAsync(service, clock, method, request);
            await TestService.WaitForAsync(async () => (await ElmServiceAdminTests.RowsAsync(service, action)).Length > run);
            AuditEntry[] rows = await ElmServiceAdminTests.RowsAsync(service, action);
            Assert.Equal(run + 1, rows.Length);
            Assert.Equal(("ELM_NETWORK_ERROR", "oidc", actorId, null), (rows[^1].Result, rows[^1].ActorType, rows[^1].ActorId, rows[^1].Target));
            Assert.Equal(detail, Members(rows[^1].Detail));
        }

        await TestService.AssertAuditIntactAsync(test.Http(service, owner));
    }

    /// <summary>
    /// A refused admin UI sign-in whose page cannot be written because the browser is gone is audited as other refusals are
    /// (design §15.3, §18.2, V1d9a): the session.create row has the result ELM_NETWORK_ERROR and the refusal's detail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake identity provider and signs in to the admin UI, on a clock that can hold one read of
    ///   the time; an owner key; the provider signs in bob, whose group maps to no role and who is no administrator.
    ///
    /// Steps:
    /// - In a new browser, GET /auth/login and follow it to the provider, whose answer sends the browser to the callback.
    /// - Hold the read of the time that the callback's handler of the ticket makes itself: it comes after its read of the
    ///   administrator and before the refusal's page.
    /// - On a socket of its own, send the callback's request with the browser's cookies. Once the service waits in the held
    ///   read, end the caller's side and read until the service has closed the connection; let the held read go on; wait
    ///   for one more session.create row.
    /// - Run these steps 20 times, each sign-in in a new browser. Check the chain with the owner key.
    ///
    /// Expected:
    /// - Each run adds exactly one session.create row: result ELM_NETWORK_ERROR, actor type oidc with no actor ID, no
    ///   target, and the detail of bob's issuer and subject. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task SignInRefusal_CallerGoneBeforeItsPage_IsAuditedAsNetworkError()
    {
        await using TestService test = TestService.Open("sqlite");
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        using var clock = new HeldClock(test.Time);
        await using ElmService service = await StartWebAsync(test, idp, clock);
        idp.Subject = "bob";
        idp.Groups = ["nobody"];

        for (int run = 0; run < 20; run++)
        {
            using var browser = new WebBrowser();
            using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
            using HttpResponseMessage back = await browser.FollowAsync(login);
            Uri location = back.Headers.Location!;
            Uri callback = location.IsAbsoluteUri ? location : new Uri(back.RequestMessage!.RequestUri!, location);
            string cookies = string.Join("; ", browser.Cookies(service.Url).Select(c => c.Key + "=" + c.Value));
            string request = $"GET {callback.PathAndQuery} HTTP/1.1\r\nHost: {service.Url.Authority}\r\nCookie: {cookies}\r\n\r\n";

            await LeaveWhileHeldAsync(service, clock, "OnTicketReceivedAsync", request);
            await TestService.WaitForAsync(async () => (await ElmServiceAdminTests.RowsAsync(service, "session.create")).Length > run);
            AuditEntry[] rows = await ElmServiceAdminTests.RowsAsync(service, "session.create");
            Assert.Equal(run + 1, rows.Length);
            Assert.Equal(("ELM_NETWORK_ERROR", "oidc", null, null), (rows[^1].Result, rows[^1].ActorType, rows[^1].ActorId, rows[^1].Target));
            Assert.Equal([("issuer", idp.Issuer), ("subject", "bob")], Members(rows[^1].Detail));
        }

        await TestService.AssertAuditIntactAsync(test.Http(service, owner));
    }

    /// <summary>
    /// A refused admin UI sign-in whose row cannot be written keeps the refusal's page whole (design §15.3, §18.2): the page
    /// was written before the row, so the failure only goes to the service log.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake identity provider and signs in to the admin UI; the provider signs in bob, whose
    ///   group maps to no role; the table of the chain's state renamed, so that every audit append fails.
    ///
    /// Steps:
    /// - In a new browser, GET /auth/login and follow it to the provider and back to the callback; give the table its name
    ///   back.
    ///
    /// Expected:
    /// - The callback's answer is whole: 403 with the text "This account may not use the admin UI.\n".
    /// </remarks>
    [Fact]
    public async Task SignInRefusal_RowCannotBeWritten_KeepsTheRefusalPage()
    {
        await using TestService test = TestService.Open("sqlite");
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        _ = await test.SetupAsync();
        await using ElmService service = await StartWebAsync(test, idp, test.Time);
        idp.Subject = "bob";
        idp.Groups = ["nobody"];

        await test.ExecuteAsync("ALTER TABLE audit_state RENAME TO audit_state_gone");
        using var browser = new WebBrowser();
        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        using HttpResponseMessage back = await browser.FollowAsync(login);
        using HttpResponseMessage callback = await browser.FollowAsync(back);
        await test.ExecuteAsync("ALTER TABLE audit_state_gone RENAME TO audit_state");

        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Equal("This account may not use the admin UI.\n", await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An administration call whose request body has a bad chunk is audited with ELM_MALFORMED, and ends with one line of
    /// the service's own and a closed connection, without an error entry (design §18.2, V1d9a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service whose operational log the test records from Information up; an owner key.
    ///
    /// Steps:
    /// - On a socket of its own, POST /v1/admin/api-keys with Transfer-Encoding: chunked and the chunk size "zz"; read until
    ///   the service has closed the connection, moving the service's clock a second every 50 ms while it waits, since the
    ///   failed read waits up to a second on that clock for Kestrel to cancel the request; wait for the apikey.create row.
    ///
    /// Expected:
    /// - No byte of an answer. Exactly one apikey.create row, with the result ELM_MALFORMED and no target or detail. The log
    ///   has the line "The request of an administration call (apikey.create) could not be read (400); the connection was
    ///   closed." and no entry at Error or above.
    /// </remarks>
    [Fact]
    public async Task Body_BadChunk_IsAuditedAsMalformedAndLoggedInOneLine()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        using var log = new RecordingLoggerProvider();
        ElmService service = await test.StartAsync(log: log);
        string request =
            $"POST /v1/admin/api-keys HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n";

        using Socket caller = await ElmServiceAdminTests.SendAsync(service, request);
        Assert.Equal(0, await ReadToCloseAsync(caller, TimeSpan.FromSeconds(10), test.Time));
        await TestService.WaitForAsync(async () => (await ElmServiceAdminTests.RowsAsync(service, "apikey.create")).Length > 1);

        AuditEntry row = (await ElmServiceAdminTests.RowsAsync(service, "apikey.create"))[^1];
        Assert.Equal(("ELM_MALFORMED", null, null), (row.Result, row.Target, row.Detail));
        Assert.Equal(2, (await ElmServiceAdminTests.RowsAsync(service, "apikey.create")).Length);
        AssertOneLine(log, "The request of an administration call (apikey.create) could not be read (400); the connection was closed.");
    }

    /// <summary>
    /// An administration call whose request body comes too slowly is audited with ELM_NETWORK_ERROR, and ends with one line
    /// of the service's own and a closed connection, without an error entry (design §18.2, V1d9a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service whose operational log the test records from Information up; an owner key. Kestrel's least body rate is
    ///   its default, 240 bytes a second after 5 seconds, counted on the real clock.
    ///
    /// Steps:
    /// - On a socket of its own, POST /v1/admin/api-keys with Content-Length 100 and 9 bytes of the body; read until the
    ///   service has closed the connection, for at most 30 seconds, moving the service's clock a second every 50 ms; wait for
    ///   the apikey.create row.
    ///
    /// Expected:
    /// - No byte of an answer. Exactly one more apikey.create row, with the result ELM_NETWORK_ERROR. The log has the line
    ///   "The request of an administration call (apikey.create) could not be read (408); the connection was closed." and no
    ///   entry at Error or above.
    /// </remarks>
    [Fact]
    public async Task Body_TooSlow_IsAuditedAsNetworkErrorAndLoggedInOneLine()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        using var log = new RecordingLoggerProvider();
        ElmService service = await test.StartAsync(log: log);
        string request =
            $"POST /v1/admin/api-keys HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + "Content-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"name\":\"";

        using Socket caller = await ElmServiceAdminTests.SendAsync(service, request);
        Assert.Equal(0, await ReadToCloseAsync(caller, TimeSpan.FromSeconds(30), test.Time));
        await TestService.WaitForAsync(async () => (await ElmServiceAdminTests.RowsAsync(service, "apikey.create")).Length > 1);

        AuditEntry[] rows = await ElmServiceAdminTests.RowsAsync(service, "apikey.create");
        Assert.Equal(2, rows.Length);
        Assert.Equal("ELM_NETWORK_ERROR", rows[^1].Result);
        AssertOneLine(log, "The request of an administration call (apikey.create) could not be read (408); the connection was closed.");
    }

    /// <summary>
    /// A caller who resets or ends the connection in the middle of the request body is audited with ELM_NETWORK_ERROR every
    /// time, whether the read of the body fails before Kestrel cancels the request's token or after (design §18.2, V1d9a
    /// review).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on a clock that can hold one read of the timestamp; an owner key. The clock is never moved, so the failed
    ///   read's wait for Kestrel's cancellation can only end by that cancellation.
    ///
    /// Steps:
    /// - Hold the second read of the timestamp from here on: the limit per key, after authentication and before the body is
    ///   read.
    /// - On a socket of its own, POST /v1/admin/api-keys with Content-Length 100 and 9 bytes of the body. Once the service
    ///   waits in the held read, let it go on, and wait 100 ms, while the service waits in the read of the body for the
    ///   rest. Then: reset, close the socket with a linger time of 0, which sends RST; end, end the caller's side, which
    ///   sends FIN. The failed read of the body races Kestrel's cancellation. Wait for one more apikey.create row.
    /// - Run these steps 40 times. Before the wait, 16 runs of 40 with RST gave ELM_INTERNAL_ERROR, and 9 of 40 with FIN gave
    ///   ELM_MALFORMED (the review of PR #417).
    ///
    /// Expected:
    /// - Each run adds exactly one apikey.create row, with the result ELM_NETWORK_ERROR.
    /// </remarks>
    [Theory]
    [InlineData("reset")]
    [InlineData("end")]
    public async Task Body_CallerLeavesInTheMiddle_IsAuditedAsNetworkErrorEachTime(string leave)
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        using var clock = new HeldClock(test.Time);
        await using ElmService service = await ElmServiceAdminTests.StartOnClockAsync(test, clock);
        string request =
            $"POST /v1/admin/api-keys HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + "Content-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"name\":\"";

        for (int run = 0; run < 40; run++)
        {
            Task held = clock.HoldAsync(2);
            using (Socket caller = await ElmServiceAdminTests.SendAsync(service, request))
            {
                await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                clock.Release();

                // The service goes on to read the body, and waits there for the 91 bytes that never come.
                await Task.Delay(100, TestContext.Current.CancellationToken);
                if (leave == "reset")
                {
                    caller.LingerState = new LingerOption(true, 0);
                    caller.Close();
                }
                else
                {
                    caller.Shutdown(SocketShutdown.Send);
                }
            }

            await TestService.WaitForAsync(async () => (await ElmServiceAdminTests.RowsAsync(service, "apikey.create")).Length > run + 1);
            AuditEntry[] rows = await ElmServiceAdminTests.RowsAsync(service, "apikey.create");
            Assert.Equal(run + 2, rows.Length);
            Assert.Equal("ELM_NETWORK_ERROR", rows[^1].Result);
        }
    }

    /// <summary>
    /// The result of a call that ended with an exception (design §18.2, V1d9, V1d9a): a canceled request is
    /// ELM_NETWORK_ERROR whatever the exception; otherwise Kestrel's BadHttpRequestException gives ELM_MALFORMED for 400 and
    /// ELM_NETWORK_ERROR for 408, and any other exception, or another status, is ELM_INTERNAL_ERROR.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A request whose token is not canceled, and one whose token is.
    ///
    /// Steps:
    /// - Ask for the result of BadHttpRequestException with 400, 408, and 413, and of InvalidOperationException, for each
    ///   request.
    ///
    /// Expected:
    /// - Not canceled: Malformed, NetworkError, InternalError, InternalError. Canceled: NetworkError for each.
    /// - Only 400 and 408 count as a body the caller sent that could not be read, which ends with the service's own log line
    ///   and a closed connection; 413 goes on as other exceptions do.
    /// </remarks>
    [Fact]
    public void FailureCode_ByCancellationAndStatus_GivesTheResult()
    {
        Exception[] exceptions =
        [
            new BadHttpRequestException("bad", StatusCodes.Status400BadRequest),
            new BadHttpRequestException("slow", StatusCodes.Status408RequestTimeout),
            new BadHttpRequestException("large", StatusCodes.Status413PayloadTooLarge),
            new InvalidOperationException("failed"),
        ];
        var open = new DefaultHttpContext();
        var canceled = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };

        Assert.Equal(
            [ErrorCode.Malformed, ErrorCode.NetworkError, ErrorCode.InternalError, ErrorCode.InternalError],
            exceptions.Select(e => ElmService.FailureCode(open, e))
        );
        Assert.All(exceptions, e => Assert.Equal(ErrorCode.NetworkError, ElmService.FailureCode(canceled, e)));
        Assert.Equal([true, true, false], exceptions.OfType<BadHttpRequestException>().Select(ElmService.IsUnreadableBody));
    }

    /// <summary>
    /// A write's transaction commits once its body has run, even when the caller's token is canceled after the body
    /// (design §14.2, §18.2, V1d9a): the commit does not take that token.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key; a license; each engine.
    ///
    /// Steps:
    /// - In the license's transaction, append an audit row with a token of the test, then cancel that token as the body's
    ///   last step; read the rows; check the chain.
    ///
    /// Expected:
    /// - The transaction returns the body's result, and the row is stored. The chain is intact.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task WriteTransaction_TokenCanceledAfterItsBody_Commits(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        (string license, _) = await ElmServiceActivationsTests.LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));

        using var caller = new CancellationTokenSource();
        var entry = new AuditEntry("system", "test", "lease.release", "after-the-body", "ELM_OK", null, null);
        bool done = await service.Database.WithLicenseAsync(
            Guid.Parse(license),
            async scope =>
            {
                await scope!.AppendAuditAsync(entry, caller.Token);
                await caller.CancelAsync();
                return true;
            },
            test.Time.GetUtcNow(),
            caller.Token
        );

        Assert.True(done);
        Assert.Single(await ElmServiceAdminTests.RowsAsync(service, "lease.release"), r => r.Target == "after-the-body");
        await TestService.AssertAuditIntactAsync(admin);
    }

    /// <summary>
    /// On PostgreSQL, a state change whose caller leaves while its commit runs is stored, with its one row, the
    /// transaction's (design §18.2, V1d9a): the commit does not take the request's token, which Npgsql would act on by
    /// canceling the COMMIT.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on PostgreSQL and an owner key. A deferred constraint trigger on the products sleeps 3 seconds in
    ///   pg_sleep, which runs it at the commit of the transaction that adds a product.
    ///
    /// Steps:
    /// - On a socket of its own, POST /v1/admin/products. Once a backend waits in pg_sleep, end the caller's side and read
    ///   until the service has closed the connection; wait until the product is stored; stop the service, which waits for
    ///   the request; read the rows and check the chain.
    ///
    /// Expected:
    /// - The product is stored, and exactly one product.create row exists: result ELM_OK. No row has the result
    ///   ELM_NETWORK_ERROR. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task StateChange_CallerGoneWhileItsCommitRuns_IsStoredWithOneRow()
    {
        await using TestService test = TestService.Open("postgres");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await test.ExecuteAsync(
            "CREATE FUNCTION slow_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(3); RETURN NULL; END $$"
        );
        await test.ExecuteAsync(
            "CREATE CONSTRAINT TRIGGER slow_commit AFTER INSERT ON products DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION slow_commit()"
        );

        const string Product = """{"code":"p1","name":"P1"}""";
        string request =
            $"POST /v1/admin/products HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + $"Content-Type: application/json\r\nContent-Length: {Product.Length}\r\n\r\n{Product}";
        using (Socket caller = await ElmServiceAdminTests.SendAsync(service, request))
        {
            await TestService.WaitForAsync(async () =>
                await test.ScalarAsync("SELECT COUNT(*) FROM pg_stat_activity WHERE wait_event = 'PgSleep' AND query = 'COMMIT'") > 0
            );
            await ElmServiceAdminTests.LeaveAsync(caller);
        }

        await TestService.WaitForAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM products WHERE code = 'p1'") == 1);
        await service.StopAsync();

        AuditEntry row = Assert.Single(await ElmServiceAdminTests.RowsAsync(service, "product.create"));
        Assert.Equal("ELM_OK", row.Result);
        Assert.DoesNotContain(await ElmServiceAdminTests.RowsAsync(service, null), r => r.Result == "ELM_NETWORK_ERROR");
        Assert.Null((await service.Database.VerifyAuditAsync(cancel)).FirstBroken);
    }

    // A service that trusts the fake identity provider and signs in to the admin UI, on the given clock.
    private static Task<ElmService> StartWebAsync(TestService test, FakeIdp idp, TimeProvider clock) =>
        ElmService.StartAsync(
            test.Settings(s =>
                s with
                {
                    Oidc = idp.Settings(),
                    Web = FakeIdp.WebSettings(RandomNumberGenerator.GetBytes(WebLoginSettings.KekLength)),
                }
            ),
            new ServiceHostOptions
            {
                Time = clock,
                Environment = test.HostOptions().Environment,
                MinimumLogLevel = LogLevel.Critical,
            },
            TestContext.Current.CancellationToken
        );

    // Signs in to the admin UI in the browser and returns the session cookie's value.
    private static async Task<string> SignInAsync(WebBrowser browser, ElmService service)
    {
        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        using HttpResponseMessage back = await browser.FollowAsync(login);
        using HttpResponseMessage callback = await browser.FollowAsync(back);
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        return browser.Cookies(service.Url)[ElmService.SessionCookie];
    }

    // Sends a request with the read of the time in the named method held; once the service waits there, the caller leaves,
    // and the held read goes on.
    private static async Task LeaveWhileHeldAsync(ElmService service, HeldClock clock, string method, string request)
    {
        Task held = clock.HoldTimeReadInAsync(method);
        using Socket caller = await ElmServiceAdminTests.SendAsync(service, request);
        await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await ElmServiceAdminTests.LeaveAsync(caller);
        clock.Release();
    }

    // Reads until the service has closed the connection, or reset it, and returns the bytes that came. Meanwhile the service's
    // clock moves a second every 50 ms, for a wait of the service that is counted on it.
    private static async Task<int> ReadToCloseAsync(Socket caller, TimeSpan limit, FakeTimeProvider clock)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(limit);
        using var moved = new CancellationTokenSource();
        Task mover = Task.Run(async () =>
        {
            while (!moved.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(50, CancellationToken.None);
            }
        });
        byte[] buffer = new byte[1_024];
        int total = 0;
        try
        {
            int read;
            while ((read = await caller.ReceiveAsync(buffer, bound.Token)) > 0)
            {
                total += read;
            }
        }
        catch (SocketException) { }
        finally
        {
            await moved.CancelAsync();
            await mover;
        }

        return total;
    }

    // The members of an audit detail as name and string value, in order.
    private static (string, string)[] Members(string? detail)
    {
        using JsonDocument parsed = JsonDocument.Parse(detail!);
        return [.. parsed.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString()!))];
    }

    private static void AssertOneLine(RecordingLoggerProvider log, string line)
    {
        IReadOnlyList<(string Category, LogLevel Level, string Message)> entries = log.Entries;
        Assert.Single(entries, e => e.Message == line);
        Assert.DoesNotContain(entries, e => e.Level >= LogLevel.Error);
    }
}
