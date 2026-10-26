using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Issues keys, certificates, and license tokens for tests. Keys are generated per instance at run time; nothing is read
/// from the repository (.cursor/rules/testing.mdc). The real issuing side is ElasticLM.Issuing (row M2).
/// </summary>
internal sealed class TestIssuer : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public TestIssuer()
    {
        Root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Installation = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        RootKid = Thumbprint(Root);
        SigningKid = Thumbprint(Signing);
        InstallationKid = Thumbprint(Installation);
    }

    public ECDsa Root { get; }

    public ECDsa Signing { get; }

    public ECDsa Installation { get; }

    public string RootKid { get; }

    public string SigningKid { get; }

    public string InstallationKid { get; }

    public static long NowSeconds => Now.ToUnixTimeSeconds();

    public static string Thumbprint(ECDsa key) => EcPublicJwk.Thumbprint(key.ExportParameters(false));

    public static string PublicPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    public static string B64(byte[] bytes) => Base64UrlCodec.Encode(bytes);

    public static string B64(string text) => Base64UrlCodec.Encode(Encoding.UTF8.GetBytes(text));

    public static string Sign(ECDsa key, string headerJson, string payloadJson)
    {
        string signingInput = B64(headerJson) + "." + B64(payloadJson);
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return signingInput + "." + B64(signature);
    }

    public static string Sign(ECDsa key, string kid, string typ, JsonObject payload) => Sign(key, Header(kid, typ), payload.ToJsonString());

    public static string Header(string kid, string typ) =>
        new JsonObject
        {
            ["alg"] = "ES256",
            ["kid"] = kid,
            ["typ"] = typ,
        }.ToJsonString();

    public static JsonObject Jwk(ECDsa key)
    {
        ECParameters p = key.ExportParameters(false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = B64(p.Q.X!),
            ["y"] = B64(p.Q.Y!),
        };
    }

    public TrustStore TrustStore(params string[] revoked) => Licensing.TrustStore.Create([PublicPem(Root)], revoked);

    public JsonObject CertificatePayload() =>
        new()
        {
            ["ver"] = 1,
            ["iat"] = NowSeconds - 86400,
            ["jti"] = "cert-1",
            ["sub"] = SigningKid,
            ["jwk"] = Jwk(Signing),
            ["use"] = new JsonArray("license", "lease"),
            ["nbf"] = NowSeconds - (86400 * 30),
            ["exp"] = NowSeconds + (86400 * 365),
        };

    public string Certificate(JsonObject? payload = null) => Sign(Root, RootKid, TokenTypes.KeyCertificate, payload ?? CertificatePayload());

    public JsonObject LicensePayload() =>
        new()
        {
            ["ver"] = 1,
            ["iss"] = SigningKid,
            ["iat"] = NowSeconds - 3600,
            ["exp"] = NowSeconds + (86400 * 30),
            ["jti"] = "license-token-1",
            ["lic"] = "lic-1",
            ["prd"] = "acme-cad",
            ["kind"] = "standard",
            ["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["ver"] = ">=2.0 <3.0" }, new JsonObject { ["id"] = "basic" }),
            ["cnf"] = new JsonObject { ["jkt"] = InstallationKid },
            ["fp"] = new JsonArray("2b9b86465b5d4fbda9c050845613fa89"),
            ["fpm"] = "any",
            ["grace"] = 86400 * 7,
            ["refresh"] = 86400 * 20,
            ["seq"] = 1,
        };

    public string License(JsonObject? payload = null) => Sign(Signing, SigningKid, TokenTypes.License, payload ?? LicensePayload());

    public static byte[] File(string license, params string[] certificates)
    {
        var certs = new JsonArray();
        foreach (string c in certificates)
        {
            certs.Add(c);
        }

        var file = new JsonObject
        {
            ["format"] = "elasticlm-license",
            ["version"] = 1,
            ["license"] = license,
            ["certificates"] = certs,
        };
        return Encoding.UTF8.GetBytes(file.ToJsonString());
    }

    public byte[] File(JsonObject? licensePayload = null) => File(License(licensePayload), Certificate());

    public InstallationIdentity Identity() => new(InstallationKid, ["2b9b86465b5d4fbda9c050845613fa89"]);

    public LicenseRequirement Requirement(string? feature = "pro", string? version = "2.5") =>
        new("acme-cad", LicenseKind.Standard, feature, version, Identity());

    public LicenseVerifier Verifier(
        IClockGuard? guard = null,
        LicenseVerifierOptions? options = null,
        DateTimeOffset? now = null,
        TrustStore? trust = null
    ) => new(trust ?? TrustStore(), new FakeTimeProvider(now ?? Now), guard ?? StatelessClockGuard.Instance, options);

    public void Dispose()
    {
        Root.Dispose();
        Signing.Dispose();
        Installation.Dispose();
    }
}
