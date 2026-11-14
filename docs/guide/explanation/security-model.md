---
sidebar_position: 7
---

# Security model

This page is for everyone who has to decide whether ElasticLM protects what they need and what they must do themselves: vendors who hold the keys and run `elm-service`, app developers who embed the client, and customers' administrators who run `elm-server`. After a short section on what is protected and from whom, each role has its own section; then come how to check a download, everything ElasticLM does not protect against, and what each role must do. It helps to have read [the overview](overview.md) and [the trust chain](trust-chain.md) first; the other explanation pages give the mechanisms in full.

## What it protects and from whom

A licensing system is attacked mostly by the people it serves. A customer has the app, a real license, and full control of their own machine, and some would like more than they paid for. ElasticLM uses signatures, not secrecy, to stop three things:

- forged or edited licenses,
- forged or replayed requests,
- more concurrent seats than were bought, except by a customer's administrator with root on an on-premises license server's machine ([see below](#on-the-customers-license-server)).

It does not try to stop anyone from changing the app's own code. Anything that runs on a machine the attacker controls can be changed by that attacker. The protection is in what the attacker cannot sign.

What is worth protecting:

| What | If an attacker gets it |
| --- | --- |
| The vendor's root key and signing keys | They can make any license or lease the apps will accept. |
| A license key | They can use the license as a new device, within its limits. The key of a node-locked license activates devices up to its activation limit; the key of a cloud floating license takes seats up to its seats and overage; the license key of a server license activates an `elm-server` of their own, up to that license's activation limit. The key of an unbound license activates nothing ([see below](#on-the-licensees-machine)). |
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

**Requests to the hosted service.** Every request that changes state carries a proof signed by the device's installation key; there is no shared secret in the app to steal. The proof holds a server nonce and the SHA-256 of the body, and each proof ID is accepted once while the nonce is valid (300 seconds), so a captured request cannot be replayed. A request about an activation must be signed by that activation's key, so nobody can deactivate or renew another device. A copied machine is found when the copies' sequence numbers go out of step; with `cloneStrategy`: `deny` it stops with `ELM_CLONE_DETECTED`. An offline response works only for the key that made its request, and a trial is limited to one per product and machine fingerprint. A product gives at most `trialDailyLimit` trials (1,000 by default) in any 24 hours. See [activation](activation.md).

**The administration API.** People and systems use it in three ways:

- **API keys**, for machines such as a billing system. A key is shown once when it is made; the service stores only its hash, so a copy of the database does not give usable keys. Each key has a scope (the permissions it may use) and an expiry (90 days by default, 1 year at most), and it belongs to the administrator who made it: it can never do more than that administrator's current role allows.
- **Sign-in with your identity provider (OIDC)**, for people. `elm admin login` gets an access token from the identity provider and keeps it in the user's own protected folder, like an installation key, and sends it only to the service it logged in to. The service checks each token's signature, issuer, expiry, and audience: the token must name the service's own audience value, `oidc.audience`. The service refuses to start if `oidc.audience` equals the client ID of `elm` or of the admin UI at the identity provider, so an ID token or a token meant for another app cannot be used here. The service tells administrators apart by the claim that `oidc.subjectClaim` names: `sub`, or `oid` for Microsoft Entra ID, whose `sub` differs from one app to another. Choosing `oid` does not weaken the audience check, which is what refuses tokens meant for another app.
- **Admin UI sessions.** The admin UI signs in through the service, which keeps the tokens and gives the browser only a session cookie (`__Host-elm-session`, `HttpOnly`, `Secure`, `SameSite=Lax`); the database keeps only its hash. Requests that change something need a custom header and a matching `Origin`, the content security policy is strict, and the sign-in uses PKCE, `state`, and `nonce`. While a sign-in is in progress, its state is kept in a cookie encrypted with keys stored in the database, and those keys are encrypted with a 256-bit key you give the service (`ELM_DATAPROTECTION_KEK` or `dataProtection.kekFile`). A session ends after `session.idleMinutes` without use (30 by default) and after `session.maxHours` (12 by default).

Each administrator has a role: `owner` (everything), `admin` (everything except API keys, administrators, and sessions), `support` (reading, deactivation, force-returning leases, offline requests), or `viewer` (reading). An `owner` can disable an administrator, which also stops their API keys and deletes their sessions. Admin actions are written to the audit log, whose rows are chained by hashes so that an edited or deleted row is found; `audit.retentionDays` sets how long rows are kept.

**Resource limits.** Input size, depth, and counts have upper limits. `elm-service` limits device requests per source IP (`rateLimit.clientPerIp`, 20 per second by default) and per installation key (`rateLimit.clientPerKey`, 2 per second), and administration API requests per source IP (`rateLimit.perIp`, 20 per second) and per API key or administrator (`rateLimit.perKey`, 10 per second). Device and admin traffic use separate limits, so many devices behind one address do not lock out the administrators. When `usageLog.directory` is set, the service also writes leases and borrows to a signed usage log, one chain of files per node.

For every setting of `elm-service`, see [elm-service configuration](../reference/service-configuration.md). For the rows of the audit log and the lines of the usage log, see [Logs](../reference/logs.md).

## For the app developer

The app developer embeds the trust store and the client.

- **Licenses are checked on the device without a network**, against the root public keys built into the app. A forged or edited license fails, whatever the network says. See [license files and verification](license-files-and-verification.md).
- **A license bound to a device works only there.** It names the installation key's thumbprint and machine fingerprints, and renewals and leases need a proof from that key. The key file is protected by the OS (DPAPI on Windows, file modes elsewhere). See [devices and proofs](devices-and-proofs.md). Whoever can read the installation key can copy it with the license file to another machine: an administrator of the device and, with the default user scope, the device's user too. The copy works there when the machine fingerprints also match or the policy's `fingerprintMatching` is `ignore` ([see below](#on-the-licensees-machine)).
- **A clock set back is refused** while the clock guard's state is kept. The guard records the latest trusted time and refuses a clock set behind it. Someone who deletes the state can set the clock back as far as the token's issue time, again and again ([see below](#on-the-licensees-machine)).
- **Every lease is checked**, not trusted because a server sent it: its signature, its product and key, and, on premises, that its feature, seat count, length, and end date are inside the vendor-signed server license. A customer's administrator who takes the server's key out cannot sign a lease outside the server license either, but can sign such leases for more seats than the server license grants ([see below](#on-the-customers-license-server)). The client also refuses a lease that is not the answer to its own request (`ELM_REQUEST_MISMATCH`), so someone on the network path cannot make several apps hold one seat by handing each of them the same genuine answer.
- **An on-premises server must prove who it is.** The client verifies the server license from the root key, requires `kind: server`, checks the server against its pin, and verifies leases with the key the server license names. With the default self-signed certificate, it checks on every connection that the TLS key is that key, so someone on the network who lacks the server's key cannot pose as the server. See [floating licenses](floating-licenses.md).

## For the customer's administrator

The customer's administrator runs `elm-server`.

- **The server cannot lend beyond its server license.** Seats are counted under a lock, after a restart new leases wait until running apps have reclaimed theirs, and borrows come back from the signed borrow record. The apps refuse any lease outside the server license. This holds for the server as shipped: an administrator who takes the server's key out can sign leases for more seats than the server license grants ([see below](#on-the-customers-license-server)).
- **The local administration API is for administrators only.** It listens on no TCP port, only on a socket file in the server's data folder (a file that programs on the same machine connect through, instead of a port), so a web page in a browser on the machine cannot reach it. No other user's program can stand in for the server. The `elm server` commands send the administrator token only to that socket file, and only the users that the data folder admits can create or open it. Those are the server's user and root on Linux and macOS, and the server's user, the Administrators group, and SYSTEM on Windows. A program that listens on a network port never receives the token. The API also needs the administrator token, 256 random bits, which is compared in constant time and made anew at every start of the server, so a token that leaked stops working at the next start. It counts failed attempts per minute instead of logging each one, so they cannot fill the log. Whoever can write to the data folder can replace the socket file and receive the token; that user can read the token and the server's key anyway.
- **The data folder is closed to other users.** On Windows it allows only the Administrators group, SYSTEM, and the server's user; on Linux and macOS its mode is `0700`. The server will not use a folder that another user created first.
- **Edits to the usage log are found.** The log is signed in blocks with the server's key and each file is chained to the previous one, so `elm log verify` finds edited, missing, and cut-off files. It cannot find the newest file deleted or cut after its last signature, nor a log rewritten by someone who holds the server's key ([see below](#on-the-customers-license-server)), nor the oldest files deleted ([Verify the usage log](../how-to/verify-the-usage-log.md#keep-copies-of-the-finished-files)).
- **Floods are limited, not stopped.** Requests are limited per source IP (`rateLimit.perIp`, 20 per second by default). Someone who sends from many sources can still make the server refuse lease requests while they keep sending and for up to 300 seconds after. No setting prevents it; IP rules in `allow` and `deny` limit who can reach the server ([see below](#on-the-customers-license-server)).

For every setting of `elm-server`, see [elm-server configuration](../reference/server-configuration.md). For the usage log and the borrow record, see [Logs](../reference/logs.md).

## Checking what you download

Verify every file you download from a GitHub Release before you run or ship it. The commands are in [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md); in short:

```sh
sha256sum --check --ignore-missing SHA256SUMS   # on macOS: shasum -a 256 --check --ignore-missing SHA256SUMS
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
- **Offline copies.** Someone with admin rights, and with the default user scope the device's user, can copy the installation key and its state to another machine. A copied license file works there when the machine fingerprints also match or the policy is `ignore`; a borrowed lease carries no fingerprints and works on both machines until it ends. If both copies go online, the server finds the clone. A copy that only ever runs offline is not found.
- **Revocation reaches a device only when its token expires.** There is no revocation list. Revoking a license stops new tokens and leases, but a token already on a device works until its expiry plus grace period (30 and 7 days by default). A short `tokenLifetime` narrows this. A revoked signing key reaches an app only in a new app version.
- **A token without an expiry cannot be revoked.** Such tokens come only from issuing by hand with `elm` and from `unbound` license files of licenses that never expire; `elm` warns when it issues one ([`elm license issue`](../reference/elm-cli.md#elm-license-issue)).
- **A license file still works after deactivation.** Deactivation does not delete the installation key, so a saved copy works until the token's expiry and grace period. The policy's `deactivationLimit` can limit how often this can be done; by default it sets no limit.
- **Faked machine fingerprints.** The installation key is the real identity, so fingerprints matter little, except that the one-trial-per-machine rule and the count of early borrow returns depend on them. The client reports its fingerprints, and the server cannot check them. Someone who builds requests with the device protocol can send a new installation key with made-up fingerprints, without administrator rights, and start the count of early returns again. Made-up fingerprints also get a new trial recorded, but its license file names them, so it fails on that device with `ELM_FINGERPRINT_MISMATCH`. The device's own fingerprints sent one at a time get one working trial each, because a trial's file that names one of the device's fingerprints verifies there: two trials on Windows, one on Linux and macOS. More trials that work need every OS machine ID that the fingerprints come from changed (on Windows both), or a patched app.
- **Deleting the clock guard's state.** The guard then falls back to the token's `iat`, so the clock still cannot be set behind it. But someone who deletes the state each time can keep the clock between the token's `iat` and its expiry for as long as they like while offline: the guard limits the window the clock must stay in, not how long the token is used. A short `tokenLifetime` narrows the window.
- **Keeping a returned lease token.** It stays valid until it expires, one lease time for a normal lease; early returns of borrows are limited for this reason (see [floating licenses](floating-licenses.md)).
- **An old offline request file sent after a newer one** is treated as a clone and, with `deny`, stops the device until an administrator deactivates it. Throw old request files away.
- **A TLS-inspecting proxy that the device's OS trusts**, such as a company's inspection box, can read the traffic and fake answers that are not signed. It cannot fake a license or a lease. It can fake an error: a false `ELM_CLONE_DETECTED` or `ELM_UNKNOWN_LEASE` loses a lease, and a false `ELM_ACTIVATION_NOT_FOUND` from `elm-service` deletes the local activation. The harm is limited to that customer's own devices and is undone by activating or acquiring again, like cutting the network.
- **The same proxy can read the license keys that pass through it**, in the bodies of activation and lease requests, and in offline request files sent through it. With a key, whoever runs the proxy can act as a new device and use the licensee's own limits, as the table at the top of this page says: activations, seats of a cloud floating license, or a server of their own. They cannot refresh or deactivate the licensee's devices, which needs those devices' installation keys, and the harm stays inside the licenses whose keys passed the proxy. But the licensee's own devices are then refused (`ELM_ACTIVATION_LIMIT`, `ELM_NO_SEATS`), and activating again does not undo it. Their own deactivations count against the policy's `deactivationLimit`, and under `expirationBasis` `fromFirstActivation` their first activation or lease can start the license's term. The vendor can deactivate the activations that the licensee does not recognize, which frees their places, but the key still works. To stop it, the vendor revokes the license and issues a new one with a new key, which every device of the licensee activates again; a license's key cannot be changed. Revoking stops new tokens and leases, but a token already on a device works until its expiry plus grace period (30 and 7 days by default), and a borrow until it ends.

### On the customer's license server

- **The administrator takes the server's key out.** They have root on the machine. With the key they can sign as many leases as they like, so more seats are in use than the server license grants, and they can rewrite the usage log. An app checks each lease on its own: its feature, its seat count, its length, and its end must be inside the server license, but the app does not know the other leases. The vendor cannot see how many leases are in use, because a renewal of the server license does not report them and the usage log can be rewritten. Once the vendor stops renewing the server license, the leases end after its expiry plus grace period (30 and 7 days by default). Nothing protects the key from someone with root on the server machine.
- **A copied server machine** is found only at the next renewal of its server license from `elm-service`, up to 30 days later by default. A copy of a server whose license was issued by hand is never found; only that license's expiry ends it.
- **The administrator loads an older server license file again.** The server checks a file loaded by hand as at a start, but does not compare it with the file it replaces: a file issued with `elm license issue` has no sequence number. After a renewal that grants fewer seats or features, an older file that has not expired keeps granting the old ones. A server that renews online from `elm-service` (`service.url`) goes back to its newest license within an hour when the older file has the same license ID, and at its next renewal otherwise; a server activated offline keeps the older file until the administrator imports the next renewal. Issue files by hand with a short expiry, or activate the server with `elm-service`; see [Renew a server license](../how-to/renew-a-server-license.md#what-a-new-file-cannot-take-back).
- **The administrator frees borrowed seats** by deleting the borrow record, moving the clock forward, or starting with `--set-aside-broken-records`. The usage log keeps traces, and the damage ends at the borrows' expiry.
- **The first connection is trusted** when the app does not set `ExpectedServerLicenseId`; see [floating licenses](floating-licenses.md).
- **Host and user names in `allow` and `deny` can be faked**, because the client reports them. Only IP rules are reliable.
- **Deleting the newest usage log file, or cutting it after its last signature**, cannot be found, because there is no next file to compare with. Seat counting does not depend on the log.
- **Proofs sent from many sources can fill the server's record of used proofs.** So that no proof is accepted twice, `elm-server` keeps the ID of every proof it accepted until the proof's nonce expires (at most 300 seconds), up to 100,000 IDs; when the record is full, it refuses lease requests with `503` and `ELM_SERVER_BUSY` instead of dropping IDs that are still needed. Someone on the customer's network who sends proofs from enough sources, each within `rateLimit.perIp`, fills it: at the default 20 requests per second, about 17 sources are enough (100,000 ÷ (20 × 300)). While they keep sending, no app can acquire or renew a lease, and running apps lose their leases when the leases expire. The server accepts requests again within 300 seconds after they stop.

### Around the hosted service and its administrators

- **Someone who can write to the database** can rewrite the audit log and recompute its hashes, and can add sessions. With the database and the key-encryption key, they can also read and forge sign-ins in progress. Protect the database and its backups like the signing key, and keep the key-encryption key outside the database.
- **Someone with the signing key and its passphrase** can rewrite the service's usage log, and can also forge licenses.
- **A licensee with activations can grow the audit log**, but only under `cloneStrategy`: `flag`, by triggering clone detection again and again; with `deny`, only the one line when the activation stops. Reactivating adds lines too. The per-key limit (2 per second) bounds it, and the vendor can deactivate that device.
- **A licensee with borrow rights can grow the early-return table** with new keys and fingerprints. The rows count only against their own license and are deleted after 30 days.
- **Anyone who can reach the service can start trials** of a product that offers them, with new keys and fingerprints each time, and the rows of a trial stay after it ends. The trial policy's `trialDailyLimit` (1,000 by default) bounds them for each product in any 24 hours. That limit can be used up: one source, at the default `rateLimit.clientPerIp` of 20 requests a second, takes the default 1,000 trials in under a minute, and by asking again as often as a trial turns 24 hours old, it keeps new trials of that product refused for as long as it goes on. Other products, and activations with a license key, are not affected. The service writes a warning to its operational log; [Offer a trial](../how-to/offer-a-trial.md#set-how-many-trials-the-product-gives-in-24-hours) says how to find and block such a source.
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
- **A job of the release workflow can change what other jobs publish.** A dependency or build tool taken over inside one job of the release workflow can replace this site's files, which are published without being compared with what the site's own job built, and the archives of another platform, because the jobs that build the platforms share their outputs. The attestations still look correct. Publishing the site still needs the owner's approval, and checking a download does not rely on this site.
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
- Choose policy values with the accepted threats in mind: a short `tokenLifetime` limits copies and late revocation; `leaseDuration` trades outage tolerance against the wait after a restart; `borrowLimit` and `borrowReturnLimit` bound borrows; `cloneStrategy`: `deny` stops clones; `trialDailyLimit` bounds the trials a product gives in any 24 hours. Avoid licenses without an expiry.
- Protect the database and its backups, and keep the key-encryption key outside them.
- Where it matters that a customer's administrator could use more seats than were bought, sell cloud floating licenses from `elm-service`, whose seats are counted in your own database. On premises, the seat count rests on your contract with the customer, not on cryptography: an administrator with root on the server machine can take its key out ([see above](#on-the-customers-license-server)).
- At the identity provider, allow the admin UI's client only the authorization code flow, use an `oidc.audience` different from both client IDs, put the role claim in both the ID token and the access token, put the claim of `oidc.subjectClaim` in both with the same value (with Microsoft Entra ID and `oid`, add `profile` to both `oidc.cliScopes` and `oidc.webScopes`), and show the client's name on the device approval screen.
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
- Keep the data folder and the administrator token as the server creates them: readable only by the server's user and root on Linux and macOS, and only by the server's user, the Administrators group, and SYSTEM on Windows. Keep the data folder on a local file system, and start only one server on it ([Data folder](../reference/server-configuration.md#data-folder)).
- Give the server its own `dataDirectory` when an app of the same product runs on the machine.
- Rely on IP rules in `allow` and `deny`. Behind a reverse proxy, apply IP rules at the proxy and raise `rateLimit.perIp` to match the machines behind it.
- Use `--set-aside-broken-records` only after finding out why the borrow record broke; it frees borrowed seats.
- Renew the server license before it expires, and verify every `elm-server` and `elm` download.

### Everyone

Report a vulnerability through GitHub Security Advisories, not in a public issue, as [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md) says. Do not include real private keys, license tokens, customer identifiers, or machine IDs in a report.

Why the model is drawn where it is (no obfuscation, no revocation list, no standby server) is on [design choices](design-choices.md).
