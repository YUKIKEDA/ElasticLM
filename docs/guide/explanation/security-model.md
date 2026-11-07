---
sidebar_position: 7
---

# Security model

This page is for everyone who has to decide whether ElasticLM protects what they need and what they must do themselves: vendors who hold the keys and run `elm-service`, app developers who embed the client, and customers' administrators who run `elm-server`. After a short section on what is protected and from whom, each role has its own section; then come how to check a download, everything ElasticLM does not protect against, and what each role must do. It helps to have read [the overview](overview.md) and [the trust chain](trust-chain.md) first; the other explanation pages give the mechanisms in full.

## What it protects and from whom

A licensing system is attacked mostly by the people it serves. A customer has the app, a real license, and full control of their own machine, and some would like more than they paid for. ElasticLM uses signatures, not secrecy, to stop three things:

- forged or edited licenses,
- forged or replayed requests,
- more concurrent seats than were bought.

It does not try to stop anyone from changing the app's own code. Anything that runs on a machine the attacker controls can be changed by that attacker. The protection is in what the attacker cannot sign.

What is worth protecting:

| What | If an attacker gets it |
| --- | --- |
| The vendor's root key and signing keys | They can make any license or lease the apps will accept. |
| A license key | They can activate with it. |
| A device's installation key | They can act as that device. |
| Admin access to `elm-service` | They can issue, deactivate, and revoke licenses. |
| The usage log and the audit log | The evidence for billing and disputes is lost. |
| ElasticLM's release files and this site | A fake `elm-server` or library can skip every check; a fake `elm` or `elm-service` can read the vendor's signing key; an edited page can send readers to fake downloads. |

Who attacks, and with what:

| Attacker | What they hold |
| --- | --- |
| A dishonest licensee | The app, a real license file or license key, and administrator rights on their own machines |
| Someone on the network | The ability to read, change, and replay traffic |
| Another person in the customer's company | Network access to the license server, or a logon without admin rights on the server machine |
| An outsider on the internet | Network access to `elm-service` and its administration API |
| A former or low-role vendor administrator, or another user of an administrator's machine | Old credentials, a low role, or a local logon |
| Another app at the vendor's identity provider, or a site on the same host name as the admin UI | Tokens an administrator gave that app, or cookies in the administrator's browser |
| Someone in the supply chain | A taken-over package, build tool, or base image; a pull request; a stolen credential; or a swapped file on a mirror or registry |

## For the vendor

The vendor holds the keys, issues licenses, and usually runs `elm-service`.

**Keys and issuing.** Every license, lease, and key certificate is signed with ECDSA P-256 (ES256) and verified over the exact bytes received, so an edited payload fails. The verifier uses only keys that chain from a root key in the app's trust store through a key certificate. The token header's `alg` must match the key's record and never chooses the key or algorithm, so `none` or a swapped algorithm is refused. Each kind of token has its own `typ`, so a lease cannot pass as a license. The root key is kept offline and is used only to certify signing keys. If a signing key leaks, see [the trust chain](trust-chain.md) for what revocation reaches.

**Requests to the hosted service.** Every request that changes state carries a proof signed by the device's installation key; there is no shared secret in the app to steal. The proof holds a server nonce and the SHA-256 of the body, and each proof ID is accepted once while the nonce is valid (300 seconds), so a captured request cannot be replayed. A request about an activation must be signed by that activation's key, so nobody can deactivate or renew another device. A copied machine is found when the copies' sequence numbers go out of step; with `cloneStrategy`: `deny` it stops with `ELM_CLONE_DETECTED`. An offline response works only for the key that made its request, and a trial is limited to one per product and machine fingerprint. See [activation](activation.md).

**The administration API.** People and systems use it in three ways:

- **API keys**, for machines such as a billing system. A key is shown once when it is made; the service stores only its hash, so a copy of the database does not give usable keys. Each key has a scope (the permissions it may use) and an expiry (90 days by default, 1 year at most), and it belongs to the administrator who made it: it can never do more than that administrator's current role allows.
- **Sign-in with your identity provider (OIDC)**, for people. `elm admin login` gets an access token from the identity provider and keeps it in the user's own protected folder, like an installation key, and sends it only to the service it logged in to. The service checks each token's signature, issuer, expiry, and audience: the token must name the service's own audience value, `oidc.audience`. The service refuses to start if `oidc.audience` equals the client ID of `elm` or of the admin UI at the identity provider, so an ID token or a token meant for another app cannot be used here.
- **Admin UI sessions.** The admin UI signs in through the service, which keeps the tokens and gives the browser only a session cookie (`__Host-elm-session`, `HttpOnly`, `Secure`, `SameSite=Lax`); the database keeps only its hash. Requests that change something need a custom header and a matching `Origin`, the content security policy is strict, and the sign-in uses PKCE, `state`, and `nonce`. While a sign-in is in progress, its state is kept in a cookie encrypted with keys stored in the database, and those keys are encrypted with a 256-bit key you give the service (`ELM_DATAPROTECTION_KEK` or `dataProtection.kekFile`). A session ends after `session.idleMinutes` without use (30 by default) and after `session.maxHours` (12 by default).

Each administrator has a role: `owner` (everything), `admin` (everything except API keys, administrators, and sessions), `support` (reading, deactivation, force-returning leases, offline requests), or `viewer` (reading). An `owner` can disable an administrator, which also stops their API keys and deletes their sessions. Admin actions are written to the audit log, whose rows are chained by hashes so that an edited or deleted row is found; `audit.retentionDays` sets how long rows are kept.

**Resource limits.** Input size, depth, and counts have upper limits. `elm-service` limits device requests per source IP (`rateLimit.clientPerIp`, 20 per second by default) and per installation key (`rateLimit.clientPerKey`, 2 per second), and administration API requests per source IP (`rateLimit.perIp`, 20 per second) and per API key or administrator (`rateLimit.perKey`, 10 per second). Device and admin traffic use separate limits, so many devices behind one address do not lock out the administrators. When `usageLog.directory` is set, the service also writes leases and borrows to a signed usage log, one chain of files per node.

For every setting of `elm-service`, see [elm-service configuration](../reference/service-configuration.md). For the rows of the audit log and the lines of the usage log, see [Logs](../reference/logs.md).

## For the app developer

The app developer embeds the trust store and the client.

- **Licenses are checked on the device without a network**, against the root public keys built into the app. A forged or edited license fails, whatever the network says. See [license files and verification](license-files-and-verification.md).
- **A license bound to a device works only there.** It names the installation key's thumbprint and machine fingerprints, and renewals and leases need a proof from that key. The key file is protected by the OS (DPAPI on Windows, file modes elsewhere). See [devices and proofs](devices-and-proofs.md).
- **Setting the clock back does not help.** The clock guard records the latest trusted time and refuses a clock set behind it.
- **Every lease is checked**, not trusted because a server sent it: its signature, its product and key, and, on premises, that its feature, seat count, length, and end date are inside the vendor-signed server license. A customer who takes the server's key out still cannot sign a lease beyond the server license.
- **An on-premises server must prove who it is.** The client verifies the server license from the root key, requires `kind: server`, checks the server against its pin, and verifies leases with the key the server license names. With the default self-signed certificate, it checks on every connection that the TLS key is that key, so someone on the network who lacks the server's key cannot pose as the server. See [floating licenses](floating-licenses.md).

## For the customer's administrator

The customer's administrator runs `elm-server`.

- **The server cannot lend beyond its server license.** Seats are counted under a lock, after a restart new leases wait until running apps have reclaimed theirs, and borrows come back from the signed borrow record. The apps refuse any lease outside the server license.
- **The local admin API is for administrators only.** It listens only on loopback, needs a 256-bit token compared in constant time, and counts failed attempts per minute instead of logging each one, so they cannot fill the log.
- **The data folder is closed to other users.** On Windows it allows only Administrators, SYSTEM, and the user who created it; on Linux and macOS its mode is `0700`. The server will not use a folder that another user created first.
- **Edits to the usage log are found.** The log is signed in blocks with the server's key and each file is chained to the previous one, so `elm log verify` finds edited, missing, and cut-off files.
- **Floods are limited.** Requests are limited per source IP (`rateLimit.perIp`, 20 per second by default).

For every setting of `elm-server`, see [elm-server configuration](../reference/server-configuration.md). For the usage log and the borrow record, see [Logs](../reference/logs.md).

## Checking what you download

Verify every file you download from a GitHub Release before you run or ship it. The commands are in [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md); in short:

```sh
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify <file> --repo YUKIKEDA/ElasticLM \
  --signer-workflow YUKIKEDA/ElasticLM/.github/workflows/release.yml \
  --source-ref refs/tags/v<version> --deny-self-hosted-runners
```

Keep all the options. `--repo` alone does not check which workflow signed the file, and `--source-ref` makes sure the file came from the version you meant. The attestation shows that ElasticLM's release workflow built the file from that tag; it does not show that the code is free of defects. NuGet packages are signed again by nuget.org, which changes their bytes: check a package from nuget.org with `dotnet nuget verify`, not against `SHA256SUMS`.

The container image `ghcr.io/yukikeda/elm-service` is pushed only by a release that is not a prerelease, such as `v1.0.0`; a prerelease such as `v1.0.0-rc.1` pushes no image. The command to verify the image is added to `SECURITY.md` with the first release that pushes it. Pull the image by the digest you verified, not by a tag such as `latest`, which can be moved to another image, and trust only that image name, not a label that claims this repository.

What stands behind a release:

- Releases are built only by the release workflow. Publishing waits for the owner's approval in the `release` environment, and only `v*` tags can deploy to it. Only the owner can create `v*` tags, and nobody can move or delete them. Pull requests from forks get no secrets.
- NuGet publishing trades a short-lived GitHub token for a one-hour API key each time, so no long-lived NuGet key exists to steal. The owner's GitHub and nuget.org accounts use passkeys.
- Published GitHub Releases are immutable, so their files cannot be changed afterwards, even by the owner.
- Workflow actions, tool versions, the Alpine build image, and the `elm-service` base image (`mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled`) are pinned by commit SHA or digest and raised only by reviewed pull requests. npm packages are pinned by version and hash and run no install scripts.
- This site is published only from a `v*` tag, after the owner's approval, and sends no reader data. The verify command is also in `SECURITY.md`, so you need not trust this site to check a file.

## What ElasticLM does not protect against

These are accepted: ElasticLM does not stop them. Other pages mention them in a sentence and link here.

### On the licensee's machine

- **A patched app binary.** Changing the app's code to skip the check cannot be prevented by any client-side licensing. ElasticLM does not obfuscate.
- **Offline copies.** Someone with admin rights can copy the installation key and its state to another machine. If both copies go online, the server finds the clone. A copy that only ever runs offline is not found.
- **Revocation reaches a device only when its token expires.** There is no revocation list. Revoking a license stops new tokens and leases, but a token already on a device works until its expiry plus grace period (30 and 7 days by default). A short `tokenLifetime` narrows this. A revoked signing key reaches an app only in a new app version.
- **A token without an expiry cannot be revoked.** Such tokens come only from issuing by hand with `elm` and from `unbound` license files of licenses that never expire; `elm` warns when it issues one.
- **A license file still works after deactivation.** Deactivation does not delete the installation key, so a saved copy works until the token's expiry and grace period. The policy's `deactivationLimit` limits how often this can be done.
- **Changing the OS machine ID.** The installation key is the real identity, so this does little, except that the one-trial-per-machine rule depends on the fingerprint.
- **Deleting the clock guard's state.** The clock still cannot go behind the token's `iat`, so the real limit is the token's expiry.
- **Keeping a returned lease token.** It stays valid until it expires, one lease time for a normal lease; early returns of borrows are limited for this reason (see [floating licenses](floating-licenses.md)).
- **An old offline request file sent after a newer one** is treated as a clone and, with `deny`, stops the device until an administrator deactivates it. Throw old request files away.
- **A TLS-inspecting proxy that the device's OS trusts**, such as a company's inspection box, can read the traffic and fake answers that are not signed. It cannot fake a license or a lease. It can fake an error: a false `ELM_CLONE_DETECTED` or `ELM_UNKNOWN_LEASE` loses a lease, and a false `ELM_ACTIVATION_NOT_FOUND` from `elm-service` deletes the local activation. The harm is limited to that customer's own devices and is undone by activating or acquiring again, like cutting the network. The proxy can also read license keys in request bodies, but a key alone gives no seat or token.

### On the customer's license server

- **The administrator takes the server's key out.** They have root on the machine. With the key they can sign leases beyond the seat count and rewrite the usage log. The apps still accept only leases inside the server license, and the server license expires (30 days by default).
- **A copied server machine** is found only at the next server license renewal, up to 30 days later by default.
- **The administrator frees borrowed seats** by deleting the borrow record, moving the clock forward, or starting with `--set-aside-broken-records`. The usage log keeps traces, and the damage ends at the borrows' expiry.
- **The first connection is trusted** when the app does not set `ExpectedServerLicenseId`; see [floating licenses](floating-licenses.md).
- **Host and user names in `allow` and `deny` can be faked**, because the client reports them. Only IP rules are reliable.
- **Deleting the newest usage log file, or cutting it after its last signature**, cannot be found, because there is no next file to compare with. Seat counting does not depend on the log.

### Around the hosted service and its administrators

- **Someone who can write to the database** can rewrite the audit log and recompute its hashes, and can add sessions. With the database and the key-encryption key, they can also read and forge sign-ins in progress. Protect the database and its backups like the signing key, and keep the key-encryption key outside the database.
- **Someone with the signing key and its passphrase** can rewrite the service's usage log, and can also forge licenses.
- **A licensee with activations can grow the audit log**, but only under `cloneStrategy`: `flag`, by triggering clone detection again and again; with `deny`, only the one line when the activation stops. Reactivating adds lines too. The per-key limit (2 per second) bounds it, and the vendor can deactivate that device.
- **A licensee with borrow rights can grow the early-return table** with new keys and fingerprints. The rows count only against their own license and are deleted after 30 days.
- **Changes at the identity provider arrive late.** The service is not told when an administrator leaves a group. The old role stays until their next sign-in, and their API keys stay usable until then, or until `apiKeys.inactiveAdminDays` (90 by default) after the latest of their last sign-in and the key's creation, when OIDC sign-in is configured. An access token stays valid until it expires, even after `elm admin logout`, and an admin UI session lasts up to `session.maxHours`. To stop someone at once, disable them in the service.
- **A stolen session cookie works from any IP** until the session ends. Sessions are not bound to an IP or a device.
- **Cookies do not separate ports.** A service on another port of the same host name can read and set the admin UI's cookies.
- **The identity provider's session survives an admin UI logout.** On shared machines, close the browser or log out at the identity provider too.
- **Signing in to the wrong service.** If `elm admin login --url` names a malicious service, it can pass on the real identity provider's details and use the access token it receives. `elm` shows the service URL and issuer before it waits for the sign-in; check them.
- **Device code phishing.** An attacker can start a device sign-in and send the real verification URL and code to an administrator.

### Releases and this site

- **The executables and `libelasticlm` have no Authenticode or Developer ID signature** (the `osx-arm64` files carry only the ad-hoc signature that the linker adds). Windows SmartScreen may warn, and macOS Gatekeeper will not open them until you remove the quarantine attribute after checking the file (see [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md)).
- **The owner's GitHub or nuget.org account is taken over.** Whoever holds it can tag, approve, publish, move image tags, and change this site, and the attestations will look correct. Passkeys make this harder, and immutable releases keep published files unchanged.
- **GitHub or its runners are compromised.** Releases and attestations rely on GitHub.
- **An attestation shows only which workflow built which commit.** A bad commit that the owner merged is attested correctly. Code review and CI protect the code itself.
- **Image tags on GHCR can be moved.** Pull images by verified digest.
- **Vulnerabilities in the container base image** stay until the next ElasticLM release. Users of the single-binary `elm-service` get the OS's library updates.
- **Dependabot does not warn about npm dependencies**, because it cannot read the lock files pnpm 12 writes. CI's `pnpm audit` stands in, so an advisory published between pull requests waits for the next one. Three reviewed advisories in the site's build tools are accepted because the affected functions are never called.
- **This site shares its web origin with the owner's other GitHub Pages sites.** A compromised dependency of one of them could change these pages for a reader who opens it. This is one more reason the verify command is also in `SECURITY.md`.

## What each role must do

The cryptography holds only if each role does its part.

### The vendor

- Keep the root key offline. `elm` stores it as a passphrase-encrypted file; never put it on a server.
- Give `elm-service` only a signing key certified for the uses it needs. Give the key as a file, with its passphrase through an environment variable or a file, never in the configuration file or the image. Pass the other secrets (the nonce secret, the database connection string, the admin UI's client secret, the key-encryption key) the same way, with tight permissions. Plan certificate rotation as in [the trust chain](trust-chain.md).
- If a signing key leaks: certify a new one and reissue licenses with it. Listing the leaked key ID in `trust.revokedKeyIds` on your servers stops them using or accepting that key; only an app version whose trust store lists it protects the apps.
- Choose policy values with the accepted threats in mind: a short `tokenLifetime` limits copies and late revocation; `leaseDuration` trades outage tolerance against the wait after a restart; `borrowLimit` and `borrowReturnLimit` bound borrows; `cloneStrategy`: `deny` stops clones. Avoid licenses without an expiry.
- Protect the database and its backups, and keep the key-encryption key outside them.
- At the identity provider, allow the admin UI's client only the authorization code flow, use an `oidc.audience` different from both client IDs, put the role claim in both the ID token and the access token, and show the client's name on the device approval screen.
- Give the admin UI a host name that no untrusted service shares, keep the session settings as short as your administrators can work with, and set `audit.retentionDays` to what your records require.
- Set `publicUrls` to the URLs devices use, and check the binary or image before deploying it.

### The app developer

- Embed only the vendor's root public keys and the current revoked key IDs, and act on every verification result; do not treat a failure as a warning.
- Decide how much a patched binary matters for your product; no check survives one.
- For an on-premises server, set `ExpectedServerLicenseId` when you know the customer's server license.
- Handle `Lost` leases, and check a borrow's `ExpiresAt` before relying on it.
- Keep the installation key where only the right user or machine can read it. See [devices and proofs](devices-and-proofs.md).
- Ship a new app version when the vendor revokes a signing key.

### The customer's administrator

- Protect the server machine: anyone with root on it can take the server's key.
- Keep the data folder and the admin token readable only by the service account and administrators. `admin.listen` must stay on loopback (the server refuses anything else).
- Give the server its own `dataDirectory` when an app of the same product runs on the machine.
- Rely on IP rules in `allow` and `deny`. Behind a reverse proxy, apply IP rules at the proxy and raise `rateLimit.perIp` to match the machines behind it.
- Use `--set-aside-broken-records` only after finding out why the borrow record broke; it frees borrowed seats.
- Renew the server license before it expires, and verify every `elm-server` and `elm` download.

### Everyone

Report a vulnerability through GitHub Security Advisories, not in a public issue, as [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md) says. Do not include real private keys, license tokens, customer identifiers, or machine IDs in a report.

Why the model is drawn where it is (no obfuscation, no revocation list, no standby server) is on [design choices](design-choices.md).
