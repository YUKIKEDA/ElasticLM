---
sidebar_position: 1
---

# elm command reference

This page lists every command and option of `elm`, the command line tool for vendors and administrators: it creates and certifies keys, issues and verifies license files, administers an on-premises license server on its own machine, calls the administration API of `elm-service`, passes offline request files to the service, creates the secret files of `elm-service` (the nonce secret and the key encryption key of the admin UI's sign-in), and verifies signed logs. It is for looking up a command you already know you need. For what keys, certificates, license files, and servers are, read the [Overview](../explanation/overview.md) and the [Trust chain](../explanation/trust-chain.md) first.

## Common behavior

### Form

```text
elm <group> <command> [arguments] [options]
```

The groups are `key`, `license`, `server`, `admin`, `offline`, `secret`, and `log`. Options may come in any order after the command. An option that takes several values is repeated (`--feature A --feature B`); `elm key certify --use` also takes a comma-separated list.

Every command takes `--help` (also `-h` or `-?`), which prints its usage. `elm --version` prints the version of `elm`.

A date or time (`--expires`, `--not-before`, `exp=` in `--feature`) is ISO 8601: a date without a time is midnight UTC, and a time without an offset is UTC.

A product ID (`--product`) is 1 to 64 lowercase letters, digits, and `-`, such as `example-editor`: the product's `code` in `elm-service` and the setting `product` of `elm-server`. `ElasticLM.Client`, `elm-server`, and `elm-service` accept no other form, and the `elm server` commands use it to find the server's folder. `elm license issue` does not check the form, but a license whose product ID has another form cannot be used with those three.

Errors are written to standard error as one line that starts with `error:`; warnings start with `warning:`. An unknown command or option, a missing required option, or a file or folder path option or argument that is empty or only white space is followed by `Run 'elm --help' for usage.` and exit code 2.

### `--json`

Writes the result to standard output as JSON instead of text. The commands that take it: `elm key root create`, `elm key signing create`, `elm key certify`, `elm license issue`, `elm license inspect`, `elm license verify`, `elm server status`, `elm server leases`, `elm server reload`, `elm server seal`, `elm server release`, `elm offline process`, `elm secret create`, and `elm log verify`. The `elm server` commands write the administration API's answer as it came.

### `--force`

Overwrites an output file that exists. Without it, a command that would overwrite a file stops with exit code 2 and writes nothing. The commands that take it: `elm key root create`, `elm key signing create`, `elm key certify`, `elm license issue`, `elm server activate-request`, `elm server refresh-request`, `elm server deactivate-proof`, `elm offline process`, and `elm secret create`.

### Passphrases

Every private key that `elm` writes is a PKCS#8 PEM file encrypted with a passphrase. On Linux and macOS a private key file is written with mode `0600`; on Windows it takes the access rules of its folder. With `--force`, the new file replaces the old one instead of being written into it, so it does not keep the old file's mode or access rules, and a program that had the old file open does not see the new content.

The commands that read or write a private key (`elm key root create`, `elm key signing create`, `elm key certify`, `elm license issue`) take the passphrase from an environment variable:

- `--passphrase-env <name>` names the variable. The default is `ELM_KEY_PASSPHRASE`.
- When the variable is not set or is empty and input is interactive, `elm` asks for the passphrase on the terminal without echoing it. For a new key it asks twice, and the two must match.
- When the variable is not set or is empty and input is redirected, the command stops with exit code 2.
- A new key's passphrase must be at least 12 characters.

### Trust options

The commands that verify a signature (`elm license issue`, `elm license verify`, `elm log verify`, and the server license commands of `elm server`) need the vendor's root public keys, the [trust store](../explanation/trust-chain.md):

- `--trust <file>` names a PEM file of root public keys. A file may hold several keys, and the option may be repeated.
- Without `--trust`, the keys come from the environment variable `ELM_TRUST_ROOT_KEYS`, which holds one or more PEM public keys. When `--trust` is given, the variable is not read.
- With neither, the command stops with exit code 2.
- `--revoked <key ID>` names a revoked key ID (`kid`). It may be repeated.

### Environment variables

| Variable | Used by | Meaning |
| --- | --- | --- |
| `ELM_KEY_PASSPHRASE` | `elm key`, `elm license issue` | The passphrase of a private key, unless `--passphrase-env` names another variable. |
| `ELM_TRUST_ROOT_KEYS` | Commands with `--trust` | One or more root public keys in PEM, used when `--trust` is not given. |
| `ELM_API_KEY` | `elm admin request`, `elm offline process` | An API key of `elm-service`. When set, it is used instead of a saved login. |
| `ELM_SERVICE_URL` | `elm admin request` | The `elm-service` URL when `--url` is not given. |
| `ELM_ADMIN_LOGIN` | `elm admin`, `elm offline process` | The file that holds the login of `elm admin login`, instead of the default place. |

`elm-server` reads `ELM_SERVICE_URL` too, as its setting [`service.url`](server-configuration.md#service), and the variable overrides the setting in its configuration file. When `elm` and `elm-server` run in the same environment, such as the same service account or container, setting the variable for `elm admin request` also changes the service `elm-server` refreshes its server license from.

The default login file is `%LOCALAPPDATA%\ElasticLM\elm\login` on Windows, `$XDG_DATA_HOME/elasticlm/elm/login` on Linux (`~/.local/share/elasticlm/elm/login` when `XDG_DATA_HOME` is not set), and `~/Library/Application Support/ElasticLM/elm/login` on macOS.

### Exit codes

| Code | Name | Meaning |
| --- | --- | --- |
| `0` | Success | The command did what it was asked. `elm log verify` returns 0 when the log is intact, even with warnings. |
| `1` | Verification failed | A license file or log did not verify, or a server, `elm-service`, or the identity provider refused the request or answered with an error. |
| `2` | Invalid input | An argument, option, or value is wrong or missing; a file is missing, unreadable, too large, or exists without `--force`; no passphrase, trust keys, or credentials were found. |
| `3` | Network error | A server, `elm-service`, or the identity provider cannot be reached, the request timed out, or (for `elm server` and `elm offline process`) the answer cannot be read or is larger than `elm` reads. |

When `elm` prints an [error code](error-codes.md), the exit code follows from it: `ELM_NETWORK_ERROR` gives `3`, and every other code, from a check `elm` runs or from the answer of a server or `elm-service`, gives `1`. `ELM_MALFORMED` also gives `3` when its message is one of these three, which say that an answer is larger than `elm` reads: `The service's answer is larger than the limit.` (the server license commands), `The service's answer is larger than 262144 bytes.` (`elm offline process`), and `The server's administration API answered with a body larger than 67108864 bytes.` (`elm server`). `elm` read nothing of that answer, so it cannot tell a refusal from a success. Every other `ELM_MALFORMED` gives `1`, also one whose message says that a file is larger than a limit. Exit code `2` comes from `elm`'s own checks of its input, before anything is verified or sent, and its message names the input instead of a code. The one exception is `elm license issue`: it verifies the license before writing it, and a code from that check gives `2`, because the input made the license.

## elm key

### elm key root create

```text
elm key root create --out <file> [options]
```

Creates a root key: an encrypted private key file and its public key file, and prints the key ID.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--out` | File path | — | Yes | The encrypted private key file to write. |
| `--public-out` | File path | `<out>.pub.pem` | No | The public key file to write. |
| `--passphrase-env` | Variable name | `ELM_KEY_PASSPHRASE` | No | The environment variable that holds the passphrase. |
| `--force` | — | Off | No | Overwrite files that exist. |
| `--json` | — | Off | No | Write the result as JSON. |

### elm key signing create

```text
elm key signing create --out <file> [options]
```

Creates a signing key: an encrypted private key file and its public key file, and prints the key ID. The key signs nothing that a verifier accepts until `elm key certify` certifies it.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--out` | File path | — | Yes | The encrypted private key file to write. |
| `--public-out` | File path | `<out>.pub.pem` | No | The public key file to write. |
| `--passphrase-env` | Variable name | `ELM_KEY_PASSPHRASE` | No | The environment variable that holds the passphrase. |
| `--force` | — | Off | No | Overwrite files that exist. |
| `--json` | — | Off | No | Write the result as JSON. |

### elm key certify

```text
elm key certify --root <file> --public <file> --out <file> [options]
```

Writes a key certificate that certifies a signing key with the root key, for named uses and a limited period.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--root` | File path | — | Yes | The encrypted root key file. |
| `--public` | File path | — | Yes | The public key file of the signing key to certify. |
| `--use` | `license`, `lease`, `log` | `license` | No | What the key may sign. Repeat, or separate with commas, for several. |
| `--not-before` | ISO 8601 date or time | Now | No | The start of validity. |
| `--days` | Integer, 1 to 731 | `365` | No | The validity in days. |
| `--out` | File path | — | Yes | The certificate file to write. |
| `--passphrase-env` | Variable name | `ELM_KEY_PASSPHRASE` | No | The environment variable that holds the root key's passphrase. |
| `--force` | — | Off | No | Overwrite the file if it exists. |
| `--json` | — | Off | No | Write the result as JSON. |

### elm key thumbprint

```text
elm key thumbprint <public-key-file>
```

Prints the key ID (`kid`, the JWK thumbprint) of a public key, such as a root key or a signing key. Use it to get the key ID to pass to `--revoked`, or to match a key file with the `kid` that `elm license inspect` shows in a certificate or a license. An installation key has no public key file, so this command does not give the thumbprint for `--bind-key`; see [where the binding values come from](#where-the-binding-values-come-from).

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<public-key-file>` | File path | — | Yes | Argument: a public key PEM file. |

## elm license

### elm license issue

```text
elm license issue --signing-key <file> --certificate <file> --product <id> --license-id <id> --expires <time> --out <file> [options]
```

Issues a license file signed by a certified signing key; the file is verified with the trust store before it is written, and is not written if it does not verify.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--signing-key` | File path | — | Yes | The encrypted signing key file. |
| `--certificate` | File path | — | Yes | A certificate file of the signing key. Repeat for several, at most four. |
| `--product` | Product ID | — | Yes | The product ID (see [Form](#form)). |
| `--license-id` | License ID | — | Yes | The license ID. |
| `--kind` | `standard`, `trial`, `server` | `standard` | No | The kind of license. |
| `--feature` | `ID[;ver=RANGE][;seats=N][;exp=DATE]` | — | No | A feature, with an optional version range, seat count, and expiry (see [Feature values](#feature-values)). Repeat for several. |
| `--expires` | ISO 8601 date or time, or `never` | — | Yes | The token's expiry. `never` writes the warning `The license has no expiry. It cannot be revoked once delivered.` With `--kind server` it writes a second one: `A server license issued by hand is never refreshed, so a cloned server cannot be detected. Without an expiry, a clone works for as long as the original.` |
| `--grace-days` | Number, fractions allowed | `7`; `0` for `trial` | No | The grace period in days. |
| `--refresh-days` | Number, fractions allowed | — | No | Days before expiry when clients start refreshing online. |
| `--bind-key` | Thumbprint | — | No | The installation key [thumbprint](../explanation/devices-and-proofs.md#the-installation-key) to bind the license to (see [where the binding values come from](#where-the-binding-values-come-from)). |
| `--fingerprint` | Fingerprint | — | No | A machine fingerprint to bind the license to (see [where the binding values come from](#where-the-binding-values-come-from)). Repeat for several. |
| `--fingerprint-matching` | `any`, `all`, `ignore` | `any` | No | How the machine fingerprints must match. |
| `--lease-minutes` | Number, fractions allowed | `10` | No | Server licenses only: the lease duration in minutes. |
| `--borrow-days` | Number, fractions allowed | `0` | No | Server licenses only: the borrow limit in days; `0` disallows borrowing. |
| `--overage` | Integer, 0 to 1000 | `0` | No | Server licenses only: the overage percentage. |
| `--clone` | `deny`, `flag` | `deny` | No | Server licenses only. What `elm-server` does when a copied app installation renews the same lease as the original: `deny` stops the lease for both copies, and `flag` records the clone in the usage log and renews as usual ([Retries and clones](../explanation/floating-licenses.md#retries-and-clones)). |
| `--borrow-returns` | Integer, 0 to 1000 | `2` | No | Server licenses only: early returns of borrows one installation may make in 30 days. |
| `--meta` | `key=value` | — | No | Vendor metadata. Repeat for several. |
| `--out` | File path | — | Yes | The license file to write. |
| `--trust` | File path | `ELM_TRUST_ROOT_KEYS` | No | A root public key file to verify the license with before it is written. Repeat for several. |
| `--revoked` | Key ID | — | No | A revoked key ID. Repeat for several. |
| `--passphrase-env` | Variable name | `ELM_KEY_PASSPHRASE` | No | The environment variable that holds the signing key's passphrase. |
| `--force` | — | Off | No | Overwrite the file if it exists. |
| `--json` | — | Off | No | Write the result as JSON. |

`--grace-days`, `--refresh-days`, `--lease-minutes`, and `--borrow-days` accept fractions such as `0.5`; the license holds each as whole seconds, rounded down, and `--lease-minutes` must come to at least one second. `--overage` and `--borrow-returns` take whole numbers only.

`--lease-minutes`, `--borrow-days`, `--overage`, `--clone`, and `--borrow-returns` with a `--kind` other than `server` stop the command with exit code 2. A `server` license also needs `--bind-key` and `seats=` on every feature; a `standard` or `trial` license takes no `seats=`, and a `trial` license takes only `--grace-days 0`. A license that breaks one of these rules is not written, and the command stops with exit code 2.

#### Feature values

The value is the feature ID, then any of the parts below, each after a `;`. Quote the whole value in the shell, because a version range holds spaces and `<` or `>`.

| Part | Form | Meaning |
| --- | --- | --- |
| `ID` | Text, 1 to 64 bytes in UTF-8, without `;` | The feature ID that the app asks for. |
| `ver=RANGE` | One to four comparators separated by spaces, all of which must hold. A comparator is `>=`, `<=`, `>`, `<`, or `=` followed by a version `major.minor` or `major.minor.patch` of digits only; a missing patch is 0. | The feature versions the license covers, such as `>=1.0 <2.0`. Without it, every version. |
| `seats=N` | Whole number, at least 1 | The concurrent seats of the feature. Required for a `server` license, and not allowed otherwise. |
| `exp=DATE` | ISO 8601 date or time, as for `--expires` | The feature's own expiry. Without it, the feature lasts as long as the license. |

For example, `--feature "pro;ver=>=1.0 <2.0;seats=5;exp=2027-06-30"` in a `server` license grants `pro` for versions from 1.0 up to but not including 2.0, with 5 seats, until 2027-06-30 00:00 UTC.

#### Where the binding values come from

`--bind-key` takes the thumbprint of the installation key, the key pair that names one device or server, and `--fingerprint` takes a machine fingerprint, 32 lowercase hexadecimal characters. Both come from the machine the license is for:

- **An `elm-server`.** When it starts without a server license, `elm-server` stops with a message that gives its installation key thumbprint and its fingerprints. The customer's administrator sends them to the vendor.
- **An app's device.** `elm` has no command that prints them; the app must show them to the user. The [Overview](../explanation/overview.md#offline-only) names the calls that give them.

`elm key thumbprint` does not apply here: it reads a public key file, and an installation key has none.

### elm license inspect

```text
elm license inspect <license-file> [--json]
```

Shows the headers and payloads of a license file and its certificates without verifying any signature.

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<license-file>` | File path | — | Yes | Argument: the license file. |
| `--json` | — | Off | No | Write the result as JSON. |

### elm license verify

```text
elm license verify <license-file> --product <id> [options]
```

Verifies a license file against a trust store and a requirement; it returns exit code 1 when the license is not valid.

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<license-file>` | File path | — | Yes | Argument: the license file. |
| `--trust` | File path | `ELM_TRUST_ROOT_KEYS` | No | A root public key file to trust. Repeat for several. |
| `--revoked` | Key ID | — | No | A revoked key ID. Repeat for several. |
| `--product` | Product ID | — | Yes | The product ID the license must be for. |
| `--kind` | `standard`, `trial`, `server` | `standard` | No | The kind the license must be. |
| `--feature` | Feature ID | — | No | A feature the license must grant. |
| `--version` | `major.minor[.patch]` | — | No | The feature version to check. |
| `--key-thumbprint` | Thumbprint | — | No | The installation key thumbprint, for a bound license. |
| `--fingerprint` | Fingerprint | — | No | A current machine fingerprint. Repeat for several. Needs `--key-thumbprint`. |
| `--this-machine` | — | Off | No | Use this machine's fingerprints for the product. Needs `--key-thumbprint`. |
| `--json` | — | Off | No | Write the result as JSON. |

## elm server

The `elm server` commands administer an on-premises license server on its own machine. Each command acts on the one server whose data folder it names. Without `--data`, the data folder is the machine-wide one of `--product`: `%ProgramData%\ElasticLM\<product>` on Windows, `/var/lib/elasticlm/<product>` on Linux, and `/Library/Application Support/ElasticLM/<product>` on macOS. Each request times out when its whole answer, the body included, has not come within 30 seconds of sending it.

Run the `elm server` commands as the user that `elm-server` runs as: they read the administrator token from the server's data folder, which only the server's user and root can open on Linux and macOS (for example `sudo -u <server user> <full path of elm> server status --product <id>`; see below for the full path). Root can also run these administration commands; the [server license commands](#server-license-commands) need the server's user itself, as the next paragraph says. On Windows, the folder's access list also admits the Administrators group and SYSTEM, and the administrator token is protected for the machine, not for one user, so those accounts can read it too.

Run the [server license commands](#server-license-commands) as the server's user itself, on every OS. They also read the server's installation key, and they first check the data folder as the server does ([Which folders the server accepts](server-configuration.md#which-folders-the-server-accepts)): the folder must be owned by the user that runs the command, or by root on Linux and macOS, or by the Administrators group or SYSTEM on Windows, and the folder and every folder above it must pass the rest of that check.

- Run as root on Linux and macOS, on a folder that the server's user owns, they stop with exit code 1 and a line that starts with `error: ELM_INSTALLATION_KEY_UNREADABLE: The folder '<folder>' is not safe for secrets: it is owned by another user.`
- Run as another administrator on Windows, they stop with exit code 1 and a line that starts with `error: ELM_INSTALLATION_KEY_UNREADABLE: The folder '<folder>' is not safe for secrets`, because the folder's access list admits the server's user, which is not the account running the command.

When the line names the data folder itself, it ends by telling you to remove or restrict the folder. Do not do that here: run the command as the server's user.

On Linux and macOS, use `sudo -u <server user>`. `sudo` does not pass your environment variables to the command by default (its `env_reset` option), so give the trust store with `--trust <file>` rather than `ELM_TRUST_ROOT_KEYS`. It may also look for `elm` only in its own `secure_path` rather than your `PATH` (sudoers(5)), so give the full path of `elm`, as in `sudo -u <server user> <full path of elm> server activate …`. Use files and a working folder that the server's user can read, and write where the command writes. The server's user must also be able to create files in the folder of `--license`: the commands write the license file through a temporary file beside it, so keep the file in the data folder or in another folder that user can write.

On Windows, a server that runs as LocalSystem adds SYSTEM, the account it runs as, to the access list of the data folder it creates, so the list names only the Administrators group and SYSTEM. The check lets the Administrators group through, so an administrator can run the commands on that folder. Nobody can sign in as a virtual service account or as LocalService to run the commands, so a server that runs as one of them cannot use them: run the server as LocalSystem, or as a normal user account and run the commands as that account.

The `elm server` commands reach the server's [local administration API](administration-api.md#elm-server-local-administration-api) through the socket file `admin.sock` in the same data folder (a Unix domain socket: a file that programs on the same machine connect through, instead of a port), and through nothing else: they open no TCP connection, take no address, and cannot administer a server on another machine. Use the `elm` of the same release as the `elm-server`. On Windows they need Windows 10 version 1803 or Windows Server 2019, or later. On a system without Unix domain sockets, every `elm server` command stops with `error: This system has no Unix domain sockets, which the server's administration API needs. On Windows, Windows 10 version 1803 or Windows Server 2019, or later, is required.` and exit code 2.

The path of `admin.sock` has a limit: 107 bytes in UTF-8 on Windows and Linux, and 103 on macOS. The data folder's own absolute path is therefore at most 96 bytes on Windows and Linux, and 92 on macOS; a character outside ASCII takes 2 to 4 bytes, and a relative `--data` is made absolute from the working folder first ([Data folder](server-configuration.md#data-folder)). For a data folder with a longer path, every `elm server` command stops before it reads the administrator token, with `error: The path of the administration socket '<path>' is <length> bytes in UTF-8, and this system allows <limit>. Use a data folder with a shorter path.` and exit code 2. The server itself does not start on such a folder, and exits with 1.

When a command prints `ELM_UNAUTHORIZED`, the administrator token that `elm` read is not the one the running server holds: the server started again between the read and the request, or `admin.token` was replaced by hand or restored from a backup. Run the command again: `elm` reads the token anew. If the code comes again, restart the server, which writes a new token.

### Server administration options

`elm server status`, `elm server leases`, `elm server reload`, `elm server seal`, and `elm server release` call the running server's administration API. They take these options:

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--product` | Product ID | — | `--data` or `--product` | The server's product ID, to find its data folder at the default place. |
| `--data` | Folder path | — | `--data` or `--product` | The server's data folder (its setting `dataDirectory`). |
| `--json` | — | Off | No | Write the answer as JSON. |

In each of the cases below, these five commands stop with one line on standard error:

| Case | The line | Exit code | What to do |
| --- | --- | --- | --- |
| No administrator token can be read in the folder: the folder named is not the server's; the server has never started with a server license, since it makes the token only once its license verifies; or the command runs as a user that may not read the token | `error: The administrator token cannot be read in '<folder>'. Run elm server as the server's user, on its machine.` | 2 | Name the server's folder with `--data` when its `dataDirectory` is not the default one, start the server, or run the command as the server's user. |
| The folder has no `admin.sock`: the server is not running | `error: The server cannot be reached: the administration socket '<path>' does not exist.` | 3 | Start the server, or name its folder with `--data` when its `dataDirectory` is not the default one. |
| The connection fails, or the whole answer, its body included, does not come within 30 seconds of the request. A socket file that a killed server left refuses the connection | `error: The server cannot be reached: <reason>` | 3 | When the reason says that the connection was refused: start the server, or name its folder with `--data` when its `dataDirectory` is not the default one. A socket file that a killed server left need not be deleted: the next start of the server removes it. When the reason is `No whole answer came within 30 seconds.`: the server runs but does not answer. Run the command again, and restart the server if it stays so. |
| The answer, a success or an error, has a body larger than 64 MiB | `error: ELM_MALFORMED: The server's administration API answered with a body larger than 67108864 bytes.` | 3 | Only the answer to `elm server leases` grows with the server's use: each lease takes 162 bytes with a feature code of 3 letters and at most 554 bytes, so 64 MiB hold at least 121,135 leases. A server that holds more cannot list them. From any other command, what listens on `admin.sock` in that folder is not an `elm-server` of this release: check that `--data` or `--product` names the server's folder. |
| The answer, a success or an error, has a body that is not JSON; or the success answer of `status`, `leases`, or `reload` is JSON but not that command's answer | `error: The server answered <status> with a body that is not JSON.` or `error: The server answered <status> with a body that is not the answer asked for.` | 3 | What listens on `admin.sock` in that folder is not an `elm-server` of this release. Check that `--data` or `--product` names the server's folder, and that `elm` and `elm-server` are of the same release; then restart the server. |
| The server refuses the administrator token (`401`) | `error: ELM_UNAUTHORIZED: The administrator token is missing or wrong.` | 1 | Run the command again; see above. |
| Any other error answer with an error code | `error: <code>: <reason>` | 1 | See the code's row in [Error codes](error-codes.md). |
| An error answer whose body is JSON or empty and has no error code | `error: The server's administration API answered <status>.` | 1 | What listens on `admin.sock` in that folder is not an `elm-server` of this release. Check that `--data` or `--product` names the server's folder, and use the `elm` of the server's release. |

### elm server status

```text
elm server status (--data <folder> | --product <id>) [options]
```

Shows the server license, whether the server is reclaiming leases after a restart, and the seats in use for each feature. It takes only the [server administration options](#server-administration-options).

### elm server leases

```text
elm server leases (--data <folder> | --product <id>) [options]
```

Lists the leases and borrows the server holds. It takes only the [server administration options](#server-administration-options).

### elm server reload

```text
elm server reload (--data <folder> | --product <id>) [options]
```

Makes the running server read its license file again. It takes only the [server administration options](#server-administration-options).

### elm server seal

```text
elm server seal (--data <folder> | --product <id>) [options]
```

Makes the server sign the pending lines of its usage log now. It takes only the [server administration options](#server-administration-options).

### elm server release

```text
elm server release <lease-id> (--data <folder> | --product <id>) [options]
```

Releases a lease, or a borrow, on the server's side. Besides its argument, it takes the [server administration options](#server-administration-options).

| Argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<lease-id>` | Lease ID | — | Yes | The lease ID, as `elm server leases` shows it. |

### Server license commands

The commands below manage the server license through the activation kept in the server's data folder, and work while the server is stopped. They need the server's installation key, which `elm-server` creates when it first starts. They all take the options below; the section of each command lists only the options and arguments of its own.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--product` | Product ID | — | Yes | The server's product ID (its setting `product`). |
| `--data` | Folder path | The machine-wide data folder of `--product` | No | The server's data folder (its setting `dataDirectory`). |
| `--trust` | File path | `ELM_TRUST_ROOT_KEYS` | No | A root public key file to trust. Repeat for several. |
| `--revoked` | Key ID | — | No | A revoked key ID. Repeat for several. |

When a command writes a new license file and the server is running, it asks the server's [local administration API](administration-api.md#elm-server-local-administration-api) to reload it; when the server is not running, the server reads the file when it starts. In each of the cases below the file is written, the error is one line on standard error, and the exit code is 1.

| The answer to the reload request | The line | What to do |
| --- | --- | --- |
| The server read the new file and refused it (`409`; the codes are those of [`POST /admin/v1/license/reload`](administration-api.md#post-adminv1licensereload)) | `error: the server kept its license: <code>: <reason>` | Correct what the code names, as its row in [Error codes](error-codes.md) says, then run `elm server reload`. |
| Another error with a code: the request itself was refused, the server has not read the file, and it still uses its license | `error: <code>: <reason> The license file is written; the server did not confirm a reload.` | For `ELM_UNAUTHORIZED`, run `elm server reload`, for which `elm` reads the administrator token anew. If the code comes again, `admin.token` is not the running server's (it was replaced by hand or restored from a backup): restart the server, which writes a new token and reads the license file. For another code, see its row in [Error codes](error-codes.md). |
| An answer that is not a reload's answer and has no error code, as another program on that socket gives | `error: The server's administration API answered <status>. The license file is written; the server did not confirm a reload.` | Check that `--data` or `--product` names the server's folder. If it does, restart the server: it reads the written license file when it starts. |
| The server reloaded, but its license is not the written one | `error: The server reads another license file than --license; pass its setting license.` | Run the command again with `--license` set to the server's `license` path. |

When `--license` is the `license` setting of the server whose folder was named, that server reads the written file when it next starts, or at `elm server reload`.

`deactivate`, `deactivate-proof`, `activate-request`, and `refresh-request` first ask the same API for the server's status. That question, and the reload request after a written file, end in one of these ways when the answer is not a status, or not a reload's answer:

| Case | What the command does | The line | Exit code |
| --- | --- | --- | --- |
| No administrator token can be read in the folder, the folder has no `admin.sock`, or the connection to `admin.sock` is refused (a socket file that a killed server left) | It takes the server as not running. After the status question, it goes on with its work | None after the status question. After a written file, `The server is not running; it reads the license when it starts.` on standard output | That of the command's work; 0 after a written file |
| The connection fails in any other way, or it ends before a whole answer came. The line is the same for both | It stops | `error: The server's administration API gave no whole answer.` | 3 |
| Something accepts the connection and gives no whole answer, the body included, within 30 seconds of the request | It stops | `error: The server's administration API did not answer in time.` | 3 |
| An answer, a success or an error, whose body is larger than 64 MiB | It stops | `error: ELM_MALFORMED: The server's administration API answered with a body larger than 67108864 bytes.` | 3 |
| A success answer whose body is not JSON | It stops | `error: The server's administration API answered with a body that is not JSON.` | 3 |
| The status question gets an error with a code, such as `ELM_UNAUTHORIZED` | It stops | `error: <code>: <reason>` | 1 |
| The status question gets an answer that is not a status and has no error code | It stops | `error: The server's administration API answered <status>.` | 1 |

A command that stops at the status question writes nothing. After a written file, each line with exit code 3 is followed, on the same line, by `The license file is written; the server did not confirm a reload.`; the reload request's answers with exit code 1 are in the table above.

In the rows with exit code 3 and in the last row, something may be listening on the socket file, so a server may be running. For `deactivate` and `deactivate-proof`, stop the server and run the command again. For the other commands, run the command again, and restart the server if the line comes again. For `ELM_UNAUTHORIZED`, see the top of [elm server](#elm-server).

### elm server activate

```text
elm server activate <license-key> --product <id> --service <url> --license <file> [options]
```

Activates the server's license online with `elm-service` and writes the license file. It takes the license key as an argument, while `elm server activate-request` takes it with `--key`. It also takes the [options of the server license commands](#server-license-commands).

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<license-key>` | License key | — | Yes | Argument: the license key of a server license. |
| `--service` | `https` URL, or `http` on a loopback address | — | Yes | The `elm-service` URL. |
| `--license` | File path | — | Yes | The server's license file (its setting `license`). |

### elm server refresh

```text
elm server refresh --product <id> --service <url> --license <file> [options]
```

Refreshes the server's license online with `elm-service` and writes the license file. It also takes the [options of the server license commands](#server-license-commands).

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--service` | `https` URL, or `http` on a loopback address | — | Yes | The `elm-service` URL. |
| `--license` | File path | — | Yes | The server's license file (its setting `license`). |

### elm server deactivate

```text
elm server deactivate --product <id> --service <url> [options]
```

Deactivates the server's license online and removes its license file; it stops with exit code 1 while the server is running. It also takes the [options of the server license commands](#server-license-commands).

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--service` | `https` URL, or `http` on a loopback address | — | Yes | The `elm-service` URL. |
| `--license` | File path | — | No | The server's license file. It is removed only when it is the file of this activation. |

### elm server activate-request

```text
elm server activate-request --key <license-key> --product <id> --out <file> [options]
```

Writes an offline activation request file (`.elmreq`) for `elm offline process`; it stops with exit code 1 while the running server refreshes its license online. It takes the license key with `--key`, while `elm server activate` takes it as an argument. It also takes the [options of the server license commands](#server-license-commands).

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--key` | License key | — | Yes | The license key of a server license. |
| `--out` | File path | — | Yes | Where to write the request file. |
| `--renew` | — | Off | No | Make a new request before the waiting one expires. The earlier request file is then replaced and must be discarded. |
| `--force` | — | Off | No | Overwrite the file if it exists. |

### elm server refresh-request

```text
elm server refresh-request --product <id> --out <file> [options]
```

Writes an offline refresh request file (`.elmreq`) for `elm offline process`; it stops with exit code 1 while the running server refreshes its license online. It also takes the [options of the server license commands](#server-license-commands).

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--out` | File path | — | Yes | Where to write the request file. |
| `--renew` | — | Off | No | Make a new request before the waiting one expires. The earlier request file is then replaced and must be discarded. |
| `--force` | — | Off | No | Overwrite the file if it exists. |

### elm server import

```text
elm server import <answer> --product <id> --license <file> [options]
```

Imports the service's answer file (`.elmresp`) to the waiting request and writes the license file. It also takes the [options of the server license commands](#server-license-commands).

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<answer>` | File path | — | Yes | Argument: the service's answer file (`.elmresp`). |
| `--license` | File path | — | Yes | The server's license file (its setting `license`). |

### elm server deactivate-proof

```text
elm server deactivate-proof --out <file> --product <id> [options]
```

Writes an offline deactivation proof (`.elmdeact`) and removes the activation and its license file; it stops with exit code 1 while the server is running. It also takes the [options of the server license commands](#server-license-commands).

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--out` | File path | — | Yes | Where to write the deactivation proof (`.elmdeact`). |
| `--force` | — | Off | No | Overwrite the file if it exists. |
| `--license` | File path | — | No | The server's license file. It is removed only when it is the file of this activation. |

### elm server forget-proof

```text
elm server forget-proof --product <id> [options]
```

Forgets the kept deactivation proof once the service has it, so that the server may activate again. It takes only the [options of the server license commands](#server-license-commands).

## elm admin

The `elm admin` commands sign in to `elm-service` and call its administration API. A service URL is `https`, or `http` on a loopback address. A saved login is used only for requests to the same origin (scheme, host, and port) as the URL it was made for, and its access token is refreshed when it expires within 60 seconds. Tokens and keys are never written to standard output or standard error.

### elm admin login

```text
elm admin login --url <url>
```

Signs in to `elm-service` with its identity provider (OIDC device authorization flow) and saves the login, replacing any earlier one; the sign-in address and code are written to standard error.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--url` | `https` URL, or `http` on a loopback address | — | Yes | The `elm-service` URL. |

### elm admin logout

```text
elm admin logout
```

Removes the saved login and, when the identity provider offers it, revokes its refresh token first; the access token stays valid until it expires.

This command takes no options.

### elm admin request

```text
elm admin request <METHOD> <path> [--body <file>] [--url <url>]
```

Calls the administration API once and writes the body of a successful answer to standard output, followed by a line break when the body does not end with one (an empty body writes nothing); any other answer's body goes to standard error with exit code 1.

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<METHOD>` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` | — | Yes | Argument: the HTTP method. |
| `<path>` | Path starting with `/v1/admin/` | — | Yes | Argument: the path, which may hold a query. At most 2,048 characters, with no fragment, no backslash, no `.` or `..` segment, and no `%2e`, `%2f`, or `%5c`. |
| `--body` | File path | — | No | A JSON file to send as `application/json`, up to 64 KiB. |
| `--url` | `https` URL, or `http` on a loopback address | `ELM_SERVICE_URL`, then the saved login's URL | No | The `elm-service` URL. |

The request uses `ELM_API_KEY` when it is set, and the saved login otherwise; with neither, it stops with exit code 2. Redirects are not followed, and the request times out after 100 seconds.

## elm offline

### elm offline process

```text
elm offline process --url <url> --in <file> [--out <file>] [options]
```

Sends an offline activation request, refresh request, or deactivation proof to `elm-service` and, for a request, writes the answer file that the device imports.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--url` | `https` URL, or `http` on a loopback address | — | Yes | The `elm-service` URL. |
| `--in` | File path | — | Yes | The request file (`.elmreq`) or deactivation proof (`.elmdeact`). |
| `--out` | File path | — | For a request; not allowed for a deactivation proof | Where to write the answer (`.elmresp`) of an activation or refresh request. |
| `--force` | — | Off | No | Overwrite the answer file if it exists. |
| `--json` | — | Off | No | Write the result as JSON. |

With `ELM_API_KEY`, or a saved `elm admin login` for the same origin as `--url`, the file is sent to the service's administration entry; otherwise it is sent to the public entry. Redirects are not followed. The command gives up with exit code 3 when the whole answer, its body included, has not come within 30 seconds of the request (`error: The service cannot be reached: No whole answer came within 30 seconds.`), and when the answer's body is larger than 256 KiB, whatever its status (`error: ELM_MALFORMED: The service's answer is larger than 262144 bytes.`). It then writes no answer file.

## elm secret

### elm secret create

```text
elm secret create --out <file> [options]
```

Writes a secret file of `elm-service`: 32 random bytes in base64, on one line. A file serves as either the nonce secret, which the service's setting [`nonce.secretFile`](service-configuration.md#nonce) names, or the key encryption key of the admin UI's sign-in, which [`dataProtection.kekFile`](service-configuration.md#dataprotection) names. The length is fixed at 32 bytes for both: it is the service's minimum for the nonce secret and the length of the key encryption key. Create a separate file for each use. It prints `secret file: <file>`, and with `--json` an object whose `file` is the path. The secret itself is never written to standard output or standard error. On Linux and macOS the file is written with mode `0600`; on Windows it takes the access rules of its folder. With `--force`, the new file replaces the old one, as for a private key.

| Option | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `--out` | File path | — | Yes | The secret file to write. |
| `--force` | — | Off | No | Overwrite the file if it exists. |
| `--json` | — | Off | No | Write the result as JSON. |

## elm log

### elm log verify

```text
elm log verify <files>... [options]
```

Verifies the signatures and the chain of a license server's usage log or borrow record, or of `elm-service`'s usage log; it returns exit code 1 when an error is found, and 0 when there are only warnings.

| Option or argument | Value | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `<files>` | File or folder paths | — | Yes, one or more | Argument: the log files, or folders of them. They are verified in the order of their file names, oldest first. |
| `--product` | Product ID | — | For a server's log | The product ID of the server license. Not used for `elm-service`'s log. |
| `--node` | Node name: 1 to 64 of `A`–`Z`, `a`–`z`, `0`–`9`, `-` | — | For a folder of `elm-service`'s usage logs | The node whose files to verify (`usage-<node>-*.jsonl`). Only with `--stream usage`, and only with folders. |
| `--stream` | `usage`, `borrows` | `usage` | No | Which log to verify: the usage log or the borrow record. |
| `--trust` | File path | `ELM_TRUST_ROOT_KEYS` | No | A root public key file to trust. Repeat for several. |
| `--revoked` | Key ID | — | No | A revoked key ID. Repeat for several. |
| `--json` | — | Off | No | Write the result as JSON. |

## Examples

These use fake file names, IDs, and keys.

```text
elm key root create --out root.key.pem
elm key signing create --out signing.key.pem
elm key certify --root root.key.pem --public signing.key.pem.pub.pem --use license --days 365 --out signing.cert
elm license issue --signing-key signing.key.pem --certificate signing.cert --trust root.key.pem.pub.pem \
  --product example-editor --license-id LIC-0001 --feature "pro;ver=>=1.0 <2.0" --expires 2027-12-31 --out LIC-0001.elmlic
elm license verify LIC-0001.elmlic --trust root.key.pem.pub.pem --product example-editor --feature pro --version 1.4
elm server status --product example-editor
elm admin request GET /v1/admin/licenses --url https://licensing.example.com
elm offline process --url https://licensing.example.com --in server.elmreq --out server.elmresp
elm secret create --out nonce.secret
elm log verify ./usage --product example-editor --trust root.key.pem.pub.pem
```
