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
        if (!ProofAndLease(root, signing) || !Renewal(root, signing))
        {
            return 1;
        }

        Console.WriteLine("jit: " + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? "yes" : "no (NativeAOT)"));
        return 0;
    }

    // A request proof from an installation key, checked by the server-side verifier, and a hosted lease checked by the
    // client (design §12.2, §12.7.5). The key lives in a temporary directory that is removed afterwards.
    private static bool ProofAndLease(ECDsa root, ECDsa signing)
    {
        string directory = Path.Combine(Path.GetTempPath(), "elm-selftest-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
        try
        {
            var store = new ElasticLM.Client.InstallationKeyStore("acme-cad", ElasticLM.Client.KeyScope.User, directory);
            if (!store.TryLoadOrCreate(out ElasticLM.Client.InstallationKey? key, out LicenseError? keyError))
            {
                Console.Error.WriteLine("self-test: installation key: " + keyError!.Message);
                return false;
            }

            using (key)
            {
                var target = new Uri("https://licenses.example.com/v1/leases");
                byte[] body = Encoding.UTF8.GetBytes("{\"feature\":\"pro\"}");
                var nonces = new ElasticLM.Issuing.NonceService(RandomNumberGenerator.GetBytes(32), TimeProvider.System);
                var verifier = new ElasticLM.Issuing.ProofVerifier(nonces, new ElasticLM.Issuing.MemoryReplayStore(TimeProvider.System));

                const int Runs = 1000;
                long start = Stopwatch.GetTimestamp();
                string proof = string.Empty;
                for (int i = 0; i < Runs; i++)
                {
                    proof = ElasticLM.Client.ProofBuilder.Create(
                        key!,
                        "POST",
                        target,
                        nonces.Issue(),
                        body,
                        includePublicKey: true,
                        TimeProvider.System
                    );
                }

                double create = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Runs;
                var proofs = new string[Runs];
                for (int i = 0; i < Runs; i++)
                {
                    proofs[i] = ElasticLM.Client.ProofBuilder.Create(
                        key!,
                        "POST",
                        target,
                        nonces.Issue(),
                        body,
                        includePublicKey: true,
                        TimeProvider.System
                    );
                }

                start = Stopwatch.GetTimestamp();
                foreach (string p in proofs)
                {
                    if (verifier.Verify(p, "POST", target, body) is not ElasticLM.Issuing.ProofResult.Valid)
                    {
                        Console.Error.WriteLine("self-test: a proof did not verify");
                        return false;
                    }
                }

                double verify = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Runs;
                Console.WriteLine(
                    "proof: create "
                        + create.ToString("F1", CultureInfo.InvariantCulture)
                        + " us, verify "
                        + verify.ToString("F1", CultureInfo.InvariantCulture)
                        + " us ("
                        + Runs.ToString(CultureInfo.InvariantCulture)
                        + " runs)"
                );

                string certificate = ElasticLM.Issuing.CertificateIssuer.Issue(
                    root,
                    signing,
                    ElasticLM.Issuing.KeyUses.Lease,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    TimeSpan.FromDays(30),
                    TimeProvider.System
                );
                var spec = new ElasticLM.Issuing.LeaseSpec
                {
                    LeaseId = "lease-1-AAAAAAAAAAAAAA",
                    ProductId = "acme-cad",
                    LicenseId = "lic-1",
                    FeatureId = "pro",
                    Seats = 1,
                    InstallationKeyThumbprint = key!.Thumbprint,
                    Duration = TimeSpan.FromMinutes(10),
                };
                string lease = ElasticLM.Issuing.LeaseIssuer.Issue(signing, spec, TimeProvider.System);
                TrustStore trust = TrustStore.Create([root.ExportSubjectPublicKeyInfoPem()], []);
                LeaseVerificationResult result = new LeaseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance).VerifyHosted(
                    lease,
                    [certificate],
                    new LeaseExpectation("acme-cad", key.Thumbprint)
                );
                if (result is not LeaseVerificationResult.Valid)
                {
                    Console.Error.WriteLine("self-test: the lease did not verify");
                    return false;
                }

                Console.WriteLine("lease: valid");
                return true;
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // The server side of one renewal on premises (design §12.7.1, §4.2): the lease engine judges the sequence number under
    // the feature's lock and signs the next token. The proof check of the request is measured above.
    private static bool Renewal(ECDsa root, ECDsa signing)
    {
        using ECDsa server = ElasticLM.Issuing.KeyFiles.CreateKey();
        using ECDsa client = ElasticLM.Issuing.KeyFiles.CreateKey();
        string serverKid = ElasticLM.Issuing.KeyFiles.KeyId(server);
        string clientKid = ElasticLM.Issuing.KeyFiles.KeyId(client);
        TrustStore trust = TrustStore.Create([root.ExportSubjectPublicKeyInfoPem()], []);
        string certificate = ElasticLM.Issuing.CertificateIssuer.Issue(
            root,
            signing,
            ElasticLM.Issuing.KeyUses.License,
            DateTimeOffset.UtcNow.AddDays(-1),
            TimeSpan.FromDays(30),
            TimeProvider.System
        );
        var spec = new ElasticLM.Issuing.LicenseSpec
        {
            LicenseId = "lic-srv",
            ProductId = "acme-cad",
            Kind = LicenseKind.Server,
            Features = [new ElasticLM.Issuing.FeatureSpec("pro", Seats: 10_000)],
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            GracePeriod = TimeSpan.FromDays(7),
            InstallationKeyThumbprint = serverKid,
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.Zero,
            OveragePercent = 0,
            CloneStrategy = CloneStrategy.Deny,
            BorrowReturnLimit = 2,
        };
        byte[] file = ElasticLM.Issuing.LicenseIssuer.Issue(signing, [certificate], spec, TimeProvider.System).File;
        var licenses = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
        var requirement = new LicenseRequirement("acme-cad", LicenseKind.Server, null, null, new InstallationIdentity(serverKid, []));
        if (
            licenses.Verify(file, requirement) is not LicenseVerificationResult.Valid valid
            || !ServerIdentity.TryCreate(valid.License, ElasticLM.Issuing.KeyFiles.ExportPublicJwk(server), out ServerIdentity? identity, out _)
        )
        {
            Console.Error.WriteLine("self-test: the server license did not verify");
            return false;
        }

        using (identity)
        {
            var engine = new ElasticLM.Leasing.LeaseEngine(
                identity!,
                (lease, at) => ElasticLM.Issuing.LeaseIssuer.Issue(server, lease, at),
                new LeaseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance),
                TimeProvider.System
            );

            // A fresh engine is in its reclaim window, so the lease comes in as a reclaim, as after a restart.
            var leaseSpec = new ElasticLM.Issuing.LeaseSpec
            {
                LeaseId = "lease-1-AAAAAAAAAAAAAA",
                ProductId = "acme-cad",
                LicenseId = "lic-srv",
                FeatureId = "pro",
                Seats = 1,
                InstallationKeyThumbprint = clientKid,
                Duration = TimeSpan.FromMinutes(10),
                ServerLicenseTokenId = identity!.License.TokenId,
                Sequence = 1,
            };
            string token = ElasticLM.Issuing.LeaseIssuer.Issue(server, leaseSpec, TimeProvider.System);
            if (engine.Reclaim([token], clientKid, client.ExportParameters(false))[0].Error is LicenseError reclaimError)
            {
                Console.Error.WriteLine("self-test: the reclaim failed: " + reclaimError.Message);
                return false;
            }

            const int Runs = 1000;
            long start = Stopwatch.GetTimestamp();
            for (int i = 1; i <= Runs; i++)
            {
                if (
                    engine.Renew("lease-1-AAAAAAAAAAAAAA", i, clientKid, "proof-" + i.ToString(CultureInfo.InvariantCulture), [])
                    is not ElasticLM.Leasing.LeaseOutcome.Granted
                )
                {
                    Console.Error.WriteLine("self-test: a renewal was refused");
                    return false;
                }
            }

            double renew = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Runs;
            Console.WriteLine(
                "renewal (server, without the proof): "
                    + renew.ToString("F1", CultureInfo.InvariantCulture)
                    + " us ("
                    + Runs.ToString(CultureInfo.InvariantCulture)
                    + " runs)"
            );

            // The usage log line that each renewal writes (design §18.1), signed every 100 lines as the server does.
            string folder = Path.Combine(Path.GetTempPath(), "elm-selftest-log-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
            try
            {
                const int Lines = 10_000;
                using ElasticLM.Issuing.SignedLog log = ElasticLM.Issuing.SignedLog.Open(
                    folder,
                    "usage",
                    "usage",
                    use => use(server),
                    file,
                    ElasticLM.Issuing.KeyFiles.ExportPublicJwk(server),
                    TimeProvider.System
                );
                start = Stopwatch.GetTimestamp();
                for (int i = 0; i < Lines; i++)
                {
                    log.Append(
                        "lease.renewed",
                        w =>
                        {
                            w.WriteString("lic", "lic-srv");
                            w.WriteString("key", clientKid);
                            w.WriteString("lease", "lease-1-AAAAAAAAAAAAAA");
                        }
                    );
                }

                log.Seal();
                double line = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Lines;
                Console.WriteLine(
                    "usage log line (signed every 100): "
                        + line.ToString("F1", CultureInfo.InvariantCulture)
                        + " us ("
                        + Lines.ToString(CultureInfo.InvariantCulture)
                        + " lines)"
                );
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }

            return true;
        }
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
