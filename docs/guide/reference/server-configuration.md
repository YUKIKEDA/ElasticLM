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
| 1 | The server cannot start. For example: there is no server license yet (the message gives the installation key's thumbprint and the machine fingerprints to send to the vendor), no root public keys are given, a file that a setting names (a root key, the server license, a TLS certificate) cannot be read or is not valid, the server license does not verify, the server license cannot be written while `service.url` is set, the borrow record is broken or cannot be read or written, or the usage log cannot be written. |
| 2 | An unknown argument, `--config` without a path, or a setting error: the configuration file does not exist, cannot be read, is larger than 64 KiB, or is not valid; a value is not valid; or `product` is missing. |

Before it exits with 1 or 2, `elm-server` prints `error:` and the reason.

## How settings are given

- Settings come from a JSON configuration file, from environment variables, or from both. Pass the file with `--config`:

  ```text
  elm-server --config /etc/elm-server/settings.json
  ```

- Without `--config`, `elm-server` reads environment variables only.
- An environment variable overrides the same setting in the file. An environment variable that is set to an empty value counts as not set.
- The file is at most 64 KiB. A key that the file does not know (for example a misspelt one) is an error. Key names are case-sensitive.
- A setting error stops `elm-server` before it starts: it prints `error:` and the reason. The exit codes are in [Command line and exit codes](#command-line-and-exit-codes).

### Environment variable names

The name of an environment variable is `ELM_` followed by the setting's path in upper case, with `.` replaced by `_`. For example, `rateLimit.perIp` is `ELM_RATELIMIT_PERIP` and `publicUrls` is `ELM_PUBLICURLS`. A list is one value with its entries separated by `;`, for example `ELM_ALLOW=10.0.0.0/8;192.168.1.0/24`.

Two environment variables do not follow this rule:

| Environment variable | Holds |
| --- | --- |
| `ELM_TRUST_ROOT_KEYS` | The vendor's root public keys as PEM text, not file paths. It is read only when `trust.rootKeys` (in the file, or as `ELM_TRUST_ROOTKEYS`) names no files. |
| `ELM_TLS_PASSWORD` | The password of a PKCS#12 file named by `tls.certificate`. It has no setting in the configuration file. |

## Top-level settings

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `product` | `ELM_PRODUCT` | string | none (required) | The product ID of the server license. 1 to 64 characters of lowercase letters, digits, and `-`. It also names the default `dataDirectory`. |
| `listen` | `ELM_LISTEN` | URL | `https://0.0.0.0:7070` | Where the server listens for clients. An `https` URL without query or fragment. The host is an IP address, `localhost`, `0.0.0.0`, or `[::]`. |
| `publicUrls` | `ELM_PUBLICURLS` | list of URLs | built from `listen` | The URLs that clients use to reach the server; a client's proof must name one of them. Each is an `https` URL without query or fragment. When left out: for a `listen` host of `0.0.0.0` or `[::]`, `https://<host name>:<port>/` and `https://localhost:<port>/`; otherwise the `listen` URL. |
| `license` | `ELM_LICENSE` | file path | `server.elmlic` in the folder of the `elm-server` executable | The server license file. With `service.url` set, the server must be able to write this file. |
| `allow` | `ELM_ALLOW` | list of strings | none | Entries that allow lease requests. See [Access rules](#access-rules). |
| `deny` | `ELM_DENY` | list of strings | none | Entries that refuse lease requests. See [Access rules](#access-rules). |
| `dataDirectory` | `ELM_DATADIRECTORY` | folder path | `%ProgramData%\ElasticLM\<product>` on Windows, `/Library/Application Support/ElasticLM/<product>` on macOS, `/var/lib/elasticlm/<product>` on Linux | The folder of the server's installation key, clock state, borrow records, administrator token, and (by default) usage log. When an app of the same product runs on the same machine, give the server a folder of its own. |

### Access rules

An `allow` or `deny` entry is one of:

| Entry | Matches |
| --- | --- |
| An IP address, such as `192.168.1.20` | That address. |
| A CIDR range, such as `10.0.0.0/8` | Every address in the range. |
| `host:<name>` | The host name that the client sends. Case-insensitive. |
| `user:<name>` | The OS user name that the client sends. Case-sensitive. |

- `allow` and `deny` hold at most 1,000 entries together. A `host:` or `user:` name is 1 to 255 characters.
- IP entries apply to every lease request. `host:` and `user:` entries apply only to new leases, and the client sends a host and user name only when it is set to send them (`ClientInfoStrategy: HostAndUser`). The client states these names itself; the server cannot check them.
- Within each kind (IP, host, user), a request is refused when a `deny` entry matches it, or when `allow` entries of that kind exist and none matches. A new lease without a host or user name matches no `host:` or `user:` entry.
- A refused request gets `403` with `ELM_FORBIDDEN`.

## `rateLimit`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `rateLimit.perIp` | `ELM_RATELIMIT_PERIP` | whole number | `20` | Requests per second from one source (an IPv4 address, or an IPv6 /64). 1 to 10000. A request over the limit gets `429` with `ELM_RATE_LIMITED`. `elm-server` does not read `X-Forwarded-For`: behind a reverse proxy or NAT, every client behind it shares one source. |

## `trust`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` | list of file paths | none (required) | PEM files of the vendor's root public keys, which the server license's key certificate is checked against. Instead of files, `ELM_TRUST_ROOT_KEYS` can hold the PEM text. Without either, the server does not start. |
| `trust.revokedKeyIds` | `ELM_TRUST_REVOKEDKEYIDS` | list of strings | none | Key IDs (`kid`) that the server no longer trusts. |

## `tls`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | file path | none: the server makes a self-signed certificate with its installation key at each start | The server's TLS certificate. With `tls.key` set, a PEM certificate; without it, a PKCS#12 file whose password is in `ELM_TLS_PASSWORD`. The certificate must carry its private key. |
| `tls.key` | `ELM_TLS_KEY` | file path | none | The PEM private key of a PEM `tls.certificate`. |

## `borrow`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `borrow.maxDuration` | `ELM_BORROW_MAXDURATION` | duration | the limit in the license | The longest borrow this server grants. The form is `d.hh:mm:ss`, such as `7.00:00:00`. From 1 second to 100 years. |
| `borrow.reserve` | `ELM_BORROW_RESERVE` | whole number | `0` | Seats of each feature that are kept from borrowing. 0 to 1000000. |

## `log`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `log.usage` | `ELM_LOG_USAGE` | file path | `usage.jsonl` | The usage log. Its folder and its name without the extension name the daily files (`usage-<UTC date and time>.jsonl` for the default). A relative path is under `dataDirectory`. It must name a file. |

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
