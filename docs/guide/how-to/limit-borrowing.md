---
sidebar_position: 11
---

# Limit borrowing

This guide is for a customer's administrator who runs `elm-server` and wants borrows to be shorter than the server license allows, or to keep seats free for normal leases. A borrow is a lease that an app takes away from the network until it expires; [Borrowing](../explanation/floating-licenses.md#borrowing) explains it. The guide starts where you have finished [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md): `elm-server` with a server license that allows borrowing, and that tutorial's app.

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the server's settings file `server.json`, its data folder `server-data`, the root public key `root.pub.pem`, and the tutorial's app in `app` with its data folder `client-data`.

If the server is stopped, start it again in a second terminal, as in [Start the server](../tutorials/use-a-floating-license.md#start-the-server). The app takes a normal lease before it borrows, so one of the two seats must be free: if you ran the app twice in the last hour, both seats are borrowed, and the app prints `ELM_NO_SEATS`. Wait until the borrows expire, an hour after they were made, or start the tutorial again from the beginning.

The examples use the values of the tutorial: the product `acme-cad`, the feature `pro` with two seats, and a server license that allows borrows of up to one day. Replace them with your own.

## Know the limits

Two parties limit borrows:

| Limit | Set by | Where |
| --- | --- | --- |
| The longest borrow | The vendor | The server license: `--borrow-days` of `elm license issue`. `0`, the default, turns borrowing off. |
| How often one installation may return a borrow early | The vendor | The server license: `--borrow-returns`, 2 in 30 days by default. |
| A shorter longest borrow | You | `borrow.maxDuration` in the server's settings. The server uses the shorter of this and the server license's limit. |
| Seats kept for normal leases | You | `borrow.reserve` in the server's settings, 0 by default: a borrow that would leave fewer free seats than this is refused. |

You can make borrowing stricter than the vendor allows, never looser: a longer `borrow.maxDuration` than the server license's limit has no effect, and the app refuses a borrow longer than the server license allows. To change the vendor's limits, the vendor issues a new server license, as in [Renew a server license](renew-a-server-license.md).

## Set the longest borrow

Add `borrow` to `server.json`, with `maxDuration` in the form `d.hh:mm:ss`: `00:30:00` is 30 minutes, and `7.00:00:00` would be 7 days. Keep every other setting as it is; with the settings of the tutorial, the file becomes:

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "borrow": {
    "maxDuration": "00:30:00"
  }
}
```

The server reads its settings only when it starts, so restart it. In the second terminal, press Ctrl+C to stop the server, then start it again:

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

The borrows that the server lent before stay as they are: the limit applies to new borrows. A value that is not a duration, or is out of its range (1 second to 100 years), stops the server with exit code 2 and a line that starts with `error:`.

## See a longer borrow refused

Run the tutorial's app again. It asks to borrow for one hour, longer than the new limit:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

The server has just started, so the app first waits for about a minute, as in the tutorial. Then it takes and returns a normal lease, and the borrow is refused. It ends with exit code 1 and prints:

<!-- steps:expect -->
```text
leased: pro
ELM_BORROW_NOT_ALLOWED: The borrow period is longer than allowed.
```

An app that borrows for 30 minutes or less gets its borrow. Ask the server what it holds:

<!-- steps:run -->
```shell
elm server status --data server-data
```

<!-- steps:expect -->
```text
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

The one borrow is the tutorial's, which the server kept across the restart; the refused borrow took no seat.

## Keep seats for normal leases

To keep seats from being borrowed, set `reserve` in `borrow`, for example one seat of each feature:

```json
  "borrow": {
    "maxDuration": "00:30:00",
    "reserve": 1
  }
```

After a restart, a borrow that would leave fewer free seats than `reserve` is refused with `ELM_NO_SEATS: No seats are free to borrow for the feature.` With two seats and one of them borrowed, as here, every new borrow is refused, and the free seat stays for normal leases. `reserve` counts the seats of each feature separately. The server checks the length of a borrow first, so the tutorial's app, which asks for one hour, still gets `ELM_BORROW_NOT_ALLOWED` with the `maxDuration` above; to see `ELM_NO_SEATS`, set `reserve` without `maxDuration`.

To lift the limits, take `borrow` out of `server.json` and restart the server.

## See also

- [Borrowing](../explanation/floating-licenses.md#borrowing): the limits, early returns, and borrows across restarts.
- [`borrow`](../reference/server-configuration.md#borrow) in the `elm-server` configuration.
- [`ELM_BORROW_NOT_ALLOWED` and `ELM_NO_SEATS`](../reference/error-codes.md) and the other error codes.
