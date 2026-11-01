using ElasticLM.Licensing;

// Verifies a license file offline and prints whether it is valid.
//
//   app <root-public-key.pem> <license.elmlic>
//
// A real app embeds the root public key instead of reading it from a file.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <license.elmlic>");
    return 2;
}

// <snippet:verify>
TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
var requirement = new LicenseRequirement(ProductId, LicenseKind.Standard, FeatureId, null, null);
LicenseVerificationResult result = verifier.Verify(File.ReadAllBytes(args[1]), requirement);

// </snippet:verify>
switch (result)
{
    case LicenseVerificationResult.Valid valid:
        Console.WriteLine($"valid: license {valid.License.LicenseId}, product {valid.License.ProductId}, feature {FeatureId}");
        return 0;
    case LicenseVerificationResult.Invalid invalid:
        Console.WriteLine($"invalid: {invalid.Error.WireName}: {invalid.Error.Message}");
        return 1;
    default:
        return 1;
}
