---
sidebar_position: 1
---

# Issue a license and verify it in a .NET app

In this tutorial you create a vendor's keys, issue a license with them, and write a small .NET console app that checks the license on your machine, with no network. It is for anyone new to ElasticLM who can use a terminal and the `dotnet` command.

## Before you start

1.0 is not released yet, so you build the `elm` tool and the `ElasticLM.Licensing` package from the source. You need:

- A clone of the [ElasticLM repository](https://github.com/YUKIKEDA/ElasticLM).
- The .NET 10 SDK.
- What .NET needs to build a native executable on your system, listed under Prerequisites in [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/):
  - Windows: Visual Studio with the "Desktop development with C++" workload. Also add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to your `PATH`: the build calls `vswhere.exe` from there by name.
  - Ubuntu: the packages `clang` and `zlib1g-dev`.
  - macOS: the Xcode Command Line Tools.

Create an empty folder outside the clone for this tutorial. Open a terminal in that folder and type every command of this page there. Outside the clone, the repository's own build settings do not apply to your app.

In the commands, replace `<repo>` with the path of your clone, written with `/`, such as `C:/src/ElasticLM` or `/home/me/src/ElasticLM`.

Build `elm`, the command-line tool that creates keys and issues licenses, into the folder `tools`:

<!-- steps:prebuilt name=elm -->
```shell
dotnet publish <repo>/src/ElasticLM.Cli -c Release -o tools
```

Add the `tools` folder to your `PATH` in this terminal, so that the command `elm` runs it. In PowerShell, type `$env:PATH = "$PWD/tools;$env:PATH"`; in Bash or zsh, type `export PATH="$PWD/tools:$PATH"`.

Build the `ElasticLM.Licensing` package, version 0.1.0, into the folder `feed`:

<!-- steps:run -->
```shell
dotnet pack <repo>/src/ElasticLM.Licensing -c Release -o feed
```

Create a file named `nuget.config` in the tutorial folder with this content. It makes your app take the packages whose names start with `ElasticLM.` from `feed` only, and every other package from nuget.org:

<!-- steps:file path=nuget.config -->
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="feed" value="feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="feed">
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

`elm` keeps each private key in a file encrypted with a passphrase. When it creates a key, it asks for a new passphrase, at least 12 characters long, and asks you to repeat it. When a later command uses the key, it asks for that passphrase again. Use the same passphrase for both keys in this tutorial.

Create the root key:

<!-- steps:run -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
```

`root.key.pem` is the private key and `root.pub.pem` is its public key. `elm` prints the key's ID and the two files.

Create the signing key:

<!-- steps:run -->
```shell
elm key signing create --out signing.key.pem --public-out signing.pub.pem
```

Certify the signing key with the root key, for signing licenses:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
```

`signing.cert` is the key certificate. `elm` prints the two key IDs, the use, and the dates between which the certificate is valid.

## Issue a license

Issue a license for the product `acme-cad` with the feature `pro`, valid until the end of 2099:

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
```

`first.elmlic` is the license file. It holds the signed license and the signing key's certificate. With `--trust`, `elm` checks the new license against the root public key before it writes the file. The license is not bound to a device, so it is valid on any machine.

## Create the app

Create a console app in the folder `app`, and add the `ElasticLM.Licensing` package to it:

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Licensing --version 0.1.0
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

TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], []);
var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
var requirement = new LicenseRequirement(ProductId, LicenseKind.Standard, FeatureId, null, null);
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
```

The app reads the root public key into a trust store, the set of root keys it trusts. It then verifies the license file: the signatures, the certificate, the expiry, the product `acme-cad`, and the feature `pro`. It prints `valid` with the license's ID, or `invalid` with the error code and the reason.

This app reads the root public key from a file to keep the tutorial short; a real app embeds `root.pub.pem` in its code. The app also passes `StatelessClockGuard`, which does not notice a clock set back; [Clock rollback detection and its limits](../explanation/license-files-and-verification.md#clock-rollback-detection-and-its-limits) explains the guards that do.

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

## What you made

You created a root key and a signing key, certified the signing key, issued a license, and verified it offline in a .NET app. Keep `root.key.pem` offline in a real product: it only certifies signing keys, and anyone who holds it can make licenses that your app accepts.

To learn more:

- [License files and verification](../explanation/license-files-and-verification.md): what the license file holds and every check the app made.
- [Trust chain](../explanation/trust-chain.md): the root key, signing keys, and how to keep them safe.
- [.NET API](../reference/dotnet-api.md): every type the app used, and the options of `LicenseVerifier`.
