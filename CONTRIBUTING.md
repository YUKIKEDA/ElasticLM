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

The tutorials in `docs/guide/tutorials/` have the reader download `elm`, `elm-server`, and the `.nupkg` files of a GitHub prerelease into the folders `tools` and `packages` of a work folder (design §20.7). Before the first prerelease, or to try a change, build them from a clone instead. Keep the work folder outside the clone: inside it, the repository's own build settings would also apply to the tutorial's app.

Besides the .NET 10 SDK, a NativeAOT publish needs the tools listed under [Prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) in Microsoft's Native AOT deployment page:

- Windows: Visual Studio's "Desktop development with C++" workload. Also add the Visual Studio Installer folder to `PATH` in the terminal, because Visual Studio's environment scripts call `vswhere.exe` by name: `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"` in PowerShell.
- Linux: `clang` and `zlib1g-dev` (the Ubuntu package names).
- macOS: the Xcode Command Line Tools.

In the clone, publish the executables into the work folder's `tools`:

```shell
dotnet publish src/ElasticLM.Cli -c Release -o <work-folder>/tools
dotnet publish src/ElasticLM.Server -c Release -o <work-folder>/tools
dotnet publish src/ElasticLM.Service -c Release -o <work-folder>/tools
```

`ElasticLM.Service` embeds the admin UI, so build `web/admin/dist` first (see [Requirements](#requirements)).

and pack the packages into its `packages`:

```shell
dotnet pack src/ElasticLM.Licensing -c Release -o <work-folder>/packages
dotnet pack src/ElasticLM.Client -c Release -o <work-folder>/packages
```

These packages have the version of `VersionPrefix` (`0.1.0`) with no prerelease suffix. The tutorials' `dotnet add ... --prerelease --source packages` also takes a version that is not a prerelease, so the pages work as written. When you pack the same version again, your app still gets the copy that NuGet saved the first time in its global packages folder, not the new one: `dotnet nuget locals global-packages --list` shows where the folder is, and you delete its folders `elasticlm.client` and `elasticlm.licensing` (or clear it with `dotnet nuget locals global-packages --clear`) before the next `dotnet add` or `dotnet run`.

To run the marked steps of `README.md`, the tutorials, and the how-to guides as the CI step "Guide steps" does, pack the two packages into one folder with a prerelease suffix of your own, such as `-p:VersionSuffix=local.1`, and run:

```shell
./eng/docs/Test-GuideSteps.ps1 -Elm <elm> -ElmServer <elm-server> -ElmService <elm-service> -Feed <that-folder>
```

`-Elm`, `-ElmServer`, and `-ElmService` take the executables, such as `<work-folder>/tools/elm.exe` on Windows. The tutorial with the C app also needs `-Library`, the folder of a published `libelasticlm`, and `-Page` runs only the pages it names. The script runs each page in a new work folder of its own; a how-to guide runs there after the steps of the tutorial that its `steps:after` marker names.

## Versioning

Packages and binaries are versioned with SemVer from `VersionPrefix` in `Directory.Build.props`. Before 1.0, a minor version may break the public API, the license token format, or the wire protocol; `CHANGELOG.md` says so when it does. Nothing is published yet. How a release is cut and published is in `docs/releasing.md`.
