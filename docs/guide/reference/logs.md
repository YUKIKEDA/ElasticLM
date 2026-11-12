---
sidebar_position: 5
---

# Logs

This page lists what ElasticLM's logs hold: the signed usage log of `elm-server` and `elm-service`, the signed borrow record of `elm-server`, and the hash-chained audit log of `elm-service`. It gives the files, the line and row formats, every event and action, and how to verify each log. It is for administrators who read, keep, or hand over these logs, and for anyone who checks them for tampering. What the signatures protect against, and what they do not, is in the [Security model](../explanation/security-model.md). The operational log, which is not signed, comes first.

## Operational log

While they run, `elm-server` and `elm-service` write messages about their own work to standard output, with the .NET console logger. This log is not signed, and the programs do not keep it in a file: to keep it, capture standard output, for example with the service manager or the container runtime that starts the program.

Each message takes two lines: the level (`dbug`, `info`, `warn`, `fail`, or `crit`), the category, and the event number, then the message, indented. For example:

```text
info: elm-server[2]
      server.stopped: 0 leases were not returned
```

| Category | Messages |
| --- | --- |
| `elm-server` | The start (port, public URLs, key ID, server license ID, and the end of the reclaim window) and the stop; each lease acquired, released, or reclaimed; detected clones; administration requests; license reloads and failed license renewals; a usage log that cannot be written, or whose chain could not continue at the start; a borrow record set aside; a failed cleanup. |
| `elm-service` | The start (address, database, signing key, and the end of its certificates) and the stop; the database schema versions applied; signing key certificates that end within 30 days; where the usage log is; audit log rows and admin UI sessions removed; failed periodic tasks and database requests; a license file or lease that could not be signed; a usage log that cannot be written; the keys of the admin UI's sign-in made or skipped; refused API keys of inactive administrators; refused admin UI sign-ins; an identity provider that cannot be reached; and, at the `Debug` level, why an access token was refused. |
| `Microsoft.*` | The web host's own messages, such as the address it listens on and its start and stop. |

The level is `Information` by default. To change it, set the environment variable `Logging__LogLevel__Default` to `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`: for example, `Logging__LogLevel__Default=Warning` writes only warnings and errors.

The error that stops a program before it starts is not part of this log: `elm-server` writes `error:` and the reason to standard error, and `elm-service` writes the reason to standard error. The exit codes are in [elm-server configuration](server-configuration.md#command-line-and-exit-codes) and [elm-service configuration](service-configuration.md#command-line-and-exit-codes).

## Usage log

The usage log records leases, borrows, refusals, and events of the program that writes it, such as its starts and stops. It is a record for audits and billing questions: the seat count does not depend on it, and a failure to write it goes to the operational log without failing the request.

### Who writes it and where

| Program | When | Files | Setting |
| --- | --- | --- | --- |
| `elm-server` | Always | `<name>-<UTC time>.jsonl` beside the path in `log.usage` (environment variable `ELM_LOG_USAGE`); the default `usage.jsonl` gives `usage-<UTC time>.jsonl` | `log.usage`. A relative path is under the server's data folder (`dataDirectory`). |
| `elm-service` | Only on a node with `usageLog.directory` set | `usage-<node>-<UTC time>.jsonl` in that folder, one chain per node; the node also holds `usage-<node>.lock` while it writes | `usageLog.directory` (`ELM_USAGELOG_DIRECTORY`); the node name is `node.name` (`ELM_NODE_NAME`). |

- The UTC time in a name has the form `yyyyMMddTHHmmssfffZ`, for example `usage-20270125T093000123Z.jsonl`. Names sort in time order: a new file is never named before the newest one, even when the clock went back.
- A new file starts at each start and at each new UTC day.
- A node of `elm-service` does not start when another node of the same name already writes in the folder. Each node writes only its own files; collecting the files of every node is the operator's job.
- Old files stay until the operator deletes them.
- `elm-server` also keeps the borrow record, `borrows-<UTC time>.jsonl` in its data folder. It is a separate log, described in [Borrow record (elm-server)](#borrow-record-elm-server).

### Line format

Each line is one JSON object followed by a line feed (JSON Lines). Every line starts with these members:

| Member | Value |
| --- | --- |
| `type` | The event or line type. |
| `ts` | The time, in Unix seconds. |

The other members depend on the type. For example, a lease granted by `elm-server` (fake values):

```json
{"type":"lease.acquired","ts":1800000000,"lic":"LIC-0042","feat":"pro","n":1,"key":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","lease":"Xk2bQ9rT4mVn8sLp0wZa1g","host":"ws-12","user":"alice","app":"2.1.0"}
```

### Signed lines and the chain

| Type | Where | Members |
| --- | --- | --- |
| `chain.start` | First line of a file that continues the chain of the previous file | `stream` (`usage`); `prev`: `file` (the previous file's name) and `auth` (the SHA-256 of its last `auth` line, base64url), left out when there is no previous file; then the key members below. |
| `chain.reset` | First line of a file whose chain cannot continue: the previous file cannot be read, has no signature in its last 1 MiB, or a write failed | `stream`, `reason`, then the key members below. Signature numbering starts again at 1. |
| `key.changed` | `elm-server` only, when the server license changes while it runs | `license` (the new server license file), `key` (the new public key, as a JWK). The next signature is still made with the old key; later ones use the new key. |
| `auth` | After the first lines of each file, after every 100 lines, every 60 seconds when lines are pending, before a file is closed, and on an administrator's order | `sig`: an `elm-log+jws` token. |

The key members of the first line say which key signs the file:

| Program | Members |
| --- | --- |
| `elm-server` | `license` (the server license file) and `key` (here the server's public key as a JWK, not a [thumbprint](../explanation/devices-and-proofs.md#the-installation-key)). The server signs with its installation key. |
| `elm-service` | `node` (the node name) and `certificates` (one to four `elm-cert+jws` certificates of the service's signing key for logs). |

The payload of an `auth` token:

| Member | Value |
| --- | --- |
| `ver` | `1`. |
| `iat` | The time of signing, in Unix seconds. |
| `jti` | A unique ID of the signature. |
| `stream` | `usage` or `borrows`. |
| `n` | Here the number of this signature in the chain, from 1, not a seat count. |
| `prev` | The SHA-256 of the previous `auth` line, line feed included, base64url; empty at the start of a new chain. |
| `digest` | The SHA-256 of the lines this signature covers, line feeds included, base64url. |
| `lines` | The number of lines covered. |

The administrator's order to sign now: `elm server seal` or `POST /admin/v1/log/seal` on `elm-server`, and `POST /v1/admin/usage-log/seal` on `elm-service`.

### Events of `elm-server`

Lease lines carry `lic` (the server license ID), `feat` (the feature), `n` (the seats), `key` (the thumbprint of the client's installation key), and `lease` (the lease ID), where the event knows them. `code` and `result` hold an error code name, such as `ELM_NO_SEATS`; the codes are listed in [Error codes](error-codes.md).

| Type | Written when | Members |
| --- | --- | --- |
| `lease.acquired` | A lease is granted | `lic`, `feat`, `n`, `key`, `lease`; `host` and `user` when the client sent them; `app` when sent |
| `borrow.started` | A borrow is granted | As `lease.acquired` |
| `overage` | A grant puts the seats in use above the licensed seats | `lic`, `feat`, `n`, `key`, `lease`, `inUse` |
| `lease.renewed` | A lease is renewed | `lic`, `key`, `lease`, `cloneFlagged` |
| `lease.released` | The client releases a lease, or an administrator force-releases one | `lic`, `feat`, `n`, `key`, `lease` |
| `borrow.returned` | The client returns a borrow early | `lic`, `feat`, `n`, `key`, `lease` |
| `lease.expired` | A lease expires without renewal | `lic`, `feat`, `n`, `key`, `lease` |
| `borrow.expired` | A borrow reaches its end | `lic`, `feat`, `n`, `key`, `lease` |
| `lease.reclaimed` | A client reclaims a lease after a server restart | `lic`, `key`, `lease`, `cloneFlagged` |
| `lease.denied` | Refused acquisitions, renewals, reclaims, and early returns, counted every 60 seconds and at stop | `feat` (when known), `code`, `count` |
| `admin.denied` | Administration requests without the right token, counted every 60 seconds and at stop | `code` (`ELM_UNAUTHORIZED`), `count` |
| `admin.action` | An administration request with the right token | `action` (`status`, `leases`, `release`, `reload`, or `seal`), `target` (the lease ID of `release`), `result` (an error code name; `ELM_NETWORK_ERROR` when the caller cut the connection before the answer was written, `ELM_INTERNAL_ERROR` when the operation failed inside the server) |
| `server.started` | The server starts | `lic`, `key` (here the server's key ID: the thumbprint of the server's own installation key, not a client's), `borrows` (the borrows restored) |
| `server.stopped` | The server stops | `leases` (the leases not returned) |
| `records.set_aside` | The server, run with `--set-aside-broken-records`, moved a broken borrow record aside at start, before it listens; the line is there also when the start then fails to listen | `file` |
| `license.refreshed` | The server license was renewed from `elm-service` | `lic`, `token` (the token ID), `seq` |
| `license.refresh_failed` | Renewing the server license failed | `stage` (`start`, `write`, `reload`, or `service`), `code` |

A refusal line counts one kind (feature and code) for the period. Up to 100 kinds are counted in one period; beyond that, refusals are counted without their feature.

### Events of `elm-service`

Lease lines carry `lic` (the license ID), `prd` (the product's `code`, the product ID that apps and license files use, such as `acme-cad`; not the product's `id` in the administration API), `feat`, `n` (the seats), `key` (the thumbprint of the device's installation key), and `lease`. `exp` is the lease's expiry in Unix seconds. `code` holds an error code name; see [Error codes](error-codes.md). License keys are never written. Activations, administration, and clone detection are in the audit log, not here.

| Type | Written when | Members |
| --- | --- | --- |
| `lease.acquired` | A lease is granted | `lic`, `prd`, `feat`, `n`, `key`, `lease`, `exp`; `host` and `user` when the policy keeps them; `app` when sent |
| `borrow.started` | A borrow is granted | As `lease.acquired` |
| `lease.renewed` | A lease is renewed (a retry that returns the last token again is not written) | `lic`, `prd`, `feat`, `n`, `key`, `lease`, `exp` |
| `lease.released` | A lease is released | `lic`, `prd`, `feat`, `n`, `key`, `lease`, `by` (`device` or `admin`) |
| `borrow.returned` | A borrow is returned early, or released by an administrator | As `lease.released` |
| `lease.expired` | The cleanup removes an expired lease | `lic`, `prd`, `feat`, `n`, `key`, `lease` |
| `borrow.expired` | The cleanup removes an ended borrow | `lic`, `prd`, `feat`, `n`, `key`, `lease` |
| `lease.denied` | Acquisitions, renewals, and releases refused by the lease rules, counted every 60 seconds, at stop, and on a seal order; refusals for a bad proof, the rate limit (`429`), or the body size (`413`) are not counted | `feat` (when known), `code`, `count` |
| `service.started` | The node starts, before it accepts requests | `node` |
| `service.stopped` | The node stops, or a start fails after `service.started` was written (for example, the node cannot listen) | `node` |

A line is written after the request's change is committed. An expired lease is written once, by the node whose cleanup removed it.

### Verifying the usage log

```text
elm log verify <files or folders>... [--product <ID>] [--node <name>] [--stream usage|borrows] [--trust <file>]... [--revoked <kid>]... [--json]
```

Every option is listed in [`elm log verify`](elm-cli.md#elm-log-verify). For the usage log:

- Give the log files, or folders of them. Files are verified in name order, oldest first. A folder stands for the files of the stream in it.
- `--product` is needed for the log of `elm-server`, not for that of `elm-service`.
- `--node` picks a node's `usage-<node>-*.jsonl` files in folders of `elm-service`'s usage logs. It is not allowed with file paths.
- `--json` writes `intact`, `files`, `entries`, and `issues` (each with `file`, `line`, `severity`, `message`).

The command checks the key material of each first line against the trust store at the time of that line: a server license for `elm-server`, certificates for logs for `elm-service`. It then checks every `auth` line, the chain inside each file, and the chain from file to file. The text output gives one line per issue, `error: <file>:<line>: <message>` or `warning: ...`, then `intact: <files> file(s), <lines> event line(s)` or `NOT INTACT: ...`.

| Exit code | Meaning |
| --- | --- |
| 0 | Intact: no errors. Warnings may be present. |
| 1 | Not intact: at least one error. |
| 2 | Invalid input, such as a path that is not a file or folder, or no log file found. |

Errors: a file that cannot be read or is empty, a line that is not JSON or is longer than 1 MiB, a first line that is not `chain.start` or `chain.reset`, a chain line after the first line, another stream, key material that does not verify, a signature that is not by the file's key, a `digest` or line count that does not match, a break in `prev` or `n`, no old-key signature after `key.changed`, a missing or cut file between files, a file that continues with another key than the previous file's last key under another license, files of a server and of `elm-service` mixed in one chain, and for `elm-service` a file that names another node or no node, or a `key.changed` line.

Warnings: `chain.reset` with its reason, lines after the last signature, a last line cut in the middle, a server that moved (another key of the same server license), and an `elm-service` signing key that was replaced.

Deleting or cutting the newest file, which has no next file, is not detected.

## Borrow record (elm-server)

A borrowed client is away from the network and cannot reclaim its lease after a server restart. So `elm-server` writes every borrow and every early return to the borrow record, and reads it back at start: borrows that have not ended keep their seats across a restart. The record holds nothing else; the server keeps no database.

### Files

- `borrows-<UTC time>.jsonl` in the server's data folder (`dataDirectory`), with the same time form as the usage log, for example `borrows-20270125T093000123Z.jsonl`. Unlike the usage log, it has no setting of its own.
- A new file starts at each start and at each new UTC day. Its first lines after the chain line copy the borrows that have not expired and the early returns of the last 30 days, so the newest file alone holds the whole state. A copied line has the `ts` of the copy.
- Old files stay until the operator deletes them. The server reads only the newest one.
- A file set aside with `--set-aside-broken-records` is named `borrows-broken-<UTC time>.jsonl`, where the time has the form `yyyyMMddTHHmmssZ`. The server does not read it again.

### Line format and signatures

The lines are JSON Lines, and every line starts with `type` and `ts` (Unix seconds), as in the usage log. The record is signed with the same chain as the usage log of `elm-server`, with these differences:

- The stream is `borrows`, in the first line and in every `auth` token.
- An `auth` line follows every event line, and each `auth` line is flushed to the disk before the borrow or the return takes effect.
- The first line is `chain.start` or `chain.reset` with `license` and `key`, and the server signs with its installation key. When the server license changes while it runs, a `key.changed` line is written as in the usage log; when that line cannot be written, the record keeps the old license.
- When a line cannot be written, the borrow is refused with `ELM_INTERNAL_ERROR`, or the early return with `ELM_RETURN_NOT_ALLOWED`. The next line starts a new file with `chain.reset`.
- After `--set-aside-broken-records`, the next file starts with `chain.reset` whose `reason` names the file that was set aside.

### Events

| Type | Written when | Members |
| --- | --- | --- |
| `borrow.started` | Before a borrow is granted, and when a new file copies a borrow that has not expired | `lease`, `lic`, `seats`, `borrow`, `fp` |
| `borrow.returned` | Before an early return takes effect, and when a new file copies a return of the last 30 days | `id`, `key`, `fp`, `at` |

| Member | Value |
| --- | --- |
| `lease` | The whole lease token of the borrow. |
| `lic` | The server license ID at the time of the borrow. |
| `seats` | The seats of the borrowed feature in the server license at the time of the borrow. |
| `borrow` | The server license's borrow limit at the time of the borrow, in seconds. |
| `fp` | The machine fingerprints that the client sent: an array of at most 8 strings. Anything else is read as no fingerprints. |
| `id` | The lease ID of the returned borrow. |
| `key` | The thumbprint of the client's installation key that borrowed it. |
| `at` | The time of the return, in Unix seconds. |

For example, an early return (fake values):

```json
{"type":"borrow.returned","ts":1800003600,"id":"Xk2bQ9rT4mVn8sLp0wZa1g","key":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","fp":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4"],"at":1800003600}
```

`lic`, `seats`, and `borrow` let the server refuse an early return after the server license changed. The returns of the last 30 days count against the early returns that an installation key and each fingerprint may make. The usage log has events of the same names with other members; the two logs are separate.

### At start

1. The server verifies the newest file as `elm log verify` does. The file's last key must be the server's own key, or a key of the same server license (the server moved to another machine). A record of another server counts as broken.
2. A broken record stops the start with exit code 1, and the message names the problem. With `--set-aside-broken-records`, the server renames the file, starts without its borrows, and writes `records.set_aside` with the file name to the usage log.
3. From an intact file, the server restores the borrows whose lease token it signed and that have not expired, less the returned ones.

The borrows in a file that was set aside, deleted, or edited are not counted until they end. An administrator who can write to the data folder can free borrowed seats this way; the traces are `records.set_aside` and `chain.reset` in the logs, or a missing file.

#### Reading the record yourself

A stop right after a write can leave event lines after the last `auth` line. At start, the server reads such a line when it is `borrow.started`, but not when it is `borrow.returned`: the client of such a return was not told that it succeeded.

### Verifying the borrow record

```text
elm log verify <files or folders>... --product <ID> --stream borrows [--trust <file>]... [--revoked <kid>]... [--json]
```

The options, the output, the exit codes, and the errors and warnings are those of [Verifying the usage log](#verifying-the-usage-log). `--product` is needed: without it, the first line of each file is an error. `--node` is not allowed. A folder stands for the `borrows-<UTC time>.jsonl` files in it, not for the files set aside; to check one of those, give its path.

## Audit log

`elm-service` records administration and device operations in an append-only table of its database. Each row is chained to the previous one by SHA-256.

### What is recorded

- Every authenticated call of the administration API, reads and refusals included, with the action of the call, also when the call ends without a whole answer. A successful `GET /v1/admin/me` writes no row.
- Calls without valid authentication, counted and written as one `auth.failed` row a minute.
- Admin UI sign-ins, sign-outs, and session changes.
- From devices: activations, deactivations, trials, and detected clones. Token refreshes and refused device requests are not recorded.

Secrets, such as API keys, are never written.

### Row fields

| Field | Value |
| --- | --- |
| `n` | Here the row number, not a seat count. |
| `ts` | The time. Stored as Unix seconds; the administration API returns it as `yyyy-MM-ddTHH:mm:ssZ`. |
| `actorType` | `apikey`, `oidc`, `system`, or `installation`. |
| `actorId` | For `apikey`, the API key ID. For `oidc`, the administrator's ID. For `system`, `setup` or `auth`. For `installation`, the thumbprint of the device's installation key. |
| `action` | The action, from the table below. |
| `target` | What the action acted on, such as a license, activation, or lease ID; or `null`. |
| `result` | `ELM_OK`, or the error code name of the refusal (see [Error codes](error-codes.md)). A call that ended without a whole answer has `ELM_NETWORK_ERROR` when the caller cut the connection, before the answer or partway through it, and `ELM_INTERNAL_ERROR` when the operation failed inside the service. |
| `ip` | The source address, or `null`. |
| `detail` | A JSON object with the action's details, or `null`. |
| `prev` | The hash of the previous row (32 bytes). |
| `hash` | The hash of this row (32 bytes). The administration API returns `prev` and `hash` in base64url. |

### Actions

| Action | Records |
| --- | --- |
| `admin.create` | An administrator was created at the first sign-in with OIDC; `detail` has `issuer`, `subject` (the value of the claim that `oidc.subjectClaim` names), `role`. |
| `admin.role` | An administrator's role changed at sign-in; `detail` has `from`, `to`. |
| `admin.list` | `GET /v1/admin/admins`. |
| `admin.disable` | `POST /v1/admin/admins/{id}/disable`. |
| `admin.enable` | `POST /v1/admin/admins/{id}/enable`. |
| `admin.me` | A refused `GET /v1/admin/me`. |
| `session.create` | An admin UI sign-in; `detail` has `replaced` and `evicted` sessions. A refused sign-in is `ELM_FORBIDDEN` with `issuer` and `subject` (the value of the claim that `oidc.subjectClaim` names). |
| `session.list` | `GET /v1/admin/sessions`; `detail` has `adminId`. |
| `session.revoke` | `DELETE /v1/admin/sessions/{id}`. |
| `session.logout` | An admin UI sign-out. |
| `apikey.create` | `POST /v1/admin/api-keys`, and the first key at setup (`actorType` `system`, `actorId` `setup`); `detail` has `name`, `permissions`, `expiresAt`, and at setup `owner`. |
| `apikey.list` | `GET /v1/admin/api-keys`. |
| `apikey.revoke` | `DELETE /v1/admin/api-keys/{id}`. |
| `audit.list` | `GET /v1/admin/audit`. |
| `audit.verify` | `POST /v1/admin/audit/verify`; `detail` has `intact`, `checked`. |
| `auth.failed` | Failed authentications of the last minute (`actorType` `system`, `actorId` `auth`, `result` `ELM_UNAUTHORIZED`); `detail` has `count`. |
| `product.create` | `POST /v1/admin/products`. |
| `product.list` | `GET /v1/admin/products`. |
| `product.get` | `GET /v1/admin/products/{id}`. |
| `product.update` | `PATCH /v1/admin/products/{id}`. |
| `feature.create` | `POST /v1/admin/products/{id}/features`. |
| `feature.list` | `GET /v1/admin/products/{id}/features`. |
| `feature.update` | `PATCH /v1/admin/products/{id}/features/{featureId}`. |
| `policy.create` | `POST /v1/admin/policies`. |
| `policy.list` | `GET /v1/admin/policies`. |
| `policy.get` | `GET /v1/admin/policies/{id}`. |
| `policy.update` | `PATCH /v1/admin/policies/{id}`. |
| `signingkey.get` | `GET /v1/admin/signing-key`. |
| `license.create` | `POST /v1/admin/licenses`. |
| `license.list` | `GET /v1/admin/licenses`. |
| `license.get` | `GET /v1/admin/licenses/{id}`. |
| `license.update` | `PATCH /v1/admin/licenses/{id}`. |
| `license.suspend` | `POST /v1/admin/licenses/{id}/suspend`. |
| `license.resume` | `POST /v1/admin/licenses/{id}/resume`. |
| `license.revoke` | `POST /v1/admin/licenses/{id}/revoke`. |
| `license.extend` | `POST /v1/admin/licenses/{id}/extend`. |
| `license.file` | `POST /v1/admin/licenses/{id}/file`. |
| `activation.activate` | A device activated or reactivated (`actorType` `installation`); `detail` has `licenseId`, `seq`, `reactivated`, and `channel` `offline` for an offline file. |
| `activation.deactivate` | A device deactivated (`detail` has `licenseId`, `deactivations`), or `POST /v1/admin/activations/{id}/deactivate` (`detail` has `licenseId`, `keyId`). |
| `activation.clone` | A clone of an activation was detected; `result` is `ELM_CLONE_DETECTED` with `deny` and `ELM_OK` with `flag`; `detail` has `licenseId`, `operation`, `seq`, `lastSeq`, `strategy`. |
| `activation.offline` | `POST /v1/admin/offline`, refused files included; `detail` has `operation` and `channel` `offline`. |
| `activation.list` | `GET /v1/admin/licenses/{id}/activations`. |
| `activation.get` | `GET /v1/admin/activations/{id}`. |
| `trial.create` | A device started a trial (`actorType` `installation`); `detail` has `productId`, `activationId`, `expiresAt`. |
| `trial.list` | `GET /v1/admin/trials`. |
| `trial.delete` | `DELETE /v1/admin/trials/{id}`. |
| `lease.clone` | A clone of a lease was detected; `result` as for `activation.clone`; `detail` has `licenseId`, `feature`, `seq`, `lastSeq`, `strategy`. |
| `lease.list` | `GET /v1/admin/licenses/{id}/leases`. |
| `lease.release` | `DELETE /v1/admin/leases/{id}`; `detail` has `licenseId`, `feature`, `seats`. |
| `usagelog.list` | `GET /v1/admin/usage-log/files`. |
| `usagelog.get` | `GET /v1/admin/usage-log/files/{name}`. `detail` is `{"sent": ..., "size": ...}`: the bytes handed to the answer and the length of the file. `sent` equals `size` for a whole download; for one that was cut, it is the most that the caller can have read. |
| `usagelog.seal` | `POST /v1/admin/usage-log/seal`. |

A call refused for a missing permission is recorded with its action, `result` `ELM_FORBIDDEN`, and `detail` `{"permission": ...}`. When the caller was gone before that answer could be written, `result` is `ELM_NETWORK_ERROR`, with the same `detail`.

### The hash chain

Each row holds the hash of the previous row (`prev`) and its own hash (`hash`), so a changed, removed, or inserted row breaks the chain. The service checks the chain on request; see [Verifying the audit log](#verifying-the-audit-log).

#### Checking the chain yourself

To check the rows without the service, compute each row's hash as the SHA-256 of its columns in this order: `n`, `ts`, `actorType`, `actorId`, `action`, `target`, `result`, `ip`, `detail`, `prev`. Each column is a 4-byte big-endian length followed by its UTF-8 bytes. Numbers are decimal text, `ts` in Unix seconds. `prev` is its 32 raw bytes after the length `0x00000020`. `NULL` is the length `0xFFFFFFFF` with no bytes, so it differs from an empty string. The first row's `prev` is 32 zero bytes.

### Retention

| Setting | Environment variable | Value | Default |
| --- | --- | --- | --- |
| `audit.retentionDays` | `ELM_AUDIT_RETENTIONDAYS` | 1 to 36600 days | Left out: every row is kept. |

With a retention period, the service removes old rows every hour: the rows before the first row inside the period. The hash of the last removed row stays as the start of the chain, so the chain still verifies.

### Verifying the audit log

| Call | Permission | Answer |
| --- | --- | --- |
| `POST /v1/admin/audit/verify` | `audit.read` | `intact` (true or false), `checked` (the rows read), `firstBroken` (the number of the first row that does not follow, or `null`). |
| `GET /v1/admin/audit` | `audit.read` | `items` (rows with the fields above) and `next`. Query: `limit` (1 to 200, default 50), `after` (a row number), `order` (`asc`, the default, or `desc`). |

The check reads the rows from the chain's start in one snapshot: the numbers follow, each `prev` is the previous row's hash, each hash matches its row, and the number and hash of the last row are those that the service stores apart from the rows at each append, so a removed last row or a row added after it is found. Someone who can write to the database can rewrite the rows and their hashes; the chain does not detect that.
