---
sidebar_position: 6
---

# Administration API

This page lists every route of the two HTTP administration APIs: that of `elm-service`, the hosted activation service, and the local one of `elm-server`, the on-premises floating license server. The paths of `elm-service` start with `/v1/admin/`, and those of `elm-server` with `/admin/v1/`. For each route it gives the permission, the query parameters, the request body, the answer with an example, and the error codes. It is for developers who connect a sales or billing system to `elm-service`, and for operators who script either program. The `elm admin request` and `elm server` commands of [elm command reference](elm-cli.md) call these routes for you. The routes that apps and devices call are in [Device protocol](device-protocol.md). What products, policies, and licenses are is in [Activation](../explanation/activation.md); how a lease works is in [Floating licenses](../explanation/floating-licenses.md). Each program's section starts with an index of its routes: [`elm-service`](#route-index) and [`elm-server`](#elm-server-local-administration-api).

The examples come from runs of both programs. IDs, keys, hashes, tokens, and [thumbprints](../explanation/devices-and-proofs.md#the-installation-key) are replaced with fake values of the same shape, and the JSON is indented for reading; the programs send it on one line. Where a route takes a body, the body that the example sent is given before its answer. Both programs write some characters in JSON strings as `\uXXXX` escapes, such as `\u003E` for `>`, `\u003C` for `<`, `\u0022` for `"`, and `\u0027` for `'`; a JSON parser reads them as the characters themselves.

## `elm-service` administration API

### Route index

| Route | Permission | Does |
| --- | --- | --- |
| [`GET /health`](#get-health) | none | Check that the service and its database answer |
| [`POST /v1/admin/api-keys`](#post-v1adminapi-keys) | `apikey.create` | Create an API key |
| [`GET /v1/admin/api-keys`](#get-v1adminapi-keys) | `apikey.read` | List API keys |
| [`DELETE /v1/admin/api-keys/{id}`](#delete-v1adminapi-keysid) | `apikey.revoke` | Revoke an API key |
| [`GET /v1/admin/audit`](#get-v1adminaudit) | `audit.read` | List the audit log |
| [`POST /v1/admin/audit/verify`](#post-v1adminauditverify) | `audit.read` | Check the audit log's hash chain |
| [`POST /v1/admin/products`](#post-v1adminproducts) | `product.write` | Create a product |
| [`GET /v1/admin/products`](#get-v1adminproducts) | `product.read` | List products |
| [`GET /v1/admin/products/{id}`](#get-v1adminproductsid) | `product.read` | Read a product |
| [`PATCH /v1/admin/products/{id}`](#patch-v1adminproductsid) | `product.write` | Change a product |
| [`POST /v1/admin/products/{id}/features`](#post-v1adminproductsidfeatures) | `product.write` | Create a feature |
| [`GET /v1/admin/products/{id}/features`](#get-v1adminproductsidfeatures) | `product.read` | List a product's features |
| [`PATCH /v1/admin/products/{id}/features/{featureId}`](#patch-v1adminproductsidfeaturesfeatureid) | `product.write` | Change a feature |
| [`POST /v1/admin/policies`](#post-v1adminpolicies) | `policy.write` | Create a policy |
| [`GET /v1/admin/policies`](#get-v1adminpolicies) | `policy.read` | List policies |
| [`GET /v1/admin/policies/{id}`](#get-v1adminpoliciesid) | `policy.read` | Read a policy |
| [`PATCH /v1/admin/policies/{id}`](#patch-v1adminpoliciesid) | `policy.write` | Change a policy |
| [`GET /v1/admin/signing-key`](#get-v1adminsigning-key) | `signingkey.read` | Read the signing key's status |
| [`POST /v1/admin/licenses`](#post-v1adminlicenses) | `license.create` | Create a license and its key |
| [`GET /v1/admin/licenses`](#get-v1adminlicenses) | `license.read` | List and find licenses |
| [`GET /v1/admin/licenses/{id}`](#get-v1adminlicensesid) | `license.read` | Read a license |
| [`PATCH /v1/admin/licenses/{id}`](#patch-v1adminlicensesid) | `license.update` | Change a license |
| [`POST /v1/admin/licenses/{id}/suspend`](#post-v1adminlicensesidsuspend) | `license.update` | Suspend a license |
| [`POST /v1/admin/licenses/{id}/resume`](#post-v1adminlicensesidresume) | `license.update` | Resume a license |
| [`POST /v1/admin/licenses/{id}/extend`](#post-v1adminlicensesidextend) | `license.update` | Move a license's expiry later |
| [`POST /v1/admin/licenses/{id}/revoke`](#post-v1adminlicensesidrevoke) | `license.revoke` | Revoke a license |
| [`POST /v1/admin/licenses/{id}/file`](#post-v1adminlicensesidfile) | `license.file` | Get the license file of an unbound license |
| [`GET /v1/admin/licenses/{id}/activations`](#get-v1adminlicensesidactivations) | `activation.read` | List a license's activations |
| [`GET /v1/admin/activations/{id}`](#get-v1adminactivationsid) | `activation.read` | Read an activation |
| [`POST /v1/admin/activations/{id}/deactivate`](#post-v1adminactivationsiddeactivate) | `activation.deactivate` | Deactivate a device |
| [`GET /v1/admin/trials`](#get-v1admintrials) | `trial.read` | List trial records |
| [`DELETE /v1/admin/trials/{id}`](#delete-v1admintrialsid) | `trial.delete` | Delete a trial record |
| [`POST /v1/admin/offline`](#post-v1adminoffline) | `activation.offline` | Process an offline request file |
| [`GET /v1/admin/licenses/{id}/leases`](#get-v1adminlicensesidleases) | `lease.read` | List a license's cloud leases |
| [`DELETE /v1/admin/leases/{id}`](#delete-v1adminleasesid) | `lease.release` | Release a cloud lease |
| [`GET /v1/admin/usage-log/files`](#get-v1adminusage-logfiles) | `usagelog.read` | List the usage log files |
| [`GET /v1/admin/usage-log/files/{name}`](#get-v1adminusage-logfilesname) | `usagelog.read` | Read a usage log file |
| [`POST /v1/admin/usage-log/seal`](#post-v1adminusage-logseal) | `usagelog.seal` | Sign the usage log now |
| [`GET /v1/admin/oidc`](#get-v1adminoidc) | none | Read the OIDC settings for `elm admin login` |
| [`GET /v1/admin/admins`](#get-v1adminadmins) | `admin.read` | List administrators |
| [`POST /v1/admin/admins/{id}/disable`](#post-v1adminadminsiddisable) | `admin.update` | Disable an administrator |
| [`POST /v1/admin/admins/{id}/enable`](#post-v1adminadminsidenable) | `admin.update` | Enable an administrator |
| [`GET /v1/admin/me`](#get-v1adminme) | any credential | Read the caller and its permissions |
| [`GET /v1/admin/sessions`](#get-v1adminsessions) | `session.read` | List admin UI sessions |
| [`DELETE /v1/admin/sessions/{id}`](#delete-v1adminsessionsid) | `session.revoke` | End an admin UI session |

### Requests and answers

The routes are under `/v1/admin/` on the address in `listen` (see [elm-service configuration](service-configuration.md#top-level-settings)), or under the public URL of a reverse proxy in front of the service when there is one. Bodies are JSON in UTF-8, and names are in camelCase.

#### IDs

| What | ID |
| --- | --- |
| Products, features, policies, licenses, activations, trials, API keys, administrators, and sessions | A UUID of version 7, written in lowercase as 36 characters, such as `01960000-0000-7000-8000-000000000501`. A trial's ID is the ID of its trial license. |
| Cloud leases | 22 base64url characters, such as `Xk2bQ9rT4mVn8sLp0wZa1g`. The leases of `elm-server` have IDs of the same form. |
| Audit rows | The row number `n`. |
| Usage log files | The file name. |

#### Authentication

Every route except `GET /v1/admin/oidc` and `GET /health` needs one of these credentials:

| Credential | How it is sent | Accepted when |
| --- | --- | --- |
| API key | `Authorization: Bearer elm_…`: `elm_` and 43 base64url characters. `elm-service setup` and `POST /v1/admin/api-keys` make keys. | Always. The key works until it expires or is revoked. With `oidc.issuer` set, it also stops when its administrator has not signed in for `apiKeys.inactiveAdminDays` days, counted from the latest of the last sign-in, the administrator's creation, and the key's creation. |
| OIDC access token | `Authorization: Bearer <token>`, where the value does not start with `elm_`. `elm admin login` gets one. | Only with `oidc.issuer` set. The token is a signed JWT (RS256, PS256, or ES256) of at most 8,192 characters, with `typ` `at+jwt`, `application/at+jwt`, or `JWT`, `iss` equal to `oidc.issuer`, `aud` holding `oidc.audience`, and a valid `exp` (60 seconds of clock skew allowed). Its `iss` and the claim that `oidc.subjectClaim` names (`sub` by default), each 1 to 255 characters without control characters, name the administrator. That claim must appear once, as a string. `oidc.roleClaim` (at most 100 values) gives the role through `oidc.roleMap`; a token outside these limits is not valid. |
| Admin UI session | The cookie `__Host-elm-session`, with no `Authorization` header. Requests other than `GET` and `HEAD` also need the header `X-ELM-CSRF: 1`, and an `Origin` header, when present, must be the origin of the admin UI. | Only with the admin UI's sign-in on (`oidc.webClientId` set). The session ends after `session.idleMinutes` without a request or after `session.maxHours`. |

- The scheme name `Bearer` is not case-sensitive. A request with an `Authorization` header is never authenticated by its cookie.
- A missing, unknown, revoked, or expired credential, a disabled administrator, and an ended session all get the same answer: `401` with `ELM_UNAUTHORIZED`.
- A valid access token whose role claim maps to no role gets `403` with `ELM_FORBIDDEN`. When the token's `iss` and its value of the `oidc.subjectClaim` claim are those of `bootstrapOwner` (the first administrator, named in [elm-service configuration](service-configuration.md#bootstrapowner)), the role is `owner` whatever the claim says.
- A session request without the CSRF header, or from another origin, gets `403` with `ELM_FORBIDDEN`.

The settings are in [elm-service configuration](service-configuration.md#oidc). The sign-in itself (`/auth/login`, `/auth/callback`, `/auth/logout`) is part of the admin UI, not of this API.

#### Roles and permissions

Each route needs one permission, named `resource.action`. An administrator has a role, and the role gives the permissions below. An API key carries its own list of permissions; at each request, only those that its administrator's role also holds count. An access token or a session has all the permissions of its role. The role `none` is that of an administrator whose role claim no longer maps to a role, and holds no permission.

| Permission | Allows | `owner` | `admin` | `support` | `viewer` |
| --- | --- | --- | --- | --- | --- |
| `apikey.read` | List API keys | ✓ | | ✓ | ✓ |
| `apikey.create` | Create API keys | ✓ | | | |
| `apikey.revoke` | Revoke API keys | ✓ | | | |
| `audit.read` | List and verify the audit log | ✓ | ✓ | ✓ | ✓ |
| `product.read` | Read products and features | ✓ | ✓ | ✓ | ✓ |
| `product.write` | Create and change products and features | ✓ | ✓ | | |
| `policy.read` | Read policies | ✓ | ✓ | ✓ | ✓ |
| `policy.write` | Create and change policies | ✓ | ✓ | | |
| `license.read` | Read licenses | ✓ | ✓ | ✓ | ✓ |
| `license.create` | Create licenses | ✓ | ✓ | | |
| `license.update` | Change, suspend, resume, and extend licenses | ✓ | ✓ | | |
| `license.revoke` | Revoke licenses | ✓ | ✓ | | |
| `license.file` | Get the license file of an unbound license (one whose policy has `binding` `unbound`, which is not activated) | ✓ | ✓ | | |
| `signingkey.read` | Read the signing key's status | ✓ | ✓ | ✓ | ✓ |
| `activation.read` | Read activations | ✓ | ✓ | ✓ | ✓ |
| `activation.deactivate` | Deactivate a device as an administrator | ✓ | ✓ | ✓ | |
| `activation.offline` | Process offline request files | ✓ | ✓ | ✓ | |
| `trial.read` | Read trials | ✓ | ✓ | ✓ | ✓ |
| `trial.delete` | Delete trial records | ✓ | ✓ | | |
| `lease.read` | Read [cloud leases](#lease) | ✓ | ✓ | ✓ | ✓ |
| `lease.release` | Release cloud leases and borrows | ✓ | ✓ | ✓ | |
| `usagelog.read` | List and read the usage log files | ✓ | ✓ | ✓ | ✓ |
| `usagelog.seal` | Sign the usage log now | ✓ | ✓ | | |
| `admin.read` | List administrators | ✓ | | | |
| `admin.update` | Disable and enable administrators | ✓ | | | |
| `session.read` | List admin UI sessions | ✓ | | | |
| `session.revoke` | End admin UI sessions | ✓ | | | |

A call without the permission gets `403` with `ELM_FORBIDDEN`, and the message names the permission.

#### Request bodies

- The `Content-Type` header of a request is not checked. A body is read as JSON in UTF-8 whatever type is sent, and the body of `POST /v1/admin/offline` as the text of the file.
- A body is at most 64 KiB, counted by `Content-Length` or by the bytes read. A larger one gets `413` with `ELM_PAYLOAD_TOO_LARGE`.
- A body must be one JSON object. A body that is not JSON or not an object, a member of the wrong type, nesting deeper than 8, a required member that is missing or `null`, a member given twice, or a string with an unpaired surrogate gets `400` with `ELM_MALFORMED`.
- A value of the right type outside its range or set gets `400` with `ELM_INVALID_ARGUMENT`. So does an ID in the body that names nothing, such as a `productId` of no product.
- Unknown members are ignored, so that a newer client works with an older service.
- Times are written in one form only, UTC to the second: `2027-11-01T00:00:00Z`. Answers use the same form. A time in another form (fractions of a second, `+00:00`) gets `ELM_MALFORMED`.
- Lengths in characters count Unicode characters (scalar values), not bytes, unless the table says bytes.
- `PATCH` changes only the members sent; members not sent stay as they are. Sending a member that cannot change, such as a `code`, gets `400` with `ELM_INVALID_ARGUMENT`. What `null` means differs per member, and is given in the body table of each route: for some it removes a value, for some it restores a default, and for others it is `ELM_MALFORMED`.
- Routes that take no body ignore one that is sent.

#### Paging

Every list route answers a page: an object with `items`, an array of objects, and `next`, the `after` of the next page. The example of `GET /v1/admin/api-keys` shows one.

| Query parameter | Range and meaning |
| --- | --- |
| `limit` | Items on a page: 1 to 200. Default 50. |
| `after` | Start after this item: the `next` of the previous page. Items are in ID order, which is the order of creation, unless the route says otherwise. An `after` that names no item still works: the page starts after its place in that order. |

`next` is left out on the last page. A `limit` or `after` outside its form, or a query parameter given twice, gets `400` with `ELM_INVALID_ARGUMENT`.

#### Errors

An error is a problem response (RFC 9457) with the type `application/problem+json`:

```json
{
  "type": "about:blank",
  "title": "ELM_CONFLICT",
  "status": 409,
  "detail": "The product code \u0027acme-cad\u0027 is in use.",
  "code": "ELM_CONFLICT"
}
```

`code` and `title` hold the error code, `status` the HTTP status, and `detail` an English message. Every code is in [Error codes](error-codes.md). Besides the errors of each route below, any route may answer:

| Status | Code | When |
| --- | --- | --- |
| 401 | `ELM_UNAUTHORIZED` | No valid credential. See [Authentication](#authentication). |
| 403 | `ELM_FORBIDDEN` | The permission is missing, or the access token maps to no role, or the CSRF check of a session failed. |
| 413 | `ELM_PAYLOAD_TOO_LARGE` | The body is larger than 64 KiB (routes that read a body). |
| 429 | `ELM_RATE_LIMITED` | A rate limit was reached. The answer has `Retry-After: 1`. |
| 500 | `ELM_INTERNAL_ERROR` | The database failed. The reason is in the operational log ([Logs](logs.md#operational-log)). |

A route path that does not exist gets `404` without a body, and a method that a path does not take gets `405` without a body. An `{id}` in a path that is not in the form of an ID gets the same answer as an unknown ID.

#### Rate limits

Every request is first limited per source address (an IPv4 address, or an IPv6 /64) by `rateLimit.perIp`. An authenticated request is then also limited by `rateLimit.perKey`: per API key, or per administrator for an access token or a session. The checks run in this order: the limit per source, authentication, the limit per key, the permission, then the route itself. The settings are in [elm-service configuration](service-configuration.md#ratelimit).

#### Caching

Every answer under `/v1/admin/`, errors included, has `Cache-Control: no-store`.

#### Audit log

Every authenticated call writes one row to the audit log, reads and refusals included, with the action named in each route below. A call that ends without a whole answer writes its row too. A call refused for a missing permission is written with `result` `ELM_FORBIDDEN`. Calls without a valid credential are counted and written as one `auth.failed` row a minute. Rate-limited calls are not written. `GET /v1/admin/oidc` and a successful `GET /v1/admin/me` write no row. The actions and the row fields are in [Logs](logs.md#audit-log).

### Health check

#### `GET /health`

Permission: none; no credential is needed. The limit per source applies.

Answers `200` without a body when the database answers, and `503` without a body when it does not. The answer has no `Cache-Control` header. Writes no audit row.

### Objects

The objects below are returned by several routes. A member that can be `null` says so; such members are always present. Every time is in the form `2027-11-01T00:00:00Z`.

#### Product

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The product's ID. |
| `code` | string | The product ID that apps and license files use: 1 to 64 lowercase letters, digits, and `-`. It never changes. |
| `name` | string | A display name: 1 to 64 characters. |
| `trialPolicyId` | string or `null` | The ID of the policy with `licenseKind` `trial` that gives trials of this product ([Trials](../explanation/activation.md#trials)); `null` when the product offers no trial. |
| `createdAt` | time | When it was created. |

#### Feature

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The feature's ID. |
| `productId` | string | The product it belongs to. It never changes. |
| `code` | string | The feature ID that apps and license files use: 1 to 64 lowercase letters, digits, and `-`, unique in the product. It never changes. |
| `name` | string | A display name: 1 to 64 characters. |
| `createdAt` | time | When it was created. |

#### Policy

Every member is present, the defaults included. What each setting decides is in [Activation](../explanation/activation.md#policy-settings).

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The policy's ID. |
| `productId` | string | The product. It never changes. |
| `name` | string | A display name: 1 to 64 characters. |
| `licenseKind` | string | `standard`, `trial`, or `server`. It never changes. |
| `binding` | string | `unbound` or `nodeLocked`. It never changes. |
| `maxActivations` | number | Devices that may hold an activation at once. |
| `deactivationLimit` | number or `null` | Deactivations by devices allowed in total; `null` for no limit. |
| `expirationBasis` | string | `fromIssue` or `fromFirstActivation`. |
| `duration` | number or `null` | Seconds a license lasts; `null` for no expiry. |
| `tokenLifetime` | number | Seconds one license file is valid. |
| `refreshWindow` | number | Seconds before a file's expiry from which the app refreshes it. |
| `gracePeriod` | number | Seconds an app keeps working after the file's expiry. |
| `fingerprintMatching` | string | `ignore`, `any`, or `all`. |
| `cloneStrategy` | string | `deny` or `flag`: what the service does when it detects a copy of an activated installation ([Clone detection with the sequence number](../explanation/activation.md#clone-detection-with-the-sequence-number)). In a policy with `licenseKind` `server`, the activated installation is an `elm-server`, and the value also becomes the `clone` claim of the server license: what that server does when a copied app installation renews the same lease ([Retries and clones](../explanation/floating-licenses.md#retries-and-clones)). In a policy with `floating` `cloud`, it is also what the service does with such a lease. |
| `floating` | string | `none`, `onPremises`, or `cloud`. |
| `leaseDuration` | number | Seconds one lease lasts. |
| `overage` | number or `null` | Percent of seats that may be lent above the licensed seats; `null` for none. |
| `borrowLimit` | number or `null` | The longest borrow, in seconds; `null` when borrowing is not allowed. |
| `borrowReturnLimit` | number | Borrows that may be returned early in 30 days. |
| `clientInfoStrategy` | string | `none` or `hostAndUser`. |
| `trialDuration` | number | Seconds a trial lasts. |
| `createdAt` | time | When it was created. |
| `updatedAt` | time | When it was last changed. |

#### License

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The license ID, which license files carry. |
| `productId` | string | The product of its policy. |
| `policyId` | string | The policy. It never changes. |
| `name` | string | A name, such as the customer's: 1 to 256 characters. |
| `features` | array | The features it grants, each an object with the members below. |
| `features[].code` | string | The feature's `code`. |
| `features[].ver` | string or `null` | The version range, such as `>=2.0 <3.0`; `null` for every version. |
| `features[].seats` | number or `null` | The seats of a server license or a cloud-floating license (see [Lease](#lease)); `null` for other licenses. |
| `features[].expiresAt` | time or `null` | The feature's own expiry; `null` when it has none. |
| `maxActivations` | number or `null` | The license's own activation limit; `null` when the policy's `maxActivations` applies. |
| `state` | string | `active`, `suspended`, `expired`, or `revoked`. When several apply, revoked wins over suspended, and suspended over expired. See [License states](../explanation/activation.md#license-states). |
| `createdAt` | time | When it was created. |
| `expiresAt` | time or `null` | The expiry; `null` when the license has none, or under `fromFirstActivation` until the first activation. |
| `suspendedAt` | time or `null` | When it was suspended; `null` when it is not. |
| `revokedAt` | time or `null` | When it was revoked; `null` when it is not. |
| `meta` | object | String values that are copied into license files. |
| `deactivations` | number | Deactivations by devices so far, counted against the policy's `deactivationLimit`. Deactivations by an administrator are not counted. |
| `key` | string | The license key, such as `ELM-A1B2C-3D4E5-F6G7H-J8K9M`. Only in the answer to `POST /v1/admin/licenses`: the service keeps only its hash and cannot show it again. |

A trial is also a license, named `trial`, without a key.

#### Activation

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The activation ID. |
| `licenseId` | string | The license. |
| `keyId` | string | The thumbprint of the device's installation key. |
| `fingerprints` | array of strings | The machine fingerprints the device sent: up to 8, each 32 lowercase hexadecimal characters. |
| `client` | object or `null` | `host` and `user`: the host name and OS user name the device sent, kept only under `clientInfoStrategy` `hostAndUser`; otherwise `null`. |
| `app` | string or `null` | The app version the device sent; `null` when it sent none. |
| `seq` | number | The sequence number of the latest license file. |
| `blockedAt` | time or `null` | When the activation was stopped because a clone was detected under `cloneStrategy` `deny`; `null` when it is not stopped. |
| `createdAt` | time | When the device activated. |
| `updatedAt` | time | When it last changed, such as at a refresh. |

#### Trial

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The trial's ID, which is the ID of its trial license. |
| `productId` | string | The product. |
| `fingerprints` | array of strings | The machine fingerprints that had this trial. While the record exists, they cannot start another trial of the product. |
| `createdAt` | time | When the trial started. |

#### Lease

A cloud lease: seats of a cloud-floating license, which is a license whose policy has `floating` `cloud`, lent by `elm-service` itself rather than by an `elm-server`. How cloud leases work is in [Floating licenses](../explanation/floating-licenses.md#the-same-protocol-on-the-hosted-service).

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The lease ID: 22 base64url characters. |
| `feature` | string | The feature's `code`. |
| `seats` | number | The seats it holds. |
| `keyId` | string | The thumbprint of the device's installation key. |
| `seq` | number | The sequence number of the latest lease token. |
| `expiresAt` | time | When it expires unless renewed. |
| `borrow` | boolean | Whether it is a borrow. |
| `stopped` | boolean | Whether it was stopped because a clone was detected; a stopped lease is not renewed and ends at its expiry. |
| `client` | object or `null` | `host` and `user`, as for an activation. |
| `app` | string or `null` | The app version the device sent. |
| `createdAt` | time | When it was granted. |

#### API key

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The key's ID. The audit log names the key by it. |
| `name` | string | A name: 1 to 64 characters. |
| `prefix` | string | The first 8 characters of the key, such as `elm_ExAm`, to tell keys apart. |
| `permissions` | array of strings | The key's permissions, in the order of the table in [Roles and permissions](#roles-and-permissions). At each request, only those its administrator's role holds count. |
| `createdBy` | string | The ID of the administrator the key acts for. |
| `createdAt` | time | When it was created. |
| `expiresAt` | time | When it stops working. |
| `revokedAt` | time or `null` | When it was revoked; `null` when it is not. |
| `key` | string | The key itself. Only in the answer to `POST /v1/admin/api-keys`: the service keeps only its hash. |

#### Administrator

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The administrator's ID. |
| `issuer` | string | The OIDC issuer (`iss`). |
| `subject` | string | The value at that issuer of the claim that `oidc.subjectClaim` names (`sub` by default). |
| `role` | string | `owner`, `admin`, `support`, `viewer`, or `none`. |
| `enabled` | boolean | Whether the administrator can authenticate. A disabled administrator's API keys, tokens, and sessions do not work. |
| `createdAt` | time | When the administrator was created: by `elm-service setup` for `bootstrapOwner`, or at the first sign-in. |
| `lastLoginAt` | time or `null` | The last request with an access token or admin UI sign-in, written at most once an hour; `null` before the first. API keys do not count. |

#### Session

An admin UI session. Its cookie and the cookie's hash are never returned.

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The session's ID. |
| `adminId` | string | The administrator. |
| `createdAt` | time | The sign-in. |
| `lastSeenAt` | time | The last request, written at most once a minute. |
| `expiresAt` | time | When it ends at the latest (`session.maxHours` after the sign-in). |
| `ip` | string or `null` | The source address at the sign-in; `null` when it was not known. |

#### Audit entry

One row of the audit log. The meaning of each field is in [Logs](logs.md#row-fields).

| Member | Type | Meaning |
| --- | --- | --- |
| `n` | number | The row number. |
| `ts` | time | The time. |
| `actorType` | string | `apikey`, `oidc`, `system`, or `installation`. |
| `actorId` | string or `null` | Who acted. |
| `action` | string | The action. |
| `target` | string or `null` | What it acted on. |
| `result` | string | `ELM_OK` or an error code. `ELM_NETWORK_ERROR` when the caller cut the connection, `ELM_INTERNAL_ERROR` when the operation failed inside the service. |
| `ip` | string or `null` | The source address. |
| `detail` | string or `null` | The details: a JSON object as text, exactly as stored, so that the row's hash can be computed again. |
| `prev` | string | The previous row's hash, in base64url. |
| `hash` | string | This row's hash, in base64url. |

### API keys

#### `POST /v1/admin/api-keys`

Creates an API key for the caller's administrator.

Permission: `apikey.create`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `name` | string | Yes | 1 to 64 characters. |
| `permissions` | array of strings | Yes | 1 to 32 permissions from [Roles and permissions](#roles-and-permissions), counted before repeats are removed; one given twice is kept once. Each must be one the caller holds now. |
| `expiresInDays` | number | No | Days until the key expires: 1 to 365. Default 90; `null` is the default. |

Answers `201` with an [API key](#api-key) that includes `key`. Keep the key: it is not shown again.

This example sent:

```json
{
  "name": "billing",
  "permissions": ["license.read", "license.create", "license.update"],
  "expiresInDays": 30
}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000102",
  "name": "billing",
  "prefix": "elm_ExAm",
  "permissions": [
    "license.read",
    "license.create",
    "license.update"
  ],
  "createdBy": "01960000-0000-7000-8000-000000000001",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2026-12-01T11:27:59Z",
  "revokedAt": null,
  "key": "elm_ExAmPlEaPiKeY000000000000000000000000000001"
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a name, permission count, unknown permission, or `expiresInDays` out of range; `ELM_FORBIDDEN` (403) for a permission the caller does not hold.

Audit action: `apikey.create`.

#### `GET /v1/admin/api-keys`

Lists the API keys of every administrator, revoked and expired ones included.

Permission: `apikey.read`.

Query parameters: `limit` and `after` (an API key ID); see [Paging](#paging).

Answers `200` with a page of [API keys](#api-key), without `key`. This example is the answer to `GET /v1/admin/api-keys?limit=1`; it has `next` because the `billing` key follows.

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000101",
      "name": "setup",
      "prefix": "elm_SeTu",
      "permissions": [
        "apikey.read",
        "apikey.create",
        "apikey.revoke",
        "audit.read",
        "product.read",
        "product.write",
        "policy.read",
        "policy.write",
        "license.read",
        "license.create",
        "license.update",
        "license.revoke",
        "license.file",
        "signingkey.read",
        "activation.read",
        "activation.deactivate",
        "activation.offline",
        "trial.read",
        "trial.delete",
        "lease.read",
        "lease.release",
        "usagelog.read",
        "usagelog.seal",
        "admin.read",
        "admin.update",
        "session.read",
        "session.revoke"
      ],
      "createdBy": "01960000-0000-7000-8000-000000000001",
      "createdAt": "2026-11-01T11:27:58Z",
      "expiresAt": "2027-01-30T11:27:58Z",
      "revokedAt": null
    }
  ],
  "next": "01960000-0000-7000-8000-000000000101"
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `apikey.list`.

#### `DELETE /v1/admin/api-keys/{id}`

Revokes an API key. The key stops working at once and stays in the list with `revokedAt`. Revoking a revoked key changes nothing and answers the same, and each call writes its own audit row.

Permission: `apikey.revoke`.

Answers `204` without a body.

Errors: `ELM_NOT_FOUND` (404) when no key has the ID.

Audit action: `apikey.revoke`.

### Audit

#### `GET /v1/admin/audit`

Lists the rows of the audit log.

Permission: `audit.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit` | 1 to 200. Default 50. |
| `after` | A row number `n`. With `asc`, the page starts after it; with `desc`, below it. |
| `order` | `asc` (the default): oldest first. `desc`: newest first, from the newest row when `after` is not given. |

Answers `200` with a page of [audit entries](#audit-entry). Here `next` is a number: the `n` of the last row of the page. This example is the answer to `GET /v1/admin/audit?order=desc&limit=2`, made after the examples of [Licenses](#licenses) and [`PATCH /v1/admin/policies/{id}`](#patch-v1adminpoliciesid):

```json
{
  "items": [
    {
      "n": 25,
      "ts": "2026-11-01T11:28:04Z",
      "actorType": "apikey",
      "actorId": "01960000-0000-7000-8000-000000000101",
      "action": "policy.update",
      "target": "01960000-0000-7000-8000-000000000401",
      "result": "ELM_OK",
      "ip": "127.0.0.1",
      "detail": "{\u0022deactivationLimit\u0022:5,\u0022duration\u0022:null}",
      "prev": "hAsHeXaMpLe00000000000000000000000000000024",
      "hash": "hAsHeXaMpLe00000000000000000000000000000025"
    },
    {
      "n": 24,
      "ts": "2026-11-01T11:28:04Z",
      "actorType": "apikey",
      "actorId": "01960000-0000-7000-8000-000000000101",
      "action": "license.resume",
      "target": "01960000-0000-7000-8000-000000000504",
      "result": "ELM_CONFLICT",
      "ip": "127.0.0.1",
      "detail": null,
      "prev": "hAsHeXaMpLe00000000000000000000000000000023",
      "hash": "hAsHeXaMpLe00000000000000000000000000000024"
    }
  ],
  "next": 24
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit`, `after`, or `order`.

Audit action: `audit.list`.

#### `POST /v1/admin/audit/verify`

Checks the hash chain of the audit log, from the oldest row it keeps to the newest. What the check finds, and what it cannot find, is in [Logs](logs.md#verifying-the-audit-log).

Permission: `audit.read`.

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `intact` | boolean | Whether every row follows the previous one. |
| `checked` | number | The rows read. |
| `firstBroken` | number or `null` | The number of the first row that does not follow; `null` when the chain is intact. |

```json
{
  "intact": true,
  "checked": 72,
  "firstBroken": null
}
```

Errors: only those common to all routes.

Audit action: `audit.verify`.

### Products and features

There is no route that deletes a product, a feature, or a policy.

#### `POST /v1/admin/products`

Creates a product.

Permission: `product.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `code` | string | Yes | 1 to 64 lowercase letters, digits, and `-`; not used by another product. |
| `name` | string | Yes | 1 to 64 characters. |

Answers `201` with a [product](#product). A new product has no trial policy; set one with `PATCH`.

This example sent:

```json
{"code": "acme-cad", "name": "Acme CAD"}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000201",
  "code": "acme-cad",
  "name": "Acme CAD",
  "trialPolicyId": null,
  "createdAt": "2026-11-01T11:27:59Z"
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a `code` or `name` outside its form; `ELM_CONFLICT` (409) when another product has the code.

Audit action: `product.create`.

#### `GET /v1/admin/products`

Lists the products.

Permission: `product.read`.

Query parameters: `limit` and `after` (a product ID).

Answers `200` with a page of [products](#product).

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000201",
      "code": "acme-cad",
      "name": "Acme CAD",
      "trialPolicyId": "01960000-0000-7000-8000-000000000402",
      "createdAt": "2026-11-01T11:27:59Z"
    }
  ]
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `product.list`.

#### `GET /v1/admin/products/{id}`

Returns one product.

Permission: `product.read`.

Answers `200` with a [product](#product), such as the item of the list example above.

Errors: `ELM_NOT_FOUND` (404) when no product has the ID.

Audit action: `product.get`.

#### `PATCH /v1/admin/products/{id}`

Changes a product.

Permission: `product.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `name` | string | No | 1 to 64 characters. `null` is `ELM_MALFORMED`. |
| `trialPolicyId` | string or `null` | No | The ID of a policy of this product with `licenseKind` `trial`; the product then offers trials under it. `null` removes the trial policy, and the product offers no trial. |
| `code` | — | — | Cannot change: sending it is `ELM_INVALID_ARGUMENT`. |

Answers `200` with the changed [product](#product). This example sent the ID of a trial policy, created with `{"productId": "01960000-0000-7000-8000-000000000201", "name": "Trial", "licenseKind": "trial", "gracePeriod": 0}`:

```json
{"trialPolicyId": "01960000-0000-7000-8000-000000000402"}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000201",
  "code": "acme-cad",
  "name": "Acme CAD",
  "trialPolicyId": "01960000-0000-7000-8000-000000000402",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

Errors: `ELM_NOT_FOUND` (404) when no product has the ID; `ELM_INVALID_ARGUMENT` (400) for a `name` out of range, a `trialPolicyId` that is not a trial policy of this product, or `code`.

Audit action: `product.update`.

#### `POST /v1/admin/products/{id}/features`

Creates a feature of the product.

Permission: `product.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `code` | string | Yes | 1 to 64 lowercase letters, digits, and `-`; not used by another feature of the product. |
| `name` | string | Yes | 1 to 64 characters. |

Answers `201` with a [feature](#feature). This example sent:

```json
{"code": "pro", "name": "Pro"}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000301",
  "productId": "01960000-0000-7000-8000-000000000201",
  "code": "pro",
  "name": "Pro",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

Errors: `ELM_NOT_FOUND` (404) when no product has the ID; `ELM_INVALID_ARGUMENT` (400) for a `code` or `name` outside its form; `ELM_CONFLICT` (409) when another feature of the product has the code.

Audit action: `feature.create`.

#### `GET /v1/admin/products/{id}/features`

Lists the features of a product.

Permission: `product.read`.

Query parameters: `limit` and `after` (a feature ID).

Answers `200` with a page of [features](#feature).

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000301",
      "productId": "01960000-0000-7000-8000-000000000201",
      "code": "pro",
      "name": "Pro",
      "createdAt": "2026-11-01T11:27:59Z"
    },
    {
      "id": "01960000-0000-7000-8000-000000000302",
      "productId": "01960000-0000-7000-8000-000000000201",
      "code": "export",
      "name": "Export",
      "createdAt": "2026-11-01T11:27:59Z"
    }
  ]
}
```

Errors: `ELM_NOT_FOUND` (404) when no product has the ID; `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `feature.list`.

#### `PATCH /v1/admin/products/{id}/features/{featureId}`

Changes a feature.

Permission: `product.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `name` | string | No | 1 to 64 characters. `null` is `ELM_MALFORMED`. |
| `code`, `productId` | — | — | Cannot change: sending either is `ELM_INVALID_ARGUMENT`. |

Answers `200` with the changed [feature](#feature). This example sent:

```json
{"name": "Professional"}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000301",
  "productId": "01960000-0000-7000-8000-000000000201",
  "code": "pro",
  "name": "Professional",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

Errors: `ELM_NOT_FOUND` (404) when the product has no feature with the ID; `ELM_INVALID_ARGUMENT` (400) for a `name` out of range, `code`, or `productId`.

Audit action: `feature.update`.

### Policies

#### `POST /v1/admin/policies`

Creates a policy. Each setting left out takes its default.

Permission: `policy.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `productId` | string | Yes | The ID of a product. |
| `name` | string | Yes | 1 to 64 characters. |
| `licenseKind` | string | No | `standard` (default), `trial`, or `server`. |
| `binding` | string | No | `unbound` or `nodeLocked` (default). |
| `maxActivations` | number | No | 1 to 100,000. Default 1. |
| `deactivationLimit` | number or `null` | No | 0 to 100,000. Default `null`: no limit. |
| `expirationBasis` | string | No | `fromIssue` (default) or `fromFirstActivation`. |
| `duration` | number or `null` | No | Seconds: 86,400 (1 day) to 3,162,240,000 (100 years of 366 days). Default `null`: no expiry. |
| `tokenLifetime` | number | No | Seconds: 3,600 to 3,162,240,000. Default 2,592,000 (30 days). |
| `refreshWindow` | number | No | Seconds: 0 to 3,162,240,000, and less than `tokenLifetime`. Default 1,728,000 (20 days). |
| `gracePeriod` | number | No | Seconds: 0 to 3,162,240,000. Default 604,800 (7 days). |
| `fingerprintMatching` | string | No | `ignore`, `any` (default), or `all`. |
| `cloneStrategy` | string | No | `deny` (default) or `flag`; see `cloneStrategy` of the [policy](#policy). |
| `floating` | string | No | `none` (default), `onPremises`, or `cloud`. |
| `leaseDuration` | number | No | Seconds: 120 to 86,400. Default 600. |
| `overage` | number or `null` | No | Percent: 0 to 1,000. Default `null`: none. |
| `borrowLimit` | number or `null` | No | Seconds: 1 to 3,162,240,000. Default `null`: no borrowing. |
| `borrowReturnLimit` | number | No | 0 to 1,000. Default 2. |
| `clientInfoStrategy` | string | No | `none` (default) or `hostAndUser`. |
| `trialDuration` | number | No | Seconds: 86,400 to 31,536,000 (365 days). Default 1,209,600 (14 days). |

For a member that takes a number without `null`, or a string, `null` is `ELM_MALFORMED`. The settings must also fit together; otherwise the answer is `ELM_INVALID_ARGUMENT`:

- `refreshWindow` is less than `tokenLifetime`.
- A `trial` or `server` policy is `nodeLocked` and not `floating` `cloud`.
- A `trial` policy has `fingerprintMatching` `any` or `all`, and `gracePeriod` 0.
- An `unbound` policy has `expirationBasis` `fromIssue`.

What `licenseKind` and `floating` make:

- A policy with `licenseKind` `server` makes server licenses, which `elm-server` runs on. It is `nodeLocked`, and its `floating` is `none` or `onPremises`. Each feature of its licenses needs `seats`. How `elm-server` uses a server license is in [Floating licenses](../explanation/floating-licenses.md#the-server-license-how-the-client-trusts-an-on-premises-server).
- A policy with `floating` `cloud` makes cloud-floating licenses: `elm-service` lends their seats as [cloud leases](#lease), and they are not activated. Its `licenseKind` is `standard`.
- `elm-service` treats `onPremises` as `none`. A `standard` policy with `onPremises` makes licenses that are activated like those of a policy with `none`.

Answers `201` with a [policy](#policy). This example sent:

```json
{
  "productId": "01960000-0000-7000-8000-000000000201",
  "name": "Standard, 3 devices",
  "maxActivations": 3,
  "duration": 31622400
}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000401",
  "productId": "01960000-0000-7000-8000-000000000201",
  "name": "Standard, 3 devices",
  "licenseKind": "standard",
  "binding": "nodeLocked",
  "maxActivations": 3,
  "deactivationLimit": null,
  "expirationBasis": "fromIssue",
  "duration": 31622400,
  "tokenLifetime": 2592000,
  "refreshWindow": 1728000,
  "gracePeriod": 604800,
  "fingerprintMatching": "any",
  "cloneStrategy": "deny",
  "floating": "none",
  "leaseDuration": 600,
  "overage": null,
  "borrowLimit": null,
  "borrowReturnLimit": 2,
  "clientInfoStrategy": "none",
  "trialDuration": 1209600,
  "createdAt": "2026-11-01T11:27:59Z",
  "updatedAt": "2026-11-01T11:27:59Z"
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a `productId` of no product, a value out of range or not in its set, or settings that do not fit together.

Audit action: `policy.create`.

#### `GET /v1/admin/policies`

Lists the policies.

Permission: `policy.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit`, `after` | See [Paging](#paging); `after` is a policy ID. |
| `productId` | Only the policies of this product. |

Answers `200` with a page of [policies](#policy). This example is the answer to `GET /v1/admin/policies?limit=1`; it has `next` because more policies follow.

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000401",
      "productId": "01960000-0000-7000-8000-000000000201",
      "name": "Standard, 3 devices",
      "licenseKind": "standard",
      "binding": "nodeLocked",
      "maxActivations": 3,
      "deactivationLimit": null,
      "expirationBasis": "fromIssue",
      "duration": 31622400,
      "tokenLifetime": 2592000,
      "refreshWindow": 1728000,
      "gracePeriod": 604800,
      "fingerprintMatching": "any",
      "cloneStrategy": "deny",
      "floating": "none",
      "leaseDuration": 600,
      "overage": null,
      "borrowLimit": null,
      "borrowReturnLimit": 2,
      "clientInfoStrategy": "none",
      "trialDuration": 1209600,
      "createdAt": "2026-11-01T11:27:59Z",
      "updatedAt": "2026-11-01T11:27:59Z"
    }
  ],
  "next": "01960000-0000-7000-8000-000000000401"
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit`, `after`, or `productId`.

Audit action: `policy.list`.

#### `GET /v1/admin/policies/{id}`

Returns one policy.

Permission: `policy.read`.

Answers `200` with a [policy](#policy), in the form of the answer to [`POST /v1/admin/policies`](#post-v1adminpolicies).

Errors: `ELM_NOT_FOUND` (404) when no policy has the ID.

Audit action: `policy.get`.

#### `PATCH /v1/admin/policies/{id}`

Changes a policy. A change takes effect from the next license file or lease that the service issues; files and leases already issued keep their values until they expire.

Permission: `policy.write`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `name` | string | No | 1 to 64 characters. `null` is `ELM_MALFORMED`. |
| `deactivationLimit` | number or `null` | No | 0 to 100,000. `null` removes the limit. |
| `duration` | number or `null` | No | As at creation; `null` for no expiry. The new value applies to licenses created after the change and, under `fromFirstActivation`, to a license without an expiry at its next activation or cloud lease. Licenses that already have an expiry keep it. |
| `overage` | number or `null` | No | As at creation. `null` allows no seats above the licensed seats. |
| `borrowLimit` | number or `null` | No | As at creation. `null` stops borrowing. |
| `maxActivations`, `expirationBasis`, `tokenLifetime`, `refreshWindow`, `gracePeriod`, `fingerprintMatching`, `cloneStrategy`, `floating`, `leaseDuration`, `borrowReturnLimit`, `clientInfoStrategy`, `trialDuration` | as at creation | No | The ranges of `POST /v1/admin/policies`. `null` is `ELM_MALFORMED`. |
| `productId`, `licenseKind`, `binding` | — | — | Cannot change: sending any of them is `ELM_INVALID_ARGUMENT`. |

The whole policy after the change must fit the rules of `POST /v1/admin/policies`.

Answers `200` with the changed [policy](#policy). This example changed the policy of the `POST` example after the licenses of [Licenses](#licenses) were created; those licenses keep their expiry. It sent:

```json
{"deactivationLimit": 5, "duration": null}
```

The answer is that policy with these members changed:

```json
{
  "id": "01960000-0000-7000-8000-000000000401",
  "deactivationLimit": 5,
  "duration": null,
  "updatedAt": "2026-11-01T11:28:04Z"
}
```

Errors: `ELM_NOT_FOUND` (404) when no policy has the ID; `ELM_INVALID_ARGUMENT` (400) for a value out of range, a member that cannot change, or settings that do not fit together.

Audit action: `policy.update`.

### Signing key

#### `GET /v1/admin/signing-key`

Returns the status of the service's signing key.

Permission: `signingkey.read`.

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `keyId` | string | The signing key's key ID (`kid`). |
| `uses` | array of strings | The uses that the key's certificates allowed at the service's start, in this order: `license`, `lease`, `log`. `log` can be listed even when the service writes no usage log. |
| `expiresAt` | time | When the first of the uses the service needs stops being covered by a certificate: `license` and `lease`, and `log` with a usage log. For each use, the latest-ending certificate counts. |
| `expiresSoon` | boolean | Whether fewer than 30 days remain until `expiresAt`. |

```json
{
  "keyId": "kIdExAmPlEsIgNiNgKeY0000000000000000000001A",
  "uses": [
    "license",
    "lease",
    "log"
  ],
  "expiresAt": "2027-11-01T11:27:44Z",
  "expiresSoon": false
}
```

Errors: only those common to all routes.

Audit action: `signingkey.get`.

### Licenses

A change to a license takes effect from the next license file or lease that the service issues; files already on devices keep their values until they expire.

#### `POST /v1/admin/licenses`

Creates a license and its license key.

Permission: `license.create`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `policyId` | string | Yes | The ID of a policy. The license gets its product. |
| `name` | string | Yes | 1 to 256 characters, such as the customer's name. |
| `features` | array | Yes | 1 to 256 objects, one per feature, with the members below. |
| `features[].code` | string | Yes | The `code` of a feature of the policy's product; each code once. |
| `features[].ver` | string or `null` | No | A version range of at most 64 characters, such as `>=2.0 <3.0`. Left out or `null`: every version. |
| `features[].seats` | number or `null` | For a `server` policy or a `floating` `cloud` policy | 1 to 1,000,000. Required under those policies, and not allowed under others. |
| `features[].expiresAt` | time or `null` | No | The feature's own expiry: later than now, and not later than the license's expiry. Left out or `null`: none. Under `fromFirstActivation`, a time is not allowed. |
| `maxActivations` | number or `null` | No | 1 to 100,000. Left out or `null`: the policy's `maxActivations` applies. |
| `expiresAt` | time or `null` | No | Under `fromIssue`: the expiry, later than now. Left out or `null`, the expiry is now plus the policy's `duration`, or none when `duration` is `null`. Under `fromFirstActivation`: a time is not allowed; the service sets the expiry at the first activation or the first cloud lease. |
| `meta` | object of strings | No | At most 32 entries; each key 1 to 64 bytes and each value at most 1,024 bytes in UTF-8. `null` is `ELM_MALFORMED`. |

Answers `201` with a [license](#license) that includes `key`. Hand the key to the customer now: it is not shown again.

This example sent this body to the policy of the [`POST /v1/admin/policies`](#post-v1adminpolicies) example, whose `expirationBasis` is `fromIssue` and `duration` 31,622,400 seconds (366 days):

```json
{
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {"code": "pro", "ver": ">=2.0 <3.0"},
    {"code": "export"}
  ],
  "meta": {"customer": "C-1042"}
}
```

It sent no `expiresAt`, so the expiry in the answer is `createdAt` plus 366 days:

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {
      "code": "pro",
      "ver": "\u003E=2.0 \u003C3.0",
      "seats": null,
      "expiresAt": null
    },
    {
      "code": "export",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": null,
  "state": "active",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2027-11-02T11:27:59Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {
    "customer": "C-1042"
  },
  "deactivations": 0,
  "key": "ELM-A1B2C-3D4E5-F6G7H-J8K9M"
}
```

The route takes no idempotency key. The same request sent again, for example after a network failure lost the answer, creates a second license with its own ID and key. `meta` is stored as sent and returned with the license, and `GET /v1/admin/licenses` finds licenses by name with `q`; there is no filter on `meta`.

Errors: `ELM_INVALID_ARGUMENT` (400) for a `policyId` of no policy, a value out of range, a feature code of another product or given twice, `seats` where it is required or not allowed, or an expiry that breaks the rules above.

Audit action: `license.create`. The key is never written to the audit log.

#### `GET /v1/admin/licenses`

Lists licenses. The filters can be combined.

Permission: `license.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit`, `after` | See [Paging](#paging); `after` is a license ID. |
| `productId` | Only licenses of this product. |
| `policyId` | Only licenses of this policy. |
| `state` | Only licenses in this state: `active`, `suspended`, `expired`, or `revoked`. |
| `q` | Only licenses whose name contains this text: 1 to 100 characters without control characters. ASCII letters match in either case; `%` and `_` match only themselves. |
| `keyHash` | Only the license of this license key: the SHA-256 of the key in base64url (43 characters), where the key is first written in upper case without `-` (`ELMA1B2C3D4E5F6G7HJ8K9M`). For the key `ELM-A1B2C-3D4E5-F6G7H-J8K9M`, it is `gT9XDOHEzUlMi2gDHAs39ChMHVRb1Nwvg1k0tupdFaY`. The key itself is not sent, so that it does not appear in logs of URLs. |

Answers `200` with a page of [licenses](#license), without `key`. This example is the answer to `GET /v1/admin/licenses?limit=1&q=fabrikam`; it has no `next` because no other license matches. The license was created with `{"policyId": "01960000-0000-7000-8000-000000000403", "name": "Fabrikam server", "features": [{"code": "pro", "seats": 5}]}` under a policy sent as `{"productId": "01960000-0000-7000-8000-000000000201", "name": "Server", "licenseKind": "server", "floating": "onPremises"}`, whose `duration` is `null`:

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000502",
      "productId": "01960000-0000-7000-8000-000000000201",
      "policyId": "01960000-0000-7000-8000-000000000403",
      "name": "Fabrikam server",
      "features": [
        {
          "code": "pro",
          "ver": null,
          "seats": 5,
          "expiresAt": null
        }
      ],
      "maxActivations": null,
      "state": "active",
      "createdAt": "2026-11-01T11:28:00Z",
      "expiresAt": null,
      "suspendedAt": null,
      "revokedAt": null,
      "meta": {},
      "deactivations": 0
    }
  ]
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a parameter outside its form.

Audit action: `license.list`.

#### `GET /v1/admin/licenses/{id}`

Returns one license.

Permission: `license.read`.

Answers `200` with a [license](#license), without `key`. For the license created in the example of [`POST /v1/admin/licenses`](#post-v1adminlicenses), the answer was that example's answer without `key`.

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID.

Audit action: `license.get`.

#### `PATCH /v1/admin/licenses/{id}`

Changes a license.

Permission: `license.update`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `name` | string | No | 1 to 256 characters. `null` is `ELM_MALFORMED`. |
| `maxActivations` | number or `null` | No | 1 to 100,000. `null` removes the license's own limit, and the policy's `maxActivations` applies again. |
| `meta` | object of strings | No | Replaces all of `meta`; the limits of `POST /v1/admin/licenses`. `null` is `ELM_MALFORMED`. |
| `features` | array | No | Replaces all features; the rules of `POST /v1/admin/licenses`, with the license's current expiry. `null` is `ELM_MALFORMED`. |
| `expiresAt` | — | — | Not changed here: sending it is `ELM_INVALID_ARGUMENT`. Use `POST /v1/admin/licenses/{id}/extend`. |
| `productId`, `policyId` | — | — | Cannot change: sending either is `ELM_INVALID_ARGUMENT`. |

Answers `200` with the changed [license](#license). This example changed the license of the `POST` example. It sent:

```json
{"maxActivations": 5, "meta": {"customer": "C-1042", "order": "SO-77"}}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {
      "code": "pro",
      "ver": "\u003E=2.0 \u003C3.0",
      "seats": null,
      "expiresAt": null
    },
    {
      "code": "export",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": 5,
  "state": "active",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2027-11-02T11:27:59Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {
    "customer": "C-1042",
    "order": "SO-77"
  },
  "deactivations": 0
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_INVALID_ARGUMENT` (400) for a value that breaks a rule, `expiresAt`, `productId`, or `policyId`.

Audit action: `license.update`.

#### `POST /v1/admin/licenses/{id}/suspend`

Suspends an active or expired license: it gets no new license file and no lease until it is resumed. Suspending a suspended license changes nothing and answers the same.

Permission: `license.update`.

Answers `200` with the [license](#license), now with `state` `suspended` and `suspendedAt` set. This example suspended the license of the `PATCH` example; the answer is that license with these members changed:

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "state": "suspended",
  "suspendedAt": "2026-11-01T11:28:02Z"
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_CONFLICT` (409) when the license is revoked.

Audit action: `license.suspend`.

#### `POST /v1/admin/licenses/{id}/resume`

Resumes a suspended license. Its state becomes `active`, or `expired` when its expiry has passed. Resuming a license that is not suspended changes nothing and answers the same.

Permission: `license.update`.

Answers `200` with the [license](#license), with `suspendedAt` `null`. This example resumed the license suspended in the example above; the answer is the license of the `PATCH` example again, with these members:

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "state": "active",
  "suspendedAt": null
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_CONFLICT` (409) when the license is revoked.

Audit action: `license.resume`.

#### `POST /v1/admin/licenses/{id}/extend`

Moves the expiry of an active or expired license later. An expired license becomes active again. Features' own expiries do not change; change them with `PATCH`.

Permission: `license.update`.

| Name | Type | Required | Range and meaning |
| --- | --- | --- | --- |
| `expiresAt` | time | Yes | The new expiry: later than both the current expiry and now. `null` is `ELM_MALFORMED`. |

Answers `200` with the [license](#license). This example extended another license, created at 2026-11-01T11:28:01Z under the same policy as the `POST` example with `{"policyId": "01960000-0000-7000-8000-000000000401", "name": "Tailspin site", "features": [{"code": "pro"}]}`, so that its expiry was 2027-11-02T11:28:01Z. It sent:

```json
{"expiresAt": "2028-11-01T00:00:00Z"}
```

The answer:

```json
{
  "id": "01960000-0000-7000-8000-000000000504",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Tailspin site",
  "features": [
    {
      "code": "pro",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": null,
  "state": "active",
  "createdAt": "2026-11-01T11:28:01Z",
  "expiresAt": "2028-11-01T00:00:00Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {},
  "deactivations": 0
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_INVALID_ARGUMENT` (400) when `expiresAt` is not later than the current expiry and now; `ELM_CONFLICT` (409) when the license is suspended or revoked, or has no expiry.

Audit action: `license.extend`.

#### `POST /v1/admin/licenses/{id}/revoke`

Revokes a license for good: it gets no new license file and no lease, and it cannot be resumed or extended. Revoking a revoked license changes nothing and answers the same.

Permission: `license.revoke`.

Answers `200` with the [license](#license), now with `state` `revoked` and `revokedAt` set. This example revoked the license of the extend example above; the answer is that license with these members changed:

```json
{
  "id": "01960000-0000-7000-8000-000000000504",
  "state": "revoked",
  "revokedAt": "2026-11-01T11:28:04Z"
}
```

A resume of it afterwards got `409` with `ELM_CONFLICT`, as the example of [`GET /v1/admin/audit`](#get-v1adminaudit) shows.

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID.

Audit action: `license.revoke`.

#### `POST /v1/admin/licenses/{id}/file`

Signs and returns a license file for a license whose policy has `binding` `unbound`. Such a license is not activated: this file is how it reaches the customer. The token has no installation key, fingerprints, or sequence number. It expires when the license does, and has no expiry when the license has none; with an expiry, its grace period is the policy's `gracePeriod`.

Permission: `license.file`.

Answers `200` with the license file (`application/json`, with `Content-Disposition: attachment; filename="<license ID>.elmlic"`). Its members are listed in [Token and file formats](formats.md#license-file-elmlic), and what it is for in [License files and verification](../explanation/license-files-and-verification.md#the-license-file).

```json
{
  "format": "elasticlm-license",
  "version": 1,
  "license": "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU",
  "certificates": [
    "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"
  ]
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_CONFLICT` (409) when the policy is not `unbound`, or the license is suspended, revoked, or expired; `ELM_INTERNAL_ERROR` (500) when the service cannot sign now, for example because a key certificate has ended since the start.

Audit action: `license.file`, with the token's ID and expiry.

### Activations and trials

#### `GET /v1/admin/licenses/{id}/activations`

Lists the activations of a license.

Permission: `activation.read`.

Query parameters: `limit` and `after` (an activation ID).

Answers `200` with a page of [activations](#activation).

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000601",
      "licenseId": "01960000-0000-7000-8000-000000000501",
      "keyId": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "fingerprints": [
        "3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4"
      ],
      "client": null,
      "app": "2.1.0",
      "seq": 1,
      "blockedAt": null,
      "createdAt": "2026-11-01T11:29:19Z",
      "updatedAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `activation.list`.

#### `GET /v1/admin/activations/{id}`

Returns one activation.

Permission: `activation.read`.

Answers `200` with an [activation](#activation), in the form of an item of the list example above.

Errors: `ELM_ACTIVATION_NOT_FOUND` (404) when no activation has the ID.

Audit action: `activation.get`.

#### `POST /v1/admin/activations/{id}/deactivate`

Removes an activation without the device, for example when the device was lost. It frees a place under the license's activation limit. It works on an activation stopped as a clone too, and it does not count against the policy's `deactivationLimit`. The license file on the device stays valid until it expires (see [Activation](../explanation/activation.md#deactivation)).

Permission: `activation.deactivate`.

Answers `204` without a body.

Errors: `ELM_ACTIVATION_NOT_FOUND` (404) when no activation has the ID.

Audit action: `activation.deactivate`, with the license ID and the device's key thumbprint.

#### `GET /v1/admin/trials`

Lists the trial records.

Permission: `trial.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit`, `after` | See [Paging](#paging); `after` is a trial ID. |
| `productId` | Only the trials of this product. |

Answers `200` with a page of [trials](#trial).

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000701",
      "productId": "01960000-0000-7000-8000-000000000201",
      "fingerprints": [
        "9b1e7c3a5d2f4e6081a7c9b3d5e7f901"
      ],
      "createdAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit`, `after`, or `productId`.

Audit action: `trial.list`.

#### `DELETE /v1/admin/trials/{id}`

Deletes a trial record, so that its fingerprints can start a trial of the product again. The trial's license and activation stay.

Permission: `trial.delete`.

Answers `204` without a body.

Errors: `ELM_NOT_FOUND` (404) when no trial has the ID, also when it was already deleted.

Audit action: `trial.delete`.

### Offline

#### `POST /v1/admin/offline`

Processes an offline request file for a customer whose device cannot reach the service: an activation or refresh request (`.elmreq`) or a deactivation proof (`.elmdeact`). The service runs the same checks as for the device's own online request, and gives the same answers as the public route [`POST /v1/offline`](device-protocol.md#post-v1offline), which takes no credential. The same file sent again gets the same answer while the license and the activation still allow it. How offline activation works is in [Activation](../explanation/activation.md#offline-activation-by-file-exchange), and the formats of the files are in [Token and file formats](formats.md#offline-request-elm-actreqjws).

Permission: `activation.offline`. The rate limit per installation key does not apply.

The body is the content of the file as it is (the request file's text). The `Content-Type` is not checked; `elm offline process` sends `application/jose`.

Answers:

- For an activation or a refresh: `200` with `activationId` (the activation ID) and `license` (the signed license file). `elm offline process` saves this body unchanged as the answer file (`.elmresp`), which the customer imports on the device.
- For a deactivation: `204` without a body.

```json
{
  "activationId": "01960000-0000-7000-8000-000000000602",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU",
    "certificates": [
      "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"
    ]
  }
}
```

Errors:

| Status | Code | When |
| --- | --- | --- |
| 400 | `ELM_MALFORMED` | The body is not a request file or a deactivation proof, a claim is not valid, or the request is issued more than 5 minutes ahead of the service's clock. |
| 400 | `ELM_PROOF_INVALID` | The file's signature does not verify, or the file is not signed with the installation key of the activation it names. |
| 400 | `ELM_REQUEST_EXPIRED` | The request is older than its 7 days. |
| 400 | `ELM_WRONG_PRODUCT` | The license key is for another product. |
| 400 | `ELM_FINGERPRINT_UNAVAILABLE` | The policy compares fingerprints and the request has none. |
| 403 | `ELM_LICENSE_SUSPENDED`, `ELM_LICENSE_REVOKED`, `ELM_EXPIRED` | The license is suspended, revoked, or expired. |
| 404 | `ELM_LICENSE_NOT_FOUND` | No license has the key. |
| 404 | `ELM_ACTIVATION_NOT_FOUND` | No activation has the ID the refresh or deactivation names. |
| 409 | `ELM_ACTIVATION_LIMIT`, `ELM_DEACTIVATION_LIMIT` | The license has reached its activation or deactivation limit. |
| 409 | `ELM_CLONE_DETECTED` | A clone of the installation was detected, or the activation is stopped as a clone. |
| 409 | `ELM_CONFLICT` | The license is unbound or cloud-floating, and so is not activated. |
| 409 | `ELM_REPLAYED` | Another request was made with this request's nonce. |
| 500 | `ELM_INTERNAL_ERROR` | The service cannot sign a license file now. |

Audit action: `activation.offline`, for every file, refused ones included. A file that activates or deactivates also writes the device's row, `activation.activate` or `activation.deactivate`, and a detected clone writes `activation.clone`; these rows have the caller as the actor and `channel` `offline`. A refresh writes no row of its own.

### Leases

#### `GET /v1/admin/licenses/{id}/leases`

Lists the cloud leases and borrows of a license that have not expired. On-premises leases are held by the customer's `elm-server`; see [`elm-server` local administration API](#elm-server-local-administration-api).

Permission: `lease.read`.

Query parameters: `limit` and `after` (a lease ID). The leases are in lease ID order.

Answers `200` with a page of [leases](#lease).

```json
{
  "items": [
    {
      "id": "Xk2bQ9rT4mVn8sLp0wZa1g",
      "feature": "pro",
      "seats": 1,
      "keyId": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "seq": 1,
      "expiresAt": "2026-11-01T11:39:19Z",
      "borrow": false,
      "stopped": false,
      "client": {
        "host": "ws-12",
        "user": "alice"
      },
      "app": "2.1.0",
      "createdAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

Errors: `ELM_LICENSE_NOT_FOUND` (404) when no license has the ID; `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `lease.list`.

#### `DELETE /v1/admin/leases/{id}`

Releases a cloud lease or a borrow without the device, and frees its seats. The device learns it at its next renewal ([`POST /v1/leases/{id}/renew`](device-protocol.md#post-v1leasesidrenew)), which fails with `ELM_UNKNOWN_LEASE`.

Permission: `lease.release`.

Answers `204` without a body.

Errors: `ELM_UNKNOWN_LEASE` (404) when no lease that has not expired has the ID.

Audit action: `lease.release`, with the license ID, the feature, and the seats. The usage log, when it is on, gets a `lease.released` or `borrow.returned` line with `by` `admin` ([Logs](logs.md#events-of-elm-service)).

### Usage log

These routes act on the usage log of the node that answers. A node is one running instance of `elm-service`, named by `node.name` ([elm-service configuration](service-configuration.md#node)). Each node writes its own files; collecting the files of every node is the operator's job. On a node without `usageLog.directory`, all three answer `409` with `ELM_CONFLICT`. The log itself is described in [Logs](logs.md#usage-log).

#### `GET /v1/admin/usage-log/files`

Lists this node's usage log files, oldest first.

Permission: `usagelog.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit` | 1 to 200. Default 50. |
| `after` | A file name of 1 to 255 characters: the page starts with the first name after it. |

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `node` | string | This node's name (`node.name`). |
| `items` | array | The files: `name`, `size` (bytes), and `modifiedAt` (time of the last write). |
| `next` | string | The `after` of the next page; left out on the last page. |

```json
{
  "node": "node-1",
  "items": [
    {
      "name": "usage-node-1-20261101T112758915Z.jsonl",
      "size": 1749,
      "modifiedAt": "2026-11-01T11:28:58Z"
    }
  ]
}
```

Errors: `ELM_CONFLICT` (409) when the node writes no usage log; `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `usagelog.list`.

#### `GET /v1/admin/usage-log/files/{name}`

Returns one of this node's usage log files as it is.

Permission: `usagelog.read`.

Answers `200` with the file's lines (`application/x-ndjson`), up to the length the file had when the download began. A transfer that fails partway is cut, not ended. For example, the first lines of a file:

```text
{"type":"chain.start","ts":1793532478,"stream":"usage","node":"node-1","certificates":["eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"]}
{"type":"auth","ts":1793532478,"sig":"eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"}
{"type":"service.started","ts":1793532478,"node":"node-1"}
```

Errors: `ELM_CONFLICT` (409) when the node writes no usage log; `ELM_NOT_FOUND` (404) when this node has no file of that name.

Audit action: `usagelog.get`.

#### `POST /v1/admin/usage-log/seal`

Writes the refusals counted so far and signs the pending lines now, as the timer does every minute.

Permission: `usagelog.seal`.

Answers `204` without a body.

Errors: `ELM_CONFLICT` (409) when the node writes no usage log.

Audit action: `usagelog.seal`.

### Administrators and sessions

Administrators are created by `elm-service setup` (for [`bootstrapOwner`](service-configuration.md#bootstrapowner)) and at their first sign-in with OIDC; there is no route that creates or deletes one.

#### `GET /v1/admin/oidc`

Returns the OIDC settings that `elm admin login` needs. It returns no secret.

Permission: none; no credential is needed. Only the limit per source applies.

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `issuer` | string | `oidc.issuer`. |
| `clientId` | string | `oidc.cliClientId`. |
| `scopes` | array of strings | `oidc.cliScopes`. |

```json
{
  "issuer": "https://idp.example.com",
  "clientId": "elm-cli",
  "scopes": [
    "openid",
    "offline_access"
  ]
}
```

Errors: `ELM_NOT_FOUND` (404) when `oidc.issuer` is not set; `ELM_RATE_LIMITED` (429).

Writes no audit row.

#### `GET /v1/admin/admins`

Lists the administrators.

Permission: `admin.read`.

Query parameters: `limit` and `after` (an administrator ID).

Answers `200` with a page of [administrators](#administrator).

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000001",
      "issuer": "https://idp.example.com",
      "subject": "owner-1",
      "role": "owner",
      "enabled": true,
      "createdAt": "2026-11-01T11:27:58Z",
      "lastLoginAt": null
    }
  ]
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit` or `after`.

Audit action: `admin.list`.

#### `POST /v1/admin/admins/{id}/disable`

Disables an administrator. Their API keys, access tokens, and sessions stop working at once, and their admin UI sessions are removed. Disabling a disabled administrator changes nothing and answers the same.

Permission: `admin.update`.

Answers `204` without a body.

Errors: `ELM_NOT_FOUND` (404) when no administrator has the ID; `ELM_CONFLICT` (409) for the caller's own administrator, the `bootstrapOwner`, and the last enabled `owner`.

Audit action: `admin.disable`.

#### `POST /v1/admin/admins/{id}/enable`

Enables an administrator. Enabling an enabled administrator changes nothing and answers the same.

Permission: `admin.update`.

Answers `204` without a body.

Errors: `ELM_NOT_FOUND` (404) when no administrator has the ID.

Audit action: `admin.enable`.

#### `GET /v1/admin/me`

Returns the caller's administrator and the permissions of this request.

Permission: any authenticated caller.

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `id` | string | The administrator's ID. |
| `issuer` | string | The OIDC issuer. |
| `subject` | string | The value at that issuer of the claim that `oidc.subjectClaim` names (`sub` by default). |
| `role` | string | `owner`, `admin`, `support`, `viewer`, or `none`. |
| `permissions` | array of strings | The permissions this request holds, in the order of [Roles and permissions](#roles-and-permissions). For an API key, these are the key's permissions that the role also holds. |

```json
{
  "id": "01960000-0000-7000-8000-000000000001",
  "issuer": "https://idp.example.com",
  "subject": "owner-1",
  "role": "owner",
  "permissions": [
    "apikey.read",
    "apikey.create",
    "apikey.revoke",
    "audit.read",
    "product.read",
    "product.write",
    "policy.read",
    "policy.write",
    "license.read",
    "license.create",
    "license.update",
    "license.revoke",
    "license.file",
    "signingkey.read",
    "activation.read",
    "activation.deactivate",
    "activation.offline",
    "trial.read",
    "trial.delete",
    "lease.read",
    "lease.release",
    "usagelog.read",
    "usagelog.seal",
    "admin.read",
    "admin.update",
    "session.read",
    "session.revoke"
  ]
}
```

Errors: only those common to all routes.

A successful call writes no audit row; a refused one writes `admin.me`.

#### `GET /v1/admin/sessions`

Lists the admin UI sessions that have not ended. Sessions past their idle time or their end are not listed, even before they are removed.

Permission: `session.read`.

| Query parameter | Range and meaning |
| --- | --- |
| `limit`, `after` | See [Paging](#paging); `after` is a session ID. |
| `adminId` | Only the sessions of this administrator. |

Answers `200` with a page of [sessions](#session). The example comes from a service whose admin UI sign-in was off, so it has no session:

```json
{
  "items": []
}
```

Errors: `ELM_INVALID_ARGUMENT` (400) for a bad `limit`, `after`, or `adminId`.

Audit action: `session.list`.

#### `DELETE /v1/admin/sessions/{id}`

Ends an admin UI session. The browser that holds it is signed out at its next request.

Permission: `session.revoke`.

Answers `204` without a body.

Errors: `ELM_NOT_FOUND` (404) when no session that has not ended has the ID.

Audit action: `session.revoke`.

## `elm-server` local administration API

The local administration API of `elm-server` is used by the `elm server` commands on the server's machine. It answers only on the socket file `admin.sock` in the server's data folder (a Unix domain socket: a file that programs on the same machine connect through, instead of a port), with plain `http` over that socket, on Windows, Linux, and macOS alike; it has no TCP port and no setting (see [elm-server configuration](server-configuration.md#ports)). The connection never leaves the machine, so it is not encrypted; the data folder's permissions and the administrator token protect it (see [Security model](../explanation/security-model.md#for-the-customers-administrator)). The lease protocol is not served there, and a request for `/admin/…` on the server's `https` address gets `404` without a body.

Authentication is `Authorization: Bearer <token>`, where the token is the administrator token: the 32 random bytes kept in `admin.token` in the server's data folder, written in base64url without padding (43 characters). The server makes a new token at every start and replaces the file, so a program of your own reads the file again after each start of the server; the token of an earlier start gets `401`. On Linux and macOS the file holds the 32 bytes as they are, readable and writable only by its owner. On Windows the file holds them protected with DPAPI for the machine, so a script cannot use its bytes as they are; see below. The header must start with `Bearer` and one space exactly as written here: the scheme name is case-sensitive. A missing or wrong token gets `401` with `ELM_UNAUTHORIZED`. Whoever can read the file can administer the server (see [elm-server configuration](server-configuration.md#data-folder)).

On Linux and macOS, a program of your own calls the API through the socket file, as the server's user. With curl, the first line below makes the token from the file, and the second sends a request:

```text
token=$(base64 < "<data folder>/admin.token" | tr '+/' '-_' | tr -d '=\n')
curl --unix-socket "<data folder>/admin.sock" -H "Authorization: Bearer $token" http://elm-server.invalid/admin/v1/status
```

curl sends the request to the socket file. The host of the URL is not used: `elm-server.invalid` only fills the request's `Host` header, which the server does not read.

On Windows, a script uses the `elm server` commands with `--json` instead ([Server administration options](elm-cli.md#server-administration-options)): `elm server status`, `elm server leases`, and `elm server reload` then print the API's answer as it came.

For every route:

- There are no roles and no paging. The `allow` and `deny` settings and `rateLimit.perIp` do not apply.
- No route reads a body.
- An error is a problem response (`application/problem+json`) with the members described in [Errors](#errors).
- Each request with the right token writes an `admin.action` line to the usage log; requests without it are counted every minute in an `admin.denied` line ([Logs](logs.md#events-of-elm-server)).
- Times are in Unix seconds.

Every route needs the token:

| Route | Does |
| --- | --- |
| [`GET /admin/v1/status`](#get-adminv1status) | Read the server's state |
| [`GET /admin/v1/leases`](#get-adminv1leases) | List the leases and borrows |
| [`DELETE /admin/v1/leases/{id}`](#delete-adminv1leasesid) | Release a lease |
| [`POST /admin/v1/license/reload`](#post-adminv1licensereload) | Read the server license file again |
| [`POST /admin/v1/log/seal`](#post-adminv1logseal) | Sign the usage log now |

### `GET /admin/v1/status`

Returns the state of the server.

| Member | Type | Meaning |
| --- | --- | --- |
| `product` | string | The product ID (setting `product`). |
| `license` | string | The server license ID. |
| `licenseToken` | string | The token ID (`jti`) of the server license file in use: 22 base64url characters. |
| `licenseExpires` | number | The server license's expiry, in Unix seconds; left out when it has none. |
| `key` | string | The server's key ID: the thumbprint of its own installation key. |
| `recovering` | boolean | Whether the server is in its reclaim window after a start, in which clients reclaim their leases ([Floating licenses](../explanation/floating-licenses.md#restarts-and-reclaim)). |
| `recoveryEnds` | number | When the reclaim window ends, in Unix seconds. |
| `leases` | number | The leases and borrows held, counting expired ones that the server has not removed yet. |
| `features` | array | Per feature of the server license, and per feature it no longer grants while leases still hold it: `feature` (its ID), `seats` (licensed), `allowed` (seats with the overage), `inUse` (seats held, borrows included), and `borrowed` (seats held by borrows). |
| `autoRefresh` | boolean | Whether the server renews its server license from `elm-service` itself, through the activation that `elm server activate` keeps. |

Answers `200`:

```json
{
  "product": "acme-cad",
  "license": "01960000-0000-7000-8000-000000000502",
  "licenseToken": "jTiExAmPlE000000000001",
  "licenseExpires": 1796124632,
  "key": "sErVeRkEyExAmPlE00000000000000000000000001A",
  "recovering": false,
  "recoveryEnds": 1793533250,
  "leases": 2,
  "features": [
    {
      "feature": "pro",
      "seats": 5,
      "allowed": 5,
      "inUse": 3,
      "borrowed": 2
    }
  ],
  "autoRefresh": false
}
```

Errors: `ELM_UNAUTHORIZED` (401).

### `GET /admin/v1/leases`

Lists the leases and borrows the server holds.

| Member | Type | Meaning |
| --- | --- | --- |
| `leases` | array | One object per lease, with the members below. |
| `leases[].id` | string | The lease ID. |
| `leases[].feature` | string | The feature ID. |
| `leases[].seats` | number | The seats it holds. |
| `leases[].key` | string | The thumbprint of the client's installation key. |
| `leases[].expires` | number | When it expires unless renewed, in Unix seconds. |
| `leases[].borrow` | boolean | Whether it is a borrow. |
| `leases[].stopped` | boolean | Whether it was stopped because a clone was detected. |

Answers `200`:

```json
{
  "leases": [
    {
      "id": "Xk2bQ9rT4mVn8sLp0wZa1g",
      "feature": "pro",
      "seats": 1,
      "key": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "expires": 1793533857,
      "borrow": false,
      "stopped": false
    },
    {
      "id": "Bw7nR2cK5pQx9LmT3vYz0A",
      "feature": "pro",
      "seats": 2,
      "key": "r8W2zXd1mKn3oSuB9tLeF5gHiVjPqXaY2cO6wD8fZ7N",
      "expires": 1793619657,
      "borrow": true,
      "stopped": false
    }
  ]
}
```

Errors: `ELM_UNAUTHORIZED` (401).

### `DELETE /admin/v1/leases/{id}`

Releases a lease on the server's side and frees its seats. The release does not stop the client: its next renewal ([`POST /v1/leases/{id}/renew`](device-protocol.md#post-v1leasesidrenew-on-elm-server)) gets `ELM_UNKNOWN_LEASE`, and the client then reclaims the lease ([`POST /v1/leases/reclaim`](device-protocol.md#post-v1leasesreclaim)), which takes the seats again when they are free. A borrow cannot be released this way: it ends at its expiry or when the client returns it.

Answers `204` without a body.

Errors: `ELM_UNAUTHORIZED` (401); `ELM_UNKNOWN_LEASE` (404) when the server holds no lease with the ID; `ELM_RETURN_NOT_ALLOWED` (409) for a borrow:

```json
{
  "type": "about:blank",
  "title": "ELM_RETURN_NOT_ALLOWED",
  "status": 409,
  "detail": "A borrow is not released by the administrator; it ends at its expiry.",
  "code": "ELM_RETURN_NOT_ALLOWED"
}
```

### `POST /admin/v1/license/reload`

Makes the server read its license file (setting `license`) again and use it, without a restart. `elm server activate`, `refresh`, and `import` call it after they write a new file.

Answers `200`:

| Member | Type | Meaning |
| --- | --- | --- |
| `license` | string | The server license ID now in use. |
| `licenseToken` | string | The token ID (`jti`) of the server license file now in use. |

```json
{
  "license": "01960000-0000-7000-8000-000000000502",
  "licenseToken": "jTiExAmPlE000000000001"
}
```

Errors: `ELM_UNAUTHORIZED` (401). When the new file cannot be used, the server keeps the license it had and answers `409` with the code of the check that failed, whatever status that code has elsewhere. [Error codes](error-codes.md) gives these codes the status "409 for an `elm-server` license reload". The new file gets the checks of a start: a server license of the product in `product`, bound to this server's installation key, and to its machine fingerprints when the license names any (see [License files and verification](../explanation/license-files-and-verification.md#the-order-of-checks)).

| Code | When |
| --- | --- |
| `ELM_LICENSE_NOT_FOUND` | The file does not exist. |
| `ELM_MALFORMED` | The file cannot be read, is larger than 64 KiB, or is not a valid license file. |
| `ELM_BAD_SIGNATURE`, `ELM_UNTRUSTED_KEY`, `ELM_KEY_REVOKED`, `ELM_CERTIFICATE_EXPIRED` | The signature or the certificates fail against the server's trust settings. |
| `ELM_CLOCK_TAMPERED` | The license is issued in the future, or the server's clock was set back. |
| `ELM_EXPIRED` | The license is past its expiry and grace period. |
| `ELM_WRONG_PRODUCT`, `ELM_WRONG_KIND` | The license is for another product, or is not a server license. |
| `ELM_WRONG_INSTALLATION` | The license is bound to another installation key. |
| `ELM_FINGERPRINT_UNAVAILABLE`, `ELM_FINGERPRINT_MISMATCH` | The machine fingerprints cannot be computed, or do not match the license. |
| `ELM_INTERNAL_ERROR` | The borrow record cannot record the new license. |

### `POST /admin/v1/log/seal`

Signs the pending lines of the server's usage log now.

Answers `204` without a body.

Errors: `ELM_UNAUTHORIZED` (401), as every route of this API answers without the right token:

```json
{
  "type": "about:blank",
  "title": "ELM_UNAUTHORIZED",
  "status": 401,
  "detail": "The administrator token is missing or wrong.",
  "code": "ELM_UNAUTHORIZED"
}
```
