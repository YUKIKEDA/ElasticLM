---
sidebar_position: 2
---

# elm-server configuration

This page lists every setting of `elm-server`, the on-premises license server that hands out floating license seats on a customer's network. It is for the operator who installs and runs the server. For what the server does and how seats, leases, and borrowing work, read [Floating licenses](../explanation/floating-licenses.md) first.

On Windows, `elm-server` and the `elm server` commands need Windows 10 version 1803 or Windows Server 2019, or later. The server's local administration API listens on a Unix domain socket: a file through which programs on the same machine connect, with no network port (see [Ports](#ports)). Windows has Unix domain sockets too, from those versions on. This page calls the file the socket file.

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
| 2 | An unknown argument, `--config` without a path, or a setting error: the configuration file does not exist, cannot be read, is larger than 64 KiB, or is not valid; a value is not valid; `product` is missing; or [`admin.listen`](#admin), which is not a setting, has a value. |

Before it exits with 1 or 2, `elm-server` prints `error:` and the reason to standard error.

Exit code 1 means, for example:

- There is no server license yet. This is what happens at the first start: the server makes its installation key, and the message gives the key's [thumbprint](../explanation/devices-and-proofs.md#the-installation-key) and the machine fingerprints. Send them to the vendor, who issues the server license; or activate the server license with the vendor's license key, online with [`elm server activate`](elm-cli.md#elm-server-activate), or offline with [`elm server activate-request`](elm-cli.md#elm-server-activate-request) and [`elm server import`](elm-cli.md#elm-server-import). [Floating licenses](../explanation/floating-licenses.md#running-elm-server) explains what the server needs from the vendor.
- The path of the socket file, `admin.sock` in the data folder, is longer than the system allows. The message is `error: The path of the administration socket '<path>' is <length> bytes in UTF-8, and this system allows <limit>. Use a data folder with a shorter path.` The server checks this before it reads the root keys or creates the data folder, so such a start leaves nothing. See [Data folder](#data-folder).
- The system has no Unix domain sockets, as on a Windows older than the versions at the top of this page: `error: This system has no Unix domain sockets, which the server's administration API needs. On Windows, Windows 10 version 1803 or Windows Server 2019, or later, is required.` The server checks this at the same point, after the length of the path.
- No root public keys are given.
- Another `elm-server` runs on the same data folder: `error: Another elm-server is running on the data folder '<folder>': it holds the lock file '<path>'.` Stop that server, or give this one a `dataDirectory` of its own. On Windows the same line appears while another program, such as a backup or an antivirus scan, has `server.lock` open: start the server again, and exclude `server.lock` from that program ([Data folder](#data-folder) lists the files to exclude). Where the file lock does not hold (see [Data folder](#data-folder)), a second server stops instead with `error: Another elm-server is running on the data folder '<folder>': its administration socket '<path>' accepts connections.`
- The lock file `server.lock` cannot be opened, for a reason other than another program holding it: a missing permission, a full disk, or a read-only file system, for example. `error: The lock file '<path>' cannot be opened: <reason>`. The reason names the cause: check that the server's user may write in the data folder, and that `server.lock` is a file.
- What is at the path of `admin.sock` can be neither used nor removed, such as a folder of that name: `error: The administration socket '<path>' cannot be used: <reason>`. Remove what is at that path, and start the server again.
- A file that a setting names (a root key, the server license, a TLS certificate) cannot be read or is not valid.
- The TLS certificate has an Extended Key Usage, the list of purposes the certificate may be used for, that does not include server authentication (`1.3.6.1.5.5.7.3.1`; OpenSSL shows it as `TLS Web Server Authentication`, Windows as `Server Authentication`).
- A certificate after the server's in the TLS certificate file is not a CA certificate. The message names its place in the file and its subject: `The TLS certificate '<file>' cannot be used by a server: its certificate 2 (CN=…) is not a CA certificate.`
- On Windows, the server did not send an intermediate CA certificate of the TLS certificate file, or could not check that it sends them. [Intermediate CA certificates](#tls) gives the two messages and what to do.
- The server license does not verify. The message after `error:` begins with the error code of the check that failed. For example, a license bound to another installation key gives `error: ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to another installation key. This server's installation key is <thumbprint>.` [Error codes](error-codes.md) lists every code.
- The server license cannot be written while `service.url` is set.
- The borrow record is broken, or cannot be read or written.
- The usage log cannot be written.
- The administrator token cannot be written: `error: The administrator token in '<folder>' cannot be created or read.` On Windows this also happens when another program keeps `admin.token` open for longer than 2 seconds while the server starts: start the server again, and if it happens again, exclude `admin.token` from the program that opens it, such as an antivirus scan.
- The server cannot listen on the `listen` address or on the socket file: another program uses the port, the server has no permission to listen there, this machine does not have the address, or the file system of the data folder cannot hold a socket file. The last line is `error: elm-server cannot listen on <listen> and <socket path>: <reason>`. It gives the `listen` URL as the server reads it, ending in `/`, and the absolute path of `admin.sock`.

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

The local administration API, which the `elm server` commands use, has no port and no setting. The server listens for it on the socket file `admin.sock` in its [data folder](#data-folder), on Windows, Linux, and macOS alike, with plain `http` over that socket. The connection never leaves the machine, so it is not encrypted; the data folder's permissions and the administrator token protect it (see [Security model](../explanation/security-model.md#for-the-customers-administrator)). Only a process on the server's machine that may open the data folder can connect. See [`elm-server` local administration API](administration-api.md#elm-server-local-administration-api).

## Top-level settings

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `product` | `ELM_PRODUCT` | string | none (required) | The product ID of the server license. 1 to 64 characters of lowercase letters, digits, and `-`. It also names the default `dataDirectory`. |
| `listen` | `ELM_LISTEN` | URL | `https://0.0.0.0:7070` | Where the server listens for clients. An `https` URL without query or fragment. The host is an IP address, `localhost`, `0.0.0.0`, or `[::]`. |
| `publicUrls` | `ELM_PUBLICURLS` | list of URLs | built from `listen` | The URLs that clients use to reach the server. Each is an `https` URL without query or fragment. See the notes below. |
| `license` | `ELM_LICENSE` | file path | `server.elmlic` in the folder of the `elm-server` executable | The server license file. With `service.url` set, the server must be able to write this file and to create files in its folder, since it writes a temporary file beside it. See the notes below. |
| `allow` | `ELM_ALLOW` | list of strings | none | Entries that allow lease requests. See [Access rules](#access-rules). |
| `deny` | `ELM_DENY` | list of strings | none | Entries that refuse lease requests. See [Access rules](#access-rules). |
| `dataDirectory` | `ELM_DATADIRECTORY` | folder path | `%ProgramData%\ElasticLM\<product>` on Windows, `/Library/Application Support/ElasticLM/<product>` on macOS, `/var/lib/elasticlm/<product>` on Linux | The server's data folder. Its path has a limit, and it belongs on a local file system. See [Data folder](#data-folder). |

Notes:

- `publicUrls`: a client's proof names the URL that the client sent it to, and the server accepts only a proof that names one of these URLs. When clients reach the server under a name other than the machine's host name, such as a DNS alias, put that URL here. When left out: for a `listen` host of `0.0.0.0` or `[::]`, `https://<host name>:<port>/` and `https://localhost:<port>/`; otherwise the `listen` URL.
- `license`: the folder of the executable, such as one under `Program Files` or `/usr/local/bin`, is often not writable. Set `license` to a file kept apart from the executable, such as `server.elmlic` in the data folder. The commands that write the server license file, [`elm server activate`](elm-cli.md#elm-server-activate), [`elm server refresh`](elm-cli.md#elm-server-refresh), and [`elm server import`](elm-cli.md#elm-server-import), require its path in `--license` and do not read the server's settings: give them the same path as `license`.
- `dataDirectory`: an app of the same product that keeps its installation key for the whole machine uses the same default folder on that machine. Give the server a folder of its own.

### Data folder

`elm-server` keeps these files in its data folder. Back up the folder without `admin.sock`, `server.lock`, and `admin.token`: the first two hold nothing, and the server makes a new token at every start. Exclude the same three files from an antivirus scan of the folder. On Windows, a program that reads `admin.sock` or `server.lock` while the server runs gets an error.

| File | Holds |
| --- | --- |
| `installation.key` | The server's installation key, made at the first start. The server license is bound to it, so a new key needs a new server license. On Windows it is protected with DPAPI. |
| `clock` | The clock state, signed with the installation key, with which the server notices a clock that was set back. |
| `activation.json` | The activation that `elm server activate` or `elm server import` got from `elm-service`, which renews the server license. It exists only after one of those commands. `activation.lock` beside it is a lock file. |
| `admin.token` | The administrator token that the `elm server` commands send to the administration API. The server makes a new token at every start and replaces the file, so the token of an earlier start is refused; the file stays after the server stops. Whoever can read it can administer the server. |
| `admin.sock` | The socket file of the local administration API. It is there while the server runs: the server removes it when it stops, and a server that was killed leaves it, for the next start to remove. It holds no secret. |
| `server.lock` | The lock file, empty, that the server holds open while it runs, so that a second `elm-server` does not start on the same folder. It stays after the server stops. Do not delete it while the server runs. |
| `borrows-<UTC time>.jsonl` | The borrow record. See [Logs](logs.md#borrow-record-elm-server). |
| `usage-<UTC time>.jsonl` | The usage log, unless `log.usage` names another folder. See [Logs](logs.md#usage-log). |

The data folder and `admin.token` are readable only by the server's user and root on Linux and macOS, and only by the server's user, the Administrators group, and SYSTEM on Windows. Here the server's user is the user who started the server when it created the folder. The server creates the folder that way: on Linux and macOS with mode `0700`, and `admin.token` with mode `0600`; on Windows with an access list of those three, and `admin.token` protected with DPAPI for the machine, not for one user. Only the same users can connect to `admin.sock`. The server does not use an existing folder that users other than these own, can read, or can change; it stops with a message that says what to fix.

Keep the data folder on a local file system. The file lock on `server.lock` keeps a second server off the folder. On Linux and macOS, that lock holds only where the file system has file locks, and some network file systems have none. On Linux and macOS it does not hold either when the environment variable `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is `1` or `true` in the server's environment; leave it unset. Where the lock does not hold, two servers started on one folder at almost the same moment can both run, and `elm server deactivate` can then take a running server for a stopped one.

The absolute path of `admin.sock` is at most 107 bytes in UTF-8 on Windows and Linux, and 103 on macOS. The name and its separator take 11 of them, so the data folder's own absolute path is at most 96 bytes on Windows and Linux, and 92 on macOS; a character outside ASCII takes 2 to 4 bytes. A relative `dataDirectory` is made absolute from the working folder first. With a longer path, the server does not start and exits with 1; the `elm server` commands stop with the same message and exit with 2. The default folder is within the limit on Windows and Linux for every product ID. On macOS it is within the limit only for a product ID of up to 53 characters: a server with a longer one needs a `dataDirectory` with a shorter path, and the `elm server` commands for it need `--data`, since `--product` names the default folder.

The installation key is in the data folder, so a server started on a new, empty folder makes a new key, which the server license does not match. A data folder that is moved must still pass the server's check at the new path, or the server stops with a line that starts `error: The folder '<folder>' is not safe for secrets`. On Linux and macOS, the folder must be owned by the server's user or root and give no access to group or others (mode `0700`), and every folder above it, up to `/`, must be owned by the server's user or root and must not be writable by group or others, so a data folder under `/tmp` is refused; the files in it, such as `installation.key`, have mode `0600`, so the folder must also keep its owner. On Windows, the folder's owner must be the server's user, the Administrators group, or SYSTEM, and its access list must admit no one else and must not be inherited from the parent folder; every folder above it, up to the root of the drive, must be owned by one of them or by TrustedInstaller and must not let anyone else delete, rename, or change the access of what is in it, so a data folder on a drive that gives Authenticated Users `Modify` is refused, and so is one under a folder directly under the root of a drive, such as `C:\elm\data`, since such a folder takes `Modify` for Authenticated Users from `C:\`. On macOS, an extended access list (`chmod +a`) that lets anyone else into the folder, or lets anyone else delete or change what is in a folder above it, is refused too. A data folder reached through a symbolic link or a junction is checked where it leads as well, and one on a network location is refused: a UNC path or a network drive on Windows, and on Linux and macOS a file system that .NET counts as a network one (NFS, SMB and CIFS, every FUSE file system, autofs, aufs, ecryptfs, and 9p, such as WSL 2's `/mnt/c`). A copy of the folder takes the access list of its new parent, and is refused. `admin.sock`, `admin.token`, and `server.lock` need not be moved.

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
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | file path | none: the server makes a self-signed certificate with its installation key at each start | The server's TLS certificate. With `tls.key` set, a PEM certificate; without it, a PKCS#12 file whose password is in `ELM_TLS_PASSWORD` (not set: no password). The certificate must carry its private key. If it has an Extended Key Usage, that must include server authentication (`1.3.6.1.5.5.7.3.1`). |
| `tls.key` | `ELM_TLS_KEY` | file path | none | The PEM private key of a PEM `tls.certificate`. |

Intermediate CA certificates:

- If your CA signs the server's certificate with an intermediate CA, put the intermediate's certificate in the same file, and the server sends it with its own. In a PEM file, the server's certificate comes first and the intermediates after it; the server pairs `tls.key` with the first certificate. In a PKCS#12 file, the first certificate with a private key is the server's, and the certificates without a private key are the intermediates. The root CA is not needed: the apps' machines must trust it on their own.
- Every certificate after the server's must be a CA certificate (basic constraints with CA). Otherwise the server does not start (exit code 1). An intermediate that does not lead from the server's certificate towards a root is left out without a message.
- The server builds the chain from these certificates when it starts, and also uses the system's certificate stores and the addresses in the certificates (Authority Information Access): when the chain does not reach a root that the server's machine knows, the server tries to download the missing certificates. On a network where those addresses do not answer, this makes the start slower, also without intermediates in the file: on Linux it was about 15 seconds; on Windows and macOS it was not measured. On Linux, the server also asks the OCSP responder named in its certificate for a status to send with it (OCSP stapling).
- On Windows, when the chain cannot be built from the certificate stores alone, .NET puts the intermediates into the certificate store of intermediate CAs, of the machine or else of the server's user, and leaves them there: Windows sends only intermediates from that store. If the chain still does not reach a root that the machine trusts, .NET puts the last certificate of the chain into the same store too: a root from the file or from a download that the machine does not trust. A certificate in that store does not make anything trusted, a root there included. If the server's user can write to neither store, Windows cannot send the intermediates, and the server does not start (see the next item).
- On Windows, each time the server starts with an intermediate in the file, it makes one TLS connection to its own `listen` address right after it starts to listen (to the loopback address when `listen` is `0.0.0.0` or `[::]`), reads which certificates it is sent, and closes the connection without a request. Windows decides at the first TLS handshake of a process which certificates go with the server's, and keeps to that until the process ends; when another program changes the certificate store at that moment, Windows can leave the intermediates out. If an intermediate that belongs to the server's chain is missing, the server stops with exit code 1 and names it: `error: Windows left the intermediate CA certificate <subject> (thumbprint <thumbprint>) of the TLS certificate '<file>' out of the first TLS handshake, and what that handshake sent stays for as long as this process runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate Certification Authorities store.` When several are missing, the message names each, joined with `and`, and ends with `add these certificates`. Start the server again. If every start ends this way, the most likely cause is that the server's user can write to neither store, so Windows never has the intermediate: an administrator adds the intermediate CA's certificate to the Intermediate Certification Authorities store of the computer (the Local Computer store, not the user's). An intermediate also counts as sent when Windows sends another certificate of the same CA, with the same subject and key, that is valid at the server's current time, such as a renewed one that the store holds; whether an app accepts the chain with it is the app's own check.
- If that connection fails, or brings no certificates within 10 seconds, the server stops with exit code 1 as well: `error: The check that Windows sends the intermediate CA certificates of the TLS certificate '<file>' did not get an answer: the TLS handshake with <address> <what happened>.` `<what happened>` is `failed`, `failed (socket error <name>)`, or `did not finish within 10 seconds`. Start the server again. If every start ends this way, look for what can stop a TLS connection to the server's own address on that machine; possible causes are TLS versions turned off for clients in the Windows settings, so that the server's own connection offers none that the server accepts, and a program that intercepts TLS connections on the loopback address, such as some security software. A start can also stop this way while another program adds and removes many certificates in the Windows certificate stores: in the tests, with about 300 certificates added and removed over and over in the user's Intermediate Certification Authorities store, a TLS handshake on the loopback address took tens of seconds or was closed. Start the server again when that program has finished.
- From the moment the server starts to listen until this check ends, usually a few tens of milliseconds and at most 10 seconds, an app that connects can get the server's certificate without the intermediates, and the server answers no request: it closes the connection, as if it were not listening yet, so a server that then stops has done nothing for that request. The server makes this check on Windows only, and only when it starts.

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

`elm-server` has no `admin` settings: the local administration API listens on the socket file (see [Ports](#ports)). A configuration file that gives `admin.listen` a value other than `null`, or an `ELM_ADMIN_LISTEN` that is not empty, stops the start with `error: admin.listen is no longer a setting: the administration API listens on the socket file admin.sock in the data folder. Remove admin.listen (ELM_ADMIN_LISTEN).` and exit code 2. Any other key under `admin` is an unknown key.

## `service`

| Setting | Environment variable | Type | Default | Range and notes |
| --- | --- | --- | --- | --- |
| `service.url` | `ELM_SERVICE_URL` | URL | none: the server license is not refreshed on its own | The `elm-service` that refreshes the server license. An `https` URL, or `http` on a loopback address. When set, the server checks at start that it can create a file beside the `license` file. |

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
