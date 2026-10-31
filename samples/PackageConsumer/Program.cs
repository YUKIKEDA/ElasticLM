using System.Security.Cryptography;
using ElasticLM.Client;
using ElasticLM.Licensing;

// design §20.5: uses one thing from each package, through their public API only. Exits with 0 when both behave.

// ElasticLM.Licensing: a license file that is not one is refused as malformed.
using var root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
TrustStore trust = TrustStore.Create([root.ExportSubjectPublicKeyInfoPem()], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
LicenseVerificationResult result = verifier.Verify(
    "not a license file"u8,
    new LicenseRequirement("acme-cad", LicenseKind.Standard, null, null, null)
);
if (result is not LicenseVerificationResult.Invalid { Error.Code: ErrorCode.Malformed })
{
    Console.Error.WriteLine($"Expected a malformed license file, got {result}.");
    return 1;
}

// ElasticLM.Client: an installation key made in a folder is loaded again with the same thumbprint.
string folder = Path.Combine(Path.GetTempPath(), "elasticlm-consumer-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new InstallationKeyStore("acme-cad", KeyScope.User, folder);
    if (!store.TryLoadOrCreate(out InstallationKey? made, out LicenseError? error))
    {
        Console.Error.WriteLine($"Could not make an installation key: {error}.");
        return 1;
    }

    using (made)
    {
        if (!new InstallationKeyStore("acme-cad", KeyScope.User, folder).TryLoad(out InstallationKey? loaded, out error))
        {
            Console.Error.WriteLine($"Could not load the installation key: {error}.");
            return 1;
        }

        using (loaded)
        {
            if (loaded!.Thumbprint != made!.Thumbprint)
            {
                Console.Error.WriteLine("The loaded installation key is not the one made.");
                return 1;
            }
        }
    }
}
finally
{
    if (Directory.Exists(folder))
    {
        Directory.Delete(folder, recursive: true);
    }
}

Console.WriteLine("ElasticLM.Licensing and ElasticLM.Client work from their packages.");
return 0;
