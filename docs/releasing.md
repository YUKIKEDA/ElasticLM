# Releasing ElasticLM

This is the owner's guide to the release path of design §20.5 (decision 0.109) and the threats it holds against (§5.3
T22–T25). `.github/workflows/release.yml` builds, checks, and attests every release; nothing is published without the
owner's approval. Nothing has been published yet: the first release is roadmap row V1e.

## One-time setup

Do these once, before the first tag. Each step closes a gap the threat model names.

### Repository

1. Keep write access to the owner alone: add no collaborators (T22). Anyone with write access could push a tag.
2. Settings → Actions → General → Workflow permissions: **Read repository contents and packages permissions**. Each job of
   `release.yml` asks for what it needs.
3. Settings → Rules → Rulesets → New tag ruleset:
   - Target: tags matching `v*`.
   - Bypass list: empty.
   - Rules: **Restrict creations** with the owner as the only actor allowed to create, **Restrict updates**, and
     **Restrict deletions**. A tag that ran a release is never moved or removed; a failed release gets the next version.
4. Turn on **Immutable releases** in the repository settings
   (https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases). A published release's tag
   and files cannot change, even for the owner; a draft still can (T24).
5. Settings → Secrets and variables → Actions → Variables: add `NUGET_USER` with the owner's nuget.org profile name. It is
   not a secret; `NuGet/login` sends it with the OIDC token.

### Environment `release`

Settings → Environments → New environment `release`:

- Required reviewers: the owner.
- **Prevent self-review**: off. The owner is the only reviewer and also pushes the tag; with it on, nobody could approve.
  (https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)
- **Allow administrators to bypass configured protection rules**: off.
- Deployment branches and tags: **Selected branches and tags**, with the tag rule `v*` only.
- No environment secrets. Publishing to NuGet needs none.

### nuget.org

1. Turn on two-factor authentication for the nuget.org account, and the GitHub account too (a passkey where possible).
2. Create no API key. Delete any that exist. Put no NuGet secret in GitHub (T23).
3. Account → Trusted Publishing → add a policy (https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):
   - Repository owner `YUKIKEDA`, repository `ElasticLM`, workflow file `release.yml`, environment `release`.
   - Packages: `ElasticLM.*`.
   - The first release creates the two packages, so the policy needs the scope that allows new packages. After the first
     release, narrow it to new versions of existing packages.
4. Before V1e, decide whether to reserve the ID prefix `ElasticLM`
   (https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation).

## Cutting a release

1. Open a pull request that sets `VersionPrefix` in `Directory.Build.props` and adds a `CHANGELOG.md` section for the
   version, `## [<version>] - <date>`. A prerelease tag (`v1.0.0-rc.1`) needs its own section (`## [1.0.0-rc.1]`).
   Merge it.
2. Check the container images pinned in `release.yml` (`ALMALINUX_IMAGE`, `ALPINE_SDK_IMAGE`, `BUILDKIT_IMAGE`):
   Dependabot does not update a digest in a workflow's `env`. Take the current digest of `almalinux:8`, of
   `mcr.microsoft.com/dotnet/sdk:<DOTNET_SDK_VERSION>-alpine3.24-aot`, and of the latest `moby/buildkit` release (for
   example with `docker buildx imagetools inspect <image>`), and update them in a pull request if they changed. Raising
   `DOTNET_SDK_VERSION` always changes the Alpine image's tag and digest. The base of the `elm-service` image, in
   `eng/container/Dockerfile`, is raised by Dependabot's pull requests; merge the open one before the release.
3. Release a candidate first: tag the merge commit on `main` with `v<version>-rc.1` and push the tag.
4. `release.yml` checks that the tag is `VersionPrefix` (or `VersionPrefix-<suffix>`), that the commit is on `main`, and
   that `CHANGELOG.md` has the section. It builds the seven runtime identifiers and the packages, checks them, writes
   `SHA256SUMS`, attests every file, and creates a **draft** GitHub Release.
5. Open the draft. Check the files, the notes, and the job summaries (sizes, runtime dependencies, glibc version).
6. Approve the `release` environment in the run. The `publish` job pushes the `.nupkg` and `.snupkg` from the draft to
   nuget.org after checking them against `SHA256SUMS`, and then publishes the release.
7. If the push succeeds and publishing the release fails, re-run the `publish` job: the push skips versions that exist.
8. When the candidate is good, tag `v<version>` the same way.

A run that fails before the draft leaves nothing behind. Fix the cause on `main` and release the next version: a tag is
never moved.

## The container image

The `publish` job pushes the `elm-service` image to `ghcr.io/yukikeda/elm-service` (design §20.6). A package that is
published for the first time is private.

1. After the first release pushes it, open the package's settings on GitHub and make it public. This cannot be undone
   (https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility).
2. In the same settings, give Actions access to this repository only.
3. Create no personal access token (classic) with `write:packages` or `delete:packages`: GitHub Packages accepts only
   classic tokens, and such a token could push or move a tag without the workflow (design §5.3 T26).

## What users can check

`SECURITY.md` tells users how to check what they download: the `SHA256SUMS`, the attestation with
`gh attestation verify`, and how macOS treats the binaries, which are not notarized.
