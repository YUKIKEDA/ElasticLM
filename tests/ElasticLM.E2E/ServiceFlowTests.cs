using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.E2E;

/// <summary>
/// The hosted activation service's flows against the published elm-service, with ElasticLM.Client as the app (design §12,
/// §14, §20.7), on SQLite and, where a server is given, on PostgreSQL.
/// </summary>
public sealed class ServiceFlowTests
{
    /// <summary>
    /// An app activates, refreshes, and deactivates online; a trial starts; a refresh after the license is revoked is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service with product acme-cad (feature pro), a standard policy, a trial policy set as the product's
    ///   trial, and a license of the standard policy.
    ///
    /// Steps:
    /// - Client a: activate with the key, refresh, check pro, deactivate. Client t: start a trial. Client b: activate with the
    ///   key; revoke the license through the admin API; refresh.
    ///
    /// Expected:
    /// - a: sequence 1, then 2; the check is Valid; the deactivation gives no error. t: a Trial license. b: the refresh fails
    ///   with ELM_LICENSE_REVOKED and the state is Refused, while the kept file still checks Valid.
    /// </remarks>
    /// <param name="database">The service's database.</param>
    [Theory]
    [MemberData(nameof(ServiceProcess.Databases), MemberType = typeof(ServiceProcess))]
    public async Task Online_ActivateRefreshTrialDeactivate_RefusedAfterRevocation(string database)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, database);
        string product = await service.ProductAsync("acme-cad");
        (string licenseId, string key) = await service.LicenseAsync(await service.PolicyAsync(product));
        string trial = await service.PolicyAsync(product, "\"licenseKind\":\"trial\",\"gracePeriod\":0");
        await service.SendAsync(HttpMethod.Patch, $"products/{product}", $$"""{"trialPolicyId":"{{trial}}"}""");

        await using (ActivationClient a = Activation(vendor, service, "client-a"))
        {
            Assert.Equal(1L, Activated(await a.ActivateAsync(key, Cancel)).Sequence);
            Assert.Equal(2L, Activated(await a.RefreshAsync(Cancel)).Sequence);
            Assert.IsType<LicenseVerificationResult.Valid>(a.Check("pro", null));
            Assert.Null(await a.DeactivateAsync(Cancel));
        }

        await using (ActivationClient t = Activation(vendor, service, "client-t"))
        {
            Assert.Equal(LicenseKind.Trial, Activated(await t.StartTrialAsync(Cancel)).Kind);
        }

        await using ActivationClient b = Activation(vendor, service, "client-b");
        Activated(await b.ActivateAsync(key, Cancel));
        await service.SendAsync(HttpMethod.Post, $"licenses/{licenseId}/revoke");
        ActivationResult.Failed refused = Assert.IsType<ActivationResult.Failed>(await b.RefreshAsync(Cancel));
        Assert.Equal(ErrorCode.LicenseRevoked, refused.Error.Code);
        Assert.Equal(ActivationState.Refused, b.State);
        Assert.IsType<LicenseVerificationResult.Valid>(b.Check("pro", null));
    }

    /// <summary>
    /// A device without the network activates through files: the app writes a request, the published elm passes it to the
    /// service, and the app imports the answer (design §12.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service with product acme-cad (feature pro), a standard policy, and a license; a client with no
    ///   service address.
    ///
    /// Steps:
    /// - Create the activation request and write it to a file; elm offline process --url the service --in it --out the answer;
    ///   import the answer; check pro.
    ///
    /// Expected:
    /// - elm exits 0; the import gives the license with sequence 1; the check is Valid.
    /// </remarks>
    /// <param name="database">The service's database.</param>
    [Theory]
    [MemberData(nameof(ServiceProcess.Databases), MemberType = typeof(ServiceProcess))]
    public async Task Offline_RequestThroughElm_ImportsTheAnswer(string database)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, database);
        (_, string key) = await service.LicenseAsync(await service.PolicyAsync(await service.ProductAsync("acme-cad")));
        await using ActivationClient client = Activation(vendor, service, "client-offline", offline: true);

        OfflineRequestResult.Created request = Assert.IsType<OfflineRequestResult.Created>(
            await client.CreateActivationRequestAsync(key, cancellationToken: Cancel)
        );
        await File.WriteAllTextAsync(vendor.File("device.elmreq"), request.File, Cancel);
        await vendor.ElmOkAsync("offline", "process", "--url", service.Url.ToString(), "--in", "device.elmreq", "--out", "device.elmresp");

        string answer = await File.ReadAllTextAsync(vendor.File("device.elmresp"), Cancel);
        Assert.Equal(1L, Activated(await client.ImportResponseAsync(answer, Cancel)).Sequence);
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("pro", null));
    }

    /// <summary>
    /// An app takes and returns a lease of a cloud-floating license, and the service's audit log and usage log verify
    /// (design §12.7, §18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service with a usage log, product acme-cad (feature pro), a cloud-floating policy, and a license of
    ///   2 seats.
    ///
    /// Steps:
    /// - A LicenseClient with the key acquires a lease of pro and releases it (no renewal is waited for: the policy's lease is
    ///   at least 120 seconds). POST audit/verify. POST usage-log/seal; elm log verify the usage log folder --node e2e-node.
    ///
    /// Expected:
    /// - The lease is Held with 1 seat, then Released; the audit log checked at least one line and found no break; elm log
    ///   verify exits 0.
    /// </remarks>
    /// <param name="database">The service's database.</param>
    [Theory]
    [MemberData(nameof(ServiceProcess.Databases), MemberType = typeof(ServiceProcess))]
    public async Task CloudLease_AndTheLogsVerify(string database)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, database);
        string product = await service.ProductAsync("acme-cad");
        (_, string key) = await service.LicenseAsync(await service.PolicyAsync(product, "\"floating\":\"cloud\""), seats: 2);

        var options = new LicenseClientOptions
        {
            ProductId = "acme-cad",
            ServerUrl = service.Url,
            LicenseKey = key,
            Trust = await TrustAsync(vendor),
            DataDirectory = vendor.File("client-lease"),
        };
        Assert.True(LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error), error?.Message);
        await using (LicenseClient client = created!)
        {
            Lease lease = Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: Cancel)).Lease;
            Assert.Equal((LeaseState.Held, 1), (lease.State, lease.Seats));
            Assert.Null(await client.ReleaseLeaseAsync(lease, Cancel));
            Assert.Equal(LeaseState.Released, lease.State);
        }

        JsonElement audit = await service.SendAsync(HttpMethod.Post, "audit/verify");
        Assert.True(audit.GetProperty("checked").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, audit.GetProperty("firstBroken").ValueKind);

        await service.SendAsync(HttpMethod.Post, "usage-log/seal");
        await vendor.ElmOkAsync("log", "verify", service.UsageLogDirectory, "--node", ServiceProcess.NodeName, "--trust", vendor.RootPublicKey);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<TrustStore> TrustAsync(Vendor vendor) =>
        TrustStore.Create([await File.ReadAllTextAsync(vendor.RootPublicKey, Cancel)], []);

    private static ActivationClient Activation(Vendor vendor, ServiceProcess service, string folder, bool offline = false)
    {
        var options = new ActivationClientOptions
        {
            ProductId = "acme-cad",
            ServiceUrl = offline ? null : service.Url,
            Trust = TrustStore.Create([File.ReadAllText(vendor.RootPublicKey)], []),
            DataDirectory = vendor.File(folder),
        };
        Assert.True(ActivationClient.TryCreate(options, out ActivationClient? client, out LicenseError? error), error?.Message);
        return client!;
    }

    private static VerifiedLicense Activated(ActivationResult result) => Assert.IsType<ActivationResult.Activated>(result).License;
}
