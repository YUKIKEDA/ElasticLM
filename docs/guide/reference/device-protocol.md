---
sidebar_position: 7
---

# Device protocol

This page lists every HTTP route that devices use: the routes of the hosted activation service (`elm-service`) for activation, refresh, deactivation, trials, offline files, and cloud leases, and the routes of the on-premises license server (`elm-server`) for leases. For each route it gives the authentication, the request body, the answer, its headers, and the error codes. It is for people who write their own client or read the traffic while debugging. Most apps do not need it: `ElasticLM.Client` (`ActivationClient` and `LicenseClient`) and `libelasticlm` send these requests for them. Read [Devices and proofs](../explanation/devices-and-proofs.md) first for why requests are signed, and [Activation](../explanation/activation.md) and [Floating licenses](../explanation/floating-licenses.md) for what the routes do.

## Rules for every route

### Transport

- JSON over HTTP. Every path starts with `/v1/`.
- `elm-service` answers on its `listen` URL. A proof names one of its `publicUrls`, which default to the `listen` URL only when that is a loopback address (see [elm-service configuration](service-configuration.md#top-level-settings)).
- `elm-server` answers these routes only on its `listen` port, over TLS. On the socket file of its local administration API (a file that programs on the server's machine connect through, instead of a port), the same paths answer `404` with an empty body (see [elm-server configuration](server-configuration.md#ports)). A proof names one of its `publicUrls`, which default to URLs built from `listen` (see [elm-server configuration](server-configuration.md#top-level-settings)).
- A request body is at most 64 KiB. A larger one gets `413` with `ELM_PAYLOAD_TOO_LARGE`. Neither program checks `Content-Type`.

The client accepts the TLS certificate of `elm-server` on either of two grounds. The OS validates it, as it does an administrator's own certificate. Or its public key is a P-256 key whose [thumbprint](formats.md#key-ids) is the `cnf.jkt` of the server license, as the default self-signed certificate's key is. The client takes `cnf.jkt` from the server license that `GET /v1/server/license` returned, after it has verified that license. On the first connection, the libraries record the certificate, fetch and verify the server license, and compare only then; on a mismatch they discard the answer and report `ELM_SERVER_PIN_MISMATCH`. A proxy that intercepts TLS is accepted only when the OS trusts its certificate. `elm-service` is checked with the OS's trust only. See [TLS](../explanation/floating-licenses.md#tls).

### Headers

| Header | Sent in | Program | Value |
| --- | --- | --- | --- |
| `ELM-Proof` | Request | Both | The proof of the request (below). One header field: a request with two fields gets `ELM_PROOF_INVALID`. |
| `ELM-Nonce` | Response | Both | A new nonce for the next proof. `elm-service` sends it in every answer of its device routes, errors included, except the answers of `POST /v1/offline` and the `429` of the per-source rate limit. `elm-server` sends it in every answer of `GET /v1/nonce` and of its four lease routes, errors included, except the `403` of its IP rules and the `429` of its rate limit. |
| `ELM-Server-License` | Response | `elm-server` | The token ID (`jti`) of the server license file that the server issues leases under now, in the success answers of `POST /v1/leases` and `POST /v1/leases/{id}/renew`. It is the value that the local administration API shows as `licenseToken` (see [`GET /admin/v1/status`](administration-api.md#get-adminv1status)). When it differs from the token ID of the client's copy, the client fetches `GET /v1/server/license` again. |
| `Retry-After` | Response | Both | Seconds to wait. With `ELM_RATE_LIMITED` it is always `1`. With `ELM_SERVER_RECOVERING` it is the rest of the reclaim window, rounded up. |

### The proof

A proof shows that the holder of an installation key made this request, for this server, now. It is a compact JWS of at most 16,384 characters, signed with ES256: the signature is the 64-byte `r || s` form, in base64url.

Header members, exactly these three:

| Name | Value |
| --- | --- |
| `alg` | `ES256` |
| `kid` | The thumbprint of the installation key: the RFC 7638 JWK thumbprint of its public key (SHA-256, base64url). |
| `typ` | `elm-proof+jws` |

Payload claims:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `iat` | integer | yes | When the proof was made, in seconds since 1970, from 0 to 253402300799. It is not used for freshness: the nonce is. |
| `jti` | string | yes | 1 to 64 characters, used only once (see [Nonces](#nonces)). The libraries send 128 random bits in base64url. |
| `htm` | string | yes | The request method, exactly as sent, such as `POST`. |
| `htu` | string | yes | The URL the request is sent to, normalized (see [The target URL in `htu`](#the-target-url-in-htu)). |
| `nonce` | string | yes | A nonce from this server that has not expired. A proof without this claim gets `ELM_PROOF_INVALID`. |
| `bdy` | string | yes | The SHA-256 of the exact body bytes, in base64url. Without a body, the hash of the empty string: `47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU`. |
| `jwk` | object | on some routes | The public key: `kty` `EC`, `crv` `P-256`, and the coordinates `x` and `y` (32 bytes each, base64url), and no other member. When the server uses it, its thumbprint must equal `kid`, and the point must be on the curve. |

Unknown claims are ignored, even when one is given twice. A claim of this table given twice makes the proof invalid.

Which key checks the signature:

| Routes | Key |
| --- | --- |
| `elm-service`: `POST /v1/activations`, `POST /v1/trials`, `POST /v1/leases`. `elm-server`: `POST /v1/leases`, `POST /v1/leases/reclaim`, and `DELETE /v1/leases/{id}` of a borrow | The `jwk` in the proof. |
| `elm-service`: `POST /v1/activations/{id}/refresh`, `POST /v1/activations/{id}/deactivate` | The key the activation was made with. |
| Both: `POST /v1/leases/{id}/renew`, `DELETE /v1/leases/{id}` of a lease that is not a borrow | The key the lease was acquired or reclaimed with. |

The server finds the key for the signature by `kid`, in this order: the keys it has verified since it started (up to 10,000), then the key the activation or lease is bound to, then the `jwk` in the proof.

- `jwk` missing where the server does not know the key yet, such as on a first activation or after a restart: `401` with `ELM_PROOF_INVALID`. A proof without `jwk` passes on a route of the first row only while the server still remembers the key, so send `jwk` on those routes every time.
- `jwk` present where the server knows the key: the known key checks the signature, and `jwk` is not used. It must still be an object with only the string members `kty`, `crv`, `x`, and `y`; otherwise the proof gets `ELM_PROOF_INVALID`.
- A proof signed by another key than the one the activation or lease is bound to gets `401` with `ELM_PROOF_INVALID`.
- `elm-server` does not keep the key of a borrow that it restored from its borrow record after a restart, so the return of a borrow must carry `jwk`.

The checks, in order:

| Check | Refused with |
| --- | --- |
| The header is present once, is a compact JWS with the header above, and the signature verifies with the key | `401` `ELM_PROOF_INVALID` |
| The payload has every claim of the table above within its range. A missing claim, `nonce` included, fails here | `401` `ELM_PROOF_INVALID` |
| `htm` and `htu` match the request | `401` `ELM_PROOF_INVALID` |
| `bdy` matches the body received | `401` `ELM_PROOF_INVALID` |
| `nonce` was issued by this server and has not expired. An empty or unknown `nonce` fails here | `401` `ELM_NONCE_REQUIRED`, with a new nonce in `ELM-Nonce` |
| `elm-server` only: `jti` was not used before | `401` `ELM_REPLAYED` |
| `elm-server` only: the store of used `jti` values has room (100,000) | `503` `ELM_SERVER_BUSY` |
| `elm-service` only: the proof is signed by the key the activation or lease is bound to | `401` `ELM_PROOF_INVALID` |
| `elm-service` only: the per-key rate limit (`rateLimit.clientPerKey`) | `429` `ELM_RATE_LIMITED`, with `Retry-After: 1` |
| `elm-service` only: `jti` was not used before | `401` `ELM_REPLAYED` |

`elm-server` has no per-key rate limit. It checks that a proof is signed by the lease's key after the body is read, with the route's own checks.

A client without a nonce, such as at its first request, gets one from `GET /v1/nonce` (see [Nonces](#nonces)). A refusal of the proof check carries a new nonce in `ELM-Nonce`, as every answer named in [Headers](#headers) does.

`elm-service` records the `jti` in its database after the per-key rate limit and before it parses the body: a request refused after that point still uses up its `jti`. `elm-server` records the `jti` in memory.

A proof as the libraries make it, for a refresh, decoded (the signature is left out). The libraries write the `+` of `typ` as the escape `\u002B`, as the example shows; a JSON parser reads it as `+` (see [Token and file formats](formats.md#header)):

```json
{"alg":"ES256","kid":"FAKE-THUMBPRINT-0000000000000000000000000A1","typ":"elm-proof\u002Bjws"}
```

```json
{
  "ver": 1,
  "iat": 1793532548,
  "jti": "FAKEjti000000000000001",
  "htm": "POST",
  "htu": "https://licensing.example.com/v1/activations/01900000-0000-7000-8000-00000000a001/refresh",
  "nonce": "AAAAAGrnIoQfakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonce1",
  "bdy": "tB6VxD84jVYbFGMm2E46a54xxO8yq5fAR3TDGq2jJGo"
}
```

Here `bdy` is the hash of the body `{"seq":1}`.

### The target URL in `htu`

The client puts in `htu` the absolute URL it sends the request to, in this form:

- The scheme and host in lowercase.
- The port only when it is not the default (443 for `https`, 80 for `http`).
- The path as sent, with `.` and `..` segments resolved, percent-encoded unreserved characters decoded, and the remaining percent-encodings in uppercase hex.
- No query and no fragment.

The libraries take the server URL the app was given, add a `/` when it does not end with one, and append the route's path without its leading `/`, such as `v1/activations`. A path in the server URL is kept.

The server makes its own list of accepted values. For each entry of its `publicUrls`, it takes the entry with any `/` at the end of its path removed, appends the path the request arrived at, and normalizes the result the same way. `htu` must equal one of these values exactly. The `Host` header is not used. So:

- A `publicUrls` entry with or without a `/` at the end gives the same value.
- The case of the host and a default port written out in a `publicUrls` entry do not matter, because the entry is normalized. In `htu`, they must already be in the normalized form: a proof whose `htu` is `https://Licensing.example.com:443/v1/activations` gets `ELM_PROOF_INVALID`.
- Behind a reverse proxy that serves the program under a path prefix, the prefix is part of the `publicUrls` entry and of `htu`. The program serves its routes only at paths that start with `/v1/`, so the proxy forwards the request without the prefix.
- A proxy that forwards the request to the URL the client sent it to, such as an HTTP proxy the client is set to use or a transparent proxy, keeps `htu` valid. A debugging proxy that the client addresses instead of the server, such as `https://localhost:8888`, changes the URL: the proof names the proxy's URL and gets `401` with `ELM_PROOF_INVALID`, unless that URL is in `publicUrls`.

Two examples:

| `publicUrls` entry | Request | `htu` |
| --- | --- | --- |
| `https://licensing.example.com` | `POST https://Licensing.Example.com:443/v1/activations` | `https://licensing.example.com/v1/activations` |
| `https://www.example.com/elm/`, behind a proxy that forwards `/elm/v1/…` as `/v1/…` | `POST https://www.example.com/elm/v1/leases/FAKElease0000000000001/renew` | `https://www.example.com/elm/v1/leases/FAKElease0000000000001/renew` |

### Nonces

- A client gets a nonce from `GET /v1/nonce`, or from the `ELM-Nonce` header of an earlier answer.
- A nonce is an opaque string. It is valid for 300 seconds after the server made it. It may be used by several proofs while it is valid; each proof needs its own `jti`.
- `elm-server` keeps each used `jti` until its nonce expires. `elm-service` keeps it for 360 seconds after the request, which is longer than any nonce is valid.
- All nodes of `elm-service`, the instances that share one database (see [`node`](service-configuration.md#node)), share one nonce secret (`nonce.secretFile`, see [elm-service configuration](service-configuration.md#nonce)), so any node accepts a nonce that another node made. `elm-server` makes a new secret at each start: nonces from before a restart get `ELM_NONCE_REQUIRED`.

### Sequence numbers and `retry`

The license token of an activation and every lease token carry a sequence number, `seq`. The first token has `seq` 1, and each new token adds 1. A request that names an activation or a lease sends the `seq` of the last token the client received, and the server compares it with the `seq` of the last token it issued:

| `seq` sent | Result |
| --- | --- |
| The same | A new token with the next `seq`. |
| One less, and `retry` holds the `jti` of the request that got the last token | The answer to that request was lost, or the client refused it: the last token again, and nothing changes. A lease token sent again names the earlier request in `areq`, not the retry. |
| Anything else, a negative `seq` on `elm-server` included | A clone: another copy of the installation went ahead. With `cloneStrategy` `deny` (the policy's, see [Policy settings](../explanation/activation.md#policy-settings), or the server license's on `elm-server`), the answer is `409` with `ELM_CLONE_DETECTED`, and the activation or lease is stopped. With `flag`, the clone is recorded and the request goes on. For a lease, the new token's `seq` is then one more than the greater of the server's `seq` and the `seq` sent; a renewal for which no greater `seq` exists (a `seq` of 9223372036854775807) gets `400` with `ELM_MALFORMED`. |

- `elm-service` takes a `seq` of 0 or more; a negative one gets `ELM_MALFORMED`. `elm-server` takes any whole number that fits 64 bits.
- `retry` lists the `jti` of the client's earlier requests that got no answer and, for a lease renewal, of those whose answer the client refused: at most 16 strings, each 1 to 64 characters. A success answer whose body is larger than the 256 KiB that the client reads counts as no answer. A longer list, another length, or a `null` entry gets `ELM_MALFORMED`. Both programs apply the same limits.
- A first activation whose answer was lost is sent again with `seq` `0` and the first request's `jti` in `retry`. A trial whose answer was lost is sent again with the first request's `jti` in `retry`. Both get the file they were issued.
- A stopped activation refuses every refresh and activation with `ELM_CLONE_DETECTED`. It is deactivated only with the `seq` of its last token, or by an administrator. A stopped lease refuses renewal and release until it expires.

[Clone detection with the sequence number](../explanation/activation.md#clone-detection-with-the-sequence-number) and [Retries and clones](../explanation/floating-licenses.md#retries-and-clones) explain why.

### Request bodies

- A body is one JSON object, nested at most 8 levels. Anything else gets `400` with `ELM_MALFORMED`.
- Unknown members are ignored, so clients of other versions keep working.
- A member given twice gets `ELM_MALFORMED`. On `elm-server`, this holds only for the members the route reads: an unknown member given twice is ignored.
- `null` for an optional member counts as not sent. `null` for a required member gets `ELM_MALFORMED`.
- An integer is a whole number; a fraction gets `ELM_MALFORMED`. A size in bytes is counted in UTF-8.

### Machine fingerprints

A machine fingerprint is 32 lowercase hexadecimal characters; any other value gets `ELM_MALFORMED`. On `elm-service`, the same fingerprint given twice counts once. The servers check only this form, so they accept any value, and a value that stays the same for one product on one machine serves their purposes: `elm-service` counts trials by fingerprint, both programs count early borrow returns by fingerprint, and `elm-service` puts the fingerprints of an activation into its license token's `fp`. An app that verifies that token with the libraries compares `fp` with the fingerprints the libraries compute, so a client that the libraries will check must send the same values. See [Machine fingerprints](../explanation/devices-and-proofs.md#machine-fingerprints).

The libraries compute one fingerprint from each 16-byte OS machine ID they can read:

1. Take the first 16 bytes of the SHA-256 of the product code in UTF-8.
2. Compute HMAC-SHA256 over those 16 bytes, keyed with the machine ID, and keep its first 16 bytes.
3. Set byte 6 to `(byte 6 & 0x0F) | 0x40` and byte 8 to `(byte 8 & 0x3F) | 0x80`.
4. Write the 16 bytes in lowercase hex.

The machine IDs are the 16 bytes that `/etc/machine-id` holds as 32 hex characters on Linux; `MachineGuid` from the registry and the SMBIOS system UUID (16 raw bytes) on Windows; and `IOPlatformUUID` on macOS. `MachineGuid` and `IOPlatformUUID` are UUID strings, taken as their 16 bytes in the order they are written. An ID that is missing, all zeros, or all `0xFF` bytes gives no fingerprint.

### Error answers

An error is an RFC 9457 problem with the media type `application/problem+json`. `title` and `code` both hold the error code; [Error codes](error-codes.md) explains each one and what to do. An example from `elm-service`, for a proof with a nonce the service did not make:

```json
{
  "type": "about:blank",
  "title": "ELM_NONCE_REQUIRED",
  "status": 401,
  "detail": "The nonce is missing or expired; get a new one.",
  "code": "ELM_NONCE_REQUIRED"
}
```

`500` with `ELM_INTERNAL_ERROR` is an internal failure: on `elm-service`, the database failed or a license file or lease could not be signed; on `elm-server`, the borrow record could not be written.

### Rate limits

- `elm-service` limits every device route per source address before anything else (`rateLimit.clientPerIp`), and every request with a proof per installation key after the proof check and before its `jti` is recorded (`rateLimit.clientPerKey`, see [The proof](#the-proof)). An offline file counts against the key that signed it, after its signature and time checks.
- `elm-server` applies its IP rules (`allow` and `deny`) first: a refused address gets `403` with `ELM_FORBIDDEN`. Then it limits every route per source address (`rateLimit.perIp`).
- A request over a limit gets `429` with `ELM_RATE_LIMITED` and `Retry-After: 1`.

The limits and their defaults are in [elm-service configuration](service-configuration.md#ratelimit) and [elm-server configuration](server-configuration.md#ratelimit).

## `elm-service` routes

`elm-service` has nine device routes. Every route can answer `429` with `ELM_RATE_LIMITED` for its source address. Every route with a proof can also answer the codes of the proof check and the rate limits above, `400` with `ELM_MALFORMED` for a body that breaks the rules, `413` with `ELM_PAYLOAD_TOO_LARGE`, and `500` with `ELM_INTERNAL_ERROR`. Each route below lists its other codes, in the order the service checks them.

The policy settings named below, such as `binding`, `floating`, `fingerprintMatching`, and `cloneStrategy`, are listed in [Policy settings](../explanation/activation.md#policy-settings). A license is found by its license key, in which case and `-` do not matter. A value that is not a license key, and a key that the service does not know, get the same answer: `404` with `ELM_LICENSE_NOT_FOUND`.

### `GET /v1/nonce`

- Authentication: none.
- Request body: none.
- Success: `200`.
- Response headers: `ELM-Nonce`, with the same nonce as the body.

```json
{
  "nonce": "AAAAAGrnIoQfakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonce2"
}
```

Errors: `429` `ELM_RATE_LIMITED`.

### `POST /v1/activations`

Activates the installation for a license, or activates the same installation key again. See [Online activation](../explanation/activation.md#online-activation).

- Authentication: a proof with `jwk`.
- Success: `201` for a new activation. `200` when the same installation key already has an activation of this license: a new file with the next `seq`, or the last file again for a retry.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `licenseKey` | string | yes | The license key. |
| `product` | string | yes | The product code: 1 to 64 characters of `a-z`, `0-9`, and `-`. |
| `fingerprints` | array of strings | yes | The machine fingerprints (see [Machine fingerprints](#machine-fingerprints)), 0 to 8. May be empty unless the policy's `fingerprintMatching` is `any` or `all`. |
| `seq` | integer | no | 0 or more. Left out on a first activation. When the same installation key activates the same license again, the `seq` of its last token, or `0` to send a first activation again (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). Left out on a second activation, it counts as a clone. |
| `retry` | array of strings | no | The `jti` of earlier requests that got no answer (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |
| `client` | object | no | `{"host": <string>, "user": <string>}`: the host name and OS user name. Both members are required strings of at most 255 bytes. Kept only when the policy's `clientInfoStrategy` is `hostAndUser`; otherwise ignored. |
| `app` | string | no | The app's version, at most 64 bytes. |

The answer holds the activation ID and the license file, the same JSON as a `.elmlic` file (see [The license file](../explanation/license-files-and-verification.md#the-license-file)). The client keeps the ID with the file, because the token does not carry it. Tokens and certificates are shortened to `eyJ…` here:

```json
{
  "activationId": "01900000-0000-7000-8000-00000000a001",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJ…",
    "certificates": ["eyJ…"]
  }
}
```

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_LICENSE_NOT_FOUND` | The key is not a license key, or no license has it. |
| `400` | `ELM_WRONG_PRODUCT` | The license is for another product than `product`. |
| `409` | `ELM_CONFLICT` | The policy's `binding` is `unbound` (its file comes from the administration API), or its `floating` is `cloud` (its seats are leased). |
| `400` | `ELM_FINGERPRINT_UNAVAILABLE` | The policy's `fingerprintMatching` is not `ignore`, and `fingerprints` is empty. |
| `403` | `ELM_LICENSE_REVOKED`, `ELM_LICENSE_SUSPENDED`, `ELM_EXPIRED` | The license is revoked, suspended, or expired. |
| `409` | `ELM_CLONE_DETECTED` | The same installation key activates again with a wrong `seq` under `cloneStrategy` `deny`, or its activation is stopped. |
| `409` | `ELM_ACTIVATION_LIMIT` | A new activation, and the license has as many activations as its `maxActivations`. |

### `POST /v1/activations/{id}/refresh`

Gets a new license file for an activation. `{id}` is the activation ID. See [Refresh](../explanation/activation.md#refresh).

- Authentication: a proof signed by the activation's installation key. It needs no `jwk`.
- Success: `200`: a new file with the next `seq`, or the last file again for a retry.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `seq` | integer | yes | 0 or more: the `seq` of the last token the client received. |
| `retry` | array of strings | no | The `jti` of earlier requests that got no answer (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |

The answer has the same form as the answer of `POST /v1/activations`:

```json
{
  "activationId": "01900000-0000-7000-8000-00000000a001",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJ…",
    "certificates": ["eyJ…"]
  }
}
```

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_ACTIVATION_NOT_FOUND` | No activation has the ID, or the ID is not a UUID. Answered before the proof is checked. Also when the activation is removed while the request runs. |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the activation's. |
| `403` | `ELM_LICENSE_REVOKED`, `ELM_LICENSE_SUSPENDED`, `ELM_EXPIRED` | The license is revoked, suspended, or expired. |
| `409` | `ELM_CLONE_DETECTED` | A wrong `seq` under `cloneStrategy` `deny`, or the activation is stopped. |

### `POST /v1/activations/{id}/deactivate`

Removes an activation and frees its place. `{id}` is the activation ID. See [Deactivation](../explanation/activation.md#deactivation).

- Authentication: a proof signed by the activation's installation key. It needs no `jwk`.
- Success: `204`, with no body.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `seq` | integer | yes | 0 or more: the `seq` of the last token the client received. |
| `retry` | array of strings | no | The `jti` of earlier requests that got no answer, such as a refresh whose answer was lost (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |

The license's state is not checked: a revoked, suspended, or expired license can still be deactivated.

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_ACTIVATION_NOT_FOUND` | No activation has the ID, or the ID is not a UUID. Answered before the proof is checked. A deactivation sent again after its answer was lost gets this code. |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the activation's. |
| `409` | `ELM_CLONE_DETECTED` | The activation is stopped and `seq` is not the `seq` of its last token, or a wrong `seq` under `cloneStrategy` `deny`. |
| `409` | `ELM_DEACTIVATION_LIMIT` | The license has as many deactivations as the policy's `deactivationLimit`. |

### `POST /v1/trials`

Starts a trial: the service makes a license of the product's trial policy, the `trial` policy that the product names in `trialPolicyId`, with every feature of the product, and activates it for the installation. See [Trials](../explanation/activation.md#trials).

- Authentication: a proof with `jwk`.
- Success: `201` for a new trial. `200` for a retry: the same installation key, an activation that is still at `seq` 1, and `retry` holding the `jti` of the request that got it. The retry gets the file it was issued.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `product` | string | yes | The product code: 1 to 64 characters of `a-z`, `0-9`, and `-`. |
| `fingerprints` | array of strings | yes | The machine fingerprints (see [Machine fingerprints](#machine-fingerprints)), 0 to 8 as in `POST /v1/activations`, except that an empty list gets `ELM_FINGERPRINT_UNAVAILABLE`: a trial needs at least one. |
| `retry` | array of strings | no | The `jti` of an earlier trial request that got no answer (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |
| `client` | object | no | As in `POST /v1/activations`. Kept only when the trial policy's `clientInfoStrategy` is `hostAndUser`. |
| `app` | string | no | The app's version, at most 64 bytes. |

```json
{
  "activationId": "01900000-0000-7000-8000-00000000a002",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJ…",
    "certificates": ["eyJ…"]
  }
}
```

| Status | Code | When |
| --- | --- | --- |
| `400` | `ELM_FINGERPRINT_UNAVAILABLE` | `fingerprints` is empty. |
| `404` | `ELM_TRIAL_NOT_OFFERED` | No product has the code, or the product has no trial policy. |
| `409` | `ELM_TRIAL_USED` | One of the fingerprints already had a trial of the product. |

### `POST /v1/offline`

Takes an offline request file and answers it: an activation or refresh request (`.elmreq`, type `elm-actreq+jws`) or a deactivation proof (`.elmdeact`, type `elm-deact+jws`). Anyone may send the file: the file's own signature authenticates it. `elm offline process` sends files here unless it has administrator credentials. See [Offline activation by file exchange](../explanation/activation.md#offline-activation-by-file-exchange).

- Authentication: none. The file is signed by the installation key: an activation or refresh request with the `jwk` it carries, whose thumbprint must be its `kid`; a deactivation proof with the key of the activation it names.
- Request body: the file's text, a compact JWS; white space around it is removed.
- Success: `200` with the same answer as `POST /v1/activations` for an activation or refresh request, a new activation included. This body, unchanged, is the response file (`.elmresp`) that [`elm offline process`](elm-cli.md#elm-offline-process) writes with `--out` and that the device imports. `204`, with no body, for a deactivation proof.
- Response headers: none of the protocol's headers. The file carries its own nonce, so the answer has no `ELM-Nonce`.

The header of a file has `alg` `ES256`, `kid` (the installation key's thumbprint), and `typ`. The formats of the request, the answer, and the deactivation proof, with examples, are in [Token and file formats](formats.md#offline-request-elm-actreqjws). The claims the service reads from an activation or refresh request:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `op` | string | yes | `activate` or `refresh`. |
| `key` | string | for `activate` | The license key. |
| `prd` | string | for `activate` | The product code. |
| `act` | string | for `refresh` | The activation ID, a UUID. |
| `seq` | integer | for `refresh` | 0 or more, as in the online request. |
| `retry` | array of strings | no | As in the online request. |
| `fp` | array of strings | yes | The machine fingerprints, 0 to 8. A refresh does not store them again. |
| `nonce` | string | yes | 22 characters of base64url, made by the device. |
| `iat` | integer | yes | When the request was made, in seconds since 1970. |
| `exp` | integer | yes | Exactly `iat` + 604800 (7 days). |
| `jwk` | object | yes | The installation key's public key, as in a proof. |

The claims of a deactivation proof: `ver` (`1`), `act` (the activation ID), `seq` (required, 0 or more), `retry` (optional), and `iat`. Unknown claims are ignored. A claim given twice gets `ELM_MALFORMED`, except a second `jwk` in an activation or refresh request, which gets `ELM_PROOF_INVALID`.

- The same request file sent again gets the answer it got before, as long as the license is not revoked, suspended, or expired, and the activation still exists and is not stopped. Another request file with the same installation key and `nonce` gets `409` with `ELM_REPLAYED`.
- An activation request that the sequence rules judge a retry gets the next file, not the last one, because the last file names another request's nonce.
- An offline activation keeps no `client` or `app`.

| Status | Code | When |
| --- | --- | --- |
| `413` | `ELM_PAYLOAD_TOO_LARGE` | The body is larger than 64 KiB. |
| `400` | `ELM_MALFORMED` | The text is not a compact JWS of either type, or a claim is missing or out of range, or `iat` is more than 5 minutes ahead of the service's clock. |
| `400` | `ELM_PROOF_INVALID` | An activation or refresh request has no valid `jwk` whose thumbprint is its `kid`, or its signature does not verify with that `jwk`. |
| `404` | `ELM_ACTIVATION_NOT_FOUND` | A deactivation proof names no activation. Checked once `act` is read, before the signature and the other claims. |
| `400` | `ELM_PROOF_INVALID` | A deactivation proof is not signed by the activation's key. |
| `400` | `ELM_REQUEST_EXPIRED` | An activation or refresh request is past its `exp`. |
| `429` | `ELM_RATE_LIMITED` | Too many requests with this installation key. |
| `404`, `400`, `409`, `403` | The codes of `POST /v1/activations` | For an activation request, checked in the same order. |
| `404` | `ELM_ACTIVATION_NOT_FOUND` | A refresh request names no activation. |
| `400` | `ELM_PROOF_INVALID` | A refresh request is signed by another key than the activation's. |
| `409` | `ELM_REPLAYED` | Another request file used this request's installation key and `nonce`. For an activation request, checked after the license, its product, and its policy; for a refresh request, once the activation is found. In both, before the license's state. |
| `403`, `409` | The codes of `POST /v1/activations/{id}/refresh` | For a refresh request. |
| `409` | The codes of `POST /v1/activations/{id}/deactivate` | For a deactivation proof. |
| `500` | `ELM_INTERNAL_ERROR` | The database failed, or the license file could not be signed. |

### `POST /v1/leases`

Lends seats of a feature of a license whose policy's `floating` is `cloud`, or borrows them when the body has `borrow`. Borrowing has no route of its own. See [The same protocol on the hosted service](../explanation/floating-licenses.md#the-same-protocol-on-the-hosted-service).

- Authentication: a proof with `jwk`.
- Success: `201`.
- Response headers: `ELM-Nonce`. There is no `ELM-Server-License`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `licenseKey` | string | yes | The license key. |
| `product` | string | yes | The product code: 1 to 64 characters of `a-z`, `0-9`, and `-`. |
| `feature` | string | yes | The feature code, of the same form. The license must grant it with `seats`. |
| `version` | string | no | The version of the feature the app needs, `major.minor[.patch]`, at most 32 characters. It must be in the license's version range for the feature. |
| `count` | integer | yes | The seats to take: 1 to the feature's `seats` (the policy's `overage` does not raise this limit). |
| `borrow` | integer | no | Makes the lease a borrow for this many seconds: 1 to the policy's `borrowLimit`. A value below 0 or above 3,162,240,000 (100 years) gets `ELM_MALFORMED`. |
| `fingerprints` | array of strings | no | The machine fingerprints (see [Machine fingerprints](#machine-fingerprints)), 0 to 8. A borrow needs at least one. |
| `client` | object | no | As in `POST /v1/activations`. Kept only when the policy's `clientInfoStrategy` is `hostAndUser`. |
| `app` | string | no | The app's version, at most 64 bytes. |

The lease lasts the policy's `leaseDuration`, or the `borrow` period, cut at the license's and the feature's expiry. The answer holds the lease token and the certificates of the service's signing key, which check it; the token is shortened to `eyJ…` here. The lease token is a compact JWS whose payload is JSON. The claims a client needs are `lse`, the lease ID that `POST /v1/leases/{id}/renew` and `DELETE /v1/leases/{id}` name; `seq`, the sequence number that the next renewal sends; and `exp`, when the lease ends. [Token and file formats](formats.md#lease-token-elm-leasejws) lists every claim, and [What the client checks in every lease](../explanation/floating-licenses.md#what-the-client-checks-in-every-lease) says how a client checks the token.

```json
{
  "lease": "eyJ…",
  "certificates": ["eyJ…"]
}
```

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_LICENSE_NOT_FOUND` | The key is not a license key, or no license has it. |
| `400` | `ELM_WRONG_PRODUCT` | The license is for another product than `product`. |
| `409` | `ELM_CONFLICT` | The policy's `floating` is not `cloud`. |
| `403` | `ELM_LICENSE_REVOKED`, `ELM_LICENSE_SUSPENDED`, `ELM_EXPIRED` | The license is revoked, suspended, or expired. |
| `403` | `ELM_FEATURE_NOT_LICENSED` | The license does not grant the feature, or grants it without `seats`. |
| `403` | `ELM_FEATURE_EXPIRED` | The feature is past its own expiry. |
| `400` | `ELM_INVALID_ARGUMENT` | `version` is not `major.minor[.patch]`. |
| `403` | `ELM_FEATURE_VERSION_NOT_LICENSED` | `version` is outside the license's range. |
| `400` | `ELM_INVALID_ARGUMENT` | `count` is outside 1 to the feature's `seats`, or `borrow` is 0. |
| `403` | `ELM_BORROW_NOT_ALLOWED` | `borrow` is longer than the policy's `borrowLimit`, or the policy has none. |
| `400` | `ELM_FINGERPRINT_UNAVAILABLE` | A borrow without fingerprints. |
| `409` | `ELM_NO_SEATS` | The seats held and `count` together are more than the feature's seats with the overage. Stopped leases and borrows are counted. |
| `403` | `ELM_EXPIRED` | Less than one second of the lease would be left before the license or the feature expires. |

### `POST /v1/leases/{id}/renew`

Gets a new lease token with a new expiry. `{id}` is the lease ID: 22 characters of base64url, the `lse` claim of the token.

- Authentication: a proof signed by the lease's installation key. It needs no `jwk`.
- Success: `200`: a new token with the next `seq`, or the last token again for a retry.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `seq` | integer | yes | 0 or more: the `seq` of the last token the client received. |
| `retry` | array of strings | no | The `jti` of earlier renewals that got no answer or whose answer the client refused (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |

The new token lasts the policy's current `leaseDuration`, cut as on acquire.

```json
{
  "lease": "eyJ…",
  "certificates": ["eyJ…"]
}
```

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_UNKNOWN_LEASE` | No lease has the ID, the lease has expired, or the ID does not have the form of a lease ID. Answered before the proof is checked. Also when the lease goes while the request runs. The client does not reclaim from `elm-service`: the lease is lost. |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the lease's. |
| `409` | `ELM_CLONE_DETECTED` | The lease is stopped. |
| `400` | `ELM_INVALID_ARGUMENT` | The lease is a borrow: a borrow lasts its whole period and is not renewed. |
| `409` | `ELM_CONFLICT` | The policy's `floating` is no longer `cloud`. |
| `403` | `ELM_LICENSE_REVOKED`, `ELM_LICENSE_SUSPENDED`, `ELM_EXPIRED` | The license is revoked, suspended, or expired. |
| `403` | `ELM_FEATURE_NOT_LICENSED`, `ELM_FEATURE_EXPIRED` | The license no longer grants the feature with `seats`, or the feature has expired. |
| `409` | `ELM_NO_SEATS` | The seats held, this lease included, are more than the license allows now. |
| `409` | `ELM_CLONE_DETECTED` | A wrong `seq` under `cloneStrategy` `deny`; the lease is stopped until it expires. |
| `403` | `ELM_EXPIRED` | Less than one second would be left before the license or the feature expires. |

### `DELETE /v1/leases/{id}`

Returns a lease, or returns a borrow early. `{id}` is the lease ID. A release is idempotent. See [Release](../explanation/floating-licenses.md#release) and [Early return](../explanation/floating-licenses.md#early-return).

- Authentication: a proof signed by the lease's installation key. It needs no `jwk`.
- Request body: none. A body sent anyway is not read, but the proof's `bdy` must still be its hash.
- Success: `204`, with no body, also when no lease has the ID or the lease has expired (answered before the proof is checked).
- Response headers: `ELM-Nonce`.

| Status | Code | When |
| --- | --- | --- |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the lease's. |
| `409` | `ELM_CLONE_DETECTED` | The lease is stopped; it ends at its expiry. |
| `409` | `ELM_RETURN_NOT_ALLOWED` | A borrow, and the early returns of the last 30 days of this installation key or one of the borrow's fingerprints have reached the policy's `borrowReturnLimit`, or the license has fewer seats of the feature or a shorter `borrowLimit` than when it was borrowed. |

## `elm-server` routes

`elm-server` has six routes for clients. Every route can also answer `403` with `ELM_FORBIDDEN` for an address its IP rules refuse, and `429` with `ELM_RATE_LIMITED`. Every route with a proof can also answer the codes of the proof check (including `503` with `ELM_SERVER_BUSY`), `400` with `ELM_MALFORMED` for a body that breaks the rules, and `413` with `ELM_PAYLOAD_TOO_LARGE`. Each route below lists its other codes, in the order the server checks them.

`elm-server` lends the features of its server license. After every start, it refuses new leases for a reclaim window as long as the server license's lease period, while clients reclaim the leases they still hold; the window ends early once every feature's seats are taken again. See [Restarts and reclaim](../explanation/floating-licenses.md#restarts-and-reclaim).

### `GET /v1/nonce` on `elm-server`

- Authentication: none.
- Request body: none.
- Success: `200`.
- Response headers: `ELM-Nonce`, with the same nonce as the body.

```json
{
  "nonce": "AAAAAGrnIoYfakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonceFakeNonce3"
}
```

Errors: only those of the IP rules and the rate limit.

### `GET /v1/server/license`

Returns the server license file and the server's public key, with which a client checks the server and its lease tokens. See [The server license](../explanation/floating-licenses.md#the-server-license-how-the-client-trusts-an-on-premises-server).

- Authentication: none.
- Request body: none.
- Success: `200`.
- Response headers: none of the protocol's headers.

`license` is the server license file, the same JSON as a `.elmlic` file (see [Token and file formats](formats.md#server-license)). `key` is the public key of the server's installation key, as a JWK with `kty`, `crv`, `x`, and `y`; its thumbprint is the server license's `cnf.jkt`. Tokens and certificates are shortened to `eyJ…` here:

```json
{
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJ…",
    "certificates": ["eyJ…"]
  },
  "key": {
    "kty": "EC",
    "crv": "P-256",
    "x": "FAKE-X-COORDINATE-00000000000000000000000X1",
    "y": "FAKE-Y-COORDINATE-00000000000000000000000Y1"
  }
}
```

Errors: only those of the IP rules and the rate limit.

### `POST /v1/leases` on `elm-server`

Lends seats of a feature of the server license, or borrows them when the body has `borrow`. Borrowing has no route of its own. See [Acquire](../explanation/floating-licenses.md#acquire) and [Borrowing](../explanation/floating-licenses.md#borrowing).

- Authentication: a proof with `jwk`.
- Success: `201`.
- Response headers: `ELM-Nonce` and `ELM-Server-License`. A refusal during the reclaim window also has `Retry-After`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `feature` | string | yes | The feature code: 1 to 64 bytes in UTF-8, or the answer is `400` with `ELM_MALFORMED`. The server license must grant it. |
| `version` | string | no | The version of the feature the app needs, `major.minor[.patch]`, at most 32 characters. It must be in the server license's version range for the feature. |
| `count` | integer | yes | The seats to take: 1 to the feature's seats in the server license. A number that does not fit 32 bits gets `ELM_MALFORMED`. |
| `borrow` | integer | no | Makes the lease a borrow for this many seconds: 1 to the server license's borrow limit, or to `borrow.maxDuration` when that is shorter. A value below 0 or above 3,162,240,000 (100 years) gets `ELM_MALFORMED`. |
| `fingerprints` | array of strings | no | The machine fingerprints (see [Machine fingerprints](#machine-fingerprints)), 0 to 8. A borrow needs at least one. |
| `client` | object | no | `{"host": <string>, "user": <string>}`, each optional and at most 255 bytes. Checked against the `host:` and `user:` entries of `allow` and `deny` (see [Access rules](server-configuration.md#access-rules)). |
| `app` | string | no | The app's version, at most 64 bytes. It is written to the usage log. |

`elm-server` does not read `licenseKey` or `product`. The lease lasts the server license's lease period, or the `borrow` period, and ends no later than the server license's expiry plus its grace period. The answer holds the lease token, which the server signed with its installation key, and an empty `certificates`; the token is shortened to `eyJ…` here. The client checks the token with the key from `GET /v1/server/license`. As on `elm-service`, the claims a client needs are `lse`, the lease ID that `POST /v1/leases/{id}/renew` and `DELETE /v1/leases/{id}` name; `seq`, the sequence number that the next renewal sends; and `exp`, when the lease ends. [Token and file formats](formats.md#lease-token-elm-leasejws) lists every claim.

```json
{
  "lease": "eyJ…",
  "certificates": []
}
```

An example of `ELM-Server-License`: `ELM-Server-License: FAKElicenseJti00000001`.

| Status | Code | When |
| --- | --- | --- |
| `403` | `ELM_FORBIDDEN` | The `allow` and `deny` entries refuse the host name or user name in `client`. |
| `403` | `ELM_EXPIRED` | The server license's expiry plus its grace period has passed. |
| `403` | `ELM_FEATURE_NOT_LICENSED` | The server license does not grant the feature. |
| `403` | `ELM_FEATURE_EXPIRED` | The feature is past its own expiry. |
| `400` | `ELM_INVALID_ARGUMENT` | `version` is not `major.minor[.patch]`. |
| `403` | `ELM_FEATURE_VERSION_NOT_LICENSED` | `version` is outside the server license's range. |
| `400` | `ELM_INVALID_ARGUMENT` | `count` is outside 1 to the feature's seats, or `borrow` is 0. |
| `403` | `ELM_BORROW_NOT_ALLOWED` | `borrow` is longer than allowed, or borrowing is not allowed. |
| `400` | `ELM_FINGERPRINT_UNAVAILABLE` | A borrow without fingerprints. |
| `503` | `ELM_SERVER_RECOVERING` | The reclaim window after a start has not ended. `Retry-After` gives its rest. |
| `409` | `ELM_NO_SEATS` | The seats held and `count` together are more than the feature's seats with the overage; for a borrow, less the seats kept by `borrow.reserve`. |
| `403` | `ELM_EXPIRED` | Less than one second of the lease would be left before the server license's expiry plus its grace period. |
| `500` | `ELM_INTERNAL_ERROR` | A borrow, and the borrow record cannot be written. |

The answer to a request in the reclaim window, as `elm-server` sent it with `Retry-After: 15`:

```json
{
  "type": "about:blank",
  "title": "ELM_SERVER_RECOVERING",
  "status": 503,
  "detail": "The server is reclaiming leases after a restart.",
  "code": "ELM_SERVER_RECOVERING"
}
```

### `POST /v1/leases/reclaim`

Hands back to a restarted server the leases that a client still holds, so that the server counts their seats again. The client sends it when a renewal gets `ELM_UNKNOWN_LEASE`. It is accepted during the reclaim window and after it.

- Authentication: a proof with `jwk`. Each token must be bound to that key.
- Success: `200`, with one result per token, in the order of the tokens.
- Response headers: `ELM-Nonce`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `leases` | array of strings | yes | 0 to 64 lease tokens this server issued, none of them `null`. |

Each token is checked as a client checks a lease, except that it may have been issued under an earlier server license file of this server; it must still name the current server license's license ID and fit that license. A token is taken back only when it fits the free seats, the overage included. A token of a lease the server already holds is the same client only with the same `seq`; any other `seq` counts as a clone. A reclaimed lease keeps the `seq` and expiry of its token, and the client renews it with that `seq`.

`lease` is the lease ID; it is left out when the token could not be checked. This answer is for one valid token and the string `not-a-token`:

```json
{
  "results": [
    { "lease": "FAKElease0000000000002", "code": "ELM_OK" },
    { "code": "ELM_MALFORMED" }
  ]
}
```

The codes of a result:

| Code | When |
| --- | --- |
| `ELM_OK` | The lease is held again, or it was already held with the same `seq`, or a clone was recorded under the server license's `clone` `flag`. |
| `ELM_MALFORMED` | The token cannot be read. |
| `ELM_BAD_SIGNATURE` | The token is not signed by this server's key. |
| `ELM_LEASE_OUT_OF_BOUNDS` | The token does not fit the current server license: another license ID, a feature or seat count it does not have, too long, or lasting past the server license's expiry plus its grace period. |
| `ELM_WRONG_PRODUCT` | The token is for another product. |
| `ELM_WRONG_INSTALLATION` | The token is bound to another installation key than the proof's. |
| `ELM_CLOCK_TAMPERED` | The token's issue time is more than 5 minutes ahead of the server's clock. |
| `ELM_EXPIRED` | The token has expired. |
| `ELM_BORROW_NOT_ALLOWED` | The token is a borrow; borrows come back from the borrow record. |
| `ELM_NO_SEATS` | No seats are free for it. |
| `ELM_CLONE_DETECTED` | The lease is stopped, or the server holds it with another `seq` under the server license's `clone` `deny`; it is stopped until it expires. |

Errors of the request itself: only the common ones (a body with more than 64 tokens, or a `null` token, gets `400` with `ELM_MALFORMED`).

### `POST /v1/leases/{id}/renew` on `elm-server`

Gets a new lease token with a new expiry. `{id}` is the lease ID, the `lse` claim of the token.

- Authentication: a proof signed by the lease's installation key. It needs no `jwk`.
- Success: `200`: a new token with the next `seq`, or the last token again for a retry.
- Response headers: `ELM-Nonce` and `ELM-Server-License`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `seq` | integer | yes | The `seq` of the last token the client received. Any whole number that fits 64 bits: a negative value is accepted and judged a clone (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |
| `retry` | array of strings | no | The `jti` of earlier renewals that got no answer or whose answer the client refused (see [Sequence numbers and `retry`](#sequence-numbers-and-retry)). |

The new token lasts the server license's lease period, cut as on acquire.

```json
{
  "lease": "eyJ…",
  "certificates": []
}
```

| Status | Code | When |
| --- | --- | --- |
| `404` | `ELM_UNKNOWN_LEASE` | The server does not know the lease: it has restarted, or the lease has expired or was released. Answered before the proof is checked when the server has no record of the ID. The client reclaims its leases. |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the lease's. |
| `409` | `ELM_CLONE_DETECTED` | The lease is stopped. |
| `400` | `ELM_INVALID_ARGUMENT` | The lease is a borrow: a borrow lasts its whole period and is not renewed. |
| `403` | `ELM_EXPIRED` | The server license's expiry plus its grace period has passed. |
| `403` | `ELM_FEATURE_NOT_LICENSED`, `ELM_FEATURE_EXPIRED` | The server license no longer grants the feature, or the feature has expired. |
| `409` | `ELM_NO_SEATS` | The server license now allows fewer seats than are held. |
| `409` | `ELM_CLONE_DETECTED` | A wrong `seq` under the server license's `clone` `deny`; the lease is stopped until it expires. |
| `403` | `ELM_EXPIRED` | Less than one second would be left before the server license's expiry plus its grace period. |

### `DELETE /v1/leases/{id}` on `elm-server`

Returns a lease, or returns a borrow early. `{id}` is the lease ID. A release is idempotent. See [Release](../explanation/floating-licenses.md#release) and [Early return](../explanation/floating-licenses.md#early-return).

- Authentication: a proof signed by the lease's installation key. It needs no `jwk` for a lease; send `jwk` for a borrow, because the server may have restored it from its borrow record without the key.
- Request body: none. A body sent anyway is not read, but the proof's `bdy` must still be its hash.
- Success: `204`, with no body, also when the server does not know the lease (answered before the proof is checked when it has no record of the ID).
- Response headers: `ELM-Nonce`.

| Status | Code | When |
| --- | --- | --- |
| `401` | `ELM_PROOF_INVALID` | The proof is signed by another key than the lease's. |
| `409` | `ELM_CLONE_DETECTED` | The lease is stopped; it ends at its expiry. |
| `409` | `ELM_RETURN_NOT_ALLOWED` | A borrow, and the early returns of the last 30 days of this installation key or one of the borrow's fingerprints have reached the server license's limit; the server license is another one, or has fewer seats of the feature or a shorter borrow limit than when it was borrowed; or the return cannot be written to the borrow record. |
