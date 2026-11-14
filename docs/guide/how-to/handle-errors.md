---
sidebar_position: 13
---

# Handle errors

This guide is for a developer who builds ElasticLM's activation into an app and decides what the app does when an activation, a check, or a refresh fails: try again later, ask the user, or tell the vendor. The guide starts where `elm-service` is running at `http://127.0.0.1:8080` with a product; [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets this up. [Error codes](../reference/error-codes.md) lists every code.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the root public key `root.pub.pem`, the folder `packages` with the two ElasticLM packages, and the file `nuget.config` in the tutorial folder. If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own.

## How a failure reaches the app

The ElasticLM API reports a failure as a result, not as an exception:

- `ActivationClient.TryCreate` returns `false` with a `LicenseError` in its `out` argument when the client cannot start, for example with `ELM_INSTALLATION_KEY_UNREADABLE` when the installation key in the data folder cannot be read: on Windows, that is also what a data folder copied from another machine or user gives.
- `ActivationClient.ActivateAsync`, `StartTrialAsync`, and `RefreshAsync` return `ActivationResult`: `Activated` with the license, or `Failed` with a `LicenseError` and an optional `RetryAfter`.
- `ActivationClient.Check` returns `LicenseVerificationResult`: `Valid` with the license and `InGracePeriod`, or `Invalid` with a `LicenseError`.
- A `LicenseError` has a `Code` (an `ErrorCode`), its `WireName` such as `ELM_LICENSE_NOT_FOUND`, and a `Message`, an English sentence for logs and support, not for the app's logic.

An exception means a mistake in the call itself, such as an argument out of range (`ArgumentException`) or a disposed client (`ObjectDisposedException`); [How failures are reported](../reference/dotnet-api.md#how-failures-are-reported) lists them. The app decides by the operation and the `Code`, never by the `Message`: the message of `ELM_NETWORK_ERROR`, for example, holds the system's own words, which differ between Windows, Linux, and macOS.

The client already retries on its own in two places, so the app does not need to: it sends a request once more after `ELM_NONCE_REQUIRED`, and while it runs with a service URL, it refreshes a kept activation again an hour after a failure. [What the client retries on its own](../reference/error-codes.md#what-the-client-retries-on-its-own) lists the details.

## Decide by the operation and the code

The same code can call for different things in different operations: `ELM_LICENSE_NOT_FOUND` from an activation means that the key is wrong, and from `Check` that nothing is activated yet. Decide by both:

| Operation | Code | What the app does |
| --- | --- | --- |
| Activate, start a trial | [`ELM_NETWORK_ERROR`](../reference/error-codes.md), [`ELM_RATE_LIMITED`](../reference/error-codes.md) | Try again later; wait at least `RetryAfter` when it is set. |
| Start a trial | [`ELM_RATE_LIMITED`](../reference/error-codes.md) with a `RetryAfter` longer than a few seconds | The product has given all its trials of the last 24 hours, and the wait can be up to a day. Do not try again in a loop: show the user when to try again, or offer to buy a license. |
| Activate | [`ELM_LICENSE_NOT_FOUND`](../reference/error-codes.md), [`ELM_WRONG_PRODUCT`](../reference/error-codes.md), [`ELM_LICENSE_SUSPENDED`](../reference/error-codes.md), [`ELM_LICENSE_REVOKED`](../reference/error-codes.md), [`ELM_EXPIRED`](../reference/error-codes.md) | Ask the user for another license key, or to contact the vendor. |
| Activate | [`ELM_ACTIVATION_LIMIT`](../reference/error-codes.md) | Ask the user to deactivate the license on another device first. |
| Start a trial | [`ELM_TRIAL_USED`](../reference/error-codes.md) | Offer to buy a license. |
| Start a trial | [`ELM_TRIAL_NOT_OFFERED`](../reference/error-codes.md) | The product ID is wrong, or the vendor offers no trial: check the app's product ID, and tell the vendor. |
| Activate, start a trial | [`ELM_INVALID_STATE`](../reference/error-codes.md) | An activation is kept already, or another process of the app holds the data folder: check first, or try again when the other process is done. The message names the case. |
| Check | [`ELM_LICENSE_NOT_FOUND`](../reference/error-codes.md) | Nothing is activated: ask the user to activate. |
| Check | [`ELM_EXPIRED`](../reference/error-codes.md) | The kept file and its grace period ended: refresh, which needs the network. |
| Check | [`ELM_CLOCK_TAMPERED`](../reference/error-codes.md) | The clock is earlier than a time the client saw: ask the user to correct the clock, or refresh online. |
| Check | [`ELM_FEATURE_NOT_LICENSED`](../reference/error-codes.md), [`ELM_FEATURE_EXPIRED`](../reference/error-codes.md) | Leave the feature out. |
| Check | [`ELM_WRONG_INSTALLATION`](../reference/error-codes.md), [`ELM_FINGERPRINT_MISMATCH`](../reference/error-codes.md) | The kept file is not this installation's, or the machine changed too much: deactivate with `DeactivateAsync`, then activate again. |
| Check | [`ELM_WRONG_KIND`](../reference/error-codes.md) | The kept file is of another kind than the app asked for, such as a trial checked as `Standard`: pass the kind the app activated. |
| Refresh | [`ELM_NETWORK_ERROR`](../reference/error-codes.md), [`ELM_RATE_LIMITED`](../reference/error-codes.md) | Keep working with the kept file; the client tries again on its own. |
| Refresh | [`ELM_LICENSE_SUSPENDED`](../reference/error-codes.md), [`ELM_LICENSE_REVOKED`](../reference/error-codes.md), [`ELM_EXPIRED`](../reference/error-codes.md) | The kept file works until it ends; ask the user to contact the vendor. |
| Refresh | [`ELM_ACTIVATION_NOT_FOUND`](../reference/error-codes.md) | The vendor removed the activation, and the client removed the kept file: ask the user to activate again. |
| Refresh | [`ELM_INVALID_STATE`](../reference/error-codes.md) | No activation is kept: ask the user to activate. The message names the other cases. |
| Any | [`ELM_INTERNAL_ERROR`](../reference/error-codes.md) | There are three cases, and the message says which. The client cannot write its data folder: check the folder's permissions and the free disk space. The service failed: tell the vendor. The answer to a failed request named no error code that the client accepts from a server, and the message ends with "without a known error code" (it says "server" for the service too): check that the URL reaches the server or service itself and not a proxy or another program, and that the client library is not older than it. If both hold, tell the vendor, with the message. |
| Any | Any other code, such as [`ELM_CLONE_DETECTED`](../reference/error-codes.md) or [`ELM_PROOF_INVALID`](../reference/error-codes.md) | Tell the vendor, with the code and the message. |

`ELM_INVALID_STATE` also comes back when the app calls a method that needs a service URL on a client without one, such as `RefreshAsync`, which is a mistake in the app's code, and after 16 requests in a row got no answer, which needs the vendor's administrator; the message says which. [Error codes](../reference/error-codes.md) lists every case of each code.

## Create the app

The app below activates, checks, and refreshes, and prints for each failure what an app would do, from the table above. Create it in the folder `resilient`, and add the `ElasticLM.Client` package to it from `packages`:

<!-- steps:run -->
```shell
dotnet new console -o resilient
dotnet add resilient package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder; `dotnet nuget locals global-packages --list` shows where it is, also when the environment variable `NUGET_PACKAGES` moves it. That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `resilient/Program.cs` with this code:

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

Each command prints the decision on one line that is the same on every system, such as `retry later: ELM_NETWORK_ERROR`, then the `RetryAfter` when there is one, and the reason. `check` gives the client no service URL, to show that it reaches no service; a real app gives the URL also when it only checks, so that the client refreshes in the background. [Run without a network](run-without-a-network.md) uses `check`.

## See the decisions

Activate a new data folder, `errors-data`, with a key that the service does not know:

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem errors-data activate http://127.0.0.1:8080 ELM-AAAAA-AAAAA-AAAAA-AAAAA
```

It ends with exit code 1 and prints:

<!-- steps:expect -->
```text
ask for another key: ELM_LICENSE_NOT_FOUND
reason: No license has this key.
```

Activate against an address that does not answer, `http://127.0.0.1:8081`, which stands in for a network that is down:

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem errors-data activate http://127.0.0.1:8081 ELM-AAAAA-AAAAA-AAAAA-AAAAA
```

It ends with exit code 1 and prints, with the reason in your system's words:

```text
retry later: ELM_NETWORK_ERROR
reason: The service cannot be reached: Connection refused (127.0.0.1:8081)
```

<!-- steps:expect -->
```text
retry later: ELM_NETWORK_ERROR
```

Neither attempt kept an activation in `errors-data`; the client made its installation key there. [Run without a network](run-without-a-network.md) activates with a real key and shows what the app keeps when a refresh fails.

## Leases from a license server

`LicenseClient`, the client of a floating license, reports failures the same way: `AcquireLeaseAsync` and `BorrowLeaseAsync` return `LeaseResult`, `Acquired` or `Failed` with a `LicenseError` and a `RetryAfter`. The client does not wait by itself: after `ELM_SERVER_RECOVERING`, which a server sends for one lease time after it starts (10 minutes by default), wait `RetryAfter` and ask again, as the app of [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md) does. `ELM_NO_SEATS` means that every seat is in use: tell the user, or try again later. `ELM_FEATURE_EXPIRED` and `ELM_FEATURE_VERSION_NOT_LICENSED` mean that the license does not cover the feature now, or not this version of it, and `ELM_EXPIRED` that the license has ended (or, when its message names the feature, that the feature expires in that very second): asking again does not help, so tell the user that the license needs renewing. A lease that the client already holds is renewed on its own; its state and `LastError` say when that fails. [Floating licenses](../explanation/floating-licenses.md) explains the lease's states.

## In C

`libelasticlm` returns the code as an `int32_t`, `ELM_OK` (0) when it worked, with the same numbers as the .NET `ErrorCode` and the macros of `elasticlm.h`, such as `ELM_NETWORK_ERROR`. `elm_last_error_message` returns the message of the calling thread's last failure, and the functions that can be refused with a wait return it in `retry_after_seconds`. Decide by the code, as above; there is no function that turns a code into its name. [C API](../reference/c-api.md#errors) lists the functions.

## See also

- [Error codes](../reference/error-codes.md): every code, its number, its HTTP status, and what to do.
- [Run without a network](run-without-a-network.md): what the client keeps and does while the network is down.
- [.NET API](../reference/dotnet-api.md): every result type and method.
