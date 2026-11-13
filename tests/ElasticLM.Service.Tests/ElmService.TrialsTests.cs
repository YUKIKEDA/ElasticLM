using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;
using ElasticLM.Server.Tests;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Trials (design §12.9, M4b1): one a product and fingerprint, a license of the product's trial policy activated at once,
/// and the administration API of trials, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceTrialsTests
{
    /// <summary>
    /// A trial gives a trial token with every feature of the product, the trial's duration, and no grace; any fingerprint
    /// that had one refuses another until an administrator removes the record; it refreshes like any activation.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with features core and pro and a trial policy of 7 days; product other with no trial policy; each engine.
    ///
    /// Steps:
    /// - Device A asks for a trial with fingerprints f1, f2; asks again naming the first request in retry, as after a lost
    ///   answer; refreshes it; asks again; device B asks with f3, f2; with none; asks for a trial of other, of an unknown
    ///   product, and of "App!"; list the trials of app; read the trial's license and the audit row; remove the trial; B asks
    ///   again with f3, f2; remove it again.
    ///
    /// Expected:
    /// - 201 with a trial token for app, core and pro, expiring 7 days on with no grace, sequence 1; 200 with the same
    ///   activation and file; the refresh 200 with sequence 2; 409 ELM_TRIAL_USED twice; 400 ELM_FINGERPRINT_UNAVAILABLE; 404
    ///   ELM_TRIAL_NOT_OFFERED twice; 400 ELM_MALFORMED; one trial
    ///   with f1 and f2; a license named trial with the 7-day expiry; the row by the installation; 204; 201; 404
    ///   ELM_NOT_FOUND.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_OncePerFingerprint_UntilRemoved(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await ElmServiceActivationsTests.StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core", "pro");
        await ElmServiceActivationsTests.ProductAsync(admin, "other", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDuration\":604800");
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{policy}}"}""");
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints(3);
        string[] first = [prints[0], prints[1]];
        string[] second = [prints[2], prints[1]];

        DeviceAnswer trial = await a.TrialAsync("app", first);
        Assert.Equal(HttpStatusCode.Created, trial.Status);
        VerifiedLicense license = a.Verify(trial.File, first, LicenseKind.Trial);
        Assert.Equal(
            (LicenseKind.Trial, "app", "core pro", ElmServiceActivationsTests.Time(7 * 86_400), TimeSpan.Zero, 1L),
            (
                license.Kind,
                license.ProductId,
                string.Join(' ', license.Features.Select(f => f.Id).Order()),
                license.ExpiresAt,
                license.GracePeriod,
                license.Sequence!.Value
            )
        );
        DeviceAnswer again = await a.TrialAsync("app", first, retry: [trial.Jti]);
        Assert.Equal((HttpStatusCode.OK, trial.ActivationId), (again.Status, again.ActivationId));
        Assert.Equal(trial.File, again.File);
        Assert.Equal(2L, a.Verify((await a.RefreshAsync(trial.ActivationId, 1)).File, first, LicenseKind.Trial).Sequence);

        const string Used = "A fingerprint of this installation already had a trial of the product.";
        ElmServiceActivationsTests.AssertProblem(await a.TrialAsync("app", first), HttpStatusCode.Conflict, "ELM_TRIAL_USED", Used);
        ElmServiceActivationsTests.AssertProblem(await b.TrialAsync("app", second), HttpStatusCode.Conflict, "ELM_TRIAL_USED", Used);
        ElmServiceActivationsTests.AssertProblem(
            await b.TrialAsync("app", []),
            HttpStatusCode.BadRequest,
            "ELM_FINGERPRINT_UNAVAILABLE",
            "A trial needs at least one fingerprint."
        );
        const string NotOffered = "The product does not offer a trial.";
        ElmServiceActivationsTests.AssertProblem(await b.TrialAsync("other", second), HttpStatusCode.NotFound, "ELM_TRIAL_NOT_OFFERED", NotOffered);
        ElmServiceActivationsTests.AssertProblem(await b.TrialAsync("unknown", second), HttpStatusCode.NotFound, "ELM_TRIAL_NOT_OFFERED", NotOffered);
        ElmServiceActivationsTests.AssertProblem(
            await b.TrialAsync("App!", second),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            "The request body is not a valid trial request."
        );

        JsonElement items = (await TestService.GetAsync(admin, $"v1/admin/trials?productId={product}")).GetProperty("items");
        string trialId = license.LicenseId;
        Assert.Equal(
            (1, trialId, string.Join(' ', first.Order())),
            (
                items.GetArrayLength(),
                items[0].GetProperty("id").GetString(),
                string.Join(' ', items[0].GetProperty("fingerprints").EnumerateArray().Select(f => f.GetString()))
            )
        );
        JsonElement trialLicense = await TestService.GetAsync(admin, $"v1/admin/licenses/{trialId}");
        Assert.Equal(
            ("trial", ElmServiceActivationsTests.Iso(7 * 86_400)),
            (trialLicense.GetProperty("name").GetString(), trialLicense.GetProperty("expiresAt").GetString())
        );
        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r => r.GetProperty("action").GetString() == "trial.create");
        Assert.Equal(
            ("installation", a.KeyId, trialId),
            (row.GetProperty("actorType").GetString(), row.GetProperty("actorId").GetString(), row.GetProperty("target").GetString())
        );

        using (HttpResponseMessage removed = await admin.DeleteAsync($"v1/admin/trials/{trialId}", cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await b.TrialAsync("app", second)).Status);
        await TestService.AssertProblemAsync(
            await admin.DeleteAsync($"v1/admin/trials/{trialId}", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND",
            "No trial has this ID."
        );
    }

    /// <summary>
    /// Two trials at once that share a fingerprint give one trial, and nothing of the other is left (design §12.9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy; four devices with the same fingerprint; each engine.
    ///
    /// Steps:
    /// - All four ask for a trial at once; count the trials and the licenses.
    ///
    /// Expected:
    /// - One 201 and three 409 ELM_TRIAL_USED; one trial; one license.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_AtOnce_GivesOne(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await ElmServiceActivationsTests.StartAsync(test);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"licenseKind\":\"trial\",\"gracePeriod\":0");
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{policy}}"}""");
        TestDevice[] devices = [.. Enumerable.Range(0, 4).Select(_ => TestDevice.Create(test, service))];
        string[] prints = TestDevice.Fingerprints();

        DeviceAnswer[] answers = await Task.WhenAll(devices.Select(d => d.TrialAsync("app", prints)));
        Assert.Equal((1, 3), (answers.Count(x => x.Status == HttpStatusCode.Created), answers.Count(x => x.Code == "ELM_TRIAL_USED")));
        Assert.Equal((1L, 1L), (await test.ScalarAsync("SELECT COUNT(*) FROM trials"), await test.ScalarAsync("SELECT COUNT(*) FROM licenses")));
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// A product makes at most its trial policy's trialDailyLimit trials in 24 hours; past it a trial gets 429
    /// ELM_RATE_LIMITED with the seconds until the oldest trial counted leaves the 24 hours, writes nothing, and comes after
    /// the resend and the used fingerprints (design §12.9, V1d17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy of trialDailyLimit 2; product other with a trial policy of trialDailyLimit 1; each
    ///   engine. Times are seconds from the test's start, t0.
    ///
    /// Steps:
    /// - t0: device A asks for a trial of app. t0+100: B asks. t0+200: C asks; A asks again with its fingerprint, and again
    ///   naming its first request in retry; count the rows; remove A's trial record; A asks again.
    /// - t0+86,499: C asks. t0+86,500: C asks; D asks; the policy's trialDailyLimit is patched to 3; D asks; E asks for a
    ///   trial of other.
    ///
    /// Expected:
    /// - A and B 201. C 429 ELM_RATE_LIMITED "The product has reached its limit of 2 trials in 24 hours." with Retry-After
    ///   86200 (A's t0 + 86,400 − t0+200); A 409 ELM_TRIAL_USED, then 200 with its first activation; still 2 trials, licenses,
    ///   activations, and fingerprints, and 2 trial.create rows; after the removal A gets 201 at t0+200, as a removed
    ///   trial's row is gone and no longer counts.
    /// - C 429 with Retry-After 1 at t0+86,499, then 201 at t0+86,500 (B's trial has left the 24 hours); D 429 with
    ///   Retry-After 100 (A's second trial, t0+200); after the patch D 201; E 201, as app's trials do not count for other; 6
    ///   trial.create rows in all.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_DailyLimit_RefusesUntilTheOldestLeaves(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await ElmServiceActivationsTests.StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string app = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        string other = await ElmServiceActivationsTests.ProductAsync(admin, "other", "core");
        string policy = await TestService.PolicyAsync(admin, app, "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":2");
        await TestService.PatchAsync(admin, $"v1/admin/products/{app}", $$"""{"trialPolicyId":"{{policy}}"}""");
        string otherPolicy = await TestService.PolicyAsync(admin, other, "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":1");
        await TestService.PatchAsync(admin, $"v1/admin/products/{other}", $$"""{"trialPolicyId":"{{otherPolicy}}"}""");
        TestDevice[] devices = [.. Enumerable.Range(0, 5).Select(_ => TestDevice.Create(test, service))];
        (TestDevice a, TestDevice b, TestDevice c, TestDevice d, TestDevice e) = (devices[0], devices[1], devices[2], devices[3], devices[4]);
        string[][] prints = [.. Enumerable.Range(0, 5).Select(_ => TestDevice.Fingerprints())];
        const string Limit2 = "The product has reached its limit of 2 trials in 24 hours.";
        static void AssertLimited(DeviceAnswer answer, string detail, string retryAfter)
        {
            ElmServiceActivationsTests.AssertProblem(answer, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED", detail);
            Assert.Equal(retryAfter, answer.RetryAfter);
        }

        async Task<int> CreatedRowsAsync() =>
            (await TestService.AuditRowsAsync(test, service, owner)).Count(r => r.GetProperty("action").GetString() == "trial.create");

        DeviceAnswer first = await a.TrialAsync("app", prints[0]);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        test.Time.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.Created, (await b.TrialAsync("app", prints[1])).Status);
        test.Time.Advance(TimeSpan.FromSeconds(100));
        AssertLimited(await c.TrialAsync("app", prints[2]), Limit2, "86200");
        ElmServiceActivationsTests.AssertProblem(
            await a.TrialAsync("app", prints[0]),
            HttpStatusCode.Conflict,
            "ELM_TRIAL_USED",
            "A fingerprint of this installation already had a trial of the product."
        );
        DeviceAnswer resent = await a.TrialAsync("app", prints[0], retry: [first.Jti]);
        Assert.Equal((HttpStatusCode.OK, first.ActivationId), (resent.Status, resent.ActivationId));
        Assert.Equal(
            (2L, 2L, 2L, 2L, 2),
            (
                await test.ScalarAsync("SELECT COUNT(*) FROM trials"),
                await test.ScalarAsync("SELECT COUNT(*) FROM licenses"),
                await test.ScalarAsync("SELECT COUNT(*) FROM activations"),
                await test.ScalarAsync("SELECT COUNT(*) FROM trial_fingerprints"),
                await CreatedRowsAsync()
            )
        );
        string trialA = a.Verify(first.File, prints[0], LicenseKind.Trial).LicenseId;
        using (HttpResponseMessage removed = await admin.DeleteAsync($"v1/admin/trials/{trialA}", cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await a.TrialAsync("app", prints[0])).Status);

        test.Time.Advance(TimeSpan.FromSeconds(86_299));
        AssertLimited(await c.TrialAsync("app", prints[2]), Limit2, "1");
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await c.TrialAsync("app", prints[2])).Status);
        AssertLimited(await d.TrialAsync("app", prints[3]), Limit2, "100");
        await TestService.PatchAsync(admin, $"v1/admin/policies/{policy}", """{"trialDailyLimit":3}""");
        Assert.Equal(HttpStatusCode.Created, (await d.TrialAsync("app", prints[3])).Status);
        Assert.Equal(HttpStatusCode.Created, (await e.TrialAsync("other", prints[4])).Status);
        Assert.Equal(6, await CreatedRowsAsync());
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// A limit lowered below the product's count of the last 24 hours refuses at once, with the seconds until enough trials
    /// have left the 24 hours for one place to free (design §12.9, V1d17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy of trialDailyLimit 3; each engine. Times are seconds from the test's start, t0.
    ///
    /// Steps:
    /// - Trials of devices A at t0, B at t0+100, and C at t0+200; the policy's trialDailyLimit is patched to 1; D asks at
    ///   t0+300; D asks again at t0+86,599 and at t0+86,600.
    ///
    /// Expected:
    /// - A, B, and C 201. D 429 ELM_RATE_LIMITED "The product has reached its limit of 1 trials in 24 hours." with
    ///   Retry-After 86300: one place frees only when the third oldest, C (t0+200), leaves the 24 hours, not the oldest, A
    ///   (which would give 86100). D 429 with Retry-After 1 at t0+86,599, and 201 at t0+86,600.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_LimitLoweredBelowTheCount_WaitsForEnoughToLeave(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await ElmServiceActivationsTests.StartAsync(test);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":3");
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{policy}}"}""");
        TestDevice[] devices = [.. Enumerable.Range(0, 4).Select(_ => TestDevice.Create(test, service))];
        string[][] prints = [.. Enumerable.Range(0, 4).Select(_ => TestDevice.Fingerprints())];
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await devices[i].TrialAsync("app", prints[i])).Status);
            test.Time.Advance(TimeSpan.FromSeconds(100));
        }

        await TestService.PatchAsync(admin, $"v1/admin/policies/{policy}", """{"trialDailyLimit":1}""");
        const string Limit1 = "The product has reached its limit of 1 trials in 24 hours.";
        DeviceAnswer refused = await devices[3].TrialAsync("app", prints[3]);
        ElmServiceActivationsTests.AssertProblem(refused, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED", Limit1);
        Assert.Equal("86300", refused.RetryAfter);
        test.Time.Advance(TimeSpan.FromSeconds(86_299));
        refused = await devices[3].TrialAsync("app", prints[3]);
        ElmServiceActivationsTests.AssertProblem(refused, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED", Limit1);
        Assert.Equal("1", refused.RetryAfter);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await devices[3].TrialAsync("app", prints[3])).Status);
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// The limit counts the product's trials of every policy, and not the licenses an administrator makes from a trial
    /// policy (design §12.9, V1d17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with trial policies P1 and P2, each of trialDailyLimit 2; the product names P1; each engine.
    ///
    /// Steps:
    /// - An administrator makes two licenses of P1; device A asks for a trial; the product is switched to P2; B asks; C asks.
    ///
    /// Expected:
    /// - A 201 (the administrator's licenses do not count); B 201; C 429 ELM_RATE_LIMITED "The product has reached its limit
    ///   of 2 trials in 24 hours.", as A's trial of P1 counts for P2 too.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_DailyLimit_CountsEveryPolicyOfTheProduct(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await ElmServiceActivationsTests.StartAsync(test);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        const string Members = "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":2";
        string first = await TestService.PolicyAsync(admin, product, Members);
        string second = await TestService.PolicyAsync(admin, product, Members);
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{first}}"}""");
        await ElmServiceActivationsTests.LicenseAsync(admin, first);
        await ElmServiceActivationsTests.LicenseAsync(admin, first);
        TestDevice[] devices = [.. Enumerable.Range(0, 3).Select(_ => TestDevice.Create(test, service))];

        Assert.Equal(HttpStatusCode.Created, (await devices[0].TrialAsync("app", TestDevice.Fingerprints())).Status);
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{second}}"}""");
        Assert.Equal(HttpStatusCode.Created, (await devices[1].TrialAsync("app", TestDevice.Fingerprints())).Status);
        ElmServiceActivationsTests.AssertProblem(
            await devices[2].TrialAsync("app", TestDevice.Fingerprints()),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "The product has reached its limit of 2 trials in 24 hours."
        );
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// Trials sent at once never pass the product's limit: the product's row is locked while its trials are counted and
    /// written (design §12.9, V1d17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy of trialDailyLimit 2; six devices with fingerprints of their own; each engine.
    ///
    /// Steps:
    /// - All six ask for a trial at once; count the trials and the licenses.
    ///
    /// Expected:
    /// - Two 201 and four 429 ELM_RATE_LIMITED; 2 trials; 2 licenses.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Trial_DailyLimitAtOnce_GivesTheLimit(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await ElmServiceActivationsTests.StartAsync(test);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":2");
        await TestService.PatchAsync(admin, $"v1/admin/products/{product}", $$"""{"trialPolicyId":"{{policy}}"}""");
        TestDevice[] devices = [.. Enumerable.Range(0, 6).Select(_ => TestDevice.Create(test, service))];

        DeviceAnswer[] answers = await Task.WhenAll(devices.Select(d => d.TrialAsync("app", TestDevice.Fingerprints())));
        Assert.Equal(
            (2, 4),
            (
                answers.Count(x => x.Status == HttpStatusCode.Created),
                answers.Count(x => x.Status == HttpStatusCode.TooManyRequests && x.Code == "ELM_RATE_LIMITED")
            )
        );
        Assert.Equal((2L, 2L), (await test.ScalarAsync("SELECT COUNT(*) FROM trials"), await test.ScalarAsync("SELECT COUNT(*) FROM licenses")));
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// A product that reaches its trial limit writes one warning to the operational log, and another only 24 hours after
    /// it; each product warns on its own (design §12.9, §18.3, V1d17).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Products app and other, each with a trial policy of trialDailyLimit 1; a recording log. Times are seconds from the
    ///   test's start, t0.
    ///
    /// Steps:
    /// - t0: a trial of app; t0+10 and t0+20: two more. t0+30: a trial of other; t0+40: one more. t0+86,400: a trial of app;
    ///   t0+86,405 and t0+86,410: one more each. Read the warnings.
    ///
    /// Expected:
    /// - 201, 429, 429; 201, 429; 201, 429, 429.
    /// - Three warnings, in order: app at t0+10 until t0+86,400; other at t0+40 until t0+86,430; app at t0+86,410 until
    ///   t0+172,800. None at t0+20 and t0+86,405, within 24 hours of app's warning.
    /// </remarks>
    [Fact]
    public async Task Trial_DailyLimit_WarnsOncePerProductAnd24Hours()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        using var log = new RecordingLoggerProvider();
        ElmService service = await test.StartAsync(log: log);
        HttpClient admin = test.Http(service, owner);
        var products = new Dictionary<string, string>();
        foreach (string code in new[] { "app", "other" })
        {
            products[code] = await ElmServiceActivationsTests.ProductAsync(admin, code, "core");
            string policy = await TestService.PolicyAsync(admin, products[code], "\"licenseKind\":\"trial\",\"gracePeriod\":0,\"trialDailyLimit\":1");
            await TestService.PatchAsync(admin, $"v1/admin/products/{products[code]}", $$"""{"trialPolicyId":"{{policy}}"}""");
        }

        var statuses = new List<HttpStatusCode>();
        async Task TrialAtAsync(long second, string product)
        {
            test.Time.Advance(TestService.Start.AddSeconds(second) - test.Time.GetUtcNow());
            using var device = TestDevice.Create(test, service);
            statuses.Add((await device.TrialAsync(product, TestDevice.Fingerprints())).Status);
        }

        foreach (
            (long second, string product) in new (long, string)[]
            {
                (0, "app"),
                (10, "app"),
                (20, "app"),
                (30, "other"),
                (40, "other"),
                (86_400, "app"),
                (86_405, "app"),
                (86_410, "app"),
            }
        )
        {
            await TrialAtAsync(second, product);
        }

        (HttpStatusCode created, HttpStatusCode limited) = (HttpStatusCode.Created, HttpStatusCode.TooManyRequests);
        Assert.Equal([created, limited, limited, created, limited, created, limited, limited], statuses);
        string Line(string code, long until) =>
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The product {code} ({products[code]}) reached its limit of 1 trials in 24 hours; new trials are refused at least until {TestService.Start.AddSeconds(until)}."
            );
        Assert.Equal(
            [Line("app", 86_400), Line("other", 86_430), Line("app", 172_800)],
            log.Entries.Where(x => x.Level == LogLevel.Warning && x.Message.StartsWith("The product ", StringComparison.Ordinal))
                .Select(x => x.Message)
        );
    }

    /// <summary>
    /// The trials API needs trial.read to list and trial.delete to remove, and takes only a product ID as its filter.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys with trial.read and with trial.delete.
    ///
    /// Steps:
    /// - List with the delete key; remove with the read key; list with productId "x" and with an unknown product; remove an
    ///   ID that is not a UUID.
    ///
    /// Expected:
    /// - 403 naming trial.read; 403 naming trial.delete; 400 "productId must be a product ID." and an empty list; 404
    ///   ELM_NOT_FOUND.
    /// </remarks>
    [Fact]
    public async Task Admin_TrialsApi_ChecksPermissionsAndFilters()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await ElmServiceActivationsTests.StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient reader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"trial.read\""));
        HttpClient remover = test.Http(service, await TestService.CreateKeyAsync(admin, "\"trial.delete\""));
        string id = Guid.CreateVersion7().ToString("D");

        await TestService.AssertProblemAsync(
            await remover.GetAsync("v1/admin/trials", cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission trial.read."
        );
        await TestService.AssertProblemAsync(
            await reader.DeleteAsync($"v1/admin/trials/{id}", cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission trial.delete."
        );
        await TestService.AssertProblemAsync(
            await reader.GetAsync("v1/admin/trials?productId=x", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "productId must be a product ID."
        );
        Assert.Equal(0, (await TestService.GetAsync(reader, $"v1/admin/trials?productId={id}")).GetProperty("items").GetArrayLength());
        await TestService.AssertProblemAsync(
            await remover.DeleteAsync("v1/admin/trials/x", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND",
            "No trial has this ID."
        );
    }
}
