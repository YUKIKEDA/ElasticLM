using System.Security.Cryptography;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Leasing.Tests;

/// <summary>
/// A root key, a signing key, a server key, and a server license made in the test, from which lease engines are built.
/// </summary>
internal sealed class TestServer : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly List<IDisposable> _owned = [];
    private readonly string _certificate;
    private readonly LicenseSpec _spec;

    public TestServer(
        IReadOnlyList<FeatureSpec>? features = null,
        int overage = 0,
        CloneStrategy clone = CloneStrategy.Deny,
        DateTimeOffset? expires = null,
        TimeSpan? grace = null
    )
    {
        Time = new FakeTimeProvider(Start);
        Key = KeyFiles.CreateKey();
        Trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
        _certificate = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Start.AddDays(-1), TimeSpan.FromDays(365), Time);
        _spec = new LicenseSpec
        {
            LicenseId = "lic-srv",
            ProductId = "acme-cad",
            Kind = LicenseKind.Server,
            Features = features ?? [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 5)],
            ExpiresAt = expires ?? Start.AddDays(30),
            GracePeriod = grace ?? TimeSpan.FromDays(7),
            InstallationKeyThumbprint = KeyFiles.KeyId(Key),
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.FromDays(7),
            OveragePercent = overage,
            CloneStrategy = clone,
        };
        Identity = IssueIdentity();
    }

    public FakeTimeProvider Time { get; }

    public ECDsa Key { get; }

    public TrustStore Trust { get; }

    public ServerIdentity Identity { get; private set; }

    /// <summary>Gets the number of tokens the engines of this server have signed.</summary>
    public int Signed { get; private set; }

    /// <summary>
    /// Replaces the server license with a newly issued one for the same server (a new jti), as an operator does by hand.
    /// </summary>
    public void ReplaceServerLicense()
    {
        _owned.Add(Identity);
        Identity = IssueIdentity();
    }

    public LeaseEngine Engine() =>
        new(
            Identity,
            (spec, at) =>
            {
                Signed++;
                return LeaseIssuer.Issue(Key, spec, at);
            },
            new LeaseVerifier(Trust, Time, StatelessClockGuard.Instance),
            Time
        );

    /// <summary>Returns an engine whose reclaim window is already over: it waits out the window of a fresh start.</summary>
    public LeaseEngine OpenEngine()
    {
        LeaseEngine engine = Engine();
        Time.Advance(TimeSpan.FromMinutes(10));
        return engine;
    }

    public TestClient Client()
    {
        var client = new TestClient();
        _owned.Add(client);
        return client;
    }

    public LeaseVerificationResult VerifyAsClient(string token, TestClient client) =>
        new LeaseVerifier(Trust, Time, StatelessClockGuard.Instance).VerifyOnPremises(
            token,
            Identity,
            new LeaseExpectation("acme-cad", client.KeyId)
        );

    private ServerIdentity IssueIdentity()
    {
        IssuedLicense issued = LicenseIssuer.Issue(_signing, [_certificate], _spec, Time);
        var verifier = new LicenseVerifier(Trust, Time, StatelessClockGuard.Instance);
        LicenseVerificationResult result = verifier.Verify(
            issued.File,
            new LicenseRequirement("acme-cad", LicenseKind.Server, null, null, new InstallationIdentity(KeyFiles.KeyId(Key), []))
        );
        VerifiedLicense license = Assert.IsType<LicenseVerificationResult.Valid>(result).License;
        Assert.True(ServerIdentity.TryCreate(license, KeyFiles.ExportPublicJwk(Key), out ServerIdentity? identity, out _));
        return identity!;
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }

        Identity.Dispose();
        Key.Dispose();
        _root.Dispose();
        _signing.Dispose();
    }
}

/// <summary>
/// An installation key of a client.
/// </summary>
internal sealed class TestClient : IDisposable
{
    private readonly ECDsa _key = KeyFiles.CreateKey();

    public string KeyId => KeyFiles.KeyId(_key);

    public ECParameters PublicKey => _key.ExportParameters(false);

    public LeaseRequest Request(string feature = "pro", int count = 1, string? version = "2.5") =>
        new(feature, version, count, KeyId, PublicKey, EngineExtensions.NewProofId());

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Renewal without earlier unanswered attempts, with a new proof ID, as most tests need.
/// </summary>
internal static class EngineExtensions
{
    private static int _next;

    public static string NewProofId() => "proof-" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static LeaseOutcome Renew(this LeaseEngine engine, string leaseId, long sequence, string keyId) =>
        engine.Renew(leaseId, sequence, keyId, NewProofId(), []);
}
