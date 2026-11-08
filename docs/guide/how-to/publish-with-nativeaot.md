---
sidebar_position: 15
---

# Publish with NativeAOT

This guide is for a developer who ships a .NET app that uses ElasticLM, and wants to publish it as one native executable with NativeAOT, which starts fast and needs no .NET runtime on the user's machine. The ElasticLM packages are made for this: they use no reflection or code generated at run time, so a NativeAOT publish of an app that references them has no warnings from them. The guide starts where you have finished [Issue a license and verify it in a .NET app](../tutorials/verify-a-license-offline.md): its app in `app`, its root public key `root.pub.pem`, and its license file `first.elmlic`.

<!-- steps:after page=docs/guide/tutorials/verify-a-license-offline.md -->

## Before you start

Type the commands in the folder of the tutorial. NativeAOT compiles the app with the platform's native tools, which the .NET SDK does not bring:

- **Windows**: Visual Studio 2022 or later, or its Build Tools, with the workload "Desktop development with C++".
- **Linux**: `clang` and the `zlib` development files, such as `sudo apt-get install clang zlib1g-dev` on Ubuntu.
- **macOS**: the Xcode command line tools: `xcode-select --install`.

[Build from source](https://github.com/YUKIKEDA/ElasticLM/blob/main/CONTRIBUTING.md#build-from-source) in `CONTRIBUTING.md` lists the same tools. NativeAOT builds for the system it runs on: publish on Windows for Windows, on Linux for Linux, and on macOS for macOS.

## Turn on NativeAOT

Turn on NativeAOT for the app with the property `PublishAot`. Here the guide puts it in a new file `app/Directory.Build.props`, which every project in the folder `app` reads, so that the tutorial's project file stays as it is:

<!-- steps:file path=app/Directory.Build.props -->
```xml
<Project>
  <PropertyGroup>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
</Project>
```

In your own app, you can instead add `<PublishAot>true</PublishAot>` to the `PropertyGroup` of its project file. With `PublishAot`, the build also runs the analyzers that report code which NativeAOT cannot compile, so that you see their warnings while you work, not only when you publish.

## Publish the app

Publish the app into the folder `publish`. `-p:TreatWarningsAsErrors=true` makes every warning of the build and of the NativeAOT compiler an error, so the command ends without an error only when there were none. The second property keeps NuGet's warnings about packages with known vulnerabilities (NU1901 to NU1904) as warnings: a new advisory on a package of the .NET SDK would otherwise stop the publish until you update the SDK, and you should still read them:

<!-- steps:run -->
```shell
dotnet publish app -c Release -p:TreatWarningsAsErrors=true -p:WarningsNotAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904 -o publish
```

`%3B` is how a `-p` value writes `;`, which separates the codes: a plain `;` or `,` would end the property there.

The first publish takes a minute or more: it downloads the NativeAOT compiler packages for your system, and compiles the app and the parts of .NET it uses into native code. It ends with a line such as `app -> …/publish/`.

Without `-r`, `dotnet publish` builds for the system you run it on, such as `win-x64` on 64-bit Windows or `osx-arm64` on an Apple silicon Mac. To build for another processor of the same system, add `-r` with its runtime identifier, such as `-r win-arm64` or `-r linux-arm64`; [the .NET RID catalog](https://learn.microsoft.com/dotnet/core/rid-catalog) lists them.

If the publish stops with a warning turned into an error, the message names the code that NativeAOT cannot compile, or the package it comes from. A warning from an ElasticLM package is a bug: report it as described in `SECURITY.md` or the repository's issues. On Windows, an error that `vswhere.exe` or the C++ tools cannot be found means that the C++ workload is missing; on Linux, an error about `clang` or `-lz` means that `clang` or `zlib1g-dev` is missing.

## Run the published app

The folder `publish` holds the app as one executable, `app` (`app.exe` on Windows), with its debug symbols beside it (`app.pdb` on Windows, `app.dbg` on Linux, `app.dSYM` on macOS). Run it with the same arguments as in the tutorial:

<!-- steps:run -->
```shell
publish/app root.pub.pem first.elmlic
```

It prints the same line as `dotnet run` did:

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

The executable needs no .NET runtime: copy it, without the debug symbols, to a machine of the same system and processor, and run it there. Keep the symbols of each release you ship, so that you can read the stack traces of crash reports.

Before you ship, embed the root public key in the code instead of reading `root.pub.pem` from a file, as the tutorial says: the text of the key can be a string constant, and NativeAOT keeps it in the executable.

## See also

- [Overview](../explanation/overview.md): what ships in the packages, and the systems they support.
- [.NET API](../reference/dotnet-api.md): every type the app uses.
- [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) in the .NET documentation: the limits of NativeAOT and the options of the publish.
