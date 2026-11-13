---
sidebar_position: 14
---

# Run without a network

This guide is for a developer whose app activates online with `elm-service` and must keep working when the network or the service is down. The app keeps the license file of its activation in its data folder, checks it at each start with no network, and refreshes it online now and then. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, and the service has a product with a feature. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [Activation](../explanation/activation.md) explains the refresh.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the root public key `root.pub.pem`, the folder `packages` with the two ElasticLM packages, and the file `nuget.config` in the tutorial folder.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service).

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the file below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

`elm` prints a page of products:

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}]}
```

In the outputs below, the IDs, the license key, and the times are fake. On your machine they are different every time.

## Create a license to try with

Create a policy and a license with the feature `pro` under it, so that this guide does not use up an activation of the tutorial's license. The policy has only a name, so it takes the defaults: the license itself does not expire, while each license file that the service issues for an activation expires after 30 days, has a grace period of 7 days, and is refreshed from 20 days before its expiry:

<!-- steps:file path=resilient-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, offline check"}
```

<!-- steps:run capture=resilient-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body resilient-policy.json --url http://127.0.0.1:8080
```

The `id` at the start of the answer is the policy's ID, `<resilient-policy-id>` in the next file:

<!-- steps:file path=resilient-license.json -->
```json
{"policyId":"<resilient-policy-id>","name":"Offline check customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=resilient-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body resilient-license.json --url http://127.0.0.1:8080
```

The `key` at the end of the answer is the license key, `<resilient-license-key>` below.

## Create the app

If you made `resilient` in [Handle errors](handle-errors.md#create-the-app), use it, and skip to [Activate online](#activate-online): `dotnet new` refuses to create it again. Otherwise create the app in the folder `resilient`, and add the `ElasticLM.Client` package to it from `packages`:

<!-- steps:run -->
```shell
dotnet new console -o resilient
dotnet add resilient package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder; `dotnet nuget locals global-packages --list` shows where it is, also when the environment variable `NUGET_PACKAGES` moves it. That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `resilient/Program.cs` with this code, the same as in [Handle errors](handle-errors.md#create-the-app):

<!-- sample: samples/ResilientActivation/Program.cs -->
<!-- steps:file path=resilient/Program.cs -->
```csharp
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
    // check works with files alone, and this one gives the client no service URL to show that. A real app gives the URL
    // also when it only checks: Check still sends nothing, and the client then refreshes the activation in the background.
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

                // The key does not open a license of this product that can be activated now.
                ErrorCode.LicenseNotFound or ErrorCode.WrongProduct or ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired =>
                    "ask for another key",

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

                // The kept file is not this installation's, or the machine changed too much: the app deactivates
                // (DeactivateAsync) and activates again.
                ErrorCode.WrongInstallation or ErrorCode.FingerprintMismatch => "deactivate and activate again",
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

                // The kept file works until it ends; the license itself needs the vendor.
                ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => "ask to contact the vendor",

                // Nothing is kept to refresh, among other cases that the reason names.
                ErrorCode.InvalidState => "activate first",

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
```

The app has three commands. `activate` calls `ActivateAsync`, `check` calls `Check`, and `refresh` calls `RefreshAsync`. After a success, each prints its result and the expiry of the kept license file. After a failure, it prints what the app does on one line that is the same on every system, such as `keeps working: ELM_NETWORK_ERROR`, then the reason, which may hold the system's own words, and for `refresh` the client's state. `check` gives the client no service URL, to show that it reaches no service; a real app gives the URL also when it only checks, so that the client refreshes in the background.

## Activate online

Activate the data folder `resilient-data` with the license key, while the service can be reached:

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data activate http://127.0.0.1:8080 <resilient-license-key>
```

The app prints:

```text
activated: pro
expires: 2026-02-14 09:30:00Z
grace period ends: 2026-02-21 09:30:00Z
```

<!-- steps:expect -->
```text
activated: pro
```

The client keeps the license file of the activation in `resilient-data`, signed by the service. It expires 30 days after the activation; the app may use it for 7 more days, the grace period.

## Check with no network

At each start, the app checks the kept license file. `check` gives the client no service URL, so nothing goes over the network:

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data check
```

<!-- steps:expect -->
```text
valid: pro
```

It prints the same expiry as above. `Check` verifies the file against the root public key, the product, the feature, and this installation's key and machine fingerprints, all on this machine. It works the same on a machine that is offline, and while the service is down.

## See a refresh fail

To see what happens when the service cannot be reached, refresh against an address that does not answer, `http://127.0.0.1:8081`. A real app embeds its service URL; the guide changes it here only to stand in for a network that is down. Give `refresh` only the service that activated the data folder, or an address that does not answer: another service does not know the activation, and the client would remove the kept file.

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data refresh http://127.0.0.1:8081
```

It ends with exit code 1 and prints, with the reason in your system's words:

```text
keeps working: ELM_NETWORK_ERROR
reason: The service cannot be reached: Connection refused (127.0.0.1:8081)
state: RefreshFailing
```

<!-- steps:expect -->
```text
keeps working: ELM_NETWORK_ERROR
state: RefreshFailing
```

The kept license file has not changed. Check again:

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data check
```

<!-- steps:expect -->
```text
valid: pro
```

## What the client does while the network is down

- **The kept license file keeps working** until its expiry and then for its grace period: 30 and 7 days with the defaults of the policy. `Check` returns `Valid` with `InGracePeriod` `true` during the grace period; after it, `Check` fails with `ELM_EXPIRED`, and the app needs a refresh, which needs the network. [Expiry and grace period](../explanation/license-files-and-verification.md#expiry-and-grace-period) explains the times.
- **The client refreshes on its own** while the app runs, from the start of the refresh window (20 days before the expiry by default). When a refresh fails, it tries again an hour later, or after the service's `Retry-After` when that is longer (at most a day), and the app keeps working. This needs a client with the service URL. `RefreshAsync` refreshes at once.
- **`State` and `StateChanged` say how the refresh goes**: `RefreshFailing` after a failure, `Active` again after a refresh that works, `Refused` when the license was revoked, suspended, or expired, and `CloneDetected` when the service saw a copy. The state is that of the running client: a new process starts at `Active` whenever it has a kept activation, so this guide's `check` does not show it.
- **The clock is checked too.** The client keeps the time it last saw, and a clock set back before it makes `Check` fail with `ELM_CLOCK_TAMPERED`; a refresh that works resets it. Someone who deletes the data folder's clock state can still set the clock back as far as the license file's issue time. [Clock rollback detection and its limits](../explanation/license-files-and-verification.md#clock-rollback-detection-and-its-limits) explains this.
- **A device that is never online** activates and refreshes with files instead; see [Activate a device without a network](activate-a-device-without-a-network.md).

A floating license from `elm-server` behaves differently: a lease must be renewed while the app holds it. When the server cannot be reached, the client retries the renewal (after 15 seconds, 30 seconds, then every 60 seconds), the lease's state becomes `Retrying`, and it becomes `Lost` when the lease expires. To work away from the network, the app borrows a seat with `BorrowLeaseAsync` before it leaves, and reads its borrows with `GetBorrowedLeases` at the next start, with no network. [Floating licenses](../explanation/floating-licenses.md) explains leases and borrows.

## See also

- [Handle errors](handle-errors.md): what the app does about each error.
- [Activation](../explanation/activation.md): activations, refreshes, and the policy's times.
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient`, `ActivationState`, and what each method returns.
