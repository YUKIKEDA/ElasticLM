---
sidebar_position: 4
---

# Deactivate a device

This guide is for a vendor who runs `elm-service`, and for the developer who builds the app, when a device should stop holding an activation of a license: the customer moves the app to another device, or a device is lost. Deactivation frees the device's place under the license's activation limit. There are three ways: the app deactivates itself online, the app makes a deactivation proof that a person carries to the service when the device has no network, or the vendor's administrator deactivates the device without it. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, and the service has a product with a feature. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [Deactivation](../explanation/activation.md#deactivation) explains what each way does.

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

In the outputs below, the IDs, the license key, the thumbprints, the fingerprints, and the times are fake. On your machine they are different every time.

## Prepare a device to deactivate

To try each way, you need an activated device. This section creates a license and activates a data folder with the app of [Activate a device without a network](activate-a-device-without-a-network.md), which this guide also uses for the app's own deactivation. If your devices are already activated, skip to the way you need.

Create a policy and a license with the feature `pro` under it. With `deactivationLimit`, the policy limits how many times devices may deactivate a license, in total; deactivations by an administrator do not count. This policy allows 3:

<!-- steps:file path=moving-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, 3 moves","deactivationLimit":3}
```

<!-- steps:run capture=moving-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body moving-policy.json --url http://127.0.0.1:8080
```

<!-- steps:file path=moving-license.json -->
```json
{"policyId":"<moving-policy-id>","name":"Moving customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=moving-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body moving-license.json --url http://127.0.0.1:8080
```

The `key` at the end of the answer is the license key, `<moving-license-key>` below. Create the app, and add the `ElasticLM.Client` package to it from `packages`:

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder, which `dotnet nuget locals global-packages --list` shows (or the folder that `NUGET_PACKAGES` names, if you set it). That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `offline-app/Program.cs` with this code, the same as in [Activate a device without a network](activate-a-device-without-a-network.md#create-the-app). The steps below name the method of `ActivationClient` that each command calls:

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

Activate the data folder `device-data` with the license key, by the offline exchange: make a request (`request` calls `CreateActivationRequestAsync`), hand it in, and import the answer (`import` calls `ImportResponseAsync`).

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> first.elmreq
elm offline process --url http://127.0.0.1:8080 --in first.elmreq --out first.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import first.elmresp
```

<!-- steps:expect -->
```text
activated: pro
```

## Deactivate from the app

When the device can reach the service, the app calls `DeactivateAsync`. The client needs the service URL for it, so the command `deactivate` takes it as its argument and creates the client with it:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data deactivate http://127.0.0.1:8080
```

<!-- steps:expect -->
```text
deactivated
```

The service frees the activation's place and counts one deactivation of the license, and the client removes the license file it kept. The installation key stays in the data folder, so the same copy of the app can activate again. When the license has reached its `deactivationLimit`, the service answers `ELM_DEACTIVATION_LIMIT` and nothing is removed; an administrator can still deactivate the device, as below.

## Deactivate a device without a network

A device that cannot reach the service makes a deactivation proof instead, a file that a person carries to the service. To try it, activate `device-data` again:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> second.elmreq
elm offline process --url http://127.0.0.1:8080 --in second.elmreq --out second.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import second.elmresp
```

<!-- steps:expect -->
```text
activated: pro
```

The command `deactivate-proof` calls `CreateDeactivationProofAsync`, and writes the proof that it returns to a file, as `request` writes a request file:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data deactivate-proof device.elmdeact
```

<!-- steps:expect -->
```text
file: device.elmdeact
```

The client removes the license file now, so the app stops working with the license on this device at once. The service still counts the activation until it gets the proof, so the client keeps the proof and refuses to activate again until the app forgets it:

<!-- steps:run exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> third.elmreq
```

<!-- steps:expect -->
```text
ELM_INVALID_STATE: A deactivation proof is kept; pass it to the service and forget it first.
```

Carry `device.elmdeact` to a computer that can reach the service, and hand it in, as the customer without credentials or as the vendor's administrator, the same choice as for a request file in [Hand in the request](activate-a-device-without-a-network.md#hand-in-the-request). A proof gets no answer file, so there is no `--out`:

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in device.elmdeact
```

<!-- steps:expect -->
```text
deactivated
```

The service frees the activation and counts one deactivation of the license, as for the app's own deactivation. The app cannot know when this happened, so it must ask the user, and only then call `ForgetDeactivationProofAsync`, as the command `forget` does:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data forget
```

<!-- steps:expect -->
```text
forgotten
```

The data folder can now be activated again. If the app forgot the proof before it was handed in, the service would still count the old activation: a new activation would then take a second place under the limit, or be refused with `ELM_ACTIVATION_LIMIT`. Hand in the proof even when it is late.

## Deactivate as the administrator

The vendor's administrator deactivates a device without it, for example when it was lost, or when the license has reached its `deactivationLimit`. These deactivations do not count against the limit. To try it, activate `device-data` once more, and note the license's ID, which the app prints:

<!-- steps:run capture=moving-license-id pattern="license: ([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> fourth.elmreq
elm offline process --url http://127.0.0.1:8080 --in fourth.elmreq --out fourth.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import fourth.elmresp
```

`<moving-license-id>` below is that ID, the `id` of the license. List the license's activations:

<!-- steps:run capture=moving-activation-id pattern="\x22id\x22:\x22([0-9a-f]{8}-[0-9a-f-]{27})\x22" -->
```shell
elm admin request GET /v1/admin/licenses/<moving-license-id>/activations --url http://127.0.0.1:8080
```

`elm` prints a page of activations:

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000603","licenseId":"01960000-0000-7000-8000-000000000302","keyId":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","fingerprints":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4","9b1e7c3a5d2f4e6081a7c9b3d5e7f901"],"client":null,"app":null,"seq":1,"blockedAt":null,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}]}
```

Each item is one device: `keyId` is the thumbprint of its installation key, `fingerprints` its machine fingerprints, and `createdAt` when it activated. Find the device to deactivate by these, and take its `id`, `<moving-activation-id>` here. Deactivate it:

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/activations/<moving-activation-id>/deactivate --url http://127.0.0.1:8080
```

The service answers with no body, so `elm` prints nothing. The place is free at once, but the license file on the device stays valid until it expires, since the device is not told; [Deactivation](../explanation/activation.md#deactivation) explains this. The device's next refresh is refused. The admin UI's page of a license lists its activations and deactivates them too.

## See also

- [Deactivation](../explanation/activation.md#deactivation) and [Offline activation by file exchange](../explanation/activation.md#offline-activation-by-file-exchange): what each way does.
- [`POST /v1/admin/activations/{id}/deactivate`](../reference/administration-api.md#post-v1adminactivationsiddeactivate) and [`GET /v1/admin/licenses/{id}/activations`](../reference/administration-api.md#get-v1adminlicensesidactivations) in the administration API.
- [`POST /v1/activations/{id}/deactivate`](../reference/device-protocol.md#post-v1activationsiddeactivate) in the device protocol.
- [.NET API](../reference/dotnet-api.md#activationclient): `DeactivateAsync`, `CreateDeactivationProofAsync`, and `ForgetDeactivationProofAsync`.
