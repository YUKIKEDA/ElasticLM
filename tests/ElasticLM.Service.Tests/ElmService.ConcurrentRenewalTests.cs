using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Two copies of one installation that renew at the same moment with the same sequence number (design §10.4 step 3, §20.2,
/// V1d13): the service judges each request against the row it reads again under the license's lock, so one request is the
/// renewal and the other is a clone, on SQLite and on PostgreSQL.
/// </summary>
/// <remarks>
/// Each test repeats its round, because one round can pass by chance without the read under the lock: when the second
/// request arrives after the first one is committed, the row read before the lock is already the new one. With that read
/// taken out, the first round passed in 29 of the 30 test runs measured on SQLite, and every one of those runs failed on
/// other rounds (design §20.7, the V1d13 Grill).
/// </remarks>
public sealed class ElmServiceConcurrentRenewalTests
{
    // TestService.AuditRowsAsync reads the first 200 audit rows, oldest first. The refresh test writes 5 rows before its
    // rounds and 4 a round (45 after 10 rounds, counted in a run), so the clone row of round n is row 4n + 3 and is read up
    // to round 49; the renewal test writes 3 a round (35 after 10 rounds), up to round 65. Past that the round's clone row
    // is not read and the round is reported as differing.
    private const int Rounds = 10;

    /// <summary>Gets each engine with each clone strategy.</summary>
    public static MatrixTheoryData<string, string> Cases => new(TestService.Providers.Select(row => row.Data), ["deny", "flag"]);

    /// <summary>
    /// Two renewals of one lease sent at once with the same sequence number never both count as the renewal: one gets the
    /// next token and the other is a clone, which Deny refuses and stops the lease for, and Flag records and answers with
    /// the token after it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with the case's cloneStrategy; a license with 10 seats of core and no overage, one for each round's
    ///   lease. No lease is released, and a lease that Deny stopped holds its seat until it expires, so round n acquires
    ///   with n - 1 seats held and renews with n held; a renewal is refused only when more seats are held than the license
    ///   allows, so 10 seats are exactly enough for round 10. Two copies of one installation (the same installation key); each engine with deny and with flag.
    ///
    /// Steps:
    /// - Ten rounds, the clock moved a second before each: acquire a lease (seq 1); take a nonce for each copy; send a
    ///   renewal with seq 1 from both copies at once, each with its own nonce and proof; read the lease; renew once more
    ///   with the sequence number the lease must then have (2 under Deny, 3 under Flag); read the lease.clone audit rows of the lease.
    ///
    /// Expected:
    /// - In every round, under Deny: one 200 with seq 2 whose areq is that request's jti, and one 409 ELM_CLONE_DETECTED
    ///   "Another copy of the installation went ahead; the lease is stopped until it expires."; the lease has seq 2 and is
    ///   stopped; the later renewal gives 409 ELM_CLONE_DETECTED "The lease was stopped because a clone of the installation
    ///   was detected."; one lease.clone row with result ELM_CLONE_DETECTED, seq 1, and lastSeq 2.
    /// - In every round, under Flag: one 200 with seq 2 and one with seq 3, one above the larger of the lease's and the
    ///   request's (design §10.4), each with its own request's jti in areq; the lease has seq 3 and is not stopped; the later
    ///   renewal gives seq 4; one lease.clone row with result ELM_OK, seq 1, and lastSeq 2.
    /// - A round that differs is reported with its number, the two answers, the lease, and the rows.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Renew_SameSequenceAtOnce_OneIsTheRenewalAndOneAClone(string provider, string strategy)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, $"\"floating\":\"cloud\",\"cloneStrategy\":\"{strategy}\"");
        (string licenseId, string key) = await LicenseAsync(admin, policy, $$"""[{"code":"core","seats":{{Rounds}}}]""");
        using var first = TestDevice.Create(test, service, test.FilePath("installation"));
        using var second = TestDevice.Create(test, service, test.FilePath("installation"));
        Assert.Equal(first.KeyId, second.KeyId);
        bool deny = strategy == "deny";
        string row = $"{{\"licenseId\":\"{licenseId}\",\"feature\":\"core\",\"seq\":1,\"lastSeq\":2,\"strategy\":\"{strategy}\"}}";
        string expected = deny
            ? "200 seq 2 areq own; 409 ELM_CLONE_DETECTED: Another copy of the installation went ahead; the lease is stopped until it expires."
                + " | lease seq 2 stopped True"
                + " | 409 ELM_CLONE_DETECTED: The lease was stopped because a clone of the installation was detected."
                + " | ELM_CLONE_DETECTED "
                + row
            : "200 seq 2 areq own; 200 seq 3 areq own | lease seq 3 stopped False | 200 seq 4 areq own | ELM_OK " + row;
        List<string> different = [];

        for (int round = 1; round <= Rounds; round++)
        {
            test.Time.Advance(TimeSpan.FromSeconds(1));
            DeviceAnswer acquired = await first.AcquireAsync(key);
            Assert.True(acquired.Status == HttpStatusCode.Created, $"round {round}: {Problem(acquired)}");
            string id = first.VerifyLease(acquired).LeaseId;

            DeviceAnswer[] answers = await SendAtOnceAsync(first, second, $"v1/leases/{id}/renew");
            JsonElement lease = (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases?limit=200"))
                .GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == id);
            DeviceAnswer later = await first.RenewAsync(id, deny ? 2 : 3);
            string[] rows =
            [
                .. (await TestService.AuditRowsAsync(test, service, owner))
                    .Where(r => r.GetProperty("action").GetString() == "lease.clone" && r.GetProperty("target").GetString() == id)
                    .Select(r => r.GetProperty("result").GetString() + " " + r.GetProperty("detail").GetString()),
            ];

            string seen = string.Join(
                " | ",
                string.Join("; ", answers.Select(answer => LeaseText(first, answer)).Order(StringComparer.Ordinal)),
                $"lease seq {lease.GetProperty("seq").GetInt64()} stopped {lease.GetProperty("stopped").GetBoolean()}",
                LeaseText(first, later),
                string.Join("; ", rows)
            );
            if (seen != expected)
            {
                different.Add($"round {round}: {seen}");
            }
        }

        Assert.True(different.Count == 0, Report(different, expected));
    }

    /// <summary>
    /// Two refreshes of one activation sent at once with the same sequence number never both count as the refresh: one
    /// gets the next file and the other is a clone, which Deny refuses and blocks the activation for, and Flag records and
    /// answers with the file after it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 10 activations with the case's cloneStrategy; a license of it; for each round two copies of a new
    ///   installation (the same installation key); each engine with deny and with flag.
    ///
    /// Steps:
    /// - Ten rounds, the clock moved a second before each: activate (seq 1); take a nonce for each copy; send a refresh
    ///   with seq 1 from both copies at once, each with its own nonce and proof; read the activation; refresh once more
    ///   with the sequence number the activation must then have (2 under Deny, 3 under Flag); read the activation.clone audit rows of the activation.
    ///
    /// Expected:
    /// - In every round, under Deny: one 200 with seq 2 and one 409 ELM_CLONE_DETECTED "Another copy of the installation
    ///   went ahead; the activation is stopped. Deactivate it and activate again."; the activation has seq 2 and is blocked;
    ///   the later refresh gives 409 ELM_CLONE_DETECTED "The activation was stopped because a clone of the installation was
    ///   detected."; one activation.clone row with result ELM_CLONE_DETECTED, operation refresh, seq 1, and lastSeq 2.
    /// - In every round, under Flag: one 200 with seq 2 and one with seq 3; the activation has seq 3 and is not blocked;
    ///   the later refresh gives seq 4; one activation.clone row with result ELM_OK, operation refresh, seq 1, and lastSeq 2.
    /// - A round that differs is reported with its number, the two answers, the activation, and the rows.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Refresh_SameSequenceAtOnce_OneIsTheRefreshAndOneAClone(string provider, string strategy)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, $"\"maxActivations\":{Rounds},\"cloneStrategy\":\"{strategy}\"");
        (string licenseId, string key) = await LicenseAsync(admin, policy);
        string[] prints = TestDevice.Fingerprints();
        bool deny = strategy == "deny";
        string row = $"{{\"licenseId\":\"{licenseId}\",\"operation\":\"refresh\",\"seq\":1,\"lastSeq\":2,\"strategy\":\"{strategy}\"}}";
        string expected = deny
            ? "200 seq 2; 409 ELM_CLONE_DETECTED: Another copy of the installation went ahead; the activation is stopped."
                + " Deactivate it and activate again."
                + " | activation seq 2 blocked True"
                + " | 409 ELM_CLONE_DETECTED: The activation was stopped because a clone of the installation was detected."
                + " | ELM_CLONE_DETECTED "
                + row
            : "200 seq 2; 200 seq 3 | activation seq 3 blocked False | 200 seq 4 | ELM_OK " + row;
        List<string> different = [];

        for (int round = 1; round <= Rounds; round++)
        {
            test.Time.Advance(TimeSpan.FromSeconds(1));
            using var first = TestDevice.Create(test, service, test.FilePath($"installation-{round}"));
            using var second = TestDevice.Create(test, service, test.FilePath($"installation-{round}"));
            Assert.Equal(first.KeyId, second.KeyId);
            DeviceAnswer activated = await first.ActivateAsync(key, prints);
            Assert.True(activated.Status == HttpStatusCode.Created, $"round {round}: {Problem(activated)}");
            string id = activated.ActivationId;

            DeviceAnswer[] answers = await SendAtOnceAsync(first, second, $"v1/activations/{id}/refresh");
            JsonElement activation = await TestService.GetAsync(admin, $"v1/admin/activations/{id}");
            DeviceAnswer later = await first.RefreshAsync(id, deny ? 2 : 3);
            string[] rows =
            [
                .. (await TestService.AuditRowsAsync(test, service, owner))
                    .Where(r => r.GetProperty("action").GetString() == "activation.clone" && r.GetProperty("target").GetString() == id)
                    .Select(r => r.GetProperty("result").GetString() + " " + r.GetProperty("detail").GetString()),
            ];

            string seen = string.Join(
                " | ",
                string.Join("; ", answers.Select(answer => FileText(first, answer, prints)).Order(StringComparer.Ordinal)),
                $"activation seq {activation.GetProperty("seq").GetInt64()} blocked {activation.GetProperty("blockedAt").ValueKind == JsonValueKind.String}",
                FileText(first, later, prints),
                string.Join("; ", rows)
            );
            if (seen != expected)
            {
                different.Add($"round {round}: {seen}");
            }
        }

        Assert.True(different.Count == 0, Report(different, expected));
    }

    // The failure message: how many rounds differ, what every round must show, and what each differing round showed.
    private static string Report(List<string> different, string expected) =>
        $"{different.Count} of {Rounds} rounds differ from: {expected}\n{string.Join('\n', different)}";

    // Sends {"seq":1} to a path from both copies at once. The nonces are taken first, and each request is made on a thread of
    // its own, so that neither proof waits for the other request. Nothing holds the first request back, so a round may still be
    // served one after the other; the test repeats for that reason.
    private static async Task<DeviceAnswer[]> SendAtOnceAsync(TestDevice first, TestDevice second, string path)
    {
        const string Body = """{"seq":1}""";
        string firstNonce = await first.NonceAsync();
        string secondNonce = await second.NonceAsync();
        return await Task.WhenAll(
            Task.Run(() => first.PostAsync(path, Body, includeKey: false, nonce: firstNonce), TestContext.Current.CancellationToken),
            Task.Run(() => second.PostAsync(path, Body, includeKey: false, nonce: secondNonce), TestContext.Current.CancellationToken)
        );
    }

    // What a lease answer says, without its body: the verified token's sequence number and whether its areq is the jti of the
    // request it answers, or the problem.
    private static string LeaseText(TestDevice device, DeviceAnswer answer)
    {
        if (answer.Status != HttpStatusCode.OK)
        {
            return Problem(answer);
        }

        VerifiedLease lease = device.VerifyLease(answer);
        return $"200 seq {lease.Sequence} areq {(lease.AnsweredProofId == answer.Jti ? "own" : "another")}";
    }

    // What an activation answer says, without its body: the verified file's sequence number, or the problem.
    private static string FileText(TestDevice device, DeviceAnswer answer, string[] fingerprints) =>
        answer.Status == HttpStatusCode.OK ? $"200 seq {device.Verify(answer.File, fingerprints).Sequence}" : Problem(answer);

    // The status, code, and detail of a problem answer; a successful body is never shown, since it goes to the CI log.
    private static string Problem(DeviceAnswer answer) =>
        answer.Code is string code
            ? $"{(int)answer.Status} {code}: {answer.Json.GetProperty("detail").GetString()}"
            : ((int)answer.Status).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
