---
sidebar_position: 6
---

# Rotate the signing key

This guide is for a vendor who runs `elm-service` and replaces the key with which it signs license files: on a schedule, or before the key's certificate ends. If the key may have leaked, follow [Revoke a signing key](revoke-a-signing-key.md) instead: it replaces the key and revokes the old one in a single restart. The apps need no update, since they trust the root key and not the signing key, and the license files that the old key signed keep working. The guide starts where `elm-service` is running at `http://127.0.0.1:8080`, your terminal has one of its API keys in the environment variable `ELM_API_KEY`, the service has a product with a feature, and you have the root key that certified the service's signing key. [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) sets all of this up; [Replacing a signing key](../explanation/trust-chain.md#replacing-a-signing-key) explains why the old files keep working.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the root key `root.key.pem`, the root public key `root.pub.pem`, and the service's settings file `service.json`. The app below also uses the folder `packages` with the two ElasticLM packages, and the file `nuget.config` in the tutorial folder.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The examples use the values of the tutorial: the product `acme-cad`, its feature `pro`, and the service at `http://127.0.0.1:8080`. Replace them with your own. In the files below, `<product-id>` is the `id` of your product, which this command lists with the product's `code`:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

`elm` prints a page of products:

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}]}
```

In the outputs below, the key IDs, the IDs, the license key, and the times are fake. On your machine they are different every time. Here the device and the connected computer are the same machine.

## Prepare a device that the current key signed

To see that a device keeps working after the rotation, activate one now, while the service still signs with the current key. This section creates a license and activates a data folder by the offline exchange of [Activate a device without a network](activate-a-device-without-a-network.md), whose app this guide uses. If your devices are already activated, skip to [Create the new signing key](#create-the-new-signing-key).

Create a policy and a license with the feature `pro` under it. The policy has only a name, so it takes the defaults:

<!-- steps:file path=rotation-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, rotation check"}
```

<!-- steps:run capture=rotation-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body rotation-policy.json --url http://127.0.0.1:8080
```

The `id` at the start of the answer is the policy's ID, `<rotation-policy-id>` in the next file:

<!-- steps:file path=rotation-license.json -->
```json
{"policyId":"<rotation-policy-id>","name":"Rotation customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=rotation-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body rotation-license.json --url http://127.0.0.1:8080
```

The `key` at the end of the answer is the license key, `<rotation-license-key>` below.

If you made `offline-app` in [Activate a device without a network](activate-a-device-without-a-network.md#create-the-app), use it, and skip to the activation of `rotation-data` below: `dotnet new` refuses to create it again. Otherwise create the app, and add the `ElasticLM.Client` package to it from `packages`:

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

Activate the data folder `rotation-data` with the license key: make a request (`request` calls `CreateActivationRequestAsync`), hand it in with `elm offline process`, and import the answer (`import` calls `ImportResponseAsync`, then `Check`):

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem rotation-data request <rotation-license-key> rotation.elmreq
elm offline process --url http://127.0.0.1:8080 --in rotation.elmreq --out rotation.elmresp
dotnet run --project offline-app -- root.pub.pem rotation-data import rotation.elmresp
```

The app prints the request file, `elm offline process` the activation, the license, and the sequence number of the answer, and the app then the feature, the license's ID, the license file's sequence number, and when the license file expires:

```text
file: rotation.elmreq
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000303, seq 1: rotation.elmresp
activated: pro
license: 01960000-0000-7000-8000-000000000303
sequence: 1
expires: 2026-02-14 09:30:00Z
```

<!-- steps:expect -->
```text
activated: pro
sequence: 1
```

The current signing key signed this license file. Delete `rotation.elmreq` now that its answer is imported: a request file holds the license key.

## Create the new signing key

Create the new signing key on the machine that runs the service, with new file names, so that the files of the current key stay as they are until the service has restarted. Give it a new passphrase, which differs from the root key's and from that of any key that may have leaked; the tutorial used one passphrase for every key only to keep the trial short. First remove `ELM_KEY_PASSPHRASE` from the terminal, so that `elm` asks for the new passphrase instead of taking the one in the variable: in PowerShell, type `Remove-Item Env:ELM_KEY_PASSPHRASE`; in Bash or zsh, type `unset ELM_KEY_PASSPHRASE`. Then create the key:

<!-- steps:run -->
```shell
elm key signing create --out signing-2.key.pem --public-out signing-2.pub.pem
```

`elm` asks for a new passphrase twice and prints:

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S2
private key (encrypted): signing-2.key.pem
public key: signing-2.pub.pem
```

The service reads the passphrase of its signing key from `ELM_SIGNING_PASSPHRASE` in the terminal where it runs; you set it to the new passphrase before the restart below.

Certify the new key with the root key, for signing licenses and leases, the two uses the service needs. If your settings set `usageLog.directory`, the service also signs its usage log, and the key needs `--use license,lease,log`. Certify on the offline machine that holds the root key, as in [Keep the root key safe](keep-the-root-key-safe.md): carry the new public key `signing-2.pub.pem` there, run this command there, and carry back only the certificate `signing-2.cert`. Never bring the root key to the machine that runs the service. The public key and the certificate are not secrets:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2.cert
```

`elm` asks for the root key's passphrase and prints:

```text
Passphrase for 'root.key.pem':
certificate: signing-2.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S2
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license, lease
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

<!-- steps:expect -->
```text
certificate: signing-2.cert
uses: license, lease
```

`signing key ID:` is the new key's ID. The certificate is valid for one year, the default of `--days`.

## Restart the service with the new key

Change `signing` in `service.json` to the new key and its certificate. Keep every other setting as it is; with the settings of the tutorial, the file becomes:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing-2.key.pem",
    "certificates": ["signing-2.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

The service reads its signing key only when it starts, so restart it. In the second terminal, press Ctrl+C to stop the service. Read the new key's passphrase into `ELM_SIGNING_PASSPHRASE`, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service): in PowerShell, type `$env:ELM_SIGNING_PASSPHRASE = Read-Host -MaskInput`; in Bash or zsh, type `read -rs ELM_SIGNING_PASSPHRASE; export ELM_SIGNING_PASSPHRASE`; then type the passphrase and press Enter. Then start the service:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

While the service is stopped, an app that activates or refreshes online gets `ELM_NETWORK_ERROR`, and a client that refreshes on its own tries again an hour later; the license files that devices keep go on working. The line `service.started` of its log names the new key:

```text
info: elm-service[1]
      service.started: listening on http://127.0.0.1:8080/, database Sqlite, signing key FAKE-SIGNING-KEY-ID-000000000000000000000S2 (certificates until 01/15/2027 09:30:00 +00:00), in 179 ms
```

If the service does not start, it prints the reason. For example, these end with exit code 1:

| Last line | What happened | What to do |
| --- | --- | --- |
| `The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.` | `ELM_SIGNING_PASSPHRASE` holds another passphrase than the new key's, or `signing.key` names a file that is not a signing key. | Read the new key's passphrase into `ELM_SIGNING_PASSPHRASE`, and check `signing.key`. |
| `Set ELM_SIGNING_PASSPHRASE or signing.passphraseFile to the signing key's passphrase.` | `ELM_SIGNING_PASSPHRASE` is not set in the terminal of the service. | Read the new key's passphrase into `ELM_SIGNING_PASSPHRASE` as above. |
| `The signing key or a certificate file cannot be read: Could not find file '…'.` | A file in `signing` does not exist at that path. The message names the full path. | Correct the path; paths are relative to the folder you start the service in. |
| `No certificate that the trust store vouches for lets the signing key FAKE-SIGNING-KEY-ID-000000000000000000000S2 sign for License, Lease at 2026-01-15T09:30:00Z.` | No certificate in `signing.certificates` certifies this key for these uses now: it is the certificate of another key, it lacks a use (here both; `Lease` alone when only `--use lease` is missing), it is not valid yet or has ended, or its root key is not in `trust.rootKeys`. The same comes when the key's ID is in `trust.revokedKeyIds`. | Certify the key again as above, and check that `signing.key` and `signing.certificates` name the new key's files. |

A mistake in the settings file itself, such as a missing comma, ends with exit code 2 and a line that starts with `The settings file 'service.json' is not valid:`. [Command line and exit codes](../reference/service-configuration.md#command-line-and-exit-codes) lists the exit codes. Until you delete the old key below, you can go back to it: put the old `signing` settings back, set `ELM_SIGNING_PASSPHRASE` to its passphrase, and restart the service again.

## Check the key in use

Ask the service for the status of its signing key:

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/signing-key --url http://127.0.0.1:8080
```

`elm` prints:

```text
{"keyId":"FAKE-SIGNING-KEY-ID-000000000000000000000S2","uses":["license","lease"],"expiresAt":"2027-01-15T09:30:00Z","expiresSoon":false}
```

Compare `keyId` with the `signing key ID:` that `elm key certify` printed for the new key: they are the same. `expiresAt` is the end of the new certificate. From now on the service signs every new license file with the new key.

## Check that devices keep working

A license file that the old key signed carries the old key's certificate, so apps keep accepting it, also after that certificate ends. A device moves to the new key at its next refresh: a device that refreshes online on its own, and a device without a network when its refresh files are carried, as here. Refresh the device of `rotation-data`: make the refresh request (`refresh` calls `CreateRefreshRequestAsync`), hand it in, and import the answer:

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem rotation-data refresh rotation-refresh.elmreq
elm offline process --url http://127.0.0.1:8080 --in rotation-refresh.elmreq --out rotation-refresh.elmresp
dotnet run --project offline-app -- root.pub.pem rotation-data import rotation-refresh.elmresp
```

The app prints the next sequence number and a new expiry:

```text
file: rotation-refresh.elmreq
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000303, seq 2: rotation-refresh.elmresp
activated: pro
license: 01960000-0000-7000-8000-000000000303
sequence: 2
expires: 2026-02-14 09:31:00Z
```

<!-- steps:expect -->
```text
activated: pro
sequence: 2
```

The new key signed this license file, and the app accepted it with the same trust store: the root key certified both keys. Delete `rotation-refresh.elmreq` now that its answer is imported.

## Take the old key out of use

The service does not read `signing.key.pem` any more. Once the new key works:

- Keep the old public key `signing.pub.pem`, or record its key ID, which `elm key thumbprint signing.pub.pem` prints: you need the ID if you ever revoke the key.
- Keep the old certificate out of `signing.certificates`, which names only the new key's certificate now. The license files that the old key signed carry their own copy of it.
- Delete the old private key `signing.key.pem`, and every copy of it, so that it can neither leak nor be given to the service again by mistake.

Do not revoke the old key unless it may have leaked: an app whose trust store lists a key ID as revoked refuses every license file that key signed, also those of devices that have not refreshed yet. If it may have leaked, follow [Revoke a signing key](revoke-a-signing-key.md).

## Renew the certificate of the same key

A signing key's certificate ends after its period, one year by default; after that, the service cannot issue a license file, and the request fails with `500` and `ELM_INTERNAL_ERROR`. To keep the same key, certify its public key again before then, such as with `elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2-renewed.cert`, add the new file to `signing.certificates` (`["signing-2.cert", "signing-2-renewed.cert"]`; 1 to 4 files), and restart the service before the old certificate ends. `GET /v1/admin/signing-key` answers `expiresSoon` `true` when fewer than 30 days remain.

A certificate whose start, set with `--not-before`, is still in the future can stand in `signing.certificates` ahead of time, but the service counts it only when it starts after that time. So restart the service again after the new certificate's start, and before the old one ends.

## See also

- [Rotating keys](../explanation/trust-chain.md#rotating-keys): renewing a certificate, replacing a signing key, and replacing a root key.
- [`signing` and `trust`](../reference/service-configuration.md#signing) in the `elm-service` configuration, and its [exit codes](../reference/service-configuration.md#command-line-and-exit-codes).
- [`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key) in the administration API.
- [`elm key signing create`](../reference/elm-cli.md#elm-key-signing-create) and [`elm key certify`](../reference/elm-cli.md#elm-key-certify): every option.
