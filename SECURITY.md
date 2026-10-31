# Security Policy

If you find a vulnerability, **do not write it in a public Issue or pull request**.

Report it through [GitHub Security Advisories](https://github.com/YUKIKEDA/ElasticLM/security/advisories/new). No personal email address is published.

Response targets (best effort):

- Acknowledgement: within a few days
- Plan for a fix: depending on severity

ElasticLM handles signing keys, license tokens, and activation data. Do not include real private keys, license tokens, customer identifiers, or machine IDs in a report; generate test keys and tokens for the reproduction.

## Checking a download

Every file of a GitHub Release is listed in its `SHA256SUMS`, and every file and `SHA256SUMS` itself carry a build
provenance attestation made by the release workflow (design §20.5). To check a file you downloaded:

```sh
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify <file> --repo YUKIKEDA/ElasticLM \
  --signer-workflow YUKIKEDA/ElasticLM/.github/workflows/release.yml \
  --source-ref refs/tags/v<version> --deny-self-hosted-runners
```

`--repo` alone does not check which workflow signed the file; keep the other options
(https://cli.github.com/manual/gh_attestation_verify). The attestation shows that the release workflow built the file
from that tag. It does not show that the code is free of defects.

The NuGet packages are signed by nuget.org when they are pushed, which changes their bytes: check a package you got from
nuget.org with `dotnet nuget verify`, not against `SHA256SUMS`.

The executables and `libelasticlm` are not code-signed. Windows SmartScreen may warn when you run them. macOS Gatekeeper
does not open downloaded software that is not notarized; after checking the file as above, remove the quarantine
attribute with `xattr -d com.apple.quarantine <file>`. The Linux binaries need glibc 2.28 or newer (RHEL 8, Debian 10,
Ubuntu 20.04 and later), or musl for `linux-musl-x64`; the macOS binaries need macOS 12 or newer.

## Supported versions

Nothing is released yet. After the first release, security fixes go to the latest minor version.
