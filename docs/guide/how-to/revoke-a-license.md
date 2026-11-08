---
sidebar_position: 7
---

# Revoke a license

This guide is for a vendor who runs `elm-service` and ends a license for good: a contract ended, a payment was refunded, or a license key was shared with people who did not buy it. A revoked license gets no new license file and no lease, and it cannot be resumed; to stop a license for a while instead, suspend it with [`POST /v1/admin/licenses/{id}/suspend`](../reference/administration-api.md#post-v1adminlicensesidsuspend). Revocation reaches a device at its next activation or refresh: the license file that a device already keeps stays valid until it expires. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, and the service has a product with a feature. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [License states](../explanation/activation.md#license-states) explains what each state allows.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The app below also uses the root public key `root.pub.pem`, the folder `packages` with the two ElasticLM packages, and the file `nuget.config` in the tutorial folder.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the files below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

In the outputs below, the IDs, the license key, and the times are fake. On your machine they are different every time. Here the device and the connected computer are the same machine.

## Prepare a license to revoke

To see what revocation does, this section creates a license and activates a device with it, by the offline exchange of [Activate a device without a network](activate-a-device-without-a-network.md), whose app this guide uses. To revoke a license you already have, find its `id` with [`GET /v1/admin/licenses`](../reference/administration-api.md#get-v1adminlicenses), and skip to [Revoke the license](#revoke-the-license).

Create a policy and a license with the feature `pro` under it. The policy has only a name, so it takes the defaults:

<!-- steps:file path=revoke-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, revocation check"}
```

<!-- steps:run capture=revoke-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body revoke-policy.json --url http://127.0.0.1:8080
```

<!-- steps:file path=revoke-license.json -->
```json
{"policyId":"<revoke-policy-id>","name":"Former customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=revoke-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body revoke-license.json --url http://127.0.0.1:8080
```

The `key` at the end of the answer is the license key, `<revoke-license-key>` below.

If you made `offline-app` in [Activate a device without a network](activate-a-device-without-a-network.md#create-the-app), use it, and skip to the activation of `customer-data` below: `dotnet new` refuses to create it again. Otherwise create the app, and add the `ElasticLM.Client` package to it from `packages`:

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder; `dotnet nuget locals global-packages --list` shows where it is, also when the environment variable `NUGET_PACKAGES` moves it. That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `offline-app/Program.cs` with this code, the same as in [Activate a device without a network](activate-a-device-without-a-network.md#create-the-app):

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

Activate the data folder `customer-data` with the license key: make a request (`request` calls `CreateActivationRequestAsync`), hand it in with `elm offline process`, and import the answer (`import` calls `ImportResponseAsync`, then `Check`). Note the license's ID, which the app prints:

<!-- steps:run capture=revoke-license-id pattern="license: ([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
dotnet run --project offline-app -- root.pub.pem customer-data request <revoke-license-key> customer.elmreq
elm offline process --url http://127.0.0.1:8080 --in customer.elmreq --out customer.elmresp
dotnet run --project offline-app -- root.pub.pem customer-data import customer.elmresp
```

The app prints:

```text
activated: pro
license: 01960000-0000-7000-8000-000000000304
sequence: 1
expires: 2026-02-14 09:30:00Z
```

`<revoke-license-id>` below is that ID, the `id` of the license.

## Revoke the license

Revoke the license by its ID. The request has no body:

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses/<revoke-license-id>/revoke --url http://127.0.0.1:8080
```

`elm` prints the license, now with `state` `revoked` and the time in `revokedAt`:

```text
{"id":"01960000-0000-7000-8000-000000000304","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Former customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"revoked","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":"2026-01-15T09:31:00Z","meta":{},"deactivations":0}
```

Revocation cannot be undone: a resume or an extension of the license is refused with `409` and `ELM_CONFLICT`, and revoking it again changes nothing. The admin UI's page of a license revokes it too.

## Check that devices are refused

The service refuses every new activation with the license, before it counts the devices. Make an activation request in a new data folder, `new-device-data`, as another device would, and hand it in:

<!-- steps:run capture=activation-refused stream=stderr pattern="(ELM_LICENSE_REVOKED):" exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem new-device-data request <revoke-license-key> new-device.elmreq
elm offline process --url http://127.0.0.1:8080 --in new-device.elmreq --out new-device.elmresp
```

The app makes the request file without asking the service, and `elm offline process` prints the service's refusal and ends with exit code 1:

```text
error: ELM_LICENSE_REVOKED: The license is revoked.
```

The service refuses the refresh of the activated device the same way. Make the refresh request of `customer-data` (`refresh` calls `CreateRefreshRequestAsync`), and hand it in:

<!-- steps:run capture=refresh-refused stream=stderr pattern="(ELM_LICENSE_REVOKED):" exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem customer-data refresh customer-refresh.elmreq
elm offline process --url http://127.0.0.1:8080 --in customer-refresh.elmreq --out customer-refresh.elmresp
```

```text
error: ELM_LICENSE_REVOKED: The license is revoked.
```

An app that activates or refreshes online gets the same code from `ActivateAsync` or `RefreshAsync`, and a client that refreshes on its own goes to the state `Refused`.

## What revocation does not reach

- **The license file on the device.** The device keeps the license file it has, and `Check` accepts it until it expires, and for its grace period after: with the defaults, up to 30 days and 7 more. The device is not told, since there is no revocation list that it fetches. To make revocation reach devices sooner, give licenses a policy with a shorter `tokenLifetime` and `gracePeriod` before you sell them, as in [Set up a policy](set-up-a-policy.md). [Security model](../explanation/security-model.md#on-the-licensees-machine) explains this limit.
- **License files issued by hand.** A license file that `elm license issue` signed, or an unbound license file that the service issued with [`POST /v1/admin/licenses/{id}/file`](../reference/administration-api.md#post-v1adminlicensesidfile), stays valid until its own expiry. A file without an expiry can be stopped only by revoking the key that signed it, which stops every other file of that key too; see [Revoke a signing key](revoke-a-signing-key.md).

| Code | What happened | What to do |
| --- | --- | --- |
| `ELM_LICENSE_REVOKED` | The license is revoked. The service refuses its activations, refreshes, and leases. | Nothing for the device; the customer needs another license. |
| `ELM_LICENSE_NOT_FOUND` | `elm admin request` got this with `404`: no license has the ID in the path. | Find the license's `id` with `GET /v1/admin/licenses`. |

[Error codes](../reference/error-codes.md) lists every code.

## See also

- [License states](../explanation/activation.md#license-states): active, suspended, expired, and revoked.
- [`POST /v1/admin/licenses/{id}/revoke`](../reference/administration-api.md#post-v1adminlicensesidrevoke) and [`POST /v1/admin/licenses/{id}/suspend`](../reference/administration-api.md#post-v1adminlicensesidsuspend) in the administration API.
- [Refresh](../explanation/activation.md#refresh): where a revocation reaches a device.
