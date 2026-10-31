# ElasticLM.Licensing

Offline verification of ElasticLM license files: signed tokens, a trust store of root keys, a clock guard, and machine
fingerprints. It has no dependencies and works with NativeAOT.

```csharp
using ElasticLM.Licensing;

TrustStore trust = TrustStore.Create([File.ReadAllText("root.pub.pem")], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
LicenseVerificationResult result = verifier.Verify(
    File.ReadAllBytes("app.elmlic"),
    new LicenseRequirement("acme-cad", LicenseKind.Standard, "pro", "2.1", null));

if (result is LicenseVerificationResult.Valid valid)
{
    Console.WriteLine($"Licensed until {valid.License.ExpiresAt?.ToString() ?? "no expiry"}.");
}
else if (result is LicenseVerificationResult.Invalid invalid)
{
    Console.WriteLine($"{invalid.Error.WireName}: {invalid.Error.Message}");
}
```

Source, design, and issues: https://github.com/YUKIKEDA/ElasticLM
