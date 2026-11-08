---
sidebar_position: 3
---

# Activate a device without a network

This guide is for a vendor whose customers run the app on devices that never reach `elm-service`, and for the developer who builds the app. The app on the device writes a request file; a person carries it to a computer that can reach the service, where `elm offline process` gets the answer file; the app imports the answer and keeps its license file. The same exchange refreshes the license file before it expires. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, and the service has a product with a feature. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [Offline activation by file exchange](../explanation/activation.md#offline-activation-by-file-exchange) explains how the exchange works.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The app below also uses the root public key `root.pub.pem`, the folder `packages` with the two ElasticLM packages, and the file `nuget.config` of that folder.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the files below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

In the outputs below, the IDs, the license key, and the times are fake. On your machine they are different every time. Here the device and the connected computer are the same machine; in real use, only the request file and the answer file go from one to the other.

## Create a license for the device

A device without a network refreshes its license file only when someone carries the files, so give its license a policy with a longer license file. With the defaults, each license file is valid for 30 days and the app keeps working 7 days after that, so someone must carry a refresh at least every 37 days. This policy makes license files valid for 90 days (`tokenLifetime`, 7,776,000 seconds), whose refresh is due in their last 30 days (`refreshWindow`, 2,592,000 seconds); [Set up a policy](set-up-a-policy.md) describes the other settings:

<!-- steps:file path=offline-policy.json -->
```json
{"productId":"<product-id>","name":"Offline, 90-day files","tokenLifetime":7776000,"refreshWindow":2592000}
```

<!-- steps:run capture=offline-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body offline-policy.json --url http://127.0.0.1:8080
```

Its `id` is `<offline-policy-id>` below. Create a license with the feature `pro` under it:

<!-- steps:file path=offline-license.json -->
```json
{"policyId":"<offline-policy-id>","name":"Offline customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=offline-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body offline-license.json --url http://127.0.0.1:8080
```

`elm` prints the license. The `key` at the end is its license key, `<offline-license-key>` below, which the customer types into the app:

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Offline customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00001"}
```

## Create the app

This guide uses a small console app, `offline-app`, with one command for each step. Create it, and add the `ElasticLM.Client` package to it from `packages`:

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder, which `dotnet nuget locals global-packages --list` shows (or the folder that `NUGET_PACKAGES` names, if you set it). That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `offline-app/Program.cs` with this code. The steps below name the method of `ActivationClient` that each command calls:

<!-- sample: samples/ActivateOffline/Program.cs -->
<!-- steps:file path=offline-app/Program.cs -->
```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

// Activates and refreshes an installation of acme-cad with files that a person carries to elm-service and back, checks the
// feature pro, and deactivates it online or with a deactivation proof.
//
//   app <root-public-key.pem> <data-folder> request <key> <request-file>
//   app <root-public-key.pem> <data-folder> refresh <request-file>
//   app <root-public-key.pem> <data-folder> import <response-file>
//   app <root-public-key.pem> <data-folder> deactivate <service-url>
//   app <root-public-key.pem> <data-folder> deactivate-proof <proof-file>
//   app <root-public-key.pem> <data-folder> forget
//
// A real app embeds the root public key, and the service URL if it deactivates online, instead of reading them from the
// command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

string? command = args.Length > 2 ? args[2] : null;
int length = command switch
{
    "request" => 5,
    "refresh" or "import" or "deactivate" or "deactivate-proof" => 4,
    "forget" => 3,
    _ => -1,
};
if (args.Length != length)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder> <command> [arguments]");
    Console.Error.WriteLine(
        "commands: request <key> <file>, refresh <file>, import <file>, deactivate <service-url>, deactivate-proof <file>, forget"
    );
    return 2;
}

try
{
    // Only the online deactivation reaches the service; the other commands work with files alone.
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = command == "deactivate" ? new Uri(args[3]) : null,
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
        "request" => await RequestAsync(client, args[3], args[4]),
        "refresh" => await RefreshAsync(client, args[3]),
        "import" => await ImportAsync(client, args[3]),
        "deactivate" => await DeactivateAsync(client),
        "deactivate-proof" => await DeactivationProofAsync(client, args[3]),
        _ => await ForgetAsync(client),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read or written, a first argument that is not a public key in PEM, or a service URL that is
    // neither an https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Makes the activation request. It holds the license key, so the file must not be left where others can read it.
static async Task<int> RequestAsync(ActivationClient client, string licenseKey, string file) =>
    Save(await client.CreateActivationRequestAsync(licenseKey), file);

// Makes the refresh request of the kept activation.
static async Task<int> RefreshAsync(ActivationClient client, string file) => Save(await client.CreateRefreshRequestAsync(), file);

// Writes the request or the deactivation proof to the file that the user carries to the service.
static int Save(OfflineRequestResult result, string file)
{
    switch (result)
    {
        case OfflineRequestResult.Created created:
            File.WriteAllText(file, created.File);
            Console.WriteLine($"file: {file}");
            if (created.Replaced)
            {
                // The service would take an answer to the earlier request for a clone after this one.
                Console.WriteLine("replaced: discard the earlier request file");
            }

            return 0;
        case OfflineRequestResult.Failed failed:
            return Refused(failed.Error);
        default:
            return 1;
    }
}

// Imports the service's answer, and checks the kept license file for the feature, as the app does at each start.
static async Task<int> ImportAsync(ActivationClient client, string file)
{
    ActivationResult imported = await client.ImportResponseAsync(File.ReadAllText(file));
    if (imported is ActivationResult.Failed failed)
    {
        return Refused(failed.Error);
    }

    switch (client.Check(FeatureId, version: null))
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"activated: {FeatureId}");
            Console.WriteLine($"license: {valid.License.LicenseId}");
            Console.WriteLine($"sequence: {valid.License.Sequence}");
            Console.WriteLine($"expires: {valid.License.ExpiresAt:u}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            return Refused(invalid.Error);
        default:
            return 1;
    }
}

// Deactivates online: the service frees the activation's place, and the client removes the kept license file.
static async Task<int> DeactivateAsync(ActivationClient client)
{
    if (await client.DeactivateAsync() is LicenseError error)
    {
        return Refused(error);
    }

    Console.WriteLine("deactivated");
    return 0;
}

// Makes the deactivation proof. The client removes the kept license file now, and refuses a new activation until the proof
// is forgotten.
static async Task<int> DeactivationProofAsync(ActivationClient client, string file) => Save(await client.CreateDeactivationProofAsync(), file);

// Forgets the deactivation proof, once the user has handed it in to the service.
static async Task<int> ForgetAsync(ActivationClient client)
{
    if (await client.ForgetDeactivationProofAsync() is LicenseError error)
    {
        return Refused(error);
    }

    Console.WriteLine("forgotten");
    return 0;
}

// The client or the check refused: print the error code and the reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
```

The app creates its `ActivationClient` without a service URL, so the client never contacts the service and refreshes nothing on its own; only the command `deactivate` gives it one ([Deactivate a device](deactivate-a-device.md) uses it). The client keeps its installation key, which identifies this copy of the app, and its license file in the data folder of the second argument. Use a new data folder for this guide, `offline-data`: the client refuses to activate in a folder that keeps an activation, such as `client-data` of the tutorial.

## Make the request on the device

The command `request` calls `CreateActivationRequestAsync` with the license key, and writes the text of the request that it returns to a file. Make the request:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data request <offline-license-key> request.elmreq
```

<!-- steps:expect -->
```text
file: request.elmreq
```

The request file holds the license key, the device's public key, and its machine fingerprints, signed with the installation key, and it expires 7 days after it was made. Keep it where others cannot read it, since anyone with the file has the license key. Until the device imports an answer, the app hands out the same request again; after 7 days by the device's clock it makes a new one, and prints `replaced: discard the earlier request file` as well.

## Hand in the request

Carry `request.elmreq` to a computer that can reach the service, and send it there:

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in request.elmreq --out response.elmresp
```

`elm` prints the activation, the license, and the sequence number of the license file, then writes the answer file:

```text
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000302, seq 1: response.elmresp
```

Choose who hands in the file:

- **The customer**, with `elm` and no credentials: `elm offline process` then sends the file to the public entry of the service, which needs none, since the file proves as much as an online request. Without `ELM_API_KEY`, and without a login of `elm admin login`, the same command does this.
- **The vendor's administrator**, on the customer's behalf: with `ELM_API_KEY` set, as here, or after `elm admin login`, `elm offline process` sends the file to the administration API, which writes a row of the audit log for each file. The admin UI's offline page does the same.

If the answer file gets lost, hand in the same request again: the service gives the same answer to the same request.

## Import the answer on the device

Carry `response.elmresp` back to the device. The command `import` calls `ImportResponseAsync` with the file's text, which checks the license file as an online activation does and keeps it, and then calls `Check` for the feature `pro`, as an app does at each start. Import the answer:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data import response.elmresp
```

The app prints four lines, the activation's sequence number among them:

<!-- steps:expect -->
```text
activated: pro
sequence: 1
```

The second line is the license's ID, such as `license: 01960000-0000-7000-8000-000000000302`, and the last one is when the license file expires, 90 days from now in UTC, such as `expires: 2026-04-15 09:30:00Z`. The device is now activated and works without a network until then, and for its grace period after.

## Refresh the license file

Refresh before the license file expires. The refresh is due when the file's expiry less its `RefreshWindow` has passed, and the app can read both from the verified license, as `ExpiresAt` and `RefreshWindow`. A client without a service URL never refreshes on its own, so the app must ask the user to carry the files. The command `refresh` calls `CreateRefreshRequestAsync`, which needs no license key, and writes the request to a file as `request` does.

Make the refresh request on the device, hand it in, and import its answer, as for the activation:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data refresh refresh.elmreq
```

<!-- steps:expect -->
```text
file: refresh.elmreq
```

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in refresh.elmreq --out refresh.elmresp
dotnet run --project offline-app -- root.pub.pem offline-data import refresh.elmresp
```

<!-- steps:expect -->
```text
activated: pro
sequence: 2
```

The new license file has the next sequence number, and a new expiry 90 days from now. The service refuses a refresh request whose license is suspended, revoked, or expired, as it refuses an online refresh.

## When the service or the app refuses

Import the first answer again:

<!-- steps:run exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data import response.elmresp
```

<!-- steps:expect -->
```text
ELM_REQUEST_MISMATCH: The answer is not for the request this installation waits for.
```

| Code | What happened | What to do |
| --- | --- | --- |
| `ELM_REQUEST_MISMATCH` | The answer is not the answer to the request the app waits for. | Import the answer to the latest request, or make a new request. |
| `ELM_REQUEST_EXPIRED` | `elm offline process` got this from the service: the request is older than its 7 days, or the device's clock is behind. | Make a new request. When the device's clock is behind, the app passes `renew: true` to `CreateActivationRequestAsync` or `CreateRefreshRequestAsync` to make a new request at once. |
| `ELM_INVALID_STATE` | The data folder keeps an activation, so the app makes no activation request. | Refresh it instead, or deactivate it first. |
| `ELM_ACTIVATION_LIMIT` | The license has as many devices as its policy allows. | Deactivate another device, as in [Deactivate a device](deactivate-a-device.md). |

`elm offline process` prints the service's code on a line that starts with `error:`, and ends with exit code 1. [Error codes](../reference/error-codes.md) lists every code.

## See also

- [Offline activation by file exchange](../explanation/activation.md#offline-activation-by-file-exchange): the files and the rules that make the exchange safe to repeat.
- [`elm offline process`](../reference/elm-cli.md#elm-offline-process): every option.
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient` and what each method returns.
- [Token and file formats](../reference/formats.md#offline-request-elm-actreqjws): the request and answer files.
- [Deactivate a device](deactivate-a-device.md): with a deactivation proof, when the device has no network.
