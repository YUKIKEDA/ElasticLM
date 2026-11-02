---
sidebar_position: 2
---

# elm-server configuration

This page lists every setting of `elm-server`, the on-premises license server that hands out floating license seats on a customer's network. It is for the operator who installs and runs the server. For what the server does and how seats, leases, and borrowing work, read [Floating licenses](../explanation/floating-licenses.md) first.

## Command line and exit codes

```text
elm-server [--config <settings.json>] [--set-aside-broken-records]
```

| Argument | Meaning |
| --- | --- |
| `--config <path>` | The JSON configuration file. Without it, the settings come from environment variables only. See [How settings are given](#how-settings-are-given). |
| `--set-aside-broken-records` | Starts even when the newest borrow record does not verify. The broken file is renamed `borrows-broken-<UTC time>.jsonl`, and the server starts without the borrows in it: they are not counted, so their seats can be lent again before those borrows end. Without this argument, a broken borrow record stops the start with exit code 1. The borrow record is described in [Logs](logs.md). |
| `--help`, `-h` | Prints the usage and exits with 0. |

Any other argument, or `--config` without a path, is an error.

| Exit code | Meaning |
| --- | --- |
| 0 | The server stopped normally (Ctrl+C, `SIGTERM`, or a service stop), or `--help` printed the usage. |
| 1 | The server cannot start. The reasons are listed below. |
| 2 | An unknown argument, `--config` without a path, or a setting error: the configuration file does not exist, cannot be read, is larger than 64 KiB, or is not valid; a value is not valid; or `product` is missing. |

Before it exits with 1 or 2, `elm-server` prints `error:` and the reason to standard error.

Exit code 1 means, for example:

- There is no server license yet. This is what happens at the first start: the server makes its installation key, and the message gives the key's thumbprint and the machine fingerprints. Send them to the vendor, who issues the server license; or activate the server license with the vendor's license key, online with [`elm server activate`](elm-cli.md#elm-server-activate), or offline with [`elm server activate-request`](elm-cli.md#elm-server-activate-request) and [`elm server import`](elm-cli.md#elm-server-import). [Floating licenses](../explanation/floating-licenses.md#running-elm-server) explains what the server needs from the vendor.
- No root public keys are given.
- A file that a setting names (a root key, the server license, a TLS certificate) cannot be read or is not valid.
- The server license does not verify.
- The server license cannot be written while `service.url` is set.
- The borrow record is broken, or cannot be read or written.
- The usage log cannot be written.

## How settings are given

- Settings come from a JSON configuration file, from environment variables, or from both. Pass the file with `--config`:

  ```text
  elm-server --config /etc/elm-server/settings.json
  ```

- Without `--config`, `elm-server` reads environment variables only.
- An environment variable overrides the same setting in the file. An environment variable that is set to an empty value counts as not set. The one exception is `ELM_TLS_PASSWORD`: an empty value is used as an empty password.
- The file is at most 64 KiB. A key that the file does not know (for example a misspelt one) is an error. Key names are case-sensitive.
- A relative path, in `--config` or in a setting (`license`, `dataDirectory`, `trust.rootKeys`, `tls.certificate`, `tls.key`), is relative to the working folder of the `elm-server` process: the folder it is started in. It is not relative to the folder of the configuration file or of the executable. The one exception is `log.usage`, whose relative path is under `dataDirectory`.
- A setting error stops `elm-server` before it starts: it prints `error:` and the reason. The exit codes are in [Command line and exit codes](#command-line-and-exit-codes).
- The server reads its settings only when it starts. [`elm server reload`](elm-cli.md#elm-server-reload) makes a running server read the server license file again, from the same `license` path, and nothing else. A change to any setting takes effect at the next start.
- A duration, such as `borrow.maxDuration`, has the form `d.hh:mm:ss`: days, a dot, then hours, minutes, and seconds. `7.00:00:00` is 7 days, and `0.12:00:00` is 12 hours. The days may be left out: `12:00:00` is also 12 hours.

### Environment variable names

The name of an environment variable is `ELM_` followed by the setting's path in upper case, with `.` replaced by `_`. For example, `rateLimit.perIp` is `ELM_RATELIMIT_PERIP` and `publicUrls` is `ELM_PUBLICURLS`. A list is one value with its entries separated by `;`, for example `ELM_ALLOW=10.0.0.0/8;192.168.1.0/24`.

Two environment variables do not follow this rule:

| Environment variable | Holds |
| --- | --- |
| `ELM_TRUST_ROOT_KEYS` | The vendor's root public keys as PEM text, not file paths. It is read only when `trust.rootKeys` (in the file, or as `ELM_TRUST_ROOTKEYS`) names no files. |
| `ELM_TLS_PASSWORD` | The password of a PKCS#12 file named by `tls.certificate`. It has no setting in the configuration file. An empty value is an empty password. |

**Two names for the root keys:** `ELM_TRUST_ROOTKEYS` holds file paths, as `trust.rootKeys` does. `ELM_TRUST_ROOT_KEYS`, with a `_` between `ROOT` and `KEYS`, holds the PEM text itself.

## Ports

| Setting | Default | For |
| --- | --- | --- |
| `listen` | `https://0.0.0.0:7070`: port 7070 on every IPv4 address | Clients: the lease protocol, over TLS. |
| `admin.listen` | `http://127.0.0.1:7071`: port 7071 on the loopback address | The local administration API, which the `elm server` commands use. It answers only on the server's machine. |

## Top-level settings

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `product` | `ELM_PRODUCT` | string | none (required) | The product ID of the server license. 1 to 64 characters of lowercase letters, digits, and `-`. It also names the default `dataDirectory`. |
| `listen` | `ELM_LISTEN` | URL | `https://0.0.0.0:7070` | Where the server listens for clients. An `https` URL without query or fragment. The host is an IP address, `localhost`, `0.0.0.0`, or `[::]`. |
| `publicUrls` | `ELM_PUBLICURLS` | list of URLs | built from `listen` | The URLs that clients use to reach the server. Each is an `https` URL without query or fragment. See the notes below. |
| `license` | `ELM_LICENSE` | file path | `server.elmlic` in the folder of the `elm-server` executable | The server license file. With `service.url` set, the server must be able to write this file. See the notes below. |
| `allow` | `ELM_ALLOW` | list of strings | none | Entries that allow lease requests. See [Access rules](#access-rules). |
| `deny` | `ELM_DENY` | list of strings | none | Entries that refuse lease requests. See [Access rules](#access-rules). |
| `dataDirectory` | `ELM_DATADIRECTORY` | folder path | `%ProgramData%\ElasticLM\<product>` on Windows, `/Library/Application Support/ElasticLM/<product>` on macOS, `/var/lib/elasticlm/<product>` on Linux | The server's data folder. See [Data folder](#data-folder). |

Notes:

- `publicUrls`: a client's proof names the URL that the client sent it to, and the server accepts only a proof that names one of these URLs. When clients reach the server under a name other than the machine's host name, such as a DNS alias, put that URL here. When left out: for a `listen` host of `0.0.0.0` or `[::]`, `https://<host name>:<port>/` and `https://localhost:<port>/`; otherwise the `listen` URL.
- `license`: the folder of the executable, such as one under `Program Files` or `/usr/local/bin`, is often not writable. Set `license` to a file kept apart from the executable, such as `server.elmlic` in the data folder. The commands that write the server license file, [`elm server activate`](elm-cli.md#elm-server-activate), [`elm server refresh`](elm-cli.md#elm-server-refresh), and [`elm server import`](elm-cli.md#elm-server-import), require its path in `--license` and do not read the server's settings: give them the same path as `license`.
- `dataDirectory`: an app of the same product that keeps its installation key for the whole machine uses the same default folder on that machine. Give the server a folder of its own.

### Data folder

`elm-server` keeps these files in its data folder. Back up the whole folder, and let only the server's user read it.

| File | Holds |
| --- | --- |
| `installation.key` | The server's installation key, made at the first start. The server license is bound to it, so a new key needs a new server license. On Windows it is protected with DPAPI. |
| `clock` | The clock state, signed with the installation key, with which the server notices a clock that was set back. |
| `activation.json` | The activation that `elm server activate` or `elm server import` got from `elm-service`, which renews the server license. It exists only after one of those commands. `activation.lock` beside it is a lock file. |
| `admin.token` | The administrator token that the `elm server` commands send to the administration API. Whoever can read it can administer the server. |
| `borrows-<UTC time>.jsonl` | The borrow record. See [Logs](logs.md#borrow-record-elm-server). |
| `usage-<UTC time>.jsonl` | The usage log, unless `log.usage` names another folder. See [Logs](logs.md#usage-log). |

The server creates the folder so that only its own user can open it: on Linux and macOS with mode `0700`; on Windows with an access list of the server's user, Administrators, and SYSTEM. It does not use an existing folder that other users can read or change; it stops with a message that says what to fix.

### Access rules

An `allow` or `deny` entry is one of:

| Entry | Matches |
| --- | --- |
| An IP address, such as `192.168.1.20` | That address. |
| A CIDR range, such as `10.0.0.0/8` | Every address in the range. |
| `host:<name>` | The host name that the client sends. Case-insensitive. |
| `user:<name>` | The OS user name that the client sends. Case-sensitive. |

- `allow` and `deny` hold at most 1,000 entries together. A `host:` or `user:` name is 1 to 255 characters.
- IP entries apply to every lease request. `host:` and `user:` entries apply only to new leases. A client sends its host name and OS user name only when the app turns this on (`SendClientInfo` in `LicenseClientOptions`; in C, `send_client_info`), which is off by default. The client states these names itself; the server cannot check them (see [Floating licenses](../explanation/floating-licenses.md#what-the-customers-administrator-can-and-cannot-do)).
- Within each kind (IP, host, user), a request is refused when a `deny` entry matches it, or when `allow` entries of that kind exist and none matches. A new lease without a host or user name matches no `host:` or `user:` entry.
- A refused request gets `403` with `ELM_FORBIDDEN`.

## `rateLimit`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `rateLimit.perIp` | `ELM_RATELIMIT_PERIP` | whole number | `20` | Requests per second from one source (an IPv4 address, or an IPv6 /64). 1 to 10000. A request over the limit gets `429` with `ELM_RATE_LIMITED`. `elm-server` does not read `X-Forwarded-For`: behind a reverse proxy or NAT, every client behind it shares one source. |

## `trust`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` | list of file paths | none (required) | PEM files of the vendor's root public keys, which the server license's key certificate is checked against. Instead of files, `ELM_TRUST_ROOT_KEYS` (note the second `_`) can hold the PEM text. Without either, the server does not start. |
| `trust.revokedKeyIds` | `ELM_TRUST_REVOKEDKEYIDS` | list of strings | none | Key IDs (`kid`) that the server no longer trusts. |

## `tls`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | file path | none: the server makes a self-signed certificate with its installation key at each start | The server's TLS certificate. With `tls.key` set, a PEM certificate; without it, a PKCS#12 file whose password is in `ELM_TLS_PASSWORD` (not set: no password). The certificate must carry its private key. |
| `tls.key` | `ELM_TLS_KEY` | file path | none | The PEM private key of a PEM `tls.certificate`. |

## `borrow`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `borrow.maxDuration` | `ELM_BORROW_MAXDURATION` | duration | the limit in the license | The longest borrow this server grants, as `d.hh:mm:ss` (days, then hours, minutes, and seconds): `7.00:00:00` is 7 days. From 1 second to 100 years. |
| `borrow.reserve` | `ELM_BORROW_RESERVE` | whole number | `0` | Seats of each feature that are kept from borrowing. 0 to 1000000. |

## `log`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `log.usage` | `ELM_LOG_USAGE` | file path | `usage.jsonl` | The usage log. Its folder and its name without the extension name its files, and a new file starts at each start of the server and each UTC day (`usage-<UTC date and time>.jsonl` for the default). A relative path is under `dataDirectory`. It must name a file. |

## `admin`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `admin.listen` | `ELM_ADMIN_LISTEN` | URL | `http://127.0.0.1:7071` | Where the local administration API listens. An `http` URL on a loopback address (or `localhost`), without path or query. |

## `service`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `service.url` | `ELM_SERVICE_URL` | URL | none: the server license is not refreshed on its own | The `elm-service` that refreshes the server license. An `https` URL, or `http` on a loopback address. When set, the server checks at start that it can write the `license` file. |

## Example

```json
{
  "product": "acme-cad",
  "listen": "https://0.0.0.0:7070",
  "publicUrls": ["https://licenses.example.com:7070/"],
  "license": "/etc/elm-server/server.elmlic",
  "trust": {
    "rootKeys": ["/etc/elm-server/acme-root.pem"]
  },
  "allow": ["10.0.0.0/8"],
  "rateLimit": {
    "perIp": 50
  },
  "borrow": {
    "maxDuration": "7.00:00:00",
    "reserve": 2
  }
}
```
