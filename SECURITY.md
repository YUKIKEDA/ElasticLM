# Security Policy

If you find a vulnerability, **do not write it in a public Issue or pull request**.

Report it through [GitHub Security Advisories](https://github.com/YUKIKEDA/ElasticLM/security/advisories/new). No personal email address is published.

Response targets (best effort):

- Acknowledgement: within a few days
- Plan for a fix: depending on severity

ElasticLM handles signing keys, license tokens, and activation data. Do not include real private keys, license tokens, customer identifiers, or machine IDs in a report; generate test keys and tokens for the reproduction.

## Checking a download

Every file of a GitHub Release is listed in its `SHA256SUMS`, and every file and `SHA256SUMS` itself carry a build
provenance attestation made by the release workflow. To check a file you downloaded:

```sh
sha256sum --check --ignore-missing SHA256SUMS
```

```text
gh attestation verify <file> --repo YUKIKEDA/ElasticLM --signer-workflow YUKIKEDA/ElasticLM/.github/workflows/release.yml --source-ref refs/tags/v<version> --deny-self-hosted-runners
```

Run them in the folder that holds the downloaded files and `SHA256SUMS`, and run `gh attestation verify` once for each
file. `<version>` is the release's tag without its leading `v`, as in the file names: for the tag `v1.0.0-rc.1`, the
version is `1.0.0-rc.1` and the option is `--source-ref refs/tags/v1.0.0-rc.1`. The `gh attestation verify` line works
the same in PowerShell, Bash, and zsh.

On macOS, `sha256sum` may be missing; the `shasum` that comes with macOS takes the same options when given `-a 256`.
On macOS 15.8 and 26.7, both `sha256sum` and this line print `OK` for a listed file that matches and `FAILED` for one
that does not, skip listed files that are not in the folder, and exit with 1 when any file fails:

```sh
shasum -a 256 --check --ignore-missing SHA256SUMS
```

PowerShell on Windows has no `sha256sum`. This line checks every archive and package in the folder against
`SHA256SUMS`, and prints `OK` or `FAILED` with each file's name:

```powershell
Get-ChildItem *.zip, *.tar.gz, *.nupkg | ForEach-Object { $h = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower(); if (Select-String -SimpleMatch -Quiet "$h  $($_.Name)" SHA256SUMS) { "OK $($_.Name)" } else { "FAILED $($_.Name)" } }
```

It lowers the case of the hash, because PowerShell prints it in upper case and `SHA256SUMS` holds it in lower case.
`gh attestation verify` needs the GitHub CLI (https://cli.github.com/).

`--repo` alone does not check which workflow signed the file; keep the other options
(https://cli.github.com/manual/gh_attestation_verify). The attestation shows that the release workflow built the file
from that tag. It does not show that the code is free of defects.

The NuGet packages are signed by nuget.org when they are pushed, which changes their bytes: check a package you got from
nuget.org with `dotnet nuget verify`, not against `SHA256SUMS`. A `.nupkg` file you download from a GitHub prerelease
has no nuget.org repository signature, and its bytes are those the release workflow attached to the release: check it
like the other files, against `SHA256SUMS` and with `gh attestation verify`.

The executables and `libelasticlm` have no Authenticode or Developer ID signature (the `osx-arm64` files carry only the
ad-hoc signature that the linker adds). Windows SmartScreen may warn when you run them. macOS Gatekeeper does not open
downloaded software that is not notarized; after checking the file as above, remove the quarantine attribute with
`xattr -d com.apple.quarantine <file>`. The Linux binaries need glibc 2.28 or newer (RHEL 8, Debian 10, Ubuntu 20.04
and later), or musl for `linux-musl-x64`; the macOS binaries need macOS 12 or newer. On Windows,
`elm-server` and the `elm server` commands need Windows 10 version 1803 or Windows Server 2019, or newer.

## Supported versions

Nothing is released yet. After the first release, security fixes go to the latest minor version.
