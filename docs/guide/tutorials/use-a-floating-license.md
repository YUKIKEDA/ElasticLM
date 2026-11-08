---
sidebar_position: 3
---

# Run a license server and lease a floating license in a .NET app

In this tutorial you run `elm-server`, the license server that a customer runs on its own network, give it a server license with two seats of a feature, and write a small .NET console app that leases a seat, returns it, and borrows a seat for an hour. A floating license limits how many copies of an app run at the same time rather than which machines run it; [Floating licenses](../explanation/floating-licenses.md) explains it. The tutorial is for anyone new to ElasticLM who can use a terminal and the `dotnet` command, and it stands alone: you do not need to have done another tutorial first.

## Before you start

You need:

- The .NET 10 SDK. `dotnet --list-sdks` must list a version that starts with `10.`.
- The ports 7070 and 7071 free on your machine: the server listens on both. In PowerShell, `Get-NetTCPConnection -LocalPort 7070,7071 -ErrorAction SilentlyContinue` prints nothing when both are free; on Linux and macOS, `lsof -i :7070 -i :7071` prints nothing.
- The [GitHub CLI](https://cli.github.com/), `gh`, to check the files you download; nothing else in this tutorial uses it.

Create an empty folder for this tutorial. Open a terminal in that folder and type every command of this page there.

On Linux and macOS, the folder must not be writable by your group or by others, because the server keeps its private key in a folder inside it. `chmod go-w .` in the folder makes sure of that.

You download the `elm` tool, `elm-server`, and the `ElasticLM.Licensing` and `ElasticLM.Client` packages from a GitHub prerelease. Open the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases) and take the newest release marked "Pre-release". The release that GitHub marks "Latest" is never a prerelease, so do not go by that mark.

From the release's Assets, save these files in the tutorial folder:

- The archives `elm-<version>-<RID>` and `elm-server-<version>-<RID>` for your system, where `<RID>` is one of `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`. They are `.zip` files for Windows and `.tar.gz` files for the others. Take the archives whose names do not end in `-symbols`: those hold only debug symbols.
- `ElasticLM.Client.<version>.nupkg` and `ElasticLM.Licensing.<version>.nupkg`. Your app uses `ElasticLM.Client`, which depends on `ElasticLM.Licensing`, so you need both. You do not need the `.snupkg` files of the same names, which hold the packages' debug symbols.
- `SHA256SUMS`, the list of the files' hashes.

Before you extract or use anything, check the archives and the packages in the tutorial folder as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`. That section also says how to run a downloaded program on macOS, and which Linux and macOS versions the programs need.

Extract both archives into a folder named `tools`. In PowerShell on Windows, type `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` and then `Expand-Archive elm-server-<version>-<RID>.zip -DestinationPath tools -Force`: each archive holds the same `LICENSE` file, and `-Force` lets the second one overwrite it. On Linux and macOS, type `mkdir -p tools`, then `tar -xzf elm-<version>-<RID>.tar.gz -C tools` and `tar -xzf elm-server-<version>-<RID>.tar.gz -C tools`. `tools` then holds `elm` and `elm-server` (`elm.exe` and `elm-server.exe` on Windows): `elm` is the command-line tool that creates keys and issues licenses, and `elm-server` is the license server.

<!-- steps:prebuilt name=elm -->
<!-- steps:prebuilt name=elm-server -->

Add the `tools` folder to your `PATH` in this terminal, so that the commands `elm` and `elm-server` run the programs in it. In PowerShell on Windows, type `$env:PATH = "$PWD/tools;$env:PATH"`; in Bash or zsh, type `export PATH="$PWD/tools:$PATH"`.

Move the two packages into a folder named `packages`. In PowerShell, type `New-Item -ItemType Directory packages | Out-Null` and then `Move-Item ElasticLM.*.nupkg packages`; in Bash or zsh, type `mkdir -p packages` and then `mv ElasticLM.*.nupkg packages/`.

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

You need two keys: a root key, which your app and the server trust, and a signing key, which signs licenses. The root key certifies the signing key, so that what the signing key signs is accepted.

`elm` keeps each private key in a file encrypted with a passphrase. When it creates a key, it asks for a new passphrase, at least 12 characters long, and asks you to repeat it. When a later command uses the key, it asks for that passphrase again. Nothing is shown while you type it. Use the same passphrase for both keys in this tutorial. If the environment variable `ELM_KEY_PASSPHRASE` is set, `elm` takes the passphrase from it and does not ask, so the lines that ask for it in the outputs below do not appear. To set it in this terminal, type `$env:ELM_KEY_PASSPHRASE = '<passphrase>'` in PowerShell, or `export ELM_KEY_PASSPHRASE='<passphrase>'` in Bash or zsh. This is for scripts and terminals where nobody can type; in real use, do not keep a passphrase in a script. See [Passphrases](../reference/elm-cli.md#passphrases) in the `elm` reference.

In the outputs below, the key IDs, the server's installation key, the token ID, the fingerprints, and the times are fake. On your machine they are different every time.

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

Certify the signing key with the root key, for signing licenses:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
```

`elm` prints:

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` is the key certificate. It is valid for one year: during that time the signing key may sign new licenses. The dates do not limit how long the licenses it signed stay valid, so a license may expire much later; [Key certificates](../explanation/trust-chain.md#key-certificates) explains this under "Validity and the issue time".

## Set up the server

Create a file named `server.json` in the tutorial folder with this content. These are the server's settings:

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  }
}
```

- `product` is the product the server lends seats of, `acme-cad`.
- `listen` is where apps reach the server: port 7070, on your own machine only.
- `license` is the server license file, which you issue below.
- `dataDirectory` is the folder where the server keeps its own key and its records.
- `trust.rootKeys` lists the root public keys the server accepts a server license from.

The paths are relative to the folder you start the server in. The server also listens on `http://127.0.0.1:7071` for the `elm server` commands, which is the default and needs no setting. [elm-server configuration](../reference/server-configuration.md) lists every setting.

A server license is bound to one server, through the server's installation key, a key pair that the server makes the first time it starts. Start the server once to make the key and to see its thumbprint, the short name of the key:

<!-- steps:run capture=server-key stream=stderr exit=1 pattern="installation key ([A-Za-z0-9_-]{43})" -->
```shell
elm-server --config server.json
```

The server stops at once with exit code 1, because it has no server license yet, and prints this error. That is expected:

```text
error: The server license 'server.elmlic' does not exist. Ask the vendor for a server license bound to this server: installation key FAKE-SERVER-KEY-00000000000000000000000000K, fingerprints 00000000000000000000000000000001, 00000000000000000000000000000002. Or activate one with elm server activate, or elm server activate-request offline.
```

The value after `installation key` is the thumbprint: 43 characters, which may include `-` and `_`. The key itself stays in `server-data`. The values after `fingerprints` are the machine fingerprints of your machine: a license issued with `--fingerprint` would be bound to the machine as well, and this tutorial does not use them. In real use, the customer's administrator sends this thumbprint to the vendor, who issues the server license.

On Linux and macOS, if the tutorial folder is writable by your group or by others, the server instead prints `error: The folder '…/server-data' is not safe for secrets: its parent folder '…' is writable by group or others.`, followed by one more sentence, and also ends with exit code 1. Type `chmod go-w .` in the tutorial folder, then the same `elm-server` command again. You do not need to delete `server-data` first.

Issue the server license: two seats of the feature `pro`, bound to the server's key. Replace `<server-key>` with the thumbprint that the server printed, all 43 characters exactly:

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id floating-server --kind server --feature "pro;seats=2" --expires 2099-12-31T00:00:00Z --bind-key <server-key> --lease-minutes 1 --borrow-days 1 --trust root.pub.pem --out server.elmlic
```

`elm` prints:

```text
Passphrase for 'signing.key.pem':
license file: server.elmlic
license ID: floating-server
token ID: FAKEjti000000000000002
```

- `--kind server` makes a server license, which only an `elm-server` uses.
- `--feature "pro;seats=2"` grants the feature `pro` with two seats: at most two copies of the app can hold it at the same time.
- `--expires` sets when the license expires: 00:00 UTC on 31 December 2099. The key certificate's one year does not limit it, as explained above.
- `--bind-key` binds the license to the server's installation key, so it is valid on that server only.
- `--lease-minutes 1` makes each lease last one minute. The default is 10 minutes; this tutorial uses one minute so that you wait less, as the next section explains.
- `--borrow-days 1` lets an app borrow a seat for up to one day. Without it, borrowing is not allowed.
- `--trust root.pub.pem` makes `elm` verify the new license against the root public key and write the file only if it passes, so a command that ends without an error made a license that verifies.

## Start the server

Open a second terminal in the tutorial folder, and add the `tools` folder to its `PATH` too, the same way as in the first terminal: each terminal has its own `PATH`. Start the server there:

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

The server keeps running and prints its log. Each entry is a line that starts with `info:` and names where the entry comes from, followed by the message, indented. The first entries come from the web host the server runs in: the two addresses it listens on, `Application started. Press Ctrl+C to shut down.`, the hosting environment, and a `Content root path`, which is the folder of the `elm-server` executable and has nothing to do with the paths in `server.json`. The line to look for is `server.started`, which says that the server started:

```text
info: elm-server[1]
      server.started: listening on port 7070 as https://127.0.0.1:7070/, key FAKE-SERVER-KEY-00000000000000000000000000K, license floating-server, in 68 ms; reclaim window until 2026-01-15T09:41:00.0000000+00:00
```

The time at the end is in UTC, as `+00:00` shows.

If port 7070 or 7071 is taken, the server instead logs the failure with a long stack trace and ends with exit code 1. Its last line is `error: elm-server cannot listen on https://127.0.0.1:7070/ and http://127.0.0.1:7071/: Failed to bind to address https://127.0.0.1:7070: address already in use.`; when only 7071 is taken, the address after `Failed to bind to address` is `http://127.0.0.1:7071`. Stop the program that uses the port, and start the server again.

For the first lease duration after it starts, one minute with this license, the server refuses new leases: after a restart, apps that held leases before it need that time to reclaim their seats. Leave the server running, and go back to the first terminal for the rest of the tutorial. While you create the app, most of that minute passes.

## Create the app

Create a console app in the folder `app`, and add the `ElasticLM.Client` package to it from `packages`. `ElasticLM.Licensing` comes with it. `--prerelease` lets `dotnet add` take a prerelease version, the only kind in `packages`, and `--source packages` makes it look for the version in `packages` only:

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` also keeps a copy of the packages in NuGet's global packages folder; `dotnet nuget locals global-packages --list` shows where it is, also when the environment variable `NUGET_PACKAGES` moves it. That folder holds the packages of every project on this machine: once you have tried ElasticLM, delete only its folders `elasticlm.licensing` and `elasticlm.client` there.

Replace the content of `app/Program.cs` with this code:

<!-- sample: samples/FloatingLease/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
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
```

The app reads the root public key into a trust store, the set of root keys it trusts, and creates a `LicenseClient` for the product `acme-cad` and the server at `https://127.0.0.1:7070/`. The client keeps its files in the data folder of the second argument: its own installation key, which identifies this copy of the app to the server, a record of the server's key after the first check, and the borrows.

The app then makes three requests, each through `WithRetryAsync`, which waits and asks again while the server is still in the minute after its start:

1. `AcquireLeaseAsync` leases one seat of `pro`. On the first request, the client also fetches the server license and checks it against the root public key: the server proves that the vendor gave it the seats. The connection is HTTPS with a self-signed certificate that the server makes from its installation key, and the client accepts it because the server license names that key; [TLS](../explanation/floating-licenses.md#tls) explains this. The client renews a lease in the background until it is returned.
2. `ReleaseLeaseAsync` returns the lease, so the seat is free again.
3. `BorrowLeaseAsync` borrows a seat for one hour. A borrow is not renewed: it is valid until it expires, even with no network, and the client saves it in the data folder.

`await using` disposes the client at the end, which returns any lease that is still held; a borrow stays. `LeaseResult` is either `Acquired` with the lease or `Failed` with the error, and the app prints the error code and the reason when the server refuses. This app reads the root public key from a file to keep the tutorial short; a real app embeds the text of `root.pub.pem` in its code, for example as a string constant.

## Run the app

Run the app with the root public key and the data folder `client-data`, which the client creates:

<!-- steps:run -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

The first `dotnet run` builds the app, which takes some seconds with no output. If the server started less than a minute ago, the app then prints a line such as `waiting 48 s: the server is reclaiming leases after its start`, and waits. Then it prints:

<!-- steps:expect -->
```text
leased: pro
borrowed: pro for 1 hour
```

Meanwhile, the server terminal shows the requests. The web host adds a few `info:` entries for each request, from `Request starting` to `Request finished` with the HTTP status. Among them are these lines, here with the other lines left out and with fake IDs:

```text
      Request finished HTTP/1.1 POST https://127.0.0.1:7070/v1/leases - 503 - application/problem+json 0.9008ms
      lease.acquired: FAKE-LEASE-ID-00000001 pro x1 for key FAKE-APP-KEY-00000000000000000000000000000A
      lease.released: FAKE-LEASE-ID-00000001
      lease.acquired: FAKE-LEASE-ID-00000002 pro x1 for key FAKE-APP-KEY-00000000000000000000000000000A
```

The first request for a lease finished with status 503, because the server was still reclaiming; this is the wait the app printed. Then the server lent the lease, the app returned it, and the server lent the borrow, which the log also calls `lease.acquired`. The key at the end is the app's installation key. If the app did not have to wait, the 503 line is not there.

If the app prints an error code instead, the code says what failed:

| Code | Likely cause in this tutorial | What to do |
| --- | --- | --- |
| `ELM_NETWORK_ERROR` | The server is not running, or it listens on another address. | Start the server in the second terminal as in [Start the server](#start-the-server), and check that `listen` in `server.json` is `https://127.0.0.1:7070`. |
| `ELM_NO_SEATS` | Both seats are borrowed, because you ran the app more than once within an hour. | See the next section. |
| `ELM_SERVER_PIN_MISMATCH` | `server-data` was deleted after an earlier run, so the server has a new installation key, while `client-data` still holds the key of the old one. | Start again as described at the end of [Check the server](#check-the-server). |
| `ELM_UNTRUSTED_KEY` | The first argument is a public key other than `root.pub.pem`, such as `signing.pub.pem`. | Pass `root.pub.pem`. |

If the app prints `usage: app <root-public-key.pem> <data-folder>`, it did not get exactly two arguments. If it prints a line that starts with `error: A root key is not a P-256 public key in PEM.`, the first argument is not a public key file. An `error:` line about a file that cannot be found names the path it tried, and one that says access is denied usually means the two arguments are in the wrong order. [Error codes](../reference/error-codes.md) lists every code.

## Check the server

Ask the server what it holds:

<!-- steps:run -->
```shell
elm server status --data server-data
```

`elm` reads the server's administrator token from `server-data` and asks the server on port 7071. Among other lines, it prints:

<!-- steps:expect -->
```text
serving
leases: 1
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

Before these lines, it prints the product, the server license's ID and token ID, its expiry, and the server's key. `serving` says that the server is no longer reclaiming leases: the minute after the start is over, or the leases reclaimed before it ended already use every seat. The lease the app returned is gone; the one lease left is the borrow, which uses one of the two seats until it expires an hour later. The license allows no seats over its limit, so the limit with overage is also 2.

Each run of the app within that hour borrows one more seat. A second run leaves both seats borrowed, and a third one prints `ELM_NO_SEATS: No seats are free for the feature.` and ends with exit code 1, because borrowed seats count as used.

Stop the server: press Ctrl+C in the second terminal. It prints `Application is shutting down...` and `server.stopped: 1 leases were not returned`, and ends. The one lease is the borrow. The server keeps its record of the borrows in `server-data`, so when it starts again, it still counts the borrow until the borrow expires.

To start again from the beginning, delete the whole tutorial folder with the server stopped: that removes both the server's record of the borrows in `server-data` and the app's borrow files in `client-data`. Then start again from [Before you start](#before-you-start), including the downloads into `tools` and `packages`. Do not delete only `server-data`: the server then makes a new installation key and needs a new server license, and the app with the old `client-data` refuses the new server with `ELM_SERVER_PIN_MISMATCH`, because the client keeps the key of the server it checked first.

## What you made

You created a root key and a signing key, set up `elm-server` with a server license of two seats bound to its key, and wrote a .NET app that leased a seat, returned it, and borrowed a seat for an hour. In real use, the vendor issues the server license, and the customer's administrator runs the server on its network with the default lease duration of 10 minutes.

To learn more:

- [Floating licenses](../explanation/floating-licenses.md): leases, renewal, what happens when the server restarts, and borrowing.
- [elm-server configuration](../reference/server-configuration.md): every setting of the server, and its data folder.
- [.NET API](../reference/dotnet-api.md#licenseclient): `LicenseClient`, its options, and what each method returns.
- [Issue a license and verify it in a .NET app](verify-a-license-offline.md): a license that an app checks on its own, with no server.
