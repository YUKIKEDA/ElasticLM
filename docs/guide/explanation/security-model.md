---
sidebar_position: 7
---

# Security model

This page is for everyone who has to decide whether ElasticLM protects what they need and what they must do themselves: vendors who hold the keys and run `elm-service`, app developers who embed the client, and customers' administrators who run `elm-server`. It lists what is protected, from whom, by what, and what is not protected at all. It is written from the threat model in design §5. It helps to have read [the overview](overview.md) and [the trust chain](trust-chain.md) first; the other explanation pages give the mechanisms in full.

## What the security model is for

A licensing system is attacked mostly by the people it serves. A customer has the app, a real license, and full control of their own machine, and some customers would like more than they paid for. ElasticLM's first aim is to stop, with cryptography rather than secrecy, three things (design §1.1):

- forged or edited licenses,
- forged or replayed requests,
- more concurrent seats than were bought.

It does not try to stop a person from changing the app's own code. Anything that runs on a machine the attacker controls can be changed by that attacker. The protection is in what the attacker cannot sign.

## What is protected

These are the assets in design §5.1, with what is lost if an attacker gets them.

| Asset | If an attacker gets it |
| --- | --- |
| The root key and the signing keys | They can make any license, lease, or key certificate the apps will accept. |
| The terms of a license | Expiry, features, number of machines, and number of seats stop meaning anything. |
| A license key | They can activate with it. |
| An installation key | They can act as that device. |
| Admin access to `elm-service` | They can issue, deactivate, and revoke licenses. |
| An administrator's login (`elm admin login` tokens, the admin UI session cookie `__Host-elm-session`) | They can use the admin API as that administrator until the login expires. |
| The admin UI's client secret, the Data Protection keys and their key-encryption key | Depending on the identity provider's settings, a way to admin tokens, or to the half-finished logins of other administrators. |
| The usage log and the audit log | The evidence for billing and disputes. |
| The release artifacts and the right to publish them | A fake `elm-server`, library, or package can skip every check on the customer's machine. A fake `elm` or `elm-service` can read the vendor's signing key. |
| This documentation site and the right to publish it | An edited page can point readers to fake downloads, tell them to skip a check, or recommend a weaker setting. |

## Who the attackers are

Design §5.2 names fifteen attackers. Grouped for a reader:

| Group | Attackers | What they hold |
| --- | --- | --- |
| The licensee | A1, a dishonest customer | The app, a real license file or license key, and administrator rights on their own machine |
| On the network | A2 | The ability to read, change, and replay traffic |
| Inside the customer's company | A3, another person on the network; A6, another user of the `elm-server` machine | A3 reaches the server over the network. A6 can log on to the server machine without admin rights and send requests to loopback, including from a browser. |
| Outside the vendor | A4 | The internet, and so the admin API of `elm-service` |
| Around the vendor's administrators | A5, a former or low-role admin; A8, another user of an admin's machine; A9, the owner of another app at the same identity provider; A10, someone running a site on the same host name or domain as the admin UI | Old credentials or a low role; a local logon; tokens an admin gave to another app; the ability to set cookies in the admin's browser |
| The supply chain | A7 and A15, whoever takes over an npm package of the admin UI or of this site; A11, an outsider who sends pull requests; A12, a thief of one of the owner's credentials; A13, whoever takes over a build tool, a GitHub Action, the .NET SDK, or the container base image; A14, whoever swaps files on a mirror, a download site, a company share, or a registry cache | A new version of a package or tool; a pull request; a stolen token; a file placed where users download |

## The threats and what stops each

The threats are T1 to T28 in design §5.3. Each group below names the attacker, what they hold, and what stops them.

### Forged and edited licenses (T1, T2, T3, T12)

A dishonest licensee (A1) edits a license file to add a feature or move the expiry, or writes one from scratch.

- Every token is signed with ES256 and verified over the exact bytes received. An edited payload fails signature verification. The key used is only one that chains from a root key in the app's trust store through a key certificate (T1; see [license files and verification](license-files-and-verification.md)).
- The `alg` in the token header must match the key's record; it never chooses the key or the algorithm, so `none` or a swapped algorithm is refused (T2).
- Each kind of token has its own `typ`, and each check accepts only one, so a lease cannot pass as a license (T3).
- A leaked signing key (T12) is handled by revoking its key ID in the trust store of a new app version and moving licenses to a new key at the next online renewal. The root key is kept offline. See [the trust chain](trust-chain.md) for what revocation does and does not reach.

### Moving, copying, and stretching a license (T4, T5, T9, T10, T11)

A1 copies a node-locked license to another machine, copies a whole virtual machine, sets the clock back, reuses an offline response, or starts the trial again.

- A token bound to a device carries the installation key's thumbprint (`cnf.jkt`) and machine fingerprints, and renewals and leases require a proof made with that key (T4; see [devices and proofs](devices-and-proofs.md)).
- The clock guard records the latest trusted time (the larger of the clock and each token's `iat`) and refuses a clock set behind it (T5).
- A copied machine has the same installation key, so the server finds it when the sequence number (`seq`) of the two copies goes out of step (T9). With `Deny`, the activation or lease is stopped with `ELM_CLONE_DETECTED`.
- An offline response is bound to the installation key of its request (T10; see [activation](activation.md)).
- A trial is limited to one per product and machine fingerprint (T11).

### Forged and replayed requests (T6, T7, T19)

An attacker on the network (A2) or inside the company (A3) replays a captured request, makes one up, or deactivates or renews someone else's device.

- Every request that changes state carries a proof signed by the installation key. There is no shared secret to steal from the app (T7).
- The proof holds a server nonce and the SHA-256 of the body, and the server accepts each proof ID (`jti`) once while the nonce is valid (300 seconds), so a replayed request is refused (T6).
- Every request about an activation must be signed by that activation's installation key, so nobody can deactivate or renew another device (T19).

### More seats than were bought (T8, T17, T18)

A1 or A3 tries to take more concurrent seats than the license has, fakes an on-premises server, or sets one up with a trial or standard license.

- Seats are counted and added under a lock, so two requests cannot both take the last seat. After an `elm-server` restart, new leases wait for the recovery window and borrows come back from the signed borrow record (T8; see [floating licenses](floating-licenses.md)).
- The client checks every lease against the vendor-signed server license: the feature, the seat count, the length, and the end date (T8).
- The client verifies the server license from the root key, requires `kind: server`, checks the server against its pin, and verifies leases with the key the server license names. With the default self-signed TLS, it checks on every connection that the TLS key is that same key (T17, T18).

What this does not stop: a TLS inspection proxy that the client's OS trusts can read the traffic and fake unsigned error answers. It cannot fake a lease, but a false `ELM_CLONE_DETECTED` can make an app lose one. That is the same as cutting the network and is accepted (T17).

### The admin API and the admin UI (T13, T14)

An outside attacker (A4), a former or low-role administrator (A5), another user of an admin's machine (A8), the owner of another app at the same identity provider (A9), or a site that shares the admin UI's host name or domain (A10) tries to use the admin API.

- API keys have a scope and an expiry, and only their hash is stored. Every admin action is written to the audit log (T13).
- OIDC access tokens are checked for signature, issuer, expiry, and an `aud` equal to `oidc.audience`. The service refuses to start if `oidc.audience` equals the CLI's client ID or the web client's ID, so an ID token or a token meant for another app cannot be used here (A9).
- `elm admin login` stores its tokens in the user's own protected area, like an installation key (A8), and sends them only to the origin it logged in to (A2).
- The admin UI never gives tokens to the browser. Its cookies are `__Host-`, `HttpOnly`, `Secure`, and `SameSite=Lax`; state-changing requests need a custom header and a matching `Origin`; the content security policy is strict; and the login uses PKCE, `state`, and `nonce` (T14).

### The local admin API of `elm-server` (T20)

Another user of the server machine (A6), or a web page in that user's browser, sends requests to the local admin API or reads the server's data folder.

- The admin API listens only on loopback and requires a 256-bit token, compared in constant time.
- Failed attempts are counted per minute rather than logged one by one, so they cannot fill the log.
- The data folder is limited to Administrators, SYSTEM, and the user who created it, and the server will not use a folder that another user created first.

### Logs (T15)

Someone on the customer's network (A3) edits `elm-server`'s usage log to hide use.

- The log is signed in blocks with the server's key, and each file is chained to the previous one, so `elm log verify` finds edits, missing files, and cut-off files (except the newest file; see below). `elm-service`'s usage log is signed with a vendor signing key whose certificate has the use `log`.

### Resource exhaustion (T16)

A2, A3, or A4 sends huge inputs or floods a server. Input size, depth, and counts have upper limits, and requests are rate-limited per source (`rateLimit.perIp`, 20 per second by default on both servers).

### The supply chain (T21 to T28)

The attackers here never touch a license. They change what customers download or what this site tells them.

- **Dependencies of the admin UI and this site (T21, T28; A7, A15).** npm packages are pinned by version and integrity hash in a lock file and installed with `--frozen-lockfile`. Package build scripts do not run. A version published less than 1,440 minutes ago is not used. Changes to the lock file are reviewed in the pull request. Builds that run npm code have no secrets and no publishing rights.
- **The release workflow (T22; A11, A13).** Releases are built only by `.github/workflows/release.yml`. The build job has read-only rights; the jobs that sign attestations, push to NuGet and GHCR, and publish the GitHub Release only move files the build job made in the same run. Publishing waits for the owner's approval in the `release` environment, and only `v*` tags owned by the owner can start it. Pull requests from forks get no secrets. Every action is pinned to a commit SHA, and no cache is restored.
- **Stolen publishing credentials (T23; A12).** NuGet publishing uses Trusted Publishing: each run trades a GitHub OIDC token for a one-hour API key, so no long-lived NuGet key exists to steal. The owner's GitHub and nuget.org accounts use passkeys.
- **Swapped downloads (T24; A14).** Every file of a GitHub Release is listed in `SHA256SUMS`, and every file and `SHA256SUMS` itself carry a build provenance attestation from the release workflow. Published releases are immutable, so their files cannot be changed afterwards, even by the owner.
- **Build tools and the container image (T25, T26, T27; A12, A13, A14).** Tool versions and the Alpine image are pinned, and updates come as reviewed pull requests. The `elm-service` image is built from the same `elm-service` binary that is in `SHA256SUMS`, pushed only after approval, and attested by its digest.
- **This site (T28).** The site at `https://yukikeda.github.io/ElasticLM/` is built and published by `release.yml` only on a `v*` tag push, and publishing waits for the owner's approval in the `github-pages` environment. The site does not include analytics or any plugin that sends reader data. The command to verify a download is also in `SECURITY.md` in the repository, so a reader does not have to trust this site to check a file.

## How to check a download

Verify every file you download before you run it or ship it. The exact commands are in [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md); in short:

```sh
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify <file> --repo YUKIKEDA/ElasticLM \
  --signer-workflow YUKIKEDA/ElasticLM/.github/workflows/release.yml \
  --source-ref refs/tags/v<version> --deny-self-hosted-runners
```

Keep all the options. `--repo` alone does not check which workflow signed the file, and `--source-ref` makes sure the file came from the version you meant. The attestation shows that the release workflow built the file from that tag. It does not show that the code is free of defects.

NuGet packages are signed again by nuget.org when they are pushed, which changes their bytes: check a package from nuget.org with `dotnet nuget verify`, not against `SHA256SUMS`. For the container image `ghcr.io/yukikeda/elm-service`, verify its attestation and then pull it by the digest you verified, not by a tag: a tag such as `latest` can be moved to another image (design §5.3, T26). Trust only that image name, not a label that claims this repository.

## Accepted threats

These are the threats ElasticLM does not stop, from design §5.4, in plain words.

### On the licensee's machine

- **Changing the app's code to skip the check.** No client-side licensing can prevent this. ElasticLM does not obfuscate.
- **Copying the installation key and state to another machine** by someone with admin rights there. If both copies go online, the server finds the clone. A copy that only ever runs offline is not found.
- **Changing the OS machine ID.** The installation key is the real identity, so this does little, except that the one-trial-per-machine rule depends on the fingerprint, so changing it allows another trial.
- **Keeping a lease token after returning it.** It stays valid until it expires: one lease time (10 minutes by default) for a normal lease. Early returns of borrows are limited per key and per fingerprint to make repeating this costly.
- **Keeping a license file after deactivation.** Deactivation does not delete the installation key, so a saved copy works until the token's expiry and grace period (30 and 7 days by default). The policy's `DeactivationLimit` limits how often this can be done.
- **Deleting or editing the clock guard's state.** The clock still cannot go behind the token's `iat`, so the real limit is the token's expiry. Keep `TokenLifetime` short if this matters.
- **Revocation does not reach an offline token before it expires.** There is no revocation list. Short token lifetimes and online renewal carry revocation, and a revoked signing key reaches apps only in a new app version.
- **A token without an expiry cannot be revoked.** This happens only with `elm`'s manual issue and with `Unbound` license files of licenses that never expire; `elm` warns when it issues one.
- **Passing an old offline request file after a newer one** is treated as a clone and, with `Deny`, stops the device until an administrator deactivates it. Throw old request files away.

### On the customer's license server

- **The customer's administrator takes the server's key out.** They have root on the machine. With the key they can sign leases beyond the seat count and rewrite the usage log. The damage is limited because the client accepts only leases inside the server license (feature, seats, length, end date), and because the server license expires (30 days by default).
- **Copying the server machine** is found at the next server license renewal, up to 30 days later by default.
- **The administrator frees borrowed seats** by deleting the borrow record, moving the clock forward, or starting with `--set-aside-broken-records`. The usage log keeps the traces, and the damage ends at the borrows' expiry.
- **Host and user names in `allow` and `deny` can be faked**, because the client reports them. Only IP rules are reliable.
- **Deleting the newest log file or cutting it after its last signature** cannot be found, because there is no next file to compare with. Seat counting does not depend on the log.

### On the vendor's service

- **Someone who can write to the database can rewrite the audit log** and recompute its hashes, and can add sessions. Keep the database and its backups as protected as the signing key, and keep the Data Protection key-encryption key outside the database.
- **Someone with `elm-service`'s signing key and passphrase can rewrite the usage log**, and can also forge licenses. Nothing protects only the log.
- **A TLS inspection proxy the device trusts can fake unsigned error answers** from `elm-service`, such as `ELM_ACTIVATION_NOT_FOUND`, which deletes the local activation, or `ELM_UNKNOWN_LEASE` and `ELM_CLONE_DETECTED`, which lose a hosted lease. The harm is limited to that customer's own devices and is undone by activating or acquiring again. The proxy can also read license keys in request bodies, but a key alone gives no seat or token.
- **A licensee who has activations can grow the audit log** by activating again or triggering clone detection over and over. The per-key rate limit (2 per second by default) bounds it, and the vendor can deactivate that device.
- **A licensee with borrow rights can grow the early-return table** with new keys and fingerprints. Rows are per license, bounded per return, and deleted after 30 days.

### Around the vendor's administrators

- **Choosing the wrong `--url` in `elm admin login`.** A malicious service can pass on the real identity provider's details and then use the access token it receives. `elm` shows the service URL and issuer before it waits for the login; check them.
- **Device code phishing.** An attacker can start a device login and send the real verification URL and code to an admin. Show the client's name on the identity provider's approval screen and teach admins to check it.
- **Roles and sessions outlive changes at the identity provider.** An admin removed from a group keeps the old role until they next log in, and an access token stays valid until it expires after `elm admin logout`. An admin UI session lasts up to `session.maxHours` (12 hours by default) and stops after `session.idleMinutes` (30 by default) without use. To stop someone at once, disable the admin in the service, which deletes their sessions.
- **A stolen session cookie works from any IP** until it expires. Sessions are not bound to an IP or a device key.
- **Cookies do not separate ports.** A service on another port of the same host name can read and set the admin UI's cookies. Give the admin UI a host name that no untrusted service shares.
- **Someone with the database and the key-encryption key** can read and forge the `state` of logins in progress.
- **The identity provider's session survives an admin UI logout.** On shared machines, close the browser or log out at the identity provider too.

### The release and the documentation site

- **The executables and `libelasticlm` are not code-signed.** Windows SmartScreen may warn, and macOS Gatekeeper will not open them until you remove the quarantine attribute after checking the file (see [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md)). `SHA256SUMS` and the attestation are the way to check the publisher.
- **The owner's GitHub or nuget.org account is taken over.** Whoever holds it can tag, approve, publish, move image tags, and change this site, and the attestations will look correct. Passkeys make this harder, and immutable releases keep published files unchanged. Nothing in the product can stop someone who holds the owner's rights.
- **GitHub or its runners are compromised.** Releases and attestations rely on GitHub.
- **GHCR tags can be moved.** Pull images by verified digest.
- **An attestation shows only which workflow built which commit.** A bad commit that the owner merged is attested correctly. Code review and CI protect the code itself.
- **Vulnerabilities in the container base image** stay until the next ElasticLM version. Users of the single-binary `elm-service` get the OS's library updates.
- **Dependabot does not warn about the npm dependencies** of this site and the admin UI, because it cannot read the lock files. `pnpm audit` runs on every pull request and release instead, so an advisory published in between waits for the next one.
- **This site shares its origin with the owner's other GitHub Pages sites.** A compromised dependency of one of them could change these pages for a reader who opens it. This is why the verify command is also in `SECURITY.md`.

## What each role must do

The cryptography holds only if each role does its part.

### The vendor: key custody and issuing

- Keep the root key offline. `elm` stores it as a passphrase-encrypted PKCS#8 PEM file (PBKDF2-HMAC-SHA256 with 600,000 iterations and AES-256-CBC, design §7.2); never put it on a server. It is used only to certify signing keys.
- Give `elm-service` only a signing key certified for the uses it needs, through a file or an environment variable, never in the configuration file or the image. Its certificate is valid for 1 year by default and 2 at most; plan the rotation in [the trust chain](trust-chain.md).
- If a signing key leaks: certify a new one, add the leaked key ID to the revoked list (`trust.revokedKeyIds` on the servers, and the trust store of a new app version), and reissue licenses with the new key.
- Choose policy values with the accepted threats in mind: a short `TokenLifetime` for licenses and server licenses limits copies and lost revocations; `LeaseDuration` trades outage tolerance against wait after restart; `BorrowLimit` and `BorrowReturnLimit` bound borrows; `CloneStrategy` `Deny` stops clones.
- Avoid licenses without an expiry unless you accept that they cannot be revoked.

### The operator of `elm-service`

- Pass every secret (the signing key's passphrase, the nonce secret, the database connection string, the web client secret, the key-encryption key) through environment variables or files with tight permissions.
- Keep the key-encryption key (`ELM_DATAPROTECTION_KEK`) outside the database, and protect the database and its backups.
- Set `publicUrls` to the URLs devices use.
- At the identity provider, allow the admin UI's client only the authorization code flow, use an `oidc.audience` different from both client IDs, and put the role claim in both the ID token and the access token.
- Give the admin UI a host name of its own.
- Keep `session.idleMinutes` and `session.maxHours` as short as your admins can work with, and set `audit.retentionDays` to what your records require.
- Check the image or binary before deploying, and pull the image by digest.

### The app developer

- Embed only the vendor's root public keys in the trust store, and act on every verification result; do not treat a failure as a warning.
- Accept that a patched binary skips any check, and decide how much that matters for your product.
- For an on-premises server, set `ExpectedServerLicenseId` (C: `expected_server_license_id`) when you know the customer's server license, so the client does not trust the first server it meets.
- Handle `Lost` leases, and check a borrow's `ExpiresAt` before you rely on it.
- Keep the default installation key location or choose one that only the right user (or machine) can read; it is what makes a device that device. See [devices and proofs](devices-and-proofs.md).
- Ship a new app version when the vendor revokes a signing key.

### The customer's administrator of `elm-server`

- Protect the server machine: anyone with root on it can take the server's key, and the vendor will see that only as leases within the server license.
- Keep the data folder and the admin token readable only by the service account and administrators. Do not change `admin.listen` to anything but loopback (the server refuses).
- Rely on IP rules in `allow` and `deny`, not host or user names. Behind a reverse proxy, apply IP rules at the proxy and raise `rateLimit.perIp` to match the number of machines behind it.
- Use `--set-aside-broken-records` only after finding out why the borrow record broke; it frees borrowed seats.
- Give the server its own `dataDirectory`, separate from any app of the same product on the machine.
- Renew the server license before it expires, and verify every `elm-server` and `elm` download as above.

### Everyone

Report a vulnerability through GitHub Security Advisories, not in a public issue, as [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md) says. Do not include real private keys, license tokens, customer identifiers, or machine IDs in a report.

Why the model is drawn where it is (no obfuscation, no revocation list, no standby server) is on [design choices](design-choices.md).
