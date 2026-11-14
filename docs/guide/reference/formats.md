---
sidebar_position: 8
---

# Token and file formats

This page lists the formats of the files and signed tokens that ElasticLM writes and reads: key files and key IDs, key certificates, license tokens and license files, lease tokens, the offline request, answer, and deactivation files, and the server license, with every member of each header and payload. The members of a request proof are in [Device protocol](device-protocol.md#the-proof), and those of a log signature in [Logs](logs.md#signed-lines-and-the-chain). It ends with the files that the client libraries keep on a device. It is for people who write their own verifier or client, inspect a file by hand, or need to know which files on a device may be copied or deleted. Read [Trust chain](../explanation/trust-chain.md) and [License files and verification](../explanation/license-files-and-verification.md) first for what these files are for. [`elm license inspect`](elm-cli.md#elm-license-inspect) shows the decoded contents of a license file.

The examples come from files that `elm`, `elm-server`, and `elm-service` wrote. Key IDs, key coordinates, token IDs, activation and license IDs, fingerprints, nonces, and license keys are replaced with fake values of the same length; times are kept. Signatures and whole tokens are shortened to `eyJ…`, and the body of a PEM file to `…`. The programs write JSON on one line; the decoded payloads are indented here for reading. Strings are shown as the programs write them, with their `\uXXXX` escapes (see [Header](#header)).

## Key files and key IDs

### Private key files

`elm key root create` and `elm key signing create` write the private key of a root key or a signing key to a PEM file with one `ENCRYPTED PRIVATE KEY` block: an ECDSA P-256 key in PKCS#8, encrypted with a passphrase (PBES2, with PBKDF2-HMAC-SHA256 at 600,000 iterations and AES-256-CBC).

- A new key's passphrase must have at least 12 characters.
- The programs that read a private key (`elm`, and `elm-service` for its signing key) accept only an encrypted P-256 key. A plain `PRIVATE KEY` or `EC PRIVATE KEY` block, a public key, or a key on another curve is refused, as is a wrong passphrase.

```text
-----BEGIN ENCRYPTED PRIVATE KEY-----
…
-----END ENCRYPTED PRIVATE KEY-----
```

### Public key files

The same commands write the public key to a PEM file with one `PUBLIC KEY` block (SubjectPublicKeyInfo) of the P-256 key. A root public key file is what an app builds into its trust store, and what `--trust`, `trust.rootKeys`, and `ELM_TRUST_ROOT_KEYS` name.

- The trust store of `ElasticLM.Licensing` takes one block per root key, and refuses a block that is not a P-256 public key.
- A trust file given to `elm`, `elm-server`, or `elm-service` may hold several `PUBLIC KEY` blocks, one after another.

```text
-----BEGIN PUBLIC KEY-----
…
-----END PUBLIC KEY-----
```

### Key IDs

A key ID (`kid`) is the JWK thumbprint of a P-256 public key (RFC 7638): the SHA-256 of the UTF-8 text below, with the key's coordinates in base64url and no white space, written in base64url without padding. A key ID has 43 characters.

```text
{"crv":"P-256","kty":"EC","x":"<x>","y":"<y>"}
```

The key ID names a root key or a signing key in a token header (`kid`), a certified key in a certificate (`sub`), and an installation key in a token that is bound to it (`cnf.jkt`). [`elm key thumbprint`](elm-cli.md#elm-key-thumbprint) prints the key ID of a public key file.

### Public keys in JSON

Where a public key travels inside JSON (the `jwk` of a key certificate, a proof, or an offline request, and the `key` that `GET /v1/server/license` returns), it is a JWK object with exactly these members:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `kty` | string | yes | `EC`. |
| `crv` | string | yes | `P-256`. |
| `x` | string | yes | The x coordinate: 32 bytes in base64url, 43 characters. |
| `y` | string | yes | The y coordinate: 32 bytes in base64url, 43 characters. |

A JWK with any other member is refused, and so is a point that is not on the P-256 curve.

## JWS rules

Every signed token is a JWS (RFC 7515) in compact serialization: three parts separated by `.`, each in base64url without padding. The first part is the header, the second the payload, the third the signature.

```text
eyJ….eyJ….eyJ…
```

- Only the canonical base64url form is accepted: no padding (`=`), no white space, and no other characters.
- A token has at most 16,384 characters.
- The signature is ES256 (ECDSA with P-256 and SHA-256) in the 64-byte `r || s` form, 86 characters in base64url. A DER signature is refused. The low-S form is not required: a signature with a high `s` that verifies is accepted.
- The signature is checked over the ASCII bytes of `header.payload` exactly as received. The payload is not read as JSON before the signature is checked. The exceptions are the members that name the key which checks the signature: the `jwk` of a proof or of an [offline request](#offline-request-elm-actreqjws), and the `act` of an [offline deactivation proof](#offline-deactivation-proof-elm-deactjws). Only that member is read first.

### Header

The header is a JSON object with exactly three members:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. Another value is refused; the code depends on the token type (see below the table). |
| `kid` | string | yes | The key ID of the key that signed the token. The header check requires only that it is not empty; it must then name a key that the verifier knows, and a value that names none fails later. |
| `typ` | string | yes | The token type, from the table below. Each verifier accepts one type only. |

A header with any other member (such as `jwk`, `x5c`, or `crit`), with a member missing or given twice, with a value that is not a string, or with the wrong `typ` gets `ELM_MALFORMED` in a license token, a lease token, an offline request, and an offline deactivation proof. What another `alg` and such a header get depends on which of the [token types](#token-types) the token is. A license token or a lease token with another `alg` gets `ELM_BAD_SIGNATURE`. A request proof with another `alg` or with such a header gets `ELM_PROOF_INVALID`. An offline request or an offline deactivation proof gets `ELM_MALFORMED` in both cases. A [key certificate](#key-certificate-elm-certjws) is skipped in both cases, and a token left without a certificate for its key gets `ELM_UNTRUSTED_KEY`. A log signature (`elm-log+jws`) gets no `ELM_` code in either case: [`elm log verify`](logs.md#verifying-the-usage-log) reports it as an error. Member names are compared exactly, case included. So a token cannot carry its own key, and one kind of token cannot be passed off as another.

The programs write some characters inside JSON strings as `\uXXXX` escapes: `+`, `<`, `>`, `&`, `'`, `"`, `` ` ``, and every character outside ASCII. So the header of a certificate reads `"typ":"elm-cert\u002Bjws"`, as in the examples on this page. A JSON parser reads an escape as the character itself, so a reader must decode the JSON and compare the values, not the raw text.

### Token types

| `typ` | Signed by | Holds |
| --- | --- | --- |
| `elm-cert+jws` | A root key | A [key certificate](#key-certificate-elm-certjws) |
| `elm-license+jws` | A signing key certified for `license` | A [license token](#license-token-elm-licensejws) |
| `elm-lease+jws` | A signing key certified for `lease`, or the installation key of `elm-server` | A [lease token](#lease-token-elm-leasejws) |
| `elm-proof+jws` | A device's installation key | A [request proof](#request-proof-elm-proofjws) |
| `elm-actreq+jws` | A device's installation key | An [offline activation or refresh request](#offline-request-elm-actreqjws) |
| `elm-deact+jws` | A device's installation key | An [offline deactivation proof](#offline-deactivation-proof-elm-deactjws) |
| `elm-log+jws` | A signing key certified for `log`, or the installation key of `elm-server` | A signature over lines of a usage log or a borrow record (see [Logs](logs.md#signed-lines-and-the-chain)) |

### Payload

The payload is a UTF-8 JSON object. These rules hold for every payload on this page:

- Times are integers: seconds since 1970-01-01T00:00:00Z, from 0 to 253402300799 (the end of the year 9999).
- Durations are integers: seconds, from 0 to 3162240000 (100 years of 366 days).
- A member that is not in the token's table is ignored. A member of the table given twice, a required member missing, a value of the wrong type, or `null` where a value is required makes the token invalid.
- Objects and arrays nest at most 8 levels deep.

### Size limits

| What | Limit |
| --- | --- |
| One token | 16,384 characters |
| A license file | 65,536 bytes (64 KiB) |
| `certificates` in a license file | 1 to 4 |
| `feat` in a license token | 256 features |
| A feature ID (`id` in `feat`) | 64 bytes in UTF-8 |
| `fp` in a license token | 8 fingerprints |
| `meta` in a license token | 32 entries; each key at most 64 bytes and each value at most 1,024 bytes in UTF-8 |
| JSON nesting | 8 levels |

## Key certificate: `elm-cert+jws`

A key certificate says that a root key allows a signing key to sign certain kinds of tokens for a period. [`elm key certify`](elm-cli.md#elm-key-certify) writes one to a file: the token on one line, followed by a line break. Programs that read a certificate file remove the white space around the token. A license file carries the certificates of its signing key; `elm-service` sends them with its cloud lease tokens.

Header:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. |
| `kid` | string | yes | The key ID of the root key that signed the certificate. |
| `typ` | string | yes | `elm-cert+jws`. |

Payload:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `iat` | integer | yes | When the certificate was made: a time. |
| `jti` | string | yes | The token ID. `elm` writes 128 random bits in base64url, 22 characters. |
| `sub` | string | yes | The key ID of the certified key. It must be the thumbprint of `jwk`. |
| `jwk` | object | yes | The certified public key, with exactly `kty` (`EC`), `crv` (`P-256`), `x`, and `y` (see [Public keys in JSON](#public-keys-in-json)). |
| `use` | array of strings | yes | What the key may sign: one or more of `license`, `lease`, and `log`. Other values are ignored. |
| `nbf` | integer | yes | The start of the validity period: a time. |
| `exp` | integer | yes | The end of the validity period: a time. `elm key certify` makes periods of 1 to 731 days, 365 by default. |

A token is accepted under a certificate when the token's `iat` lies between the certificate's `nbf` and `exp`, widened on both ends by the verifier's allowed clock skew. The current time is not compared with `exp`.

A verifier uses a certificate only when all of these hold. A certificate that fails one is skipped, not fatal, so an old certificate kept beside a new one does no harm:

- The header's `kid` names a root key of the trust store that is not revoked, and that key's signature verifies.
- The payload has every member of the table within its range, and `ver` is `1`.
- `sub` is not a revoked key ID, and is the thumbprint of `jwk`.
- `use` holds the use the verifier needs: `license` for a license file, `lease` for a cloud lease token, `log` for a usage log of `elm-service`.

A certificate made with `--use license lease`, decoded:

```json
{"alg":"ES256","kid":"FAKE-ROOT-KEY-ID-000000000000000000000000R1","typ":"elm-cert\u002Bjws"}
```

```json
{
  "ver": 1,
  "iat": 1793540727,
  "jti": "FAKEjti000000000000001",
  "sub": "FAKE-SIGNING-KEY-ID-000000000000000000000S1",
  "jwk": {
    "kty": "EC",
    "crv": "P-256",
    "x": "FAKE-X-COORDINATE-00000000000000000000000X1",
    "y": "FAKE-Y-COORDINATE-00000000000000000000000Y1"
  },
  "use": [
    "license",
    "lease"
  ],
  "nbf": 1793540726,
  "exp": 1825076726
}
```

## License token: `elm-license+jws`

A license token holds the license: its product, kind, features, expiry, and binding to a device. It travels inside a [license file](#license-file-elmlic). The order in which a verifier checks it is in [License files and verification](../explanation/license-files-and-verification.md#the-order-of-checks).

Header:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. |
| `kid` | string | yes | The key ID of the signing key. A key certificate in the same file must certify this key for `license`. |
| `typ` | string | yes | `elm-license+jws`. |

Payload:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `iss` | string | yes | The key ID of the signing key. It must equal the header's `kid`. |
| `iat` | integer | yes | When the token was issued: a time. It must lie in the period of a key certificate in the file, and must not be later than the current time plus the allowed clock skew. |
| `exp` | integer | no | When the token expires: a time. Without it, the token never expires. |
| `jti` | string | yes | The token ID, not empty. The issuers write 128 random bits in base64url, 22 characters. |
| `lic` | string | yes | The license ID, not empty. `elm-service` writes the license's `id`; `elm license issue` writes `--license-id`. |
| `prd` | string | yes | The product ID (the product's `code`), not empty. It must equal the product ID that the app requires. |
| `kind` | string | yes | `standard`, `trial`, or `server`. |
| `feat` | array of objects | yes | The features, 0 to 256, members below. |
| `cnf` | object | in a `server` token; optional otherwise | The installation key the token is bound to: one member, `jkt`, its key ID. With `cnf`, the token verifies only for an installation with that key ID (`ELM_WRONG_INSTALLATION`). |
| `fp` | array of strings | no | The machine fingerprints the token is bound to, 0 to 8, compared as `fpm` says. An empty or missing `fp` binds the token to no fingerprint, and `fpm` is then not read. |
| `fpm` | string | when `fp` has an entry | How `fp` is compared: `any` (one must match), `all` (all must match), or `ignore` (not compared). |
| `grace` | integer | no | How long after `exp` the token is still valid, in the grace period: a duration. It must be `0` or absent in a `trial` token. The issuers write it only with `exp`, and it has no effect without `exp`. |
| `refresh` | integer | no | How long before `exp` the client starts to refresh online: a duration. `elm-service` writes the policy's `refreshWindow`; `elm license issue` writes it only with `--refresh-days`. |
| `seq` | integer | no | The activation's sequence number: `1` in the first file of an activation, and one more in each new file. `elm-service` writes it in every file of an activation; `elm license issue` and the file of an unbound license leave it out. |
| `areq` | string | no | The ID of the request that this token answers, 1 to 64 characters: the `jti` of the [request proof](#request-proof-elm-proofjws) of an online activation, refresh, or trial, or the `nonce` of an [offline request](#offline-request-elm-actreqjws). `elm-service` writes it in every file of an activation. A request sent again after its answer was lost gets the earlier file, so that file names the earlier request. `elm license issue` and the file of an unbound license leave it out. |
| `act` | string | no | The activation ID: a UUID, 36 characters with hyphens. `elm-service` writes it in every file of an activation; `elm license issue` and the file of an unbound license leave it out. |
| `lease` | integer | in a `server` token; optional otherwise | The lease time, in seconds: a duration, and at least 1 in a `server` token. |
| `borrow` | integer | in a `server` token; optional otherwise | The longest borrow, in seconds: a duration. `0` allows no borrowing. |
| `overage` | integer | in a `server` token; optional otherwise | How many seats over `seats` may be lent, in percent: 0 to 1,000. |
| `clone` | string | in a `server` token; optional otherwise | What `elm-server` does when a copied app installation renews the same lease as the original: `deny` or `flag` ([Retries and clones](../explanation/floating-licenses.md#retries-and-clones)). |
| `brl` | integer | in a `server` token; optional otherwise | How many early returns of borrows one installation key, or one machine fingerprint, may make in 30 days: 0 to 1,000. |
| `meta` | object | no | Vendor values: 0 to 32 members, each a string. A key is at most 64 bytes and a value at most 1,024 bytes in UTF-8. The verifier does not read them. |

Members of each item of `feat`:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `id` | string | yes | The feature ID: 1 to 64 bytes in UTF-8, of any characters. |
| `ver` | string | no | The feature versions the license covers: 1 to 4 comparators separated by spaces, all of which must hold. A comparator is `>=`, `<=`, `>`, `<`, or `=`, followed by a version `major.minor` or `major.minor.patch` of digits only, at most 9 digits each; a missing patch is 0. Without `ver`, every version. |
| `seats` | integer | in a `server` token; not allowed otherwise | The concurrent seats of the feature, 1 or more. Required in every feature of a `server` token, and not allowed in other kinds. |
| `exp` | integer | no | The feature's own expiry: a time. No grace period is added. |

Member of `cnf`:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `jkt` | string | yes | The key ID of the installation key. |

A token that breaks a rule of these tables, such as `seats` in a `standard` token or a `server` token without `lease`, gets `ELM_MALFORMED` once its signature is checked. The members that only a server license needs are listed again in [Server license](#server-license).

The verifier checks only the form of `areq` and `act`, and a token without them verifies. The client that sent an activation, refresh, or trial request compares them with its request, and refuses a file that answers another request with `ELM_REQUEST_MISMATCH` (see [`POST /v1/activations`](device-protocol.md#post-v1activations)).

A token that `elm license issue` wrote, decoded:

```json
{"alg":"ES256","kid":"FAKE-SIGNING-KEY-ID-000000000000000000000S1","typ":"elm-license\u002Bjws"}
```

```json
{
  "ver": 1,
  "iss": "FAKE-SIGNING-KEY-ID-000000000000000000000S1",
  "iat": 1793540746,
  "exp": 1830211200,
  "jti": "FAKEjti000000000000002",
  "lic": "LIC-0001",
  "prd": "acme-cad",
  "kind": "standard",
  "feat": [
    {
      "id": "pro",
      "ver": "\u003E=2.0 \u003C3.0"
    },
    {
      "id": "export",
      "exp": 1814313600
    }
  ],
  "grace": 604800,
  "meta": {
    "customer": "Contoso"
  }
}
```

It was issued with `--feature "pro;ver=>=2.0 <3.0" --feature "export;exp=2027-06-30" --expires 2027-12-31 --meta customer=Contoso`. A token from `elm-service` for an activation also carries `cnf`, `refresh`, and `seq`, and `fp` and `fpm` when the device sent fingerprints and the policy's `fingerprintMatching` is not `ignore`; see the example in [Offline answer](#offline-answer-elmresp). `elm license issue` writes `fp` and `fpm` whenever it is given `--fingerprint`, even with `--fingerprint-matching ignore`.

## License file: `.elmlic`

A license file carries a license token and the key certificates that let a verifier trace it to a root key, with nothing else needed. It is UTF-8 JSON, at most 65,536 bytes, with the extension `.elmlic`. The same JSON object is the `license` member of an activation answer and of `GET /v1/server/license`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `format` | string | yes | `elasticlm-license`. |
| `version` | integer | yes | `1`. |
| `license` | string | yes | The [license token](#license-token-elm-licensejws). |
| `certificates` | array of strings | yes | 1 to 4 [key certificates](#key-certificate-elm-certjws) of the key that signed the token, none of them `null`. |

- The outer JSON is not signed. Each token in it is signed on its own, so a change to the outer JSON can make the file fail but not make it pass.
- A file that is larger than the limit, is not this object, or has another `format` or `version` gets `ELM_MALFORMED`. Other members are ignored.
- `elm license issue` and `elm-service` write the file on one line without a line break at the end.

The file of the token above:

```json
{
  "format": "elasticlm-license",
  "version": 1,
  "license": "eyJ…",
  "certificates": [
    "eyJ…"
  ]
}
```

## Lease token: `elm-lease+jws`

A lease token lends seats of a feature to one installation key until it expires. `elm-server` signs it with its installation key; `elm-service` signs it with its signing key and sends the key's certificates with it. The routes that return lease tokens are in [Device protocol](device-protocol.md#post-v1leases-on-elm-server), and what a client checks is in [Floating licenses](../explanation/floating-licenses.md#what-the-client-checks-in-every-lease).

Header:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. |
| `kid` | string | yes | The key ID of the signer. From `elm-server`, the key ID of its installation key, which must be the `cnf.jkt` of its server license. From `elm-service`, the key ID of its signing key, which a certificate sent with the lease must certify for `lease`. |
| `typ` | string | yes | `elm-lease+jws`. |

Payload:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `iss` | string | yes | The key ID of the signer. It must equal the header's `kid`. |
| `iat` | integer | yes | When the token was issued: a time. It must not be later than the current time plus the allowed clock skew. |
| `exp` | integer | yes | When the lease ends: a time later than `iat`. |
| `jti` | string | yes | The token ID, not empty. The issuers write 128 random bits in base64url, 22 characters. |
| `lse` | string | yes | The lease ID: 22 characters of `A`–`Z`, `a`–`z`, `0`–`9`, `-`, and `_`. The lease routes name it in their path. |
| `prd` | string | yes | The product ID, not empty. It must equal the client's product ID. |
| `lic` | string | yes | The license ID, not empty. From `elm-server`, the `lic` of its server license, which must equal it; from `elm-service`, the license's `id`. |
| `feat` | string | yes | The feature ID, not empty. From `elm-server`, it must be a feature of the server license. |
| `n` | integer | yes | The seats lent, 1 or more. From `elm-server`, at most the feature's `seats` in the server license. |
| `cnf` | object | yes | The installation key that holds the lease: one member, `jkt`, its key ID, which must be the client's own. |
| `brw` | boolean | yes | `true` for a borrow, `false` for a lease that is renewed. |
| `srv` | string | yes | The key ID of the issuing server. It must equal `iss`. |
| `slic` | string | from `elm-server` | The token ID (`jti`) of the server license the lease was issued under. The client requires it to be the server license the client uses now (the one it fetched and verified last; earlier ones it keeps only to return and reclaim the leases issued under them); otherwise it refuses the lease with `ELM_LEASE_OUT_OF_BOUNDS` and fetches the server license again. `elm-service` leaves it out. |
| `seq` | integer | yes | The lease's sequence number. The verifier accepts 0 or more; `elm-server` and `elm-service` write 1 in the first token and one more at each renewal, so neither writes 0. |
| `areq` | string | yes | The `jti` of the [request proof](#request-proof-elm-proofjws) that this token answers, 1 to 64 characters. A renewal sent again after an answer that was lost or refused gets the earlier token again, so that token names the earlier request. The verifier checks only the length. The client that sent the request compares the value, and refuses a token that names another request with `ELM_REQUEST_MISMATCH`. |

Member of `cnf`:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `jkt` | string | yes | The key ID of the installation key that holds the lease. |

A lease from `elm-server` must also stay inside its server license: `exp` minus `iat` is at most the server license's `lease`, or its `borrow` when `brw` is `true`, and `exp` is no later than the server license's `exp` plus `grace`. `exp` is also no later than the `exp` of the server license's feature that the lease's `feat` names, which has no grace. A lease that breaks one of these gets `ELM_LEASE_OUT_OF_BOUNDS`.

A client also compares the lease in an answer with what it asked for. For an acquisition or a borrow: the feature, the seats, whether it is a borrow, for a borrow that it is no longer than the period asked for, and `areq`. For a renewal: the lease ID, the feature, and the seats of the lease it holds, that it is not a borrow, a `seq` greater than the one it holds, and `areq`. A lease that differs gets `ELM_REQUEST_MISMATCH`. From `elm-server`, when the app asked for a version and the server license gives the feature a range, a version outside it gets `ELM_FEATURE_VERSION_NOT_LICENSED`, since the token has no version. Without a version, the range is not checked. [Floating licenses](../explanation/floating-licenses.md#what-the-client-checks-in-every-lease) explains the comparison.

A lease token that `elm-server` returned for `{"feature":"pro","version":"2.1","count":1}`, decoded:

```json
{"alg":"ES256","kid":"FAKE-SERVER-KEY-ID-0000000000000000000000V1","typ":"elm-lease\u002Bjws"}
```

```json
{
  "ver": 1,
  "iss": "FAKE-SERVER-KEY-ID-0000000000000000000000V1",
  "iat": 1793541428,
  "exp": 1793542028,
  "jti": "FAKEjti000000000000005",
  "lse": "FAKElease0000000000001",
  "prd": "acme-cad",
  "lic": "01900000-0000-7000-8000-00000000b001",
  "feat": "pro",
  "n": 1,
  "cnf": {
    "jkt": "FAKE-CLIENT-KEY-ID-0000000000000000000000C1"
  },
  "brw": false,
  "srv": "FAKE-SERVER-KEY-ID-0000000000000000000000V1",
  "slic": "FAKEjti000000000000003",
  "seq": 1,
  "areq": "FAKEjti000000000000006"
}
```

The server license it was issued under is the one in the [offline answer](#offline-answer-elmresp) example: `lic` is that license's ID, `slic` its `jti`, and `exp` minus `iat` is its `lease` of 600 seconds.

## Request proof: `elm-proof+jws`

A proof is a short-lived token that a device sends in the `ELM-Proof` header of each request to `elm-service` or `elm-server`, signed with its installation key. It names the request's method, URL, and body hash, and a nonce from the server, so that a request cannot be changed or sent again. Its header and claims are listed in [Device protocol](device-protocol.md#the-proof), and why requests are signed is in [Devices and proofs](../explanation/devices-and-proofs.md#proofs).

## Offline request: `elm-actreq+jws`

An offline activation or refresh request is a file with the extension `.elmreq` that holds one `elm-actreq+jws` token, without a line break. A device writes it when it cannot reach `elm-service`: the app with `CreateActivationRequestAsync` or `CreateRefreshRequestAsync`, and `elm-server` through [`elm server activate-request`](elm-cli.md#elm-server-activate-request) and [`elm server refresh-request`](elm-cli.md#elm-server-refresh-request). Someone carries it to [`elm offline process`](elm-cli.md#elm-offline-process), which sends it to [`POST /v1/offline`](device-protocol.md#post-v1offline) or [`POST /v1/admin/offline`](administration-api.md#post-v1adminoffline). The file has no server nonce, because there is no round trip: it carries its own `nonce` and the device's public key.

Header:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. |
| `kid` | string | yes | The key ID of the device's installation key. It must be the thumbprint of the payload's `jwk`. |
| `typ` | string | yes | `elm-actreq+jws`. |

Payload:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `op` | string | yes | `activate` or `refresh`. |
| `key` | string | for `activate` | The license key. |
| `prd` | string | for `activate` | The product's `code`: 1 to 64 characters of `a`–`z`, `0`–`9`, and `-`. |
| `act` | string | for `refresh` | The activation ID, a UUID. |
| `seq` | integer | for `refresh` | The `seq` of the last license token the device received, 0 or more. The libraries write it in both kinds: `0` in an activation request. |
| `retry` | array of strings | no | The `jti` of requests that got no answer: at most 16, each 1 to 64 characters. |
| `fp` | array of strings | yes | The device's machine fingerprints: 0 to 8, each 32 lowercase hexadecimal characters. |
| `jwk` | object | yes | The installation key's public key, with exactly `kty` (`EC`), `crv` (`P-256`), `x`, and `y` (see [Public keys in JSON](#public-keys-in-json)). |
| `nonce` | string | yes | 128 random bits that the device made, in base64url: 22 characters of `A`–`Z`, `a`–`z`, `0`–`9`, `-`, and `_`. The answer's license token carries it back in `areq`. |
| `iat` | integer | yes | When the request was made: a time. The service refuses a request made more than 5 minutes ahead of its own clock with `ELM_MALFORMED`. |
| `exp` | integer | yes | When the request expires: exactly `iat` + 604800 (7 days). After it, the service answers `ELM_REQUEST_EXPIRED`. |

- `jwk` is the one claim read before the signature is checked: the service reads it, checks that its thumbprint is `kid` and that the point is on the curve, and checks the signature with it. A request that fails this gets `ELM_PROOF_INVALID`; every other broken claim gets `ELM_MALFORMED`.
- Until the request is answered, the device keeps its claims and writes the same payload each time it is asked for the file; only the signature differs. A new request (`--renew`, or `renew` in the libraries) has a new `nonce` and replaces the earlier one, whose file must be discarded.

A request that `elm server activate-request` wrote, decoded:

```json
{"alg":"ES256","kid":"FAKE-SERVER-KEY-ID-0000000000000000000000V1","typ":"elm-actreq\u002Bjws"}
```

```json
{
  "ver": 1,
  "op": "activate",
  "key": "ELM-AAAAA-BBBBB-CCCCC-DDDDD",
  "prd": "acme-cad",
  "seq": 0,
  "retry": [],
  "fp": [
    "fa000000000000000000000000000001",
    "fa000000000000000000000000000002"
  ],
  "jwk": {
    "kty": "EC",
    "crv": "P-256",
    "x": "FAKE-X-COORDINATE-00000000000000000000000X2",
    "y": "FAKE-Y-COORDINATE-00000000000000000000000Y2"
  },
  "nonce": "FAKEnonce0000000000001",
  "iat": 1793540797,
  "exp": 1794145597
}
```

## Offline answer: `.elmresp`

The answer file has the extension `.elmresp`. It is the body of the service's `200` answer to an offline request, unchanged: the same JSON as the answer to [`POST /v1/activations`](device-protocol.md#post-v1activations). `elm offline process --out` writes it, and the device imports it (`ImportResponseAsync`, or [`elm server import`](elm-cli.md#elm-server-import)). It is not signed as a whole; the license token in it is.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `activationId` | string | yes | The activation ID, a UUID. |
| `license` | object | yes | The [license file](#license-file-elmlic). Its token is bound to the request's installation key and fingerprints, its `areq` is the request's `nonce`, and its `act` is the activation ID. |

The device accepts the answer only when it is at most 256 KiB and is this JSON with an `activationId` in UUID form, its license file verifies for this installation, its token's `act` equals `activationId`, and `areq` is the `nonce` of the request it is waiting for. An answer that is larger or is not this JSON gets `ELM_MALFORMED`, a license file that does not verify gets the verification error, and a token whose `act` is not `activationId`, or whose `areq` is not the waiting request's `nonce`, gets `ELM_REQUEST_MISMATCH`. The device keeps the activation ID of the token, which the signature covers.

The answer to the request above, as `elm offline process` saved it:

```json
{
  "activationId": "01900000-0000-7000-8000-00000000a001",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJ…",
    "certificates": [
      "eyJ…"
    ]
  }
}
```

The license token in it, decoded. It is a server license, so it also has the members that only a server license needs:

```json
{"alg":"ES256","kid":"FAKE-SIGNING-KEY-ID-000000000000000000000S1","typ":"elm-license\u002Bjws"}
```

```json
{
  "ver": 1,
  "iss": "FAKE-SIGNING-KEY-ID-000000000000000000000S1",
  "iat": 1793540797,
  "exp": 1796132797,
  "jti": "FAKEjti000000000000003",
  "lic": "01900000-0000-7000-8000-00000000b001",
  "prd": "acme-cad",
  "kind": "server",
  "feat": [
    {
      "id": "pro",
      "seats": 5
    }
  ],
  "cnf": {
    "jkt": "FAKE-SERVER-KEY-ID-0000000000000000000000V1"
  },
  "fp": [
    "fa000000000000000000000000000001",
    "fa000000000000000000000000000002"
  ],
  "fpm": "any",
  "grace": 604800,
  "refresh": 1728000,
  "seq": 1,
  "areq": "FAKEnonce0000000000001",
  "act": "01900000-0000-7000-8000-00000000a001",
  "lease": 600,
  "borrow": 86400,
  "overage": 0,
  "clone": "deny",
  "brl": 2,
  "meta": {
    "customer": "Contoso"
  }
}
```

## Offline deactivation proof: `elm-deact+jws`

An offline deactivation proof is a file with the extension `.elmdeact` that holds one `elm-deact+jws` token, without a line break. The app writes it with `CreateDeactivationProofAsync`, and `elm-server` through [`elm server deactivate-proof`](elm-cli.md#elm-server-deactivate-proof). Writing it removes the activation and its license file from the device. `elm offline process` sends it to the service, which frees the activation and answers `204` with no body; there is no answer file.

Header:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `alg` | string | yes | `ES256`. |
| `kid` | string | yes | The key ID of the installation key that the activation was made with. |
| `typ` | string | yes | `elm-deact+jws`. |

Payload:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `ver` | integer | yes | `1`. |
| `act` | string | yes | The activation ID, a UUID. |
| `seq` | integer | yes | The `seq` of the last license token the device received, 0 or more. |
| `retry` | array of strings | no | The `jti` of requests that got no answer: at most 16, each 1 to 64 characters. |
| `iat` | integer | yes | When the proof was made: a time. The service refuses a proof made more than 5 minutes ahead of its own clock with `ELM_MALFORMED`. |

- The proof has no `jwk`, no `jti`, and no `exp`. The service reads `act` first (`ELM_MALFORMED` when it is not a UUID, `ELM_ACTIVATION_NOT_FOUND` when no activation has that ID), then checks that `kid` is the key ID of the key the activation was made with and that the signature verifies with it (`ELM_PROOF_INVALID` otherwise), and only then reads the other claims.
- Until the user says that the proof was handed in, the device keeps it and returns the same file each time it is asked; `ForgetDeactivationProofAsync`, or [`elm server forget-proof`](elm-cli.md#elm-server-forget-proof), drops it.

A proof that `elm server deactivate-proof` wrote for the activation above, decoded:

```json
{"alg":"ES256","kid":"FAKE-SERVER-KEY-ID-0000000000000000000000V1","typ":"elm-deact\u002Bjws"}
```

```json
{
  "ver": 1,
  "act": "01900000-0000-7000-8000-00000000a001",
  "seq": 1,
  "retry": [],
  "iat": 1793541450
}
```

## Server license

A server license is a [license token](#license-token-elm-licensejws) with `kind` `server`, in a [license file](#license-file-elmlic). It lets one `elm-server` lend the seats of its features: `cnf.jkt` names the server's installation key, and clients accept lease tokens signed by that key only within the limits the server license sets. How a client checks a server is in [Floating licenses](../explanation/floating-licenses.md#the-server-license-how-the-client-trusts-an-on-premises-server).

A token of kind `server` must have these members, besides the ones every license token has:

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `cnf` | object | yes | `jkt`: the key ID of the server's installation key. |
| `seats` | integer | yes, in every item of `feat` | The concurrent seats of the feature, 1 or more. |
| `lease` | integer | yes | The lease time, in seconds, 1 or more. |
| `borrow` | integer | yes | The longest borrow, in seconds; `0` allows no borrowing. |
| `overage` | integer | yes | Seats over `seats`, in percent: 0 to 1,000. |
| `clone` | string | yes | `deny` or `flag`: what `elm-server` does when a copied app installation renews the same lease as the original ([Retries and clones](../explanation/floating-licenses.md#retries-and-clones)). |
| `brl` | integer | yes | Early returns of borrows allowed in 30 days: 0 to 1,000. |

`fp` and `fpm` are optional: `elm-server` checks them against its own machine each time it reads the file, and clients do not check them. `elm-service` writes a server license from a policy with `licenseKind` `server`; `elm license issue --kind server` writes one by hand (see [elm command reference](elm-cli.md#elm-license-issue)).

A server license that `elm license issue --kind server --feature "pro;seats=5" --borrow-days 1` wrote, with the server's key ID in `--bind-key` and its fingerprint in `--fingerprint`, decoded:

```json
{"alg":"ES256","kid":"FAKE-SIGNING-KEY-ID-000000000000000000000S1","typ":"elm-license\u002Bjws"}
```

```json
{
  "ver": 1,
  "iss": "FAKE-SIGNING-KEY-ID-000000000000000000000S1",
  "iat": 1793540851,
  "exp": 1830211200,
  "jti": "FAKEjti000000000000004",
  "lic": "SRV-0001",
  "prd": "acme-cad",
  "kind": "server",
  "feat": [
    {
      "id": "pro",
      "seats": 5
    }
  ],
  "cnf": {
    "jkt": "FAKE-SERVER-KEY-ID-0000000000000000000000V1"
  },
  "fp": [
    "fa000000000000000000000000000001"
  ],
  "fpm": "any",
  "grace": 604800,
  "lease": 600,
  "borrow": 86400,
  "overage": 0,
  "clone": "deny",
  "brl": 2
}
```

### The server license file

`elm-server` reads its server license from the `.elmlic` file that its setting `license` names (see [elm-server configuration](server-configuration.md#top-level-settings)). [`elm server activate`](elm-cli.md#elm-server-activate), [`elm server refresh`](elm-cli.md#elm-server-refresh), [`elm server import`](elm-cli.md#elm-server-import), and the server's own online renewal write that file: first to a temporary file beside it, which then replaces it, so that a reader never sees half a file.

### `GET /v1/server/license`

`elm-server` returns its server license file and its public key at [`GET /v1/server/license`](device-protocol.md#get-v1serverlicense). A client verifies the license file from the root key, requires `kind` `server`, and requires the key's thumbprint to equal `cnf.jkt`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `license` | object | yes | The server license file, the same JSON as the `.elmlic` file. |
| `key` | object | yes | The server's installation public key, as a JWK (see [Public keys in JSON](#public-keys-in-json)), at most 1,024 bytes. Its key ID is the server license's `cnf.jkt`. |

The `jti` of the server license is also the value of the `ELM-Server-License` header, which `elm-server` sends with each new lease token (see [Device protocol](device-protocol.md#headers)).

## Files the client keeps

`ElasticLM.Client` and `libelasticlm` keep their files in the installation key's folder: by default a folder per product, in the user scope or the machine scope (see [Devices and proofs](../explanation/devices-and-proofs.md#where-the-key-is-stored)), or the folder the app passes. The table says what each file is and whether it may be copied or deleted. Their inner format is not a published interface and may change. The files of `elm-server` are listed in [elm-server configuration](server-configuration.md#data-folder).

| File or folder | Where | What it holds | Copy or delete |
| --- | --- | --- | --- |
| `installation.key` | The installation key's folder | The device's installation key, a P-256 private key. On Windows it is protected with DPAPI, for the user or the machine; on Linux and macOS it is a file that only its owner can read (`0600`, in a `0700` folder). | Do not copy it: a copy on another device acts as the same device, and the service treats the two as a clone once both go online. Do not delete it while an activation, a lease, a borrow, or a server pin depends on it: tokens bound to it stop verifying on this device, and the next activation makes a new key, which uses one more activation. A key that cannot be read is never replaced on its own (`ELM_INSTALLATION_KEY_UNREADABLE`). |
| `activation.json` and `activation.lock` | The installation key's folder | The kept activation: its ID, its last `seq`, its kind, the license file it received, the `jti` of requests that got no answer, a waiting offline request, and a deactivation proof not yet handed in. `activation.lock` lets one process at a time send activation requests. | Do not copy it: with the key, a copy is a clone. Delete it only after the activation is deactivated, or when an administrator has deactivated it: an activation request without the last `seq` looks like a clone. A damaged file makes `Check` return `ELM_MALFORMED`. `activation.lock` holds nothing and is made again when it is needed. |
| `borrows/` | A folder in the installation key's folder | One file per borrowed lease, `<lease ID>.json`: the lease token with what verifies it, the server license file and the server key from `elm-server`, or the certificates from `elm-service`. At most 1,000 files, each at most 256 KiB. The client deletes a file once its borrow has expired. | A copy is of no use on another device: the lease names this installation key. Deleting a file loses the borrow on this device after a restart, while its seats stay taken on the server until it expires. |
| `servers/` | A folder in the installation key's folder | One file per `elm-server` URL. Its name is the SHA-256 of the UTF-8 bytes of the server URL in normalized form, in 64 lowercase hexadecimal characters, with the `.json` extension. The normalized form is the one a proof signs in `htu` (see [Device protocol](device-protocol.md#the-target-url-in-htu)): scheme and host in lowercase, no default port, percent-encodings in uppercase, no query or fragment, so `https://Licenses.Example:443/` becomes `https://licenses.example/`. The file holds the normalized URL, and the license ID and the server key ID the client accepted first for that URL. | It holds no secret. Deleting a file makes the client accept the next server license it sees at that URL, as `ResetServerPin` (C: `elm_client_reset_server_pin`) does. A damaged file makes the client refuse that server. |
| `clock` | The installation key's folder: `ActivationClient` and `LicenseClient` keep it there unless the app passes its own clock guard, the activation and the client of `libelasticlm` always do, and so does `libelasticlm`'s verifier with an installation unless `clock_guard_path` names another file | The clock state of the clock guard: the last time seen, signed with the installation key. | Deleting or editing it lets the clock be set back as far as the `iat` of the token being verified, and no further. A copy does not verify with another device's key and is ignored there. |
| The `FileClockGuard` state | The path the app passes to `FileClockGuard`, or to `clock_guard_path` in C | The same clock state. It is signed only when the app passes an installation key; without one, anyone who can write the file can reset it. | As for `clock`. |
| The `InstallationClockGuard` state | The path the app passes to `CreateClockGuard` of `InstallationKey` | The same clock state, signed with the installation key. | As for `clock`. |
