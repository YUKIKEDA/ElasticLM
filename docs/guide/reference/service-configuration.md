---
sidebar_position: 3
---

# elm-service configuration

This page lists every setting of `elm-service`, the hosted activation service that activates devices, issues license tokens, and serves the administration API and the admin UI. It is for the vendor's operator who deploys the service. For how activation works, read [Activation](../explanation/activation.md); for what the service's keys and secrets protect, read [Security model](../explanation/security-model.md).

## Command line and exit codes

```text
elm-service [setup] [--config <settings.json>]
```

| Argument | Meaning |
| --- | --- |
| `setup` | Prepares the database and makes the first API key instead of starting the service. It must be the first argument. It creates or updates the database tables, then makes a new API key for the administrator in `bootstrapOwner`, with the `owner` permissions, valid for 90 days, and records it in the audit log. The key alone goes to standard output, so `elm-service setup > key.txt` keeps it; the key ID, its first characters, the owner, and the expiry go to standard error. Each run makes another key. |
| `--config <path>` | The JSON configuration file, for the service and for `setup` alike. Without it, the settings come from environment variables only. See [How settings are given](#how-settings-are-given). |

Any other argument, `setup` in another place, or `--config` without a path prints the usage (`Usage: elm-service [setup] [--config <file>]`) and exits with 2. There is no `--help`.

Exit codes of the service:

| Exit code | Meaning |
| --- | --- |
| 0 | The service stopped normally. |
| 1 | The service cannot start, for example: no root public keys are given, the signing key's passphrase is not given, the signing key or a certificate file cannot be read, the signing key and its certificates do not match, the database fails, the usage log cannot be written, or the service cannot listen on `listen`. |
| 2 | The arguments are not valid, or a setting error: the configuration file or a value is not valid, or a required value is missing. |

Exit codes of `setup`:

| Exit code | Meaning |
| --- | --- |
| 0 | The API key was made. |
| 1 | The database fails: it cannot be used, its schema is newer than this version of `elm-service` or was changed differently by another version, the administrator in `bootstrapOwner` exists but is disabled or holds another role, or the key cannot be recorded. |
| 2 | The arguments are not valid, a setting error as for the service, `bootstrapOwner` is not set, or the `database` settings cannot be used. |

Before it exits with 1 or 2, `elm-service` prints the reason to standard error.

## How settings are given

- Settings come from a JSON configuration file, from environment variables, or from both. Pass the file with `--config`, for the service and for `setup` alike:

  ```text
  elm-service --config /etc/elm-service/settings.json
  elm-service setup --config /etc/elm-service/settings.json
  ```

- Without `--config`, `elm-service` reads environment variables only.
- An environment variable overrides the same setting in the file. An environment variable that is set to an empty value counts as not set. The one exception is `ELM_SIGNING_PASSPHRASE`: an empty value is used as an empty passphrase.
- The file is at most 64 KiB. A key that the file does not know (for example a misspelt one) is an error. Key names are case-sensitive.
- A value in the file may be left out, but not left empty: an empty string, or an empty or `null` entry in a list, is an error, not the default.
- Secrets are never written in the configuration file. See [Secrets](#secrets).
- `elm-service setup` does not need `signing.*`, `trust.*`, or the nonce secret. It uses the `database` settings and `bootstrapOwner`.
- A setting error stops `elm-service` before it starts, and it prints the reason. The exit codes are in [Command line and exit codes](#command-line-and-exit-codes).

### Environment variable names

The name of an environment variable is `ELM_` followed by the setting's path in upper case, with `.` replaced by `_`. For example, `rateLimit.clientPerIp` is `ELM_RATELIMIT_CLIENTPERIP` and `publicUrls` is `ELM_PUBLICURLS`. A list is one value with its entries separated by `;`, for example `ELM_SIGNING_CERTIFICATES=/etc/elm/signing-1.cert;/etc/elm/signing-2.cert`.

These settings do not follow the rule:

| Setting | Environment variable | The variable holds |
| --- | --- | --- |
| `database.connectionFile` | `ELM_DATABASE_CONNECTION` | The connection string itself, not a file path. |
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE` | The signing key's passphrase itself, not a file path. An empty value is an empty passphrase. |
| `nonce.secretFile` | `ELM_NONCE_SECRET` | The nonce secret itself (base64), not a file path. |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET` | The web client's secret itself, not a file path. |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK` | The key encryption key itself (base64), not a file path. |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS`, or `ELM_TRUST_ROOT_KEYS` | `ELM_TRUST_ROOTKEYS` follows the rule and holds file paths. `ELM_TRUST_ROOT_KEYS` holds the root public keys as PEM text, and is read only when the files give no key. **Mind the second `_`:** the two names differ only by it. |
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES` | Scopes separated by spaces, not by `;`. |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP` | `value=role` pairs separated by `;`, such as `elm-owners=owner;elm-support=support`. A claim value that holds `;` is written in the file instead. |

## Top-level settings

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `listen` | `ELM_LISTEN` | URL | `http://127.0.0.1:8080` | Where the service listens. An `http` or `https` URL without path, query, or fragment. The host is an IP address, `localhost`, `0.0.0.0`, or `[::]`. `https` needs `tls.certificate` and `tls.key`. |
| `publicUrls` | `ELM_PUBLICURLS` | list of URLs | the `listen` URL, only when `listen` is a loopback address or `localhost` | The URLs that devices use to reach the service; a device's proof must name one of them. Each is an `https` URL without query or fragment. Required when `listen` is not a loopback address. Behind a proxy that ends TLS, these are `https` even when `listen` is `http`. With the admin UI's sign-in on, the admin UI is served at the first URL, which must be `https` with the path `/` (without `publicUrls`, at the loopback `listen` URL). |

## `tls`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | file path | none | The PEM certificate for an `https` `listen`. |
| `tls.key` | `ELM_TLS_KEY` | file path | none | The PEM private key of `tls.certificate`. |

## `database`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `database.provider` | `ELM_DATABASE_PROVIDER` | string | `sqlite` | `sqlite` or `postgres`. SQLite serves one node only; several nodes need PostgreSQL (15 to 18). |
| `database.connectionFile` | `ELM_DATABASE_CONNECTION` | file path | none (required) | The file that holds the connection string. A secret: see [Secrets](#secrets). |

The connection string is that of the database driver: Microsoft.Data.Sqlite for `sqlite`, Npgsql for `postgres`. For example (fake values):

```text
Data Source=/var/lib/elm-service/elm.db
Host=db.example.com;Port=5432;Database=elm;Username=elm;Password=example-password
```

## `signing`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `signing.key` | `ELM_SIGNING_KEY` | file path | none (required; `setup` does not need it) | The signing key: an encrypted PKCS#8 PEM file of a P-256 key. |
| `signing.certificates` | `ELM_SIGNING_CERTIFICATES` | list of file paths | none (required; `setup` does not need it) | The signing key's key certificate files: 1 to 4, none empty. The certificates must be valid now and allow signing licenses and leases, and logs too when `usageLog.directory` is set. |
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE` | file path | none (required when `ELM_SIGNING_PASSPHRASE` is not set; `setup` does not need it) | The file that holds the signing key's passphrase. A secret: see [Secrets](#secrets). |

## `trust`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` | list of file paths | none (required; `setup` does not need it) | PEM files of the vendor's root public keys, which the signing key's certificates are checked against. Instead of files, `ELM_TRUST_ROOT_KEYS` can hold the PEM text. Without either, the service does not start. |
| `trust.revokedKeyIds` | `ELM_TRUST_REVOKEDKEYIDS` | list of strings | none | Key IDs (`kid`) that the service no longer trusts. |

## `bootstrapOwner`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `bootstrapOwner.iss` | `ELM_BOOTSTRAPOWNER_ISS` | string | none | The OIDC issuer of the first `owner` administrator. 1 to 255 characters. Set both `iss` and `sub`, or neither. With `oidc.issuer` set, it must equal `oidc.issuer`. |
| `bootstrapOwner.sub` | `ELM_BOOTSTRAPOWNER_SUB` | string | none | The subject (`sub`) of the first `owner` administrator at that issuer. 1 to 255 characters. |

`elm-service setup` needs `bootstrapOwner`, and makes its API keys for this administrator.

- With OIDC (`oidc.issuer` set): `iss` is the identity provider's issuer and `sub` is the subject that it gives the person who is the first `owner`. When that person signs in, they get the `owner` role whatever `oidc.roleMap` says.
- Without OIDC: nobody signs in, and the first `owner` works with the API keys that `setup` makes. `iss` and `sub` only name that administrator, and any text of 1 to 255 characters works, such as `local` and `owner`. If you may turn on OIDC later, use the identity provider's issuer and the person's subject there from the start: `bootstrapOwner.iss` must then equal `oidc.issuer`, and the person with that `sub` gets the `owner` role at sign-in.

## `nonce`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `nonce.secretFile` | `ELM_NONCE_SECRET` | file path | none (required; `setup` does not need it) | The file that holds the nonce secret: at least 32 random bytes in base64. Every node holds the same value. There is no default and the service does not make one. A secret: see [Secrets](#secrets). |

## `rateLimit`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `rateLimit.perIp` | `ELM_RATELIMIT_PERIP` | whole number | `20` | Requests per second from one source (an IPv4 address, or an IPv6 /64) to every path other than the device endpoints, before authentication. 1 to 10000. |
| `rateLimit.perKey` | `ELM_RATELIMIT_PERKEY` | whole number | `10` | Administration API requests per second of one API key, or of one administrator for an OIDC access token or an admin UI session. 1 to 10000. |
| `rateLimit.clientPerIp` | `ELM_RATELIMIT_CLIENTPERIP` | whole number | `20` | Requests per second from one source to the device endpoints (activations, trials, nonces, offline files, and leases), before authentication. 1 to 10000. |
| `rateLimit.clientPerKey` | `ELM_RATELIMIT_CLIENTPERKEY` | whole number | `2` | Device requests per second of one installation key. 1 to 10000. |

## `audit`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `audit.retentionDays` | `ELM_AUDIT_RETENTIONDAYS` | whole number | none: every row is kept | Days to keep audit log rows. 1 to 36600. Older rows are deleted every hour. |

## `usageLog`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `usageLog.directory` | `ELM_USAGELOG_DIRECTORY` | folder path | none: no usage log is written | The folder of this node's usage log. The service must be able to write it, and the signing key's certificate must allow signing logs: make it with [`elm key certify`](elm-cli.md#elm-key-certify) `--use log` (with the other uses, such as `--use license,lease,log`). |

## `node`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `node.name` | `ELM_NODE_NAME` | string | the host name, with characters other than `A-Z`, `a-z`, `0-9`, and `-` made `-`, cut to 64 characters (`node` when nothing is left) | This node's name: 1 to 64 characters of `A-Z`, `a-z`, `0-9`, and `-`. It is part of the usage log's file names. Nodes that share a usage log folder need different names. |

## `oidc`

The identity provider whose access tokens the administration API accepts. Without `oidc.issuer`, the administration API accepts API keys only, and no other `oidc` setting may be set.

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `oidc.issuer` | `ELM_OIDC_ISSUER` | URL | none: OIDC is off | The issuer. An `https` URL (or `http` on a loopback host) without query, fragment, or user information, up to 2048 characters. Compared with the tokens' `iss` as text. When set, `oidc.audience`, `oidc.cliClientId`, and `oidc.roleMap` are required. |
| `oidc.audience` | `ELM_OIDC_AUDIENCE` | string | none | The value that an access token's `aud` must hold. 1 to 255 characters. Must differ from `oidc.cliClientId`. |
| `oidc.cliClientId` | `ELM_OIDC_CLICLIENTID` | string | none | The client ID that `elm admin login` uses: a public client without a secret. 1 to 255 characters. |
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES` | list of strings | `openid`, `offline_access` | The scopes that `elm admin login` asks for. 1 to 20 scopes, each 1 to 100 printable ASCII characters without spaces, `"`, or `\`. |
| `oidc.roleClaim` | `ELM_OIDC_ROLECLAIM` | string | `groups` | The token claim whose values name roles. 1 to 255 characters. |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP` | object of strings | none | Claim values and the roles they give. 1 to 100 entries. A value is 1 to 255 characters; a role is `owner`, `admin`, `support`, or `viewer`. In the environment variable, each value appears once. |
| `oidc.webClientId` | `ELM_OIDC_WEBCLIENTID` | string | none: the admin UI's sign-in is off | The confidential client of the admin UI's sign-in. 1 to 255 characters. Needs `oidc.issuer` and the web client's secret. Must differ from `oidc.cliClientId` and from `oidc.audience`. |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET` | file path | none | The file that holds the web client's secret: 1 to 1024 printable ASCII characters without spaces. Required with `oidc.webClientId`. A secret: see [Secrets](#secrets). |

For the admin UI's sign-in, register the web client at the identity provider with these URLs, where `<origin>` is the scheme, host, and port of the first `publicUrls` entry (such as `https://activation.example.com`), or of the loopback `listen` URL without `publicUrls`:

| At the identity provider | URL |
| --- | --- |
| Redirect URI (callback) | `<origin>/auth/callback` |
| Post-logout redirect URI | `<origin>/` |

The sign-in uses the authorization code flow with PKCE and asks for the scope `openid` only. At sign-out, the admin UI sends the browser to the identity provider's `end_session_endpoint` with `client_id` and `post_logout_redirect_uri`, when the provider's discovery document has that endpoint.

## `apiKeys`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `apiKeys.inactiveAdminDays` | `ELM_APIKEYS_INACTIVEADMINDAYS` | whole number | `90` | Days without a sign-in after which an administrator's API keys stop working. 0 to 3650; `0` turns the rule off. Applies only when `oidc.issuer` is set. |

## `dataProtection`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK` | file path | none | The file that holds the key encryption key of the admin UI's sign-in: 32 bytes in base64. Required with `oidc.webClientId`, and not allowed without it. A secret: see [Secrets](#secrets). |

## `session`

The admin UI's sessions. Set these only with `oidc.webClientId`.

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `session.idleMinutes` | `ELM_SESSION_IDLEMINUTES` | whole number | `30` | Minutes without a request after which a session ends. 5 to 120. |
| `session.maxHours` | `ELM_SESSION_MAXHOURS` | whole number | `12` | The longest a session lasts, in hours. 1 to 24. |

## Secrets

A secret is given in an environment variable that holds the value, or in a file that a setting names. The configuration file names the file but never holds the secret itself, and a secret does not belong in a container image. When both are given, the environment variable wins.

| Secret | Environment variable (the value) | Setting (a file) | Form | Required |
| --- | --- | --- | --- | --- |
| Database connection string | `ELM_DATABASE_CONNECTION` | `database.connectionFile` | The connection string of `database.provider` | Always |
| Signing key's passphrase | `ELM_SIGNING_PASSPHRASE` | `signing.passphraseFile` | The passphrase of the encrypted `signing.key` | For the service; not for `setup` |
| Nonce secret | `ELM_NONCE_SECRET` | `nonce.secretFile` | At least 32 random bytes in base64; the same on every node | For the service; not for `setup` |
| Web client's secret | `ELM_OIDC_WEBCLIENTSECRET` | `oidc.webClientSecretFile` | 1 to 1024 printable ASCII characters without spaces | With `oidc.webClientId` |
| Key encryption key | `ELM_DATAPROTECTION_KEK` | `dataProtection.kekFile` | 32 bytes in base64; the same on every node | With `oidc.webClientId` |

- A secret file is at most 16 KiB and must not be empty. Line breaks at its end are removed.
- The signing key itself is a file (`signing.key`), encrypted with the passphrase above. Its key certificates (`signing.certificates`) and the root public keys (`trust.rootKeys`) are public and are not secrets.
- The CLI client (`oidc.cliClientId`) has no secret.

## Example

```json
{
  "listen": "http://0.0.0.0:8080",
  "publicUrls": ["https://activation.example.com/"],
  "database": {
    "provider": "postgres",
    "connectionFile": "/run/secrets/elm-database-connection"
  },
  "signing": {
    "key": "/etc/elm-service/signing-key.pem",
    "certificates": ["/etc/elm-service/signing-key.cert"],
    "passphraseFile": "/run/secrets/elm-signing-passphrase"
  },
  "trust": {
    "rootKeys": ["/etc/elm-service/acme-root.pem"]
  },
  "nonce": {
    "secretFile": "/run/secrets/elm-nonce-secret"
  },
  "usageLog": {
    "directory": "/var/lib/elm-service/usage"
  },
  "node": {
    "name": "node-1"
  }
}
```
