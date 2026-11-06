---
sidebar_position: 1
---

# Issue a license and verify it in a .NET app

In this tutorial you create a vendor's keys, issue a license with them, and write a small .NET console app that checks the license on your machine, with no network. It is for anyone new to ElasticLM who can use a terminal and the `dotnet` command.

## Before you start

You need the .NET 10 SDK: `dotnet --list-sdks` must list a version that starts with `10.`.

Create an empty folder for this tutorial. Open a terminal in that folder and type every command of this page there.

You download the `elm` tool and the `ElasticLM.Licensing` package from a GitHub prerelease. Open the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases) and take the newest release marked "Pre-release". The release that GitHub marks "Latest" is never a prerelease, so do not go by that mark.

From the release's Assets, download the archive `elm-<version>-<RID>` for your system, where `<RID>` is one of `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`. It is a `.zip` file for Windows and a `.tar.gz` file for the others. Take the archive whose name does not end in `-symbols`: that one holds only debug symbols. Extract the archive into a folder named `tools` in the tutorial folder. `tools` then holds `elm` (`elm.exe` on Windows), the command-line tool that creates keys and issues licenses, and `LICENSE`.

<!-- steps:prebuilt name=elm -->

Add the `tools` folder to your `PATH` in this terminal, so that the command `elm` runs it. In PowerShell on Windows, type `$env:PATH = "$PWD/tools;$env:PATH"`; in Bash or zsh, type `export PATH="$PWD/tools:$PATH"`.

From the same release, download `ElasticLM.Licensing.<version>.nupkg`, the package your app uses, into a folder named `packages` in the tutorial folder. You do not need `ElasticLM.Licensing.<version>.snupkg` beside it, which holds the package's debug symbols.

<!-- steps:prebuilt name=packages -->

Before you use what you downloaded, check it as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`. That section also says how to run a downloaded program on macOS, and which Linux and macOS versions the programs need.

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

You need two keys: a root key, which your app trusts, and a signing key, which signs licenses. The root key certifies the signing key, so that your app accepts what the signing key signs.

`elm` keeps each private key in a file encrypted with a passphrase. When it creates a key, it asks for a new passphrase, at least 12 characters long, and asks you to repeat it. When a later command uses the key, it asks for that passphrase again. Nothing is shown while you type it. Use the same passphrase for both keys in this tutorial. If the environment variable `ELM_KEY_PASSPHRASE` is set, `elm` takes the passphrase from it and does not ask, which is for scripts and terminals where nobody can type; in real use, do not keep a passphrase in a script. See [Passphrases](../reference/elm-cli.md#passphrases) in the `elm` reference.

In the outputs below, the key IDs, the token ID, and the dates are fake. On your machine they are different every time.

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

`root.key.pem` is the private key and `root.pub.pem` is its public key. The key ID is a short name computed from the public key.

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

## Issue a license

Issue a license for the product `acme-cad` with the feature `pro`, which expires at 00:00 UTC on 31 December 2099:

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
```

`elm` prints:

```text
Passphrase for 'signing.key.pem':
license file: first.elmlic
license ID: first-license
token ID: FAKEjti000000000000001
```

`first.elmlic` is the license file. It holds the signed license and the signing key's certificate. The token ID is a random ID that `elm` gives each license it signs. With `--trust`, `elm` verifies the new license against the root public key and writes the file only if it passes, so a command that ends without an error made a license that verifies. The license is not bound to a device, so it is valid on any machine.

Verify the license with `elm`, as your app will:

<!-- steps:run -->
```shell
elm license verify first.elmlic --trust root.pub.pem --product acme-cad --feature pro
```

`elm` prints:

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, kind standard
expires: 2099-12-31 00:00:00Z
features: pro
```

## Create the app

Create a console app in the folder `app`, and add the `ElasticLM.Licensing` package to it from `packages`. `--prerelease` lets `dotnet add` take a prerelease version, the only kind in `packages`, and `--source packages` makes it look for the version in `packages` only:

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Licensing --prerelease --source packages
```

Replace the content of `app/Program.cs` with this code:

<!-- sample: samples/VerifyOffline/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
using ElasticLM.Licensing;

// Verifies a license file offline and prints whether it is valid.
//
//   app <root-public-key.pem> <license.elmlic>
//
// A real app embeds the root public key instead of reading it from a file.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <license.elmlic>");
    return 2;
}

try
{
    TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []);
    var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
    var requirement = new LicenseRequirement(ProductId, LicenseKind.Standard, FeatureId, FeatureVersion: null, Installation: null);
    LicenseVerificationResult result = verifier.Verify(File.ReadAllBytes(args[1]), requirement);

    switch (result)
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"valid: license {valid.License.LicenseId}, product {valid.License.ProductId}, feature {FeatureId}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            Console.WriteLine($"invalid: {invalid.Error.WireName}: {invalid.Error.Message}");
            return 1;
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
```

The app reads the root public key into a trust store, the set of root keys it trusts. The list of revoked key IDs is empty, because no key has been revoked. It then verifies the license file: the signatures, the certificate, the expiry, the product `acme-cad`, and the feature `pro`. `FeatureVersion: null` skips the check of the feature's version, and `Installation: null` says that the app uses only licenses that are not bound to a device. It prints `valid` with the license's ID, or `invalid` with the error code and the reason.

This app reads the root public key from a file to keep the tutorial short; a real app embeds the text of `root.pub.pem` in its code, for example as a string constant. The app also passes `StatelessClockGuard`, which does not notice a clock set back; [Clock rollback detection and its limits](../explanation/license-files-and-verification.md#clock-rollback-detection-and-its-limits) explains the guards that do.

## Run the app

Run the app with the root public key and the license file:

<!-- steps:run -->
```shell
dotnet run --project app -- root.pub.pem first.elmlic
```

The app prints:

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

If it prints `invalid:` instead, the code after it says what failed:

| Code | Likely cause in this tutorial | What to do |
| --- | --- | --- |
| `ELM_UNTRUSTED_KEY` | The first argument is a public key other than `root.pub.pem`, such as `signing.pub.pem`. | Pass `root.pub.pem`. |
| `ELM_WRONG_PRODUCT` | The license was issued with a `--product` other than `acme-cad`. | Issue the license again with `--product acme-cad`, and add `--force` to overwrite `first.elmlic`. |
| `ELM_FEATURE_NOT_LICENSED` | The license was issued without `--feature pro`. | Issue the license again with `--feature pro` and `--force`. |
| `ELM_MALFORMED` | The second argument is not a license file, such as `signing.cert`. | Pass `first.elmlic`. |

If the app prints a line that starts with `error: A root key is not a P-256 public key in PEM.`, the first argument is not a public key file: check the order of the two arguments. An `error:` line about a file that cannot be found names the path it tried. [Error codes](../reference/error-codes.md) lists every code, and [What can go wrong and what to do](../explanation/license-files-and-verification.md#what-can-go-wrong-and-what-to-do) says what each check needs.

## What you made

You created a root key and a signing key, certified the signing key, issued a license, and verified it offline in a .NET app. Keep `root.key.pem` offline in a real product: it only certifies signing keys, and anyone who holds it can make licenses that your app accepts.

In [Run a license server and lease a floating license in a .NET app](use-a-floating-license.md), you lend seats of a feature from a license server to an app. A tutorial that activates a device online is being written.

To learn more:

- [License files and verification](../explanation/license-files-and-verification.md): what the license file holds and every check the app made.
- [Trust chain](../explanation/trust-chain.md): the root key, signing keys, and how to keep them safe.
- [.NET API](../reference/dotnet-api.md): every type the app used, and the options of `LicenseVerifier`.
