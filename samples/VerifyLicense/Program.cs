using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ElasticLM.Licensing;

// Verifies a license file with ElasticLM.Licensing, the way an app does.
//
//   VerifyLicense <root-public-key.pem> <license.elmlic> <product-id> [feature] [version]
//   VerifyLicense --self-test
//
// --self-test creates keys and an unbound license in memory, verifies it, and times verification. It exists to check the
// NativeAOT build (design §20.3) and to measure §4.2's verification time; it is not how licenses are issued (row M2).
if (args.Length == 1 && args[0] == "--self-test")
{
    return SelfTest.Run();
}

if (args.Length is < 3 or > 5)
{
    Console.Error.WriteLine("usage: VerifyLicense <root-public-key.pem> <license.elmlic> <product-id> [feature] [version]");
    Console.Error.WriteLine("       VerifyLicense --self-test");
    return 2;
}

TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
var requirement = new LicenseRequirement(args[2], LicenseKind.Standard, args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null, null);
return Report(verifier.Verify(File.ReadAllBytes(args[1]), requirement));

static int Report(LicenseVerificationResult result)
{
    switch (result)
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"valid: license {valid.License.LicenseId}, product {valid.License.ProductId}, grace {valid.InGracePeriod}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            Console.WriteLine($"invalid: {invalid.Error.WireName}: {invalid.Error.Message}");
            return 1;
        default:
            return 1;
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        using var root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signingKid = Thumbprint(signing);

        string certificate = Sign(
            root,
            Thumbprint(root),
            "elm-cert+jws",
            $"{{\"ver\":1,\"iat\":{now - 60},\"jti\":\"c1\",\"sub\":\"{signingKid}\",\"jwk\":{Jwk(signing)},\"use\":[\"license\"],\"nbf\":{now - 3600},\"exp\":{now + 86400}}}"
        );
        string license = Sign(
            signing,
            signingKid,
            "elm-license+jws",
            $"{{\"ver\":1,\"iss\":\"{signingKid}\",\"iat\":{now - 60},\"exp\":{now + 3600},\"jti\":\"t1\",\"lic\":\"lic-1\",\"prd\":\"acme-cad\",\"kind\":\"standard\",\"feat\":[{{\"id\":\"pro\",\"ver\":\">=2.0 <3.0\"}}]}}"
        );
        byte[] file = Encoding.UTF8.GetBytes(
            $"{{\"format\":\"elasticlm-license\",\"version\":1,\"license\":\"{license}\",\"certificates\":[\"{certificate}\"]}}"
        );

        TrustStore trust = TrustStore.Create([root.ExportSubjectPublicKeyInfoPem()], []);
        var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
        var requirement = new LicenseRequirement("acme-cad", LicenseKind.Standard, "pro", "2.5", null);

        if (verifier.Verify(file, requirement) is not LicenseVerificationResult.Valid)
        {
            Console.Error.WriteLine("self-test: the license did not verify");
            return 1;
        }

        string[] fingerprints = [.. MachineFingerprint.Compute("acme-cad")];
        const int Iterations = 2000;
        for (int i = 0; i < 200; i++)
        {
            verifier.Verify(file, requirement);
        }

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++)
        {
            verifier.Verify(file, requirement);
        }

        double microseconds = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Iterations;
        Console.WriteLine("self-test: valid");
        Console.WriteLine("fingerprints: " + fingerprints.Length.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(
            "verify: "
                + microseconds.ToString("F1", CultureInfo.InvariantCulture)
                + " us per license file ("
                + Iterations.ToString(CultureInfo.InvariantCulture)
                + " runs)"
        );
        Console.WriteLine("jit: " + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? "yes" : "no (NativeAOT)"));
        return 0;
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwk(ECDsa key)
    {
        ECParameters p = key.ExportParameters(false);
        return $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{B64(p.Q.X!)}\",\"y\":\"{B64(p.Q.Y!)}\"}}";
    }

    private static string Thumbprint(ECDsa key)
    {
        ECParameters p = key.ExportParameters(false);
        string canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{B64(p.Q.X!)}\",\"y\":\"{B64(p.Q.Y!)}\"}}";
        return B64(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Sign(ECDsa key, string kid, string typ, string payload)
    {
        string input =
            B64(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"{typ}\"}}")) + "." + B64(Encoding.UTF8.GetBytes(payload));
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return input + "." + B64(signature);
    }
}
