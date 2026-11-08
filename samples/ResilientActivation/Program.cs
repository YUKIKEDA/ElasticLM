using ElasticLM.Client;
using ElasticLM.Licensing;

// Activates an installation of acme-cad online, checks the kept license file with no network, and refreshes it, and prints
// for each failure what the app does about it, decided by the operation and the error code.
//
//   app <root-public-key.pem> <data-folder> activate <service-url> <key>
//   app <root-public-key.pem> <data-folder> check
//   app <root-public-key.pem> <data-folder> refresh <service-url>
//
// Give refresh the service that activated the installation, or an address that does not answer to try it without a
// network: another service does not know the activation and removes it. A real app embeds the root public key and the
// service URL instead of reading them from the command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

string? command = args.Length > 2 ? args[2] : null;
int length = command switch
{
    "activate" => 5,
    "refresh" => 4,
    "check" => 3,
    _ => -1,
};
if (args.Length != length)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder> <command> [arguments]");
    Console.Error.WriteLine("commands: activate <service-url> <key>, check, refresh <service-url>");
    return 2;
}

try
{
    // check works with files alone, so it gives the client no service URL.
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = command == "check" ? null : new Uri(args[3]),
        Trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []),
        DataDirectory = args[1],
    };
    if (!ActivationClient.TryCreate(options, out ActivationClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    await using ActivationClient client = created!;

    return command switch
    {
        "activate" => await ActivateAsync(client, args[4]),
        "refresh" => await RefreshAsync(client),
        _ => Check(client),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read, a first argument that is not a public key in PEM, or a service URL that is neither an
    // https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Activates with the license key. A failure is either worth trying again later, or needs the user, or the vendor.
static async Task<int> ActivateAsync(ActivationClient client, string licenseKey)
{
    switch (await client.ActivateAsync(licenseKey))
    {
        case ActivationResult.Activated activated:
            Console.WriteLine($"activated: {FeatureId}");
            PrintExpiry(activated.License);
            return 0;
        case ActivationResult.Failed failed:
            string decision = failed.Error.Code switch
            {
                // No answer, or a busy service: the same request may work later.
                ErrorCode.NetworkError or ErrorCode.RateLimited => "retry later",

                // The key does not open a license that can be activated now.
                ErrorCode.LicenseNotFound or ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => "ask for another key",

                // Every device the license allows is activated.
                ErrorCode.ActivationLimit => "ask to deactivate another device",

                // An activation is kept already, or another process of the app holds the data folder.
                ErrorCode.InvalidState => "already activated or busy",
                _ => "tell the vendor",
            };
            return Decide(decision, failed.Error, failed.RetryAfter);
        default:
            return 1;
    }
}

// Checks the kept license file for the feature, as the app does at each start; it reaches no service.
static int Check(ActivationClient client)
{
    switch (client.Check(FeatureId, version: null))
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine(valid.InGracePeriod ? $"valid in the grace period: {FeatureId}" : $"valid: {FeatureId}");
            PrintExpiry(valid.License);
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            string decision = invalid.Error.Code switch
            {
                // Nothing is kept yet: the app asks the user to activate.
                ErrorCode.LicenseNotFound => "not activated",

                // The kept file ended, grace period included: it needs a refresh, which needs the network.
                ErrorCode.Expired => "refresh needed",

                // The clock is earlier than a time the client saw before.
                ErrorCode.ClockTampered => "ask to correct the clock",

                // The license does not grant the feature: the app leaves it out.
                ErrorCode.FeatureNotLicensed or ErrorCode.FeatureExpired => "feature not licensed",

                // The data folder was copied to another machine, or the machine changed too much.
                ErrorCode.WrongInstallation or ErrorCode.FingerprintMismatch => "activate again",
                _ => "tell the vendor",
            };
            return Decide(decision, invalid.Error, null);
        default:
            return 1;
    }
}

// Refreshes the kept activation now. When it fails, the kept license file still works until it ends, and the client tries
// again on its own every hour while it runs; State says what this process saw.
static async Task<int> RefreshAsync(ActivationClient client)
{
    switch (await client.RefreshAsync())
    {
        case ActivationResult.Activated refreshed:
            Console.WriteLine($"refreshed: {FeatureId}");
            PrintExpiry(refreshed.License);
            return 0;
        case ActivationResult.Failed failed:
            string decision = failed.Error.Code switch
            {
                ErrorCode.NetworkError or ErrorCode.RateLimited => "keeps working",
                ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => "ask for another key",

                // The service no longer has the activation, and the client removed the kept file.
                ErrorCode.ActivationNotFound => "activate again",
                _ => "tell the vendor",
            };
            int code = Decide(decision, failed.Error, failed.RetryAfter);
            Console.WriteLine($"state: {client.State}");
            return code;
        default:
            return 1;
    }
}

// Prints the decision on one line that does not change between systems, then the reason, which may.
static int Decide(string decision, LicenseError error, TimeSpan? retryAfter)
{
    Console.WriteLine($"{decision}: {error.WireName}");
    if (retryAfter is TimeSpan wait)
    {
        Console.WriteLine($"retry after: {wait.TotalSeconds:0} s");
    }

    Console.WriteLine($"reason: {error.Message}");
    return 1;
}

// The kept license file works until its expiry, and then for its grace period.
static void PrintExpiry(VerifiedLicense license)
{
    if (license.ExpiresAt is DateTimeOffset expires)
    {
        Console.WriteLine($"expires: {expires:u}");
        Console.WriteLine($"grace period ends: {expires + license.GracePeriod:u}");
    }
    else
    {
        Console.WriteLine("expires: never");
    }
}
