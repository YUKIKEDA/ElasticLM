---
sidebar_position: 1
---

# Overview

This page is for everyone who meets ElasticLM for the first time: vendors who issue licenses, app developers who embed the client in a .NET or C app, and customers' administrators who run a license server. You need no knowledge of ElasticLM. It helps to know what a public and a private key are. The page says what ElasticLM is for, what the six deliverables are, who runs each one, and which ways you can put them together. The other explanation pages go deeper into each part.

## What ElasticLM is for

ElasticLM lets the vendor of a .NET or C/C++ app issue licenses for its products, check those licenses on the customer's machines, and count how many people use the product at the same time. It is open source (MIT) and ships as NativeAOT single binaries and NuGet packages.

ElasticLM puts one goal first: stop three kinds of abuse with cryptography rather than with secrecy.

- **A forged or edited license.** A license is signed by the vendor. An app accepts only licenses whose signature leads back to a key the app was built to trust.
- **A forged or replayed request.** Every request that changes state on a server is signed by a key that belongs to one installation of the app. It also carries a recent server nonce (a random value the server handed out a short time before) and a one-time ID, so a captured request cannot be sent again.
- **Use beyond the seats that were bought.** Servers count seats under a lock, and a client checks that what a server lends it fits in the license the vendor signed.

ElasticLM does not try to stop a person who changes the app's own binary so that it skips the check; the [security model](security-model.md) explains why, and lists everything that is and is not protected.

## The people

The rest of the guide uses these names.

| Name | Who or what |
| --- | --- |
| Vendor (issuer) | The organization that uses ElasticLM to license its product. It holds the root key and runs the hosted activation service. |
| Vendor's administrators | The vendor's staff. They issue and revoke licenses in the admin UI, with the `elm` CLI, or through the administration API. Their roles limit what each can do. |
| Vendor's systems | The vendor's sales, billing, or CI systems. They call the administration API with an API key. |
| Licensee (customer) | The organization or person who bought the product. |
| Customer's administrator | The person at the customer who runs the on-premises license server. |
| App | The vendor's product. It embeds the verification library, or the client (.NET or C). |

## The six deliverables

| Deliverable | Form | Used by | What it does |
| --- | --- | --- | --- |
| `ElasticLM.Licensing` | NuGet package | The app | Verifies a license file offline. It has no dependency outside the .NET base class library and never touches the network. |
| `ElasticLM.Client` | NuGet package | The app | Talks to the servers: online and offline activation, refresh, deactivation, trials, leases, and borrowing. It creates and keeps the installation key. |
| `libelasticlm` | NativeAOT shared library (`.dll`, `.so`, `.dylib`) and the C header `elasticlm.h`; on Windows also the import library `elasticlm.lib` | C and C++ apps | The functions of both packages behind a C ABI. |
| `elm-server` | Single binary | The customer's administrator | The on-premises license server. It lends floating seats inside the customer's network. |
| `elm-service` | Single binary and container image | The vendor | The hosted activation service: license keys, activations, trials, cloud leases, the administration API, and the admin UI. |
| `elm` | Single binary (CLI) | The vendor and the customer's administrator | Creates keys and key certificates, issues and inspects license files, verifies logs, calls the administration API, and administers a local `elm-server`. |

[.NET API](../reference/dotnet-api.md) lists the types of the two packages, and [C API](../reference/c-api.md) the functions of `libelasticlm` and how to link it.

The executables run with no files beside them; a settings file is optional. They are built for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64`, and `osx-x64`, and `libelasticlm` for the same targets. The NuGet packages are AOT-compatible, so an app that embeds them can itself be published with NativeAOT without warnings.

The app embeds only `ElasticLM.Licensing` and `ElasticLM.Client`, or `libelasticlm`. The code that signs licenses lives only in `elm`, `elm-server`, and `elm-service`, and never ships inside the app.

For the exact commands and settings of the three executables, see the [elm command reference](../reference/elm-cli.md), [elm-server configuration](../reference/server-configuration.md), and [elm-service configuration](../reference/service-configuration.md).

## The core idea: a signed license, checked offline

Everything else is built on one mechanism. A **license token** is a small JSON document that says which product, which features, until when, and, if the license is node-locked, which device. The vendor signs it with ECDSA P-256 (ES256). A **license file** (`.elmlic`) holds the license token and the key certificate needed to check it.

The app checks the license file with nothing but the file, the clock, and a **trust store**: the vendor's root public keys, built into the app. The chain is short:

1. The **root key** is the vendor's top key. It is kept offline and signs only key certificates.
2. A **key certificate** says "this signing key may sign licenses (or leases, or logs) until this date", signed by the root key.
3. A **signing key** signs the license tokens. It is the key that `elm-service` or `elm` holds.

Because the app trusts only the root key, the vendor can replace a signing key without shipping a new app. The [trust chain](trust-chain.md) page explains this in full, and [license files and verification](license-files-and-verification.md) gives the order of checks.

The license is signed, not encrypted. Anyone who holds the file can read it; nobody without the signing key can change it or make a new one.

The online parts of ElasticLM exist to issue and refresh these signed tokens, and to lend seats. When the network is gone, an app keeps working for as long as its token, its grace period, or its lease allows.

## How the parts fit together

The figure shows every deliverable and who talks to whom. The vendor's side is on the left; the customer's side is on the right.

```mermaid
flowchart LR
    subgraph Vendor["Vendor"]
        Root[("Root key, kept offline")]
        CLI1["elm"]
        Svc["elm-service: REST API and admin UI"]
        DB[("SQLite or PostgreSQL")]
        IdP["Identity provider (OIDC)"]
        Billing["Sales and billing system"]
    end
    subgraph Customer["Customer"]
        Srv["elm-server"]
        App1[".NET app with ElasticLM.Client"]
        App2["C/C++ app with libelasticlm"]
        CLI2["elm"]
    end
    Root -. "key certificates" .-> CLI1
    CLI1 -- "signing key certificate" --> Svc
    Svc --- DB
    IdP -- "admin login" --> Svc
    Billing -- "API key" --> Svc
    App1 -- "activation, refresh, trial, cloud leases" --> Svc
    App2 -- "activation, refresh, trial, cloud leases" --> Svc
    Srv -- "server activation and refresh" --> Svc
    App1 -- "leases" --> Srv
    App2 -- "leases" --> Srv
    CLI2 -- "local admin API" --> Srv
```

Read the figure like this:

- The vendor uses `elm` with the root key to create a signing key and its key certificate, and gives both to `elm-service`.
- `elm-service` keeps products, features, policies, licenses, activations, and leases in SQLite or PostgreSQL. Vendor administrators log in through the vendor's identity provider; the vendor's systems use API keys.
- Apps talk to `elm-service` to activate a license key, refresh their token, start a trial, or borrow a seat from the cloud.
- An `elm-server` at the customer is activated by `elm-service` like any other device. It then lends seats to the apps on the customer's network, and the customer's administrator manages it with `elm` from the same machine.

## Deployment shapes

You do not have to run every part. There are four shapes, and a vendor may use more than one at once. The most common combination, a hosted service that activates on-premises servers, is described after the four.

| Shape | Parts | In short |
| --- | --- | --- |
| Offline only | `elm`, `ElasticLM.Licensing` | The vendor makes license files with the CLI and hands them over. No service runs. |
| Hosted activation | `elm-service`, `ElasticLM.Client` | The customer activates with a license key and the app refreshes its token from time to time. |
| Cloud floating | `elm-service`, `ElasticLM.Client` | Apps borrow seats (leases) from the vendor's service. |
| On-premises floating | `elm-server`, `ElasticLM.Client`, and `elm-service` or `elm` to activate the server | Apps borrow seats from a server inside the customer's network. |

"Lease" means a seat lent for a short, fixed time. To pick a shape:

```mermaid
flowchart TD
    S{Does the license limit concurrent users, by seats?} -- no --> V{Does the vendor run elm-service?}
    V -- no --> OFF[Offline only]
    V -- yes --> HA[Hosted activation]
    S -- yes --> R{Can the apps reach the vendor's service,<br/>and may the seats live there?}
    R -- yes --> CF[Cloud floating]
    R -- no --> OP[On-premises floating]
    OP --> L{Does the vendor run elm-service?}
    L -- yes --> LS[The server activates its license with elm-service]
    L -- no --> LE[The vendor issues the server license with elm]
```

- If the license limits only who may run the app, on which device, and until when, use **offline only** when the vendor runs no service, or **hosted activation** when it does.
- If the license limits concurrent users (seats), use **cloud floating** when the apps can reach the vendor's service, or **on-premises floating** when they cannot or the customer wants the seats on site.
- An on-premises server gets its server license either from `elm-service` (see "On-premises floating with elm-service", below) or from `elm`, issued by hand.

### Offline only

The vendor runs `elm license issue` and sends the `.elmlic` file to the customer. The app checks it with `ElasticLM.Licensing` (or `libelasticlm`).

The file can be node-locked to one device. Each device has an **installation key**: a key pair that the client creates on the device and keeps there, and whose **thumbprint** (a short hash of the public key) names the device. Each device also has **machine fingerprints**, values derived from the operating system's machine ID. [Devices and proofs](devices-and-proofs.md) explains both. `elm license issue` takes `--bind-key` (the thumbprint) and `--fingerprint` (the fingerprints), which the vendor must first get from the customer's device. `elm` has no command that prints them for an app's device, so the app must show them: in .NET, `InstallationKey.Thumbprint` from an `InstallationKeyStore` and `MachineFingerprint.Compute(productId)`; C exposes only the thumbprint, with `elm_installation_thumbprint`. Without `--bind-key` and `--fingerprint`, the file works on any device.

There is no refresh and no revocation in this shape. A file is good until its expiry plus its grace period (`elm license issue --grace-days`, 7 days by default; a trial always has 0). A file issued with `--expires never` is good forever; `elm` warns when it writes one. Choose expiry dates with that in mind.

### Hosted activation

The vendor runs `elm-service`, defines products and policies, and issues a **license key** (a string such as `ELM-7Q2KX-M9D4R-HV0T8-3NPWZ`) for each sale. The service stores only the SHA-256 hash of the key and shows the key once, when it is issued. In the app, `ElasticLM.Client` (`ActivationClient`) activates that key: it creates the device's installation key, signs the request with it, and receives a license file bound to that key. The policy's `maxActivations` limits how many devices can activate one license.

The token in that file expires (by default after 30 days, `tokenLifetime`), and the client refreshes it online before then; if it cannot, the license still works for the grace period (by default 7 days, `gracePeriod`). This short life is how revocation reaches a device: a revoked or suspended license gets no new token. The same service handles deactivation, trials (one per product and machine), and offline activation by carrying request and response files by hand. See [devices and proofs](devices-and-proofs.md) and [activation](activation.md).

`elm-service` can also produce a license file that is not bound to any device (`binding`: `unbound`) from its administration API, for customers who will never go online.

### Cloud floating

A policy with `floating`: `cloud` gives each feature a number of seats. The app's `LicenseClient`, given the license key, asks `elm-service` for a lease, renews it every third of the lease length (the default lease is 10 minutes), and returns it when done. The service keeps leases in its database, so a restart of the service does not lose them. See [floating licenses](floating-licenses.md).

### On-premises floating

The customer's administrator runs `elm-server` on a machine in their own network. The server has its own installation key and a **server license**: a license token of kind `server` that names the server's key, the features, and the seats for each. `elm-server` prints its thumbprint and fingerprints when it first starts without a license, so the administrator can send them to the vendor. The server lends seats only within that license, and signs each lease with its own installation key. Apps use `LicenseClient` pointed at the server and check every lease against the server license.

Those checks limit what a stolen server key can do. A person who takes the server's installation key out of its machine can sign leases only inside the server license's product, features, seat count per lease, lease length, and end. They can, however, lend the same seats to more clients than were bought, for example from a copied server. The server license's expiry, and the refresh that renews it, are what end that.

`elm-server` keeps its leases in memory and has no database. After a restart, clients hand their still-valid leases back during a short reclaim window. Only borrowed seats, which leave the network, are written to a signed record on disk. There is no high-availability pair: the operating system's service manager restarts the server, and running apps hold leases long enough to ride out the restart.

### On-premises floating with elm-service

When the vendor runs `elm-service` and a customer runs `elm-server`, the server activates its server license with the service like any device (`elm server activate`, or a request file when the server has no internet access). The server license expires (by default after 30 days) and the server refreshes it from the service, online when `service.url` is set, or through files (`elm server refresh-request`, then `elm server import`). Each refresh lets the service notice a copied server.

Without `elm-service`, the vendor issues the server license by hand with `elm license issue --kind server`. Such a license is never refreshed, so a copied server is not detected; only the license's expiry ends a copy's use. `elm` warns when such a license has no expiry.

## Who holds which secret

Most of what keeps ElasticLM safe is about where private keys and secrets live. The table gives the main ones.

| Secret | Who holds it | Where |
| --- | --- | --- |
| Root key | The vendor | A passphrase-protected PKCS#8 PEM file, kept offline. Only `elm` reads it, to sign key certificates. It never goes on a server. |
| Signing key | The vendor | Given to `elm-service` as an encrypted file with its passphrase, or read by `elm` to issue license files. |
| Installation key | Each device | Created by the client on first use and stored per product: with DPAPI on Windows, with file mode `0600` on Linux and macOS. |
| `elm-server`'s installation key and admin token | The customer's administrator | The server's data folder, readable only by Administrators, SYSTEM, and the account that created it (Windows), or by its owner (Linux and macOS). |
| License key | The customer | Typed in by a person. `elm-service` stores only its hash. |

No vendor private key is built into a binary or a resource. The app carries only the vendor's root public keys. The only private key on a customer's device is that device's own installation key, which the client creates there; `elm-server` likewise creates its own installation key and admin token.

## What can go wrong, and what you must do

Each role has a few things that only it can do. The pages that follow explain why.

**Vendors**

- Keep the root key offline and its passphrase secret. With the root key, anyone can certify a signing key and make any license.
- Build your root public key into the app's trust store. If a signing key leaks, you revoke it by listing its key ID in the trust store, which reaches devices only through an app update. See the [trust chain](trust-chain.md).
- Choose token lifetimes and lease lengths on purpose. A revoked license keeps working until its token and grace period run out, and a token with no expiry cannot be revoked, except by revoking the key that signed it. The [security model](security-model.md) explains this.
- If you issue server licenses by hand, give them an expiry. Such a license is never refreshed, so the expiry is the only thing that ends a copied server's use.

**App developers**

- Embed only the vendor's root public keys. A private key in an app lets anyone who has the app make licenses.
- Decide what the app does when verification fails, when it is in its grace period, and when a lease is lost. ElasticLM reports these states; the app decides what to stop.
- For node-locked license files issued by hand, show the device's installation key thumbprint and machine fingerprints in the app, so the customer can send them to the vendor.
- Do not count on ElasticLM to stop someone who patches your binary; see the [security model](security-model.md).

**Customers' administrators**

- Run `elm-server` under the operating system's service manager (a Windows service, systemd, or launchd) so that it restarts on its own. With the default 10-minute lease, apps keep working if the server is back within about 6 minutes 40 seconds. `elm-server` aims to listen within 1 second of starting; on a Linux test machine it took about 0.1 s.
- Keep the server license fresh: let the server refresh online, or carry refresh files at least once every 37 days with the default 30-day token and 7-day grace period.
- Keep the server's data folder private. It holds the server's installation key, the admin token, and the borrow record.

**When the network goes away**

| Shape | The app keeps working until |
| --- | --- |
| Offline only | Not affected: the file needs no network and works until its expiry plus its grace period. |
| Hosted activation | The last token's expiry plus its grace period: up to 37 days after the last refresh with the defaults. |
| Cloud or on-premises floating | The current lease ends: at most one lease length, by default 10 minutes. A borrowed lease lasts until its own end. |

## Where to read next

- [Trust chain](trust-chain.md): root key, signing keys, key certificates, the trust store, rotation and revocation.
- [License files and verification](license-files-and-verification.md): the file format, the order of checks, the grace period, and clock rollback.
- [Devices and proofs](devices-and-proofs.md): installation keys, signed requests, nonces, and machine fingerprints.
- [Activation](activation.md): online and offline activation, refresh, deactivation, and clone detection.
- [Floating licenses](floating-licenses.md): leases, reclaim after a restart, borrowing, and seat limits.
- [Security model](security-model.md): what is protected, from whom, and what is not.
- [Design choices](design-choices.md): why ElasticLM is built this way.
