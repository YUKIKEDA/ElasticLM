---
sidebar_position: 2
---

# Run the hosted activation service and activate a .NET app online

In this tutorial you run `elm-service`, the hosted activation service that the vendor runs, create a product with a trial and a license in it through its administration API, and write a small .NET console app that starts a trial on your machine and then activates with the license's key. Activation binds a license to one device, which then keeps a license file from the service; [Activation](../explanation/activation.md) explains it. The tutorial is for anyone new to ElasticLM who can use a terminal and the `dotnet` command, and it stands alone: you do not need to have done another tutorial first.

## Before you start

You need:

- The .NET 10 SDK. `dotnet --list-sdks` must list a version that starts with `10.`.
- The port 8080 free on your machine: the service listens on it.
- The [GitHub CLI](https://cli.github.com/), `gh`, to check the files you download; nothing else in this tutorial uses it.
- On Windows, PowerShell 7.1 or later, which the tutorial uses to read secret values without showing them. `$PSVersionTable.PSVersion` shows the version.

Create an empty folder for this tutorial. Open a terminal in that folder and type every command of this page there.

You download the `elm` tool, `elm-service`, and the `ElasticLM.Licensing` and `ElasticLM.Client` packages from a GitHub prerelease. Open the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases) and take the newest release marked "Pre-release". The release that GitHub marks "Latest" is never a prerelease, so do not go by that mark.

From the release's Assets, save these files in the tutorial folder:

- The archives `elm-<version>-<RID>` and `elm-service-<version>-<RID>` for your system, where `<RID>` is one of `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`. They are `.zip` files for Windows and `.tar.gz` files for the others. Take the archives whose names do not end in `-symbols`: those hold only debug symbols.
- `ElasticLM.Client.<version>.nupkg` and `ElasticLM.Licensing.<version>.nupkg`. Your app uses `ElasticLM.Client`, which depends on `ElasticLM.Licensing`, so you need both. You do not need the `.snupkg` files of the same names, which hold the packages' debug symbols.
- `SHA256SUMS`, the list of the files' hashes.

Before you extract or use anything, check the archives and the packages in the tutorial folder as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`. That section also says how to run a downloaded program on macOS, and which Linux and macOS versions the programs need.

Extract both archives into a folder named `tools`. In PowerShell on Windows, type `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` and then `Expand-Archive elm-service-<version>-<RID>.zip -DestinationPath tools -Force`: each archive holds the same `LICENSE` file, and `-Force` lets the second one overwrite it. On Linux and macOS, type `mkdir -p tools`, then `tar -xzf elm-<version>-<RID>.tar.gz -C tools` and `tar -xzf elm-service-<version>-<RID>.tar.gz -C tools`. `tools` then holds `elm` and `elm-service` (`elm.exe` and `elm-service.exe` on Windows): `elm` is the command-line tool that creates keys and calls the service's administration API, and `elm-service` is the service.

<!-- steps:prebuilt name=elm -->
<!-- steps:prebuilt name=elm-service -->

Add the `tools` folder to your `PATH` in this terminal, so that the commands `elm` and `elm-service` run the programs in it. In PowerShell on Windows, type `$env:PATH = "$PWD/tools;$env:PATH"`; in Bash or zsh, type `export PATH="$PWD/tools:$PATH"`.

Move the two packages into a folder named `packages`. In PowerShell, type `New-Item -ItemType Directory packages` and then `Move-Item ElasticLM.*.nupkg packages`; in Bash or zsh, type `mkdir -p packages` and then `mv ElasticLM.*.nupkg packages/`.

<!-- steps:prebuilt name=packages -->

Create a file named `nuget.config` in the tutorial folder with this content. It makes your app take the packages whose names start with `ElasticLM.` from `packages` only, and every other package from nuget.org. The path `packages` is relative to the folder of `nuget.config`. The ElasticLM packages you use come from the GitHub prerelease, and a package of the same name on nuget.org would not be ElasticLM's; the mapping makes sure such a package is never used.

<!-- steps:file path=nuget.config -->
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packages" value="packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packages">
      <package pattern="ElasticLM.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## Create the keys

You need two keys: a root key, which your app trusts, and a signing key, with which the service signs the license files it issues. The root key certifies the signing key, so that your app accepts what the signing key signs.

`elm` keeps each private key in a file encrypted with a passphrase. When it creates a key, it asks for a new passphrase, at least 12 characters long, and asks you to repeat it. When a later command uses the key, it asks for that passphrase again. Nothing is shown while you type it. Use the same passphrase for both keys in this tutorial. If the environment variable `ELM_KEY_PASSPHRASE` is set, `elm` takes the passphrase from it and does not ask, so the lines that ask for it in the outputs below do not appear. To set it in this terminal, type `$env:ELM_KEY_PASSPHRASE = '<passphrase>'` in PowerShell, or `export ELM_KEY_PASSPHRASE='<passphrase>'` in Bash or zsh. This is for scripts and terminals where nobody can type; in real use, do not keep a passphrase in a script. See [Passphrases](../reference/elm-cli.md#passphrases) in the `elm` reference.

In the outputs below, the key IDs, the API key, the IDs of what you create, the license key, and the times are fake. On your machine they are different every time.

Create the root key:

<!-- steps:run -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
```

`elm` prints:

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
private key (encrypted): root.key.pem
public key: root.pub.pem
```

Create the signing key:

<!-- steps:run -->
```shell
elm key signing create --out signing.key.pem --public-out signing.pub.pem
```

`elm` prints:

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
private key (encrypted): signing.key.pem
public key: signing.pub.pem
```

Certify the signing key with the root key, for signing licenses and leases. The service needs both uses:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license,lease --out signing.cert
```

`elm` prints:

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license, lease
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` is the key certificate. It is valid for one year: during that time the signing key may sign new licenses. The dates do not limit how long the licenses it signed stay valid; [Key certificates](../explanation/trust-chain.md#key-certificates) explains this under "Validity and the issue time".

## Set up the service

Each request that an app sends to the service carries a nonce, a one-time value that the service gave out shortly before, so that a copied request cannot be sent again; [Devices and proofs](../explanation/devices-and-proofs.md) explains it. The service makes and checks its nonces with a secret. Create a file with a new random secret:

<!-- steps:run -->
```shell
elm secret create --out nonce.secret
```

`elm` prints:

```text
secret file: nonce.secret
```

The service keeps its data in a database. This tutorial uses SQLite, a database in one file, `service.db`, which the service creates. Create a file named `database.connection` in the tutorial folder with the database's connection string:

<!-- steps:file path=database.connection -->
```text
Data Source=service.db
```

Create a file named `service.json` in the tutorial folder with this content. These are the service's settings:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
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

- `database` names the database: SQLite, with the connection string in `database.connection`.
- `signing` names the signing key and its certificate, with which the service signs license files.
- `trust.rootKeys` lists the root public keys that the signing key's certificate is checked against.
- `nonce.secretFile` names the secret you created above.
- `bootstrapOwner` names the first administrator of the service. Without a sign-in with an identity provider (OIDC), as here, it only names the owner of the API key that you make below: nobody signs in with it. If you add OIDC later, `iss` must be the same as the setting `oidc.issuer`, or the service does not start.

The paths are relative to the folder you start the service in. The connection string, the nonce secret, and the signing key's passphrase are secrets: the settings name files or variables that hold them, never the values. The service listens on `http://127.0.0.1:8080`, the default, which needs no setting. In production, run it with https instead; [elm-service configuration](../reference/service-configuration.md) lists `tls` and every other setting.

Prepare the database and make the first API key, the key with which `elm` calls the administration API:

<!-- steps:run capture=api-key env=ELM_API_KEY -->
```shell
elm-service setup --config service.json
```

`elm-service setup` creates the database's tables, prints the new API key on the first line, and describes it on the second:

```text
elm_FAKEapiKEY000000000000000000000000000000000
API key 01960000-0000-7000-8000-000000000001 (elm_FAKE…) for https://idp.example tutorial, owner permissions, expires 2026-04-15T09:30:00Z.
```

`elm admin request` reads the API key from the environment variable `ELM_API_KEY`. Copy the key from the first line, and read it into the variable in this terminal, so that it is not shown again and does not stay in the shell's history. In PowerShell, type `$env:ELM_API_KEY = Read-Host -MaskInput`, paste the key, and press Enter. In Bash or zsh, type `read -rs ELM_API_KEY; export ELM_API_KEY`, paste the key, and press Enter. Nothing is shown while you paste.

## Start the service

Open a second terminal in the tutorial folder, and add the `tools` folder to its `PATH` too, the same way as in the first terminal: each terminal has its own `PATH`.

The service reads the signing key's passphrase from the environment variable `ELM_SIGNING_PASSPHRASE`. Read it into the variable in the second terminal the same way: in PowerShell, type `$env:ELM_SIGNING_PASSPHRASE = Read-Host -MaskInput`; in Bash or zsh, type `read -rs ELM_SIGNING_PASSPHRASE; export ELM_SIGNING_PASSPHRASE`. Then type the passphrase of `signing.key.pem` and press Enter.

Start the service in the second terminal:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

The service keeps running and prints its log. Each entry is a line that starts with `info:` and names where the entry comes from, followed by the message, indented. The first entries come from the web host the service runs in: the address it listens on, `Application started. Press Ctrl+C to shut down.`, the hosting environment, and a `Content root path`, which is the folder of the `elm-service` executable and has nothing to do with the paths in `service.json`. The line to look for is `service.started`, which says that the service started:

```text
info: elm-service[1]
      service.started: listening on http://127.0.0.1:8080/, database Sqlite, signing key FAKE-SIGNING-KEY-ID-000000000000000000000S1 (certificates until 01/15/2027 09:30:00 +00:00), in 223 ms
```

Leave the service running, and go back to the first terminal for the rest of the tutorial. Type `127.0.0.1` in the URLs below, not `localhost`: an app's request names the URL it was sent to, and the service accepts only its own address, `http://127.0.0.1:8080`, and refuses a request sent to `http://localhost:8080` with `ELM_PROOF_INVALID`.

## Create a product and a license

You create the product, its feature, its policies, and a license with `elm admin request`, which sends one request to the administration API. Each body is a JSON file that you write first. Each answer is the object that the request created or changed, as one line of JSON that starts with its `id`. In the files below, replace each `<name>`, such as `<product-id>`, with the value in the answer of an earlier step. [Administration API](../reference/administration-api.md) lists every route and member.

Create the product `acme-cad`. Its `code` is the product ID that the app uses:

<!-- steps:file path=product.json -->
```json
{"code":"acme-cad","name":"ACME CAD"}
```

<!-- steps:run capture=product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8080
```

`elm` prints the product:

```text
{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}
```

Its `id` is the product's ID, `<product-id>` below. Add the feature `pro` to the product, with the product's ID in the path:

<!-- steps:file path=feature.json -->
```json
{"code":"pro","name":"Pro"}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/products/<product-id>/features --body feature.json --url http://127.0.0.1:8080
```

A policy holds the rules of the licenses made under it. Create the trial policy: `licenseKind` `trial` makes it a policy for trials, and a trial has no grace period after its end, so `gracePeriod` is 0. A trial lasts 14 days, the default:

<!-- steps:file path=trial-policy.json -->
```json
{"productId":"<product-id>","name":"Trial","licenseKind":"trial","gracePeriod":0}
```

<!-- steps:run capture=trial-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body trial-policy.json --url http://127.0.0.1:8080
```

`elm` prints the policy with every setting, the defaults included:

```text
{"id":"01960000-0000-7000-8000-000000000201","productId":"01960000-0000-7000-8000-000000000101","name":"Trial","licenseKind":"trial","binding":"nodeLocked","maxActivations":1,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":null,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":0,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":1209600,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

Its `id` is `<trial-policy-id>` below. The product offers a trial only once it names a trial policy, so set the product's `trialPolicyId`:

<!-- steps:file path=product-trial.json -->
```json
{"trialPolicyId":"<trial-policy-id>"}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body product-trial.json --url http://127.0.0.1:8080
```

Create the policy of the license you sell. With only a name, it takes the defaults: a license under it is bound to one device and has no expiry.

<!-- steps:file path=policy.json -->
```json
{"productId":"<product-id>","name":"Standard"}
```

<!-- steps:run capture=policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body policy.json --url http://127.0.0.1:8080
```

Its `id` is `<policy-id>` below. Create a license with the feature `pro` under that policy:

<!-- steps:file path=license.json -->
```json
{"policyId":"<policy-id>","name":"First customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body license.json --url http://127.0.0.1:8080
```

`elm` prints the license:

```text
{"id":"01960000-0000-7000-8000-000000000301","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000202","name":"First customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00000"}
```

The `key` at the end is the license key, `<license-key>` below: the vendor gives it to the customer, who types it into the app. The service shows it only in this answer and keeps only its hash, so copy it now.

## Create the app

Create a console app in the folder `app`, and add the `ElasticLM.Client` package to it from `packages`. `ElasticLM.Licensing` comes with it. `--prerelease` lets `dotnet add` take a prerelease version, the only kind in `packages`, and `--source packages` makes it look for the version in `packages` only:

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Client --prerelease --source packages
```

Replace the content of `app/Program.cs` with this code:

<!-- sample: samples/ActivateOnline/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
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
    // A file that cannot be read, a second argument that is not a public key in PEM, or a first argument that is not an
    // https URL, or an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// The service or the check refused: print the error code and the reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
```

The app reads the root public key into a trust store, the set of root keys it trusts, and creates an `ActivationClient` for the product `acme-cad` and the service at the URL of the first argument. The client keeps its files in the data folder of the third argument: its own installation key, which identifies this copy of the app to the service, and the license file that the service issues to it.

Without a license key, the app calls `StartTrialAsync`, which asks the service for a trial; with one, it calls `ActivateAsync`. The service answers with a license file bound to the installation key and to the machine's fingerprints, which the client checks against the root public key and keeps. `ActivationResult` is either `Activated` or `Failed` with the error. The app then calls `Check`, which checks the kept file offline for the feature `pro`, as a real app does each time it starts, and prints the feature and either the trial's expiry or the license's ID. While an app runs, the client refreshes the license file before it expires; `await using` stops that when the app ends, and the activation stays in the data folder. This app reads the service URL and the root public key from the command line to keep the tutorial short; a real app embeds both in its code.

## Start a trial and activate

Start a trial with the data folder `client-data`, which the client creates:

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data
```

The first `dotnet run` builds the app, which takes some seconds with no output. Then the app prints two lines. The first one is:

<!-- steps:expect -->
```text
trial: pro
```

The second line is the end of the trial, 14 days from now, in UTC, such as `expires: 2026-01-29 09:30:00Z`. The service gives one trial of a product to each machine: it records the machine's fingerprints with the trial.

Now activate with the license key, in the same data folder. Replace `<license-key>` with the key from the license's answer:

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data <license-key>
```

The app prints two lines. The first one is:

<!-- steps:expect -->
```text
activated: pro
```

The second line is the license's ID, the `id` of the license's answer, such as `license: 01960000-0000-7000-8000-000000000301`. The activation replaced the trial in `client-data`. The order matters: the client starts a trial only when it keeps no activation, while an activation replaces a kept trial.

## When the service refuses

When the service or the check refuses, the app prints an error code and the reason, and ends with exit code 1. Each command in this section ends that way on purpose. They use a second data folder, `other-data`, which the client creates as it does `client-data`: to the service, it is another installation of the app on the same machine.

Start a trial with `other-data`:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_USED: A fingerprint of this installation already had a trial of the product.
```

Activate again with `client-data`:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data <license-key>
```

<!-- steps:expect -->
```text
ELM_INVALID_STATE: An activation is kept; deactivate it before activating another license.
```

The client keeps the activation of `client-data`, and refuses before it sends anything to the service.

Activate `other-data` with a key that has a wrong character. A key the service never issued, such as this one, gives the same answer:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data ELM-ABCDE-FGHJK-MNPQR-STVWX
```

<!-- steps:expect -->
```text
ELM_LICENSE_NOT_FOUND: No license has this key.
```

Letter case and the `-` signs do not matter in a license key: `elm-abcde-fghjk-mnpqr-stvwx` is the same key as the one above.

Send a request to a port where the service does not listen:

<!-- steps:run capture=network-error pattern="(ELM_NETWORK_ERROR):" exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8081 root.pub.pem other-data
```

The app prints a line that starts with `ELM_NETWORK_ERROR: The service cannot be reached:`, followed by the reason that the operating system gave, in its own words.

Take the trial policy off the product, and start a trial with `other-data` again:

<!-- steps:file path=product-no-trial.json -->
```json
{"trialPolicyId":null}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body product-no-trial.json --url http://127.0.0.1:8080
```

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_NOT_OFFERED: The product does not offer a trial.
```

The service refuses before it looks at the machine's fingerprints, so this code comes even though the machine had a trial.

| Code | What happened | What to do |
| --- | --- | --- |
| `ELM_TRIAL_USED` | The machine already had a trial of the product, from another data folder. | A machine gets one trial; activate with a license key. |
| `ELM_INVALID_STATE` | The data folder keeps an activation, so the client does not activate again. | Nothing: the app is activated, and `Check` checks it at each start. |
| `ELM_LICENSE_NOT_FOUND` | The service has no license with this key. | Copy the key again from the license's answer. |
| `ELM_NETWORK_ERROR` | The service cannot be reached: it is not running, or the URL is wrong. The message starts with `The service cannot be reached:`. | Start the service as in [Start the service](#start-the-service), and pass `http://127.0.0.1:8080`. |
| `ELM_TRIAL_NOT_OFFERED` | The product names no trial policy. | Set the product's `trialPolicyId` as in [Create a product and a license](#create-a-product-and-a-license). |

A trial also needs at least one fingerprint of the machine: where none can be read, such as in a Linux container without `/etc/machine-id`, the service refuses the trial with `ELM_FINGERPRINT_UNAVAILABLE`. If the app prints `usage: app <service-url> <root-public-key.pem> <data-folder> [license-key]`, it got neither three nor four arguments. If it prints a line that starts with `error: A root key is not a P-256 public key in PEM.`, the second argument is not a public key file, and `error: Invalid URI` means that the first argument is not a URL: check the order of the arguments. [Error codes](../reference/error-codes.md) lists every code.

## Stop the service

Press Ctrl+C in the second terminal to stop the service. Then close both terminals: that removes `ELM_API_KEY` and `ELM_SIGNING_PASSPHRASE`, which live only in them.

To start again from the beginning, stop the service and delete `service.db` with the files beside it whose names start with `service.db`, and the folders `client-data` and `other-data`: the service then forgets the trial and the activation, and the client forgets its activation and its key. Then follow this page again from the `elm-service setup` command in [Set up the service](#set-up-the-service), in new terminals.

## What you made

You created a root key and a signing key, set up `elm-service` with a product, a trial policy, and a license, and wrote a .NET app that started a trial on your machine and then activated with the license key. In real use, the vendor runs the service with https, and a sales or billing system creates the licenses through the administration API.

To learn more:

- [Activation](../explanation/activation.md): products, policies, licenses, refresh, deactivation, and trials.
- [elm-service configuration](../reference/service-configuration.md): every setting of the service, and its secrets.
- [Administration API](../reference/administration-api.md): every route, with its request and answer.
- [elm command reference](../reference/elm-cli.md): `elm secret create`, `elm admin request`, and the other commands.
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient`, its options, and what each method returns.
