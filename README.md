English | [日本語](README.ja.md)

# ElasticLM

A software licensing platform for .NET and C/C++ applications. Vendors issue signed licenses; apps verify them offline, activate them through a hosted service, or borrow floating (concurrent) seats from an on-premises license server. Every part is NativeAOT-compatible and ships as a single binary, and all cryptography comes from .NET's own libraries.

> **Status:** 1.0 is not released yet. The commands and APIs below are those of the `main` branch.

## What it does

- **Offline verification.** A license file carries a license token signed with ECDSA P-256 and the certificate of the key that signed it. Your app checks both against the root keys it embeds, with no network.
- **Activation.** The hosted service `elm-service` binds a license to a device's installation key, counts devices against the license's limit, refreshes short-lived tokens, and handles offline activation by file exchange and trials.
- **Floating licenses.** The on-premises server `elm-server`, or `elm-service`, leases seats for a short time and renews them automatically. Seats can be borrowed for use away from the network.
- **Operation.** `elm-service` has an administration API and an admin UI, signs in administrators with OIDC, keeps an audit log, and runs on SQLite or PostgreSQL. Usage logs are signed.

## What you get

| Deliverable | Form | Used by |
| --- | --- | --- |
| `ElasticLM.Licensing` | NuGet package: offline verification | Apps |
| `ElasticLM.Client` | NuGet package: activation, leases, borrowing, trials | Apps |
| `libelasticlm` | NativeAOT shared library and C header | C and C++ apps |
| `elm-server` | On-premises floating license server, single binary | The customer's administrator |
| `elm-service` | Hosted activation service with the admin UI, single binary and container image | The vendor |
| `elm` | CLI for keys, licenses, the administration API, and on-premises servers | The vendor and the customer's administrator |

Builds are made for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`.

## Try it

With `elm` on your path, create a root key, a signing key certified by it, and a license, then verify the license. `elm` encrypts each private key with a passphrase: it reads it from `ELM_KEY_PASSPHRASE`, or asks for it when the variable is not set.

<!-- readme-steps:start -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
elm key signing create --out signing.key.pem --public-out signing.pub.pem
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
elm license verify first.elmlic --trust root.pub.pem --product acme-cad --feature pro
```
<!-- readme-steps:end -->

The root key only certifies signing keys: keep `root.key.pem` offline. Your app embeds `root.pub.pem` and verifies `first.elmlic` the way the last command does.

## Documentation

The user documentation is in [`docs/guide/`](docs/guide/README.md). It explains how ElasticLM works, how its parts trust each other, and what it protects against. From 1.0 it is also published as a site.

The design, in Japanese, is in [`docs/design.ja.md`](docs/design.ja.md), and the plan in [`docs/roadmap.md`](docs/roadmap.md). Security reports go through [`SECURITY.md`](SECURITY.md).

## License

[MIT](LICENSE)
