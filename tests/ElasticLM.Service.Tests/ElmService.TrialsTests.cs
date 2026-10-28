using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;

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
            "A trial needs at least one fingerprint (design §12.9)."
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
