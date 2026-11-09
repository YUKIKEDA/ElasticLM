# Contributing to ElasticLM

ElasticLM is being redesigned from its requirements. The design is `docs/design.ja.md` (Japanese), the order of work is `docs/roadmap.md`, and the conventions are `docs/conventions.md`.

## Workflow

1. If the design still branches, it is settled first (a Grill, then a `design` Issue that updates `docs/design.ja.md`).
2. Every change starts from an Issue opened with a template in `.github/ISSUE_TEMPLATE/`. Blank issues are disabled.
3. Branch from `main` as `type/<issue-number>-<slug>`.
4. Run `./build.ps1` (PowerShell 7) before opening a pull request. It builds the admin UI (`web/admin/`: type check, Vitest, Vite), checks formatting (CSharpier), builds with warnings as errors in Release and Debug, and runs every test project. `elm-service` embeds `web/admin/dist`; without it the .NET build stops.
5. Open the pull request with the template. Put `Closes #N` alone on a line under `## Related`.
6. A maintainer reviews and squash-merges.

Security problems are not reported in Issues or pull requests. See `SECURITY.md`.

## Commits and pull requests

Titles follow Conventional Commits: `type(scope): subject`, in English. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `build`, `ci`.

A change on a measured path (license verification, server startup, checkout and renewal, binary size) reports its numbers before and after under `## Measurements`.

Never commit a private key, a secret, or a real license token, test data included. Tests generate their keys at run time.

## Requirements

- .NET 10 SDK (`global.json`).
- PowerShell 7 for `./build.ps1`.
- Node.js of `web/admin/.node-version` (24), which includes Corepack. pnpm needs no install: `corepack pnpm` takes the version and hash in `web/admin/package.json`, downloading it on first use. `./build.ps1 -SkipWeb` skips the admin UI and uses the `web/admin/dist` that is there.
- `dotnet tool restore` once after cloning (CSharpier).
- To see the user documentation site (`website/`, design §20.7) before a pull request, with the same Node.js: `cd website`, `corepack pnpm install --frozen-lockfile`, `corepack pnpm run build` (it fails on a broken link or anchor), then `corepack pnpm run serve` and open http://localhost:3000/ElasticLM/ (English) and http://localhost:3000/ElasticLM/ja/ (Japanese). `corepack pnpm run start` serves the pages while you edit them, without search and without the link check. A pull request does not publish the site; GitHub Pages is updated only by a release tag.
- For a NativeAOT publish on Windows: Visual Studio's "Desktop development with C++" workload (the MSVC linker). The same workload compiles the C test of `libelasticlm`: `./tests/native/run.ps1 -Publish` publishes the library, `elm`, `elm-service` (which needs `web/admin/dist`), and `elm-server`, compares the exports with `include/elasticlm.h`, links a program that references every function of the header against the library at build time and compares its `elm_version` with the version, and runs the C test (gcc on Linux, clang on macOS), the activation part against `elm-service` on loopback and the lease part against `elm-server` on https loopback too.

## Build from source

The tutorials in `docs/guide/tutorials/` and the how-to guides in `docs/guide/how-to/` have the reader download `elm`, `elm-server`, `elm-service`, `libelasticlm`, and the `.nupkg` files of a GitHub prerelease into the folders `tools`, `app`, and `packages` of a work folder (design §20.7). Before the first prerelease, or to try a change, build them from a clone instead. Keep the work folder outside the clone: inside it, the repository's own build settings would also apply to the tutorial's app.

Besides the .NET 10 SDK, a NativeAOT publish needs the tools listed under [Prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) in Microsoft's Native AOT deployment page:

- Windows: Visual Studio's "Desktop development with C++" workload. Also add the Visual Studio Installer folder to `PATH` in the terminal, because Visual Studio's environment scripts call `vswhere.exe` by name: `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"` in PowerShell.
- Linux: `clang` and `zlib1g-dev` (the Ubuntu package names).
- macOS: the Xcode Command Line Tools.

`elm-service` embeds the admin UI, so build `web/admin/dist` first with the Node.js of [Requirements](#requirements): in `web/admin`, run `corepack pnpm install --frozen-lockfile` and then `corepack pnpm build` (`./build.ps1` does the same). Then, in the clone, publish the executables into the work folder's `tools`:

```shell
dotnet publish src/ElasticLM.Cli -c Release -o <work-folder>/tools
dotnet publish src/ElasticLM.Server -c Release -o <work-folder>/tools
dotnet publish src/ElasticLM.Service -c Release -o <work-folder>/tools
```

For the tutorial with the C app, publish `libelasticlm` for your system's RID, such as `win-x64`, and copy the library files of the publish folder into the work folder's `app`, as the tutorial does with the archive: `elasticlm.dll` and `elasticlm.lib` on Windows, `libelasticlm.so` on Linux, `libelasticlm.dylib` on macOS, with `include/elasticlm.h` and `LICENSE` from the clone:

```shell
dotnet publish src/ElasticLM.Native -c Release -r <RID> -o <library-folder>
```

Pack the packages into the work folder's `packages`:

```shell
dotnet pack src/ElasticLM.Licensing -c Release -o <work-folder>/packages
dotnet pack src/ElasticLM.Client -c Release -o <work-folder>/packages
```

These packages have the version of `VersionPrefix` (`0.1.0`) with no prerelease suffix. The tutorials' `dotnet add ... --prerelease --source packages` also takes a version that is not a prerelease, so the pages work as written. When you pack the same version again, your app still gets the copy that NuGet saved the first time in its global packages folder, not the new one: `dotnet nuget locals global-packages --list` shows where the folder is, and you delete its folders `elasticlm.client` and `elasticlm.licensing` (or clear it with `dotnet nuget locals global-packages --clear`) before the next `dotnet add` or `dotnet run`.

To run the marked steps of `README.md`, the tutorials, and the how-to guides as the CI step "Guide steps" does, pack the two packages into one folder with a prerelease suffix of your own, such as `-p:VersionSuffix=local.1`, and run:

```shell
./eng/docs/Test-GuideSteps.ps1 -Elm <elm> -ElmServer <elm-server> -ElmService <elm-service> -Feed <that-folder>
```

`-Elm`, `-ElmServer`, and `-ElmService` take the executables, such as `<work-folder>/tools/elm.exe` on Windows. The tutorial with the C app also needs `-Library`, the `<library-folder>` above, and `-Page` runs only the pages it names. The script runs each page in a new work folder of its own; a how-to guide runs there after the steps of the tutorial that its `steps:after` marker names.

### Build the container image

The how-to guides that run `elm-service` in a container use an image named `elm-service`. No image is published before the first release that is not a prerelease, so build one for your machine's architecture. The NativeAOT executable for Linux is built on Linux: use a Linux machine, or a Linux distribution of WSL, with the .NET 10 SDK, PowerShell 7, `clang`, `zlib1g-dev`, the Node.js of [Requirements](#requirements) for `web/admin/dist`, and Docker with its buildx plugin. In PowerShell 7 (`pwsh`), in the clone, on an x64 machine:

```shell
./eng/release/Publish-Rid.ps1 -Rid linux-x64 -OutDir publish -LogDir publish-logs
./eng/release/Test-Publish.ps1 -Rid linux-x64 -PublishDir publish -LogDir publish-logs -RecordGlibcOnly
./eng/release/Pack-Rid.ps1 -Rid linux-x64 -Version 0.1.0 -PublishDir publish -OutDir archives
docker buildx create --name elm-builder --driver docker-container --use
./eng/container/Build-Image.ps1 -Version 0.1.0 -Revision (git rev-parse HEAD) -ArchiveDir archives -Platform linux/amd64 -Format docker -OutFile elm-service.tar
docker load --input elm-service.tar
```

On an arm64 machine, use `linux-arm64` and `linux/arm64`. `-Format docker` writes a tar that `docker load` reads, with the image named `elm-service`; without `-Platform` and `-Format`, `Build-Image.ps1` builds both platforms into an OCI layout, as the release does. The builder of the `docker-container` driver is needed because `Build-Image.ps1` writes the image to a tar file, which the default `docker` driver cannot. `-RecordGlibcOnly` lets `Test-Publish.ps1` record the glibc version the executables need instead of failing: the release builds them on glibc 2.28, while your machine's glibc is likely newer. The script replaces an `elm-service` image you already have, and says so. On Windows with WSL containers instead of Docker, build the tar in a Linux distribution as above, then load it with `wslc load --input elm-service.tar`.

To run the steps of the container, PostgreSQL, and OIDC how-to guides as the release workflow's job does, give the script the image, and `-Docker` on a Linux machine with Docker:

```shell
./eng/docs/Test-GuideSteps.ps1 -Elm <elm> -ElmService <elm-service> -Feed <that-folder> -Image <oci-layout.tar> -Docker -Page docs/guide/how-to/run-the-service-in-a-container.md, docs/guide/how-to/run-the-service-on-postgresql.md, docs/guide/how-to/sign-in-with-oidc.md
```

`-Image` takes an OCI layout tar of `Build-Image.ps1` without `-Format docker`, such as one built with `-Platform linux/amd64 -OutFile elm-service-oci.tar`; the script loads its linux/amd64 image. On Windows with WSL containers, add `-ContainerCli wslc`. Without `-Docker`, the script skips every page with `docker` lines.

## Versioning

Packages and binaries are versioned with SemVer from `VersionPrefix` in `Directory.Build.props`. Before 1.0, a minor version may break the public API, the license token format, or the wire protocol; `CHANGELOG.md` says so when it does. Nothing is published yet. How a release is cut and published is in `docs/releasing.md`.
