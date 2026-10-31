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
- For a NativeAOT publish on Windows: Visual Studio's "Desktop development with C++" workload (the MSVC linker). The same workload compiles the C test of `libelasticlm`: `./tests/native/run.ps1 -Publish` publishes the library, `elm`, `elm-service` (which needs `web/admin/dist`), and `elm-server`, compares the exports with `include/elasticlm.h`, and runs the C test (gcc on Linux, clang on macOS), the activation part against `elm-service` on loopback and the lease part against `elm-server` on https loopback too.

## Versioning

Packages and binaries are versioned with SemVer from `VersionPrefix` in `Directory.Build.props`. Before 1.0, a minor version may break the public API, the license token format, or the wire protocol; `CHANGELOG.md` says so when it does. Nothing is published yet. How a release is cut and published is in `docs/releasing.md`.
