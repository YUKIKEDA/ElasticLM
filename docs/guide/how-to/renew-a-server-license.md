---
sidebar_position: 10
---

# Renew a server license

This guide is for a vendor who issues server licenses by hand with `elm`, and for the customer's administrator who puts the new file into `elm-server`: before the server license expires, or when the seats or features change. The server reads the new file while it runs, so apps keep their leases. The guide starts where you have finished [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md): `elm-server` with a server license that `elm license issue` made. A server that got its license from `elm-service` renews it differently; see [Servers activated with elm-service](#servers-activated-with-elm-service).

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the signing key `signing.key.pem` with its certificate `signing.cert`, the root public key `root.pub.pem`, and the server's data folder `server-data`. In the tutorial, one machine plays both parts; in real use, the vendor issues the file on its own machine and sends it to the administrator.

If the server is stopped, start it again in a second terminal, as in [Start the server](../tutorials/use-a-floating-license.md#start-the-server).

The examples use the values of the tutorial: the product `acme-cad`, the license ID `floating-server`, and the feature `pro` with two seats. Replace them with your own.

## Look at the current server license

Ask the server what it holds:

<!-- steps:run -->
```shell
elm server status --data server-data
```

`elm` prints the server license and its seats:

```text
product: acme-cad
license: floating-server (token FAKEjti000000000000002)
expires: 2099-12-31 00:00:00Z
key: FAKE-SERVER-KEY-00000000000000000000000000K
serving
leases: 1
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

<!-- steps:expect -->
```text
expires: 2099-12-31 00:00:00Z
```

`license` is the license ID and the token ID of the file in use, and `expires` its expiry. `key` is the [thumbprint](../explanation/devices-and-proofs.md#the-installation-key) of the server's installation key, which the new file must be bound to.

## Issue the new file

As the vendor, issue the server license again with `elm license issue`. Change only what the renewal is for, here the expiry, and give every other option as before: the same `--license-id`, `--bind-key`, `--feature`, `--lease-minutes`, and `--borrow-days`. An option that you leave out goes back to its default: for example, the lease time to 10 minutes, borrowing to off, and the grace period to 7 days. A new `--license-id` makes a license that the apps do not know: they refuse the server with `ELM_SERVER_PIN_MISMATCH`, because each app keeps the ID of the server license it checked first. [`elm license issue`](../reference/elm-cli.md#elm-license-issue) lists every option and its default.

Replace `<server-key>` with the thumbprint after `key:` above, all 43 characters exactly. Here the vendor's machine is also the server's, so the command writes straight into the server's `license` file, and `--force` lets it overwrite the old one. First keep a copy of the old file, so that you can put it back if the server refuses the new one: in PowerShell, type `Copy-Item server.elmlic server.elmlic.old`; in Bash or zsh, type `cp server.elmlic server.elmlic.old`. Then issue:

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id floating-server --kind server --feature "pro;seats=2" --expires 2100-12-31T00:00:00Z --bind-key <server-key> --lease-minutes 1 --borrow-days 1 --trust root.pub.pem --out server.elmlic --force
```

`elm` asks for the signing key's passphrase and prints:

```text
Passphrase for 'signing.key.pem':
license file: server.elmlic
license ID: floating-server
token ID: FAKEjti000000000000003
```

The signing key's certificate must be valid when you issue: the new file carries it. In real use, send the new file to the customer's administrator. As the administrator, keep a copy of the old file, and put the new one at the path of `license` in the server's settings.

Once the server has read the new file below, delete `server.elmlic.old`, unless you need it as a record: the server must never be given the old file again.

## Make the server read it

Tell the running server to read its license file again:

<!-- steps:run -->
```shell
elm server reload --data server-data
```

`elm` prints the license ID and the token ID of the new file:

```text
reloaded: license floating-server, token FAKEjti000000000000003
```

Compare the token ID with the one that `elm license issue` printed: they are the same. The server checks the new file as it does at a start: the product, its installation key, the machine fingerprints, and the trust store. Check the expiry:

<!-- steps:run -->
```shell
elm server status --data server-data
```

<!-- steps:expect -->
```text
expires: 2100-12-31 00:00:00Z
```

The leases and borrows that the server lent stay as they are.

If the new file does not pass a check, the server keeps the old license in use, and `elm` prints `error:`, the error code, and the reason, and ends with exit code 1. For example, a file bound to another installation key gives:

```text
error: ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to another installation key. This server's installation key is FAKE-SERVER-KEY-00000000000000000000000000K.
```

The thumbprint at the end of the message is the one that `elm server status` prints as `key`. Send it to the vendor, who issues the file again with this value for `--bind-key`.

Put the old file back at the path of `license`, here from `server.elmlic.old`, until you have a file that passes: the server reads the file again at its next start, and with a file that does not verify, it does not start. [Error codes](../reference/error-codes.md) lists every code.

## Change the seats or features

A renewal can also change what the license grants: give `--feature` with the new seats or features. When the new license grants fewer seats or drops a feature, the server does not take back leases it lent. While more seats of a feature are held than the new count, it refuses every renewal of that feature with `ELM_NO_SEATS`, until enough leases have expired; borrows count as held, so this can last until they expire. It refuses the renewals of a dropped feature with `ELM_FEATURE_NOT_LICENSED`, so those leases end within one lease time. Borrows stay valid until they expire, and the server refuses an early return of a borrow made before such a change with `ELM_RETURN_NOT_ALLOWED`; [Early return](../explanation/floating-licenses.md#early-return) explains which changes count.

## What a new file cannot take back

The server does not compare a file that you load by hand with the one it replaces: a file issued with `elm license issue` has no sequence number to compare. So after a renewal that grants fewer seats, the administrator can still load the old file again, and the server uses it until it expires. Apps that have already received the newer server license then refuse every new lease and renewal from that server with `ELM_LEASE_OUT_OF_BOUNDS`, because the answers name the older one, until the server uses the newer license again. To lease while the server keeps the older file, an app calls `ResetServerPin` (C: `elm_client_reset_server_pin`) or creates its `LicenseClient` anew; without `ExpectedServerLicenseId`, the client then trusts the next server license it sees, as on a first connection. A license file issued by hand cannot be revoked.

If you as the vendor must be able to reduce what a server grants, either issue its files with a short expiry, so that each renewal is the next chance to change them, or activate the server through `elm-service` and let it renew online (`service.url`). Such a server renews with a sequence number: if an older file of the same license is loaded by hand, the server goes back to its newest license within an hour. A server activated offline keeps a file loaded by hand until the administrator imports its next renewal. [Security model](../explanation/security-model.md#on-the-customers-license-server) lists this with the other limits on the customer's server.

## Servers activated with elm-service

A server that got its server license from `elm-service` with `elm server activate` keeps an activation in its data folder, and renews the license from it:

- Online, when `service.url` names the vendor's `elm-service`: the server renews on its own before the license expires, and `elm server refresh` renews at once.
- Offline: `elm server refresh-request` writes a request file, the vendor processes it in `elm-service`, and `elm server import` reads the answer.

Do not load a file issued by hand into such a server: a renewal of the activation writes its own license over it. With `service.url`, the server also goes back to the license of its activation within an hour when the file you loaded is an older one of the same license. [Activate a server license with elm-service](activate-a-server-license-with-elm-service.md) shows both ways step by step; [Server license commands](../reference/elm-cli.md#server-license-commands) lists the commands, and [`service`](../reference/server-configuration.md#service) the setting.

## See also

- [Running `elm-server`](../explanation/floating-licenses.md#running-elm-server): what the server gets from the vendor, and how it renews.
- [`elm license issue`](../reference/elm-cli.md#elm-license-issue), [`elm server reload`](../reference/elm-cli.md#elm-server-reload), and [`elm server status`](../reference/elm-cli.md#elm-server-status): every option.
- [Limit borrowing](limit-borrowing.md): the borrow limits of the server license and of the server.
