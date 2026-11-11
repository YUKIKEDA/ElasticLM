---
sidebar_position: 3
---

# License files and verification

This page is for vendors who issue license files and set the policies behind them, and for app developers who verify license files in their app with `ElasticLM.Licensing` or `libelasticlm` (every type and function is in [.NET API](../reference/dotnet-api.md) and [C API](../reference/c-api.md)). It explains what a license token and a license file hold, the order in which a verifier checks them, how expiry, the grace period, and clock rollback detection work, and what the verifier reports. Read [the trust chain](trust-chain.md) first: this page assumes you know what a root key, a signing key, a key certificate, a key ID (`kid`), and the trust store are.

## What a license file is for

A license file lets an app decide, without a network, whether this machine may use this product and feature right now. The app reads the file, checks it against the trust store built into the app, and gets either a verified license or the reason it was refused.

The file can come from three places:

- `elm-service` returns one when a device activates or refreshes online, and as the answer to an offline request (see [activation](activation.md)).
- The administration API of `elm-service` produces one for an unbound license (a license whose policy has `binding`: `unbound`), which is not tied to a machine.
- `elm license issue` writes one without any service, for vendors who run fully offline.

All three produce the same format, and the app verifies them the same way.

For the options of `elm license issue`, and of `elm license inspect` and `elm license verify`, which read a file, see the [elm command reference](../reference/elm-cli.md).

## The license token

A license token is a JWS in compact form: three base64url parts separated by `.`, holding a header, a JSON payload, and a signature. ElasticLM uses a narrowed form of JWS, not JOSE in general.

### Header

The header has exactly three members: `alg`, which must be `ES256`; `kid`, the key ID of the signing key; and `typ`, the token type, which is `elm-license+jws` for a license token.

A header with any other member (`jwk`, `x5c`, `crit`, or anything else) is rejected, so a token cannot bring its own key. The verifier never takes a key or an algorithm from the token: it finds the key through the certificates, and `alg` must be `ES256`. Each verifying function accepts one `typ` only, so a lease token cannot be passed off as a license.

The signature is checked over the exact bytes received, and the payload is not parsed as JSON until the signature has been checked. Nothing in the payload is trusted, or even read, before then. The rules every token follows, and the seven token types, are in [Token and file formats](../reference/formats.md#jws-rules).

### Claims

Every token has `ver` (the format version, `1`), `iat` (the time it was issued), and `jti` (a random token ID). A license token also has `iss`, the key ID of the signing key, which must equal the header's `kid`. Times are UNIX seconds. Unknown claims are ignored; an unknown `ver` or a missing required claim is rejected.

The license claims carry:

- `lic`: the license ID; `prd`: the product ID (the product's `code`); `kind`: `standard`, `trial`, or `server`.
- `feat`: the features, possibly none. Each has an `id` and may have `ver` (an allowed version range) and `exp` (the feature's own expiry). Each feature has `seats` when, and only when, the license is `kind: server`.
- `exp`: the token's expiry, if it has one. With `exp` come `grace` (how long after `exp` the license is still usable; `0` for a trial) and, for tokens refreshed online, `refresh` (how long before `exp` the client starts to refresh).
- `cnf` and `fp`, `fpm`: the machine binding of a node-locked token. `cnf` holds the [thumbprint](devices-and-proofs.md#the-installation-key) of the device's installation key; `fp` holds machine fingerprints and `fpm` says how they are compared.
- `seq`: the activation's sequence number, used for clone detection; `areq`: the nonce of the offline request this token answers.
- For a server license, the lease period, borrow limit, overage, and clone strategy (what the server does when a copied app installation renews the same lease, see [Retries and clones](floating-licenses.md#retries-and-clones)) that the on-premises server must keep.
- `meta`: vendor strings that the verifier does not interpret.

`cnf`, `fp`, and `seq` are explained in [devices and proofs](devices-and-proofs.md) and [activation](activation.md); the server license values in [floating licenses](floating-licenses.md). Every claim, with its type, range, and when it is required, is listed in [Token and file formats](../reference/formats.md#license-token-elm-licensejws).

### Signed, not encrypted

A license token is signed, not encrypted. Anyone holding the file can read every claim, including `meta`. Do not put secrets in it. The machine binding does not depend on secrecy: it is protected by the signature over `cnf` and `fp`. Encrypting would not hide anything either, because the decryption key would have to sit on the client (see [design choices](design-choices.md)).

## The license file

A license file is UTF-8 JSON with the extension `.elmlic`:

```json
{
  "format": "elasticlm-license",
  "version": 1,
  "license": "<elm-license+jws>",
  "certificates": ["<elm-cert+jws>"]
}
```

`certificates` holds 1 to 4 key certificates of the key that signed the token, so the file can be verified from the root key with nothing else. The outer JSON is not signed; each token inside it is signed on its own. Changing the outer JSON can only make the file fail, not make it pass.

The verifier sets limits on the file's size, the number of entries in each list, and the nesting depth, so that a hostile file cannot use up memory or time. The file is at most 64 KiB. The members of the file are in [Token and file formats](../reference/formats.md#license-file-elmlic), and the full list of limits in [Size limits](../reference/formats.md#size-limits).

## The order of checks

The verifier checks the file in a fixed order and returns the reason for the first check that fails.

The checks compare the token with the app's **requirement**: what the app asks of a license. In .NET it is a `LicenseRequirement` the app passes to each call, holding the product ID, the license kind, and optionally a feature, the version the app runs as, and the device's installation identity. In C it is an `elm_requirement`.

```mermaid
flowchart TD
    S1["1 Size and format of the file and the token header"]
    S2["2 Signer's kid on the revoked list?"]
    S3["3 Collect certificates: trusted root, not revoked,<br/>correct signature, use license, sub equals kid"]
    S4["4 Token signature under the certified key"]
    S4b["Payload follows the claim rules,<br/>iss equals kid"]
    S5["5 Token iat inside a candidate certificate's period"]
    S6["6 Token iat not in the future"]
    S7["7 Clock guard: clock not set back"]
    S8["8 kind matches the requirement"]
    S9["9 prd matches the product"]
    S10["10 cnf matches the installation key,<br/>fp matches the machine fingerprints"]
    S11["11 Not past exp plus grace"]
    S12["12 Feature present, version in range, not expired"]
    OK["Valid, with InGracePeriod"]

    S1 -->|pass| S2 -->|pass| S3 -->|pass| S4 -->|pass| S4b -->|pass| S5 -->|pass| S6 -->|pass| S7
    S7 -->|pass| S8 -->|pass| S9 -->|pass| S10 -->|pass| S11 -->|pass| S12 -->|pass| OK

    S1 -->|fail| E1["ELM_MALFORMED"]
    S1 -->|"alg not ES256"| E4
    S2 -->|fail| E2["ELM_KEY_REVOKED"]
    S3 -->|none left| E3["ELM_UNTRUSTED_KEY"]
    S4 -->|fail| E4["ELM_BAD_SIGNATURE"]
    S4b -->|fail| E1
    S5 -->|fail| E5["ELM_CERTIFICATE_EXPIRED"]
    S6 -->|fail| E6["ELM_CLOCK_TAMPERED"]
    S7 -->|fail| E6
    S8 -->|fail| E8["ELM_WRONG_KIND"]
    S9 -->|fail| E9["ELM_WRONG_PRODUCT"]
    S10 -->|fail| E10["ELM_WRONG_INSTALLATION,<br/>ELM_FINGERPRINT_UNAVAILABLE, or<br/>ELM_FINGERPRINT_MISMATCH"]
    S11 -->|fail| E11["ELM_EXPIRED"]
    S12 -->|fail| E12["ELM_FEATURE_NOT_LICENSED,<br/>ELM_FEATURE_VERSION_NOT_LICENSED, or<br/>ELM_FEATURE_EXPIRED"]
```

1. **Size and format.** The file is within its limits, is the JSON above with `format` `elasticlm-license` and `version` `1`, and has 1 to 4 certificates. The token's header has exactly `alg`, `kid`, and `typ` `elm-license+jws`. A failure is `ELM_MALFORMED`, except that an `alg` other than `ES256` is `ELM_BAD_SIGNATURE`. Only the header is read here.
2. **Revoked signer.** If the token's `kid` is on the trust store's list of revoked key IDs, the result is `ELM_KEY_REVOKED`.
3. **Certificates.** A certificate becomes a candidate when its header names a root key in the trust store that is not revoked, the root's signature on it is correct, its `use` includes `license`, and its `sub` equals the token's `kid`. A certificate that fails is skipped, not fatal, so an old certificate kept for rotation does no harm. With no candidate left, the result is `ELM_UNTRUSTED_KEY`.
4. **Signature.** The token's signature is checked once, with the certified key. A failure is `ELM_BAD_SIGNATURE`. Only now is the payload read: a payload that breaks the claim rules, or whose `iss` differs from the `kid`, is `ELM_MALFORMED`.
5. **Certificate period.** At least one candidate's period (`nbf` to `exp`, widened by the allowed clock skew) must contain the token's `iat`. Otherwise the result is `ELM_CERTIFICATE_EXPIRED`. Why the issue time, not the current time, is compared is explained in [the trust chain](trust-chain.md).
6. **Issue time.** If `iat` is later than the current time plus the allowed skew, the result is `ELM_CLOCK_TAMPERED`.
7. **Clock guard.** If the clock has been set back (see below), the result is `ELM_CLOCK_TAMPERED`.
8. **Kind.** The license's `kind` must be the one the requirement asks for, or the result is `ELM_WRONG_KIND`. A `standard` requirement also accepts a `trial` license while the verifier's `AcceptTrialAsStandard` option is on, which is the default; with it off, a trial is refused where a standard license is required. A `server` license is accepted only by a `server` requirement, and a `server` requirement accepts nothing else.
9. **Product.** `prd` must equal the requirement's product ID, or the result is `ELM_WRONG_PRODUCT`.
10. **Machine.** If the token has `cnf`, the requirement's installation identity must have the same key thumbprint, or the result is `ELM_WRONG_INSTALLATION`. Then, unless `fpm` is `ignore`, `fp` is compared with the machine's fingerprints: if the machine yields no fingerprint at all, the result is `ELM_FINGERPRINT_UNAVAILABLE`; if they do not match as `fpm` says, it is `ELM_FINGERPRINT_MISMATCH`.
11. **Expiry.** Without `exp`, the token passes. Between `exp` and `exp + grace`, it passes in the grace period. After `exp + grace`, the result is `ELM_EXPIRED`.
12. **Feature.** If the requirement names a feature: a feature missing from `feat` is `ELM_FEATURE_NOT_LICENSED`, a version outside its range is `ELM_FEATURE_VERSION_NOT_LICENSED`, and a feature past its own `exp` is `ELM_FEATURE_EXPIRED`.

Two points of the order matter. Nothing from the payload is used until the signature is proven (steps 1 to 4). And the clock guard in step 7 records every token that passed steps 1 to 6, even one that then fails a later step such as product or expiry: its `iat` is signed by the vendor, so it is safe to record.

**Clock skew.** The verifier allows for a small difference between the device's clock and the issuer's. The skew widens the certificate period (step 5), the future-`iat` check (step 6), and the clock guard (step 7). It is not added to `exp`.

- In .NET, `LicenseVerifierOptions.ClockSkew` is 5 minutes by default and may be set from zero to one hour. Zero means no skew is allowed.
- In C, `clock_skew_seconds` of `0` means the default, 300 seconds; any other value must be 1 to 3,600 seconds.

## Expiry and grace period

The service issues node-locked tokens with a short life and refreshes them online. Three policy settings control this:

- `tokenLifetime` (default 30 days) sets `exp`.
- `refreshWindow` (default 20 days) sets `refresh`: the client starts to refresh when less than this remains.
- `gracePeriod` (default 7 days) sets `grace`.

With the defaults, a device starts refreshing 10 days after it received its token. If the service cannot be reached, the app keeps working while the token is inside its expiry and grace period, and the client tries again every hour. With the defaults, a device that cannot refresh keeps working for up to 37 days after its last refresh (30 days of token lifetime plus 7 days of grace) and then stops with `ELM_EXPIRED` until it refreshes.

The grace period exists so that a short network outage near the expiry does not stop users. The verifier reports it (`InGracePeriod`), so the app can warn the user that it needs to connect. A trial license always has a grace period of `0`.

A feature's own `exp` has no grace period: once it passes, that feature fails with `ELM_FEATURE_EXPIRED`, even if the token as a whole is still valid.

A token without `exp` never expires. Only license files issued by hand with `elm license issue --expires never`, and unbound license files of licenses without expiry, have no `exp`. Revoking or suspending a license in the service does not reach a token already on a device until it expires, so a short `tokenLifetime` is what makes revocation take effect; [the security model](security-model.md) explains this limit, and [the trust chain](trust-chain.md) the only way to stop a token without expiry.

## Product, feature, and machine binding

**Product.** `prd` must equal the requirement's product ID exactly, so a license for another product of the same vendor fails.

**Feature.** The requirement may name a feature and the version the app runs as. A feature's `ver` is a range such as `>=2.0 <3.0`, with versions written as `major.minor[.patch]`. Without `ver`, every version is allowed. A range lets a vendor grant across major versions, which a fixed "same major" rule could not express. If the requirement names no feature, step 12 is skipped; if it names no version, the range is not checked.

**Machine.** A node-locked token carries two kinds of binding:

- `cnf.jkt`, the thumbprint of the device's installation key. This is the real identifier of the device. The requirement passes an `InstallationIdentity` with the key's thumbprint; if the token has `cnf` and the requirement passes no identity, verification fails with `ELM_WRONG_INSTALLATION`.
- `fp`, the machine fingerprints, compared as `fpm` says: `any` passes when one fingerprint in the token matches the machine, `all` needs every one to match, and `ignore` skips the comparison. The policy setting `fingerprintMatching` decides `fpm`.

Both claims are signed, so a licensee cannot edit them. Copying the file to another machine fails because that machine has a different installation key. How the installation key and the fingerprints are made, and why the key is the identifier rather than the OS machine ID, is in [devices and proofs](devices-and-proofs.md).

An unbound license has neither `cnf` nor `fp` and passes on any machine. Use it only when that is what you sell.

## Clock rollback detection and its limits

A licensee could set the device clock back to keep using an expired token. The clock guard detects this:

1. For every token that passes steps 1 to 6, the guard records the later of the current time and the token's `iat` as the "last time seen".
2. If the current time is earlier than the last time seen minus the allowed skew, the clock has been set back, and the result is `ELM_CLOCK_TAMPERED`.
3. The state is kept in a file (`FileClockGuard` in .NET; in C, the file `clock` in the installation's folder unless `clock_guard_path` names another). When an installation key is available, the state is signed with it, and a state whose signature fails is treated as missing. Where each client keeps the state is in [Files the client keeps](../reference/formats.md#files-the-client-keeps).
4. A state that is missing, edited, or unreadable falls back to the `iat` of the token being verified. The token's `iat` comes from the signer's clock, so whatever happens to the state, the clock can never be set back before the token was issued.
5. The guard also runs when lease tokens are verified, including borrowed leases, which are checked against the device clock while offline (see [floating licenses](floating-licenses.md)).

A user whose clock jumped forward and then came back is locked out until real time catches up with the recorded time. A successful online refresh fixes this at once: the client takes the new token's `iat` as the service's time and overwrites the record. Importing an offline response does not overwrite it, because a response can grow old while it is carried by hand.

What the guard does not stop:

- **Deleting or editing the state.** The guard then falls back to the token's `iat`. A licensee can still set the clock back to any time after `iat`, so the real limit is the token's expiry. Keep `tokenLifetime` short.
- **An unsigned state.** Without an installation key (an unbound license), the state is not signed, and anyone who can write the file can reset the guard to the `iat` floor.
- **A stateless guard.** `StatelessClockGuard` keeps no state and checks only that `iat` is not in the future (step 6). In C you get it when you pass neither an installation nor a `clock_guard_path`.
- **Patching the app** to skip the check. This is out of scope, as [the security model](security-model.md) explains.

If the state cannot be written, verification continues with the value in memory and the token's `iat`, and does not throw.

## What the result reports

In .NET, `LicenseVerifier.Verify` returns one of two results:

- `LicenseVerificationResult.Valid`, with the license and whether it is in its grace period (`InGracePeriod`). The license is a `VerifiedLicense` that holds the claims in typed form: the license ID, product, kind, features, expiry, binding, and the rest. Only the verifier can make one, so code that holds a `VerifiedLicense` knows the checks passed.
- `LicenseVerificationResult.Invalid`, with a `LicenseError` that gives the error code, its wire name (such as `ELM_EXPIRED`), and a one-sentence message. The message never contains key material or the full token.

A broken file never throws, whatever its content. Exceptions are only for mistakes in the call: an empty product ID, a required version that is not `major.minor[.patch]`, a clock skew out of range, or a bad trust store key.

In C, `elm_verifier_verify` returns the error code and, on success, an `elm_license` handle; `elm_license_in_grace_period` tells whether the license is in its grace period.

The verifier does not decide what the app does. On `Invalid`, the app chooses whether to stop, fall back to a limited mode, or ask the user to activate again. On `Valid` with `InGracePeriod`, it should tell the user to connect.

## What can go wrong and what to do

| Code | Usual cause | What to do |
| --- | --- | --- |
| `ELM_MALFORMED` | The file was truncated, edited, or is not a license file | Get the file again from its source |
| `ELM_UNTRUSTED_KEY`, `ELM_CERTIFICATE_EXPIRED`, `ELM_KEY_REVOKED` | A trust chain problem | See [the trust chain](trust-chain.md) |
| `ELM_BAD_SIGNATURE` | The token was edited after signing | Get the file again; an edited file never verifies |
| `ELM_CLOCK_TAMPERED` | The clock is behind the issue time or behind the last time seen | Correct the clock; refresh online to reset the record |
| `ELM_WRONG_KIND` | The license's `kind` is not the one the requirement asks for: for example a trial where a server license is required, or a trial where a standard license is required and `AcceptTrialAsStandard` is off | Check the kind the app requires and the `AcceptTrialAsStandard` option; issue a license of the right kind |
| `ELM_WRONG_PRODUCT` | The file is for another product | Check the product ID the app passes against the product's `code` |
| `ELM_WRONG_INSTALLATION` | The file was made for another device, or the app passed no installation identity | Activate this device; pass the installation identity |
| `ELM_FINGERPRINT_MISMATCH` | The machine's fingerprints changed, for example after the OS was reinstalled or the VM was moved | Deactivate the device and activate it again, so that it receives a token with its current fingerprints (the deactivation counts against the policy's `deactivationLimit`), or have the vendor deactivate it before it activates again. An online refresh keeps the old fingerprints (see [activation](activation.md)) |
| `ELM_FINGERPRINT_UNAVAILABLE` | The machine yields no fingerprint, often a container without `/etc/machine-id` | Mount the host's `/etc/machine-id` read-only, or use a policy with `fingerprintMatching` set to `ignore` |
| `ELM_EXPIRED` | Past `exp + grace` | Refresh online, or bring a new file by offline refresh |
| `ELM_FEATURE_NOT_LICENSED`, `ELM_FEATURE_VERSION_NOT_LICENSED`, `ELM_FEATURE_EXPIRED` | The license does not cover this feature or version, or the feature has ended | Issue a license with the feature, the range, or a later feature expiry |

For every error code, with its number and the HTTP status the servers send, see [Error codes](../reference/error-codes.md).

What each role must do:

- **Vendor:** keep token lifetimes short enough that revocation takes effect in time; avoid tokens without expiry; do not put secrets in `meta`; issue unbound licenses only when any machine may use them.
- **App developer:** pass the product ID, the required kind, and the feature and version you need; pass the installation identity for node-locked licenses; use a clock guard with state; handle `InGracePeriod` and every `Invalid` code instead of treating them all alike.
