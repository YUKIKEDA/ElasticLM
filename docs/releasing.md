# Releasing ElasticLM

This is the owner's guide to the release path of design §20.5 (decision 0.109) and the threats it holds against (§5.3
T22–T25, and T28 for the documentation site of §20.7). `.github/workflows/release.yml` builds, checks, and attests every release; nothing is published without the
owner's approval. Nothing has been published yet: the first release is roadmap row V1e.

## One-time setup

Do these once, before the first tag. Each step closes a gap the threat model names.

Some of them are done. While `release.yml` is on `main`, a `v*` tag must not start the `publish` job without the
owner's approval, so the owner set the environment `release` and the tag rulesets before V1e (roadmap row V1d20,
Issue #339). Read with `gh api` on 2026-11-28 at 11:14Z (UTC):

| Step | State |
| --- | --- |
| Repository, steps 1 to 3 | Done. The owner is the only collaborator, the default workflow permissions are `read`, and both tag rulesets are active |
| Environment `release` | Done |
| Repository, steps 4 and 5 (immutable releases, `NUGET_USER`) | In V1e |
| nuget.org | In V1e |
| Documentation site (Pages, environment `github-pages`) | In V1e |

### Repository

1. Keep write access to the owner alone: add no collaborators (T22). Anyone with write access could push a tag.
2. Settings → Actions → General → Workflow permissions: **Read repository contents and packages permissions**. Each job of
   `release.yml` asks for what it needs.
3. Settings → Rules → Rulesets → New tag ruleset, twice. Both target tags matching `v*` and are **Active**:
   - `release`: **Restrict updates**, **Restrict deletions**, and **Block force pushes**. Bypass list: empty. Nobody,
     the owner included, can move or remove a `v*` tag; a failed release gets the next version.
   - `release-create`: **Restrict creations** and **Block force pushes**. Bypass list: **Repository admin**, mode
     **Always**. Only the owner can create a `v*` tag; the workflows' `GITHUB_TOKEN` cannot.

   One ruleset cannot do both. "Restrict creations: If selected, only users with bypass permissions can create branches
   or tags whose name matches the pattern you specify"
   (https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets),
   so a ruleset with an empty bypass list lets nobody create a tag, and a ruleset whose bypass list names the owner lets
   the owner move and remove tags too. With two rulesets, "the rules in each of these rulesets are aggregated", and a
   bypass covers "the rules in the ruleset" that grants it
   (https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets).
   The owner's bypass of `release-create` therefore lifts only the creation rule; `gh api` reads
   `current_user_can_bypass` as `always` for `release-create` and `never` for `release`. Moving or removing a tag as
   the owner has not been tried: no `v*` tag exists, and pushing one starts `release.yml`.

   Whatever holds the owner's credentials can create a `v*` tag, the owner's `gh` login and the agents that use it
   included. Publishing still waits for the approval of the environment `release`, but a token with the `repo` scope
   can give that approval through the REST API too (design §5.3 T22, and §5.4 for the owner's account).
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
4. In V1e, before the pull request that switches the user documentation to nuget.org, decide whether to reserve the ID
   prefix `ElasticLM.` or to publish first and so take the names
   (https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation).

### Documentation site (GitHub Pages)

The `pages` job of `release.yml` publishes the user documentation to `https://yukikeda.github.io/ElasticLM/` after the
release is published (design §20.7, T28).

1. Settings → Pages → Build and deployment → Source: **GitHub Actions**
   (https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).
   Never switch it back to "Deploy from a branch": a branch source publishes on a push, without the workflow and its
   checks.
2. Settings → Environments → `github-pages` (GitHub creates it with the Pages source; create it if it is missing):
   - Required reviewers: the owner. **Prevent self-review**: off.
   - **Allow administrators to bypass configured protection rules**: off.
   - Deployment branches and tags: **Selected branches and tags**, with the tag rule `v*` only. Remove any rule GitHub
     added (it may allow only the default branch, which would refuse a tag's run).
   - No environment secrets.
3. Check the settings:
   - `gh api repos/YUKIKEDA/ElasticLM/pages --jq .build_type` prints `workflow`.
   - `gh api repos/YUKIKEDA/ElasticLM/environments/github-pages` shows the owner as the required reviewer and
     `can_admins_bypass` as `false`.
   - `gh api repos/YUKIKEDA/ElasticLM/environments/github-pages/deployment-branch-policies --jq '.branch_policies[] | [.type, .name]'`
     prints only `["tag","v*"]`.
   - Once the site is published, open it: GitHub serves `github.io` sites over HTTPS
     (https://docs.github.com/en/pages/getting-started-with-github-pages/securing-your-github-pages-site-with-https).
4. Do not create a repository named `yukikeda.github.io`. A user site there would share the documentation's origin and
   could take over its pages with a service worker (design §5.4).

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
3. Release a candidate first: tag the merge commit on `main` with `v<version>-rc.1` and push the tag. Do not push any
   `v*` tag, an rc tag included, before V1e's change to `release.yml` has merged (design §20.5, "V1e で変えること"): until then, `publish`
   runs on every `v*` tag and would push the rc's packages to nuget.org and its image to GHCR, while the user
   documentation says that a prerelease pushes neither.
4. `release.yml` checks that the tag is `VersionPrefix` (or `VersionPrefix-<suffix>`), that the commit is on `main`, and
   that `CHANGELOG.md` has the section. It builds the seven runtime identifiers and the packages, checks them, writes
   `SHA256SUMS`, attests every file, and creates a **draft** GitHub Release. A job that takes files from another
   job checks them against the SHA-256 lines that job gave as its output, and that there are no other files; a job
   whose check fails stops the run before the draft. One download is not checked this way: the site's artifact,
   which the `pages` job deploys (design §20.5, "ジョブの間の artifact の確かめ"; §5.4).
5. Open the draft. Check the files, the notes, and the job summaries (sizes, runtime dependencies, glibc version). Each
   `libelasticlm-<version>-<rid>` archive holds the library, `elasticlm.h`, and `LICENSE` at its top; on `win-*` it also
   holds the import library `elasticlm.lib`, which stays out of the `-symbols` archive (decision 0.118). The build job
   has checked that the two `win-*` zips hold exactly these files and `elasticlm.pdb`.
6. Approve the `release` environment in the run. The `publish` job downloads every file of the draft and checks it
   before it pushes anything: `SHA256SUMS` against the `checksums` job's hash of it, every file against `SHA256SUMS`,
   and that the draft holds exactly the files `SHA256SUMS` lists and `SHA256SUMS` itself. It checks the image tar
   against the `image` job's hash too. Then it pushes the `.nupkg` and `.snupkg` to nuget.org and the image to GHCR.
   Right before it publishes the release, it reads the draft's files again and compares each name and GitHub's SHA-256
   of each file with the checked lines (design §20.5, "公開のジョブの確かめ"). Do not add or replace a file of the draft:
   the job stops on any file `SHA256SUMS` does not list, and if it stops at the last check, the packages and the image
   are already pushed. Do not edit the draft's notes, title, or prerelease setting either: the job publishes them as
   they are, unchecked.
7. If the push succeeds and publishing the release fails, re-run the `publish` job: the push skips versions that exist.
8. After `publish`, approve the `github-pages` environment in the same run. The `pages` job deploys the site the `site` job
   built from the tag. The site's artifact is kept for 7 days: approve both environments within that time. If the `pages` job fails because the artifact expired, re-run the `site` job of the same run: `gh run rerun <run ID> --job <site job ID>` (the job IDs are in `gh run view <run ID> --json jobs`). GitHub runs `site` again from the same tag, then `pages`, which needs the approval again; the jobs `site` does not depend on, `publish` among them, are not run again. A run can be re-run for up to 30 days. The tag's run does not run `pnpm audit`, so an advisory published since the tag does not stop it.
9. When the candidate is good, tag `v<version>` the same way. Until roadmap row A-6, each tag's site replaces the one
   before it; that row comes before any tag after `v1.0.0`.

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
