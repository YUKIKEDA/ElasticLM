using ElasticLM.Client;
using ElasticLM.Licensing;

// Leases the feature pro from the license server, returns the lease, and borrows the feature for one hour.
//
//   app <root-public-key.pem> <data-folder>
//
// A real app embeds the root public key instead of reading it from a file.
const string ProductId = "acme-cad";
const string FeatureId = "pro";
const int MaxAttempts = 3;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder>");
    return 2;
}

try
{
    var options = new LicenseClientOptions
    {
        ProductId = ProductId,
        ServerUrl = new Uri("https://127.0.0.1:7070/"),
        Trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []),
        DataDirectory = args[1],
    };
    if (!LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    // Disposing the client returns the leases it still holds, but not a borrow.
    await using LicenseClient client = created!;

    switch (await WithRetryAsync(() => client.AcquireLeaseAsync(FeatureId)))
    {
        case LeaseResult.Acquired acquired:
            Console.WriteLine($"leased: {FeatureId}");

            // The app uses the feature here, while the client renews the lease in the background.
            if (await client.ReleaseLeaseAsync(acquired.Lease) is LicenseError releaseError)
            {
                return Refused(releaseError);
            }

            break;
        case LeaseResult.Failed failed:
            return Refused(failed.Error);
    }

    switch (await WithRetryAsync(() => client.BorrowLeaseAsync(FeatureId, TimeSpan.FromHours(1))))
    {
        case LeaseResult.Acquired:
            Console.WriteLine($"borrowed: {FeatureId} for 1 hour");
            return 0;
        case LeaseResult.Failed failed:
            return Refused(failed.Error);
        default:
            return 1;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
{
    // A file that cannot be read, or a first argument that is not a public key in PEM.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// A server that has just started refuses new leases until its clients have had time to reclaim theirs, and says how long
// to wait. This waits that long and asks again, at most MaxAttempts times in all.
static async Task<LeaseResult> WithRetryAsync(Func<Task<LeaseResult>> request)
{
    for (int attempt = 1; ; attempt++)
    {
        LeaseResult result = await request();
        if (attempt == MaxAttempts || result is not LeaseResult.Failed { Error.Code: ErrorCode.ServerRecovering, RetryAfter: TimeSpan wait })
        {
            return result;
        }

        Console.WriteLine($"waiting {wait.TotalSeconds:0} s: the server is reclaiming leases after its start");
        await Task.Delay(wait);
    }
}

// The server refused the request: print its error code and reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
