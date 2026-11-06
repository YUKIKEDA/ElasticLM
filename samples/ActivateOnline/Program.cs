using ElasticLM.Client;
using ElasticLM.Licensing;

// Starts a trial at elm-service, or activates with a license key, and checks the feature pro.
//
//   app <service-url> <root-public-key.pem> <data-folder> [license-key]
//
// A real app embeds the service URL and the root public key instead of reading them from the command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

if (args.Length is not (3 or 4))
{
    Console.Error.WriteLine("usage: app <service-url> <root-public-key.pem> <data-folder> [license-key]");
    return 2;
}

try
{
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = new Uri(args[0]),
        Trust = TrustStore.Create([File.ReadAllText(args[1])], revokedKeyIds: []),
        DataDirectory = args[2],
    };
    if (!ActivationClient.TryCreate(options, out ActivationClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    // Disposing the client stops its refresh in the background; the activation stays in the data folder.
    await using ActivationClient client = created!;

    // Without a license key, start a trial, which the service gives each machine once; with one, activate.
    bool isTrial = args.Length == 3;
    ActivationResult result = isTrial ? await client.StartTrialAsync() : await client.ActivateAsync(args[3]);
    if (result is ActivationResult.Failed failed)
    {
        return Refused(failed.Error);
    }

    // Check the kept license file for the feature, as the app does at each start, also with no network.
    switch (client.Check(FeatureId, version: null, isTrial ? LicenseKind.Trial : LicenseKind.Standard))
    {
        case LicenseVerificationResult.Valid valid when isTrial:
            Console.WriteLine($"trial: {FeatureId}");
            Console.WriteLine($"expires: {valid.License.ExpiresAt:u}");
            return 0;
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"activated: {FeatureId}");
            Console.WriteLine($"license: {valid.License.LicenseId}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            return Refused(invalid.Error);
        default:
            return 1;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read, a second argument that is not a public key in PEM, or a first argument that is neither an
    // https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// The client, the service, or the check refused: print the error code and the reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
