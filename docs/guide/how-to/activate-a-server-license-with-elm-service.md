---
sidebar_position: 22
---

# Activate a server license with elm-service

This guide is for a vendor who sells floating licenses through `elm-service`, and for the customer's administrator who runs `elm-server`. Instead of issuing the server license by hand, the vendor creates it in the service, and the administrator activates the server with its license key. The server then renews the license from the service: on its own when it can reach the service, or with request and answer files when it cannot. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md); the server is set up here, as in [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md).

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. In the tutorial, one machine plays both parts; in real use, the vendor works on its own machine, and the administrator on the server's.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service).
- Ports 7070 and 7071 must be free: the server listens on them.

Download `elm-server` from the same release as the tutorial's tools: the archive `elm-server-<version>-<RID>` on the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases). Check it as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`, and extract it into `tools`. In PowerShell on Windows, type `Expand-Archive elm-server-<version>-<RID>.zip -DestinationPath tools -Force`: `-Force` lets it overwrite the `LICENSE` file that is already there. On Linux and macOS, type `tar -xzf elm-server-<version>-<RID>.tar.gz -C tools`.

<!-- steps:prebuilt name=elm-server -->

## Create a server license in the service

A server license comes from a policy whose `licenseKind` is `server`. Such a policy binds each license to one server (`binding` `nodeLocked`, the default), and its other settings may keep their defaults: the service issues each file for 30 days (`tokenLifetime`), and the server renews it in the last 20 days (`refreshWindow`). Create the policy for the product of the tutorial, replacing `<product-id>` with its ID:

<!-- steps:file path=server-policy.json -->
```json
{"productId":"<product-id>","name":"Floating server","licenseKind":"server"}
```

<!-- steps:run capture=server-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body server-policy.json --url http://127.0.0.1:8080
```

Its `id` is `<server-policy-id>` below. A server license gives each feature a number of seats, which `seats` sets: here five seats of `pro`.

<!-- steps:file path=server-license.json -->
```json
{"policyId":"<server-policy-id>","name":"First customer's server","features":[{"code":"pro","seats":5}]}
```

<!-- steps:run capture=server-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body server-license.json --url http://127.0.0.1:8080
```

`elm` prints the license. The `key` at the end is its license key, `<server-license-key>` below, which the vendor gives to the customer's administrator; the service shows it only in this answer. The `id` at the start is the license ID, which the server shows later:

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"First customer's server","features":[{"code":"pro","ver":null,"seats":5,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000S-ERVER-00000"}
```

## Set up the server

Create `server.json` in the tutorial folder:

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
  "service": {
    "url": "http://127.0.0.1:8080"
  }
}
```

- `trust.rootKeys` is the vendor's root public key, the one the service's signing key is certified by.
- `service.url` is the vendor's `elm-service`. With it, the server renews its license there on its own. Use the service's `https` address in real use; `http` is accepted on a loopback address only.
- `license` is where the license file goes. It does not exist yet: the activation writes it.

[elm-server configuration](../reference/server-configuration.md) lists every setting. Start the server once to make its installation key, the key the license is bound to:

<!-- steps:run exit=1 -->
```shell
elm-server --config server.json
```

The server stops with exit code 1 and `error: The server license 'server.elmlic' does not exist.`, followed by its installation key and how to get a license. That is expected; the key stays in `server-data`. On Linux and macOS, if the folder is writable by your group or by others, the server refuses to keep its key there; type `chmod go-w .`, as in [Set up the server](../tutorials/use-a-floating-license.md#set-up-the-server) of the floating license tutorial, and start it again.

## Activate the server

Activate the server with the license key. The `elm server` commands do not read `server.json`, so each one names the product, the data folder, and the root public key, and the commands that write a license file name its path:

<!-- steps:run capture=first-seq pattern="seq (1):" -->
```shell
elm server activate <server-license-key> --product acme-cad --data server-data --trust root.pub.pem --service http://127.0.0.1:8080 --license server.elmlic
```

`elm` prints the license ID, the license's sequence number, and the path it wrote, and then that the server is not running yet:

<!-- steps:expect -->
```text
The server is not running; it reads the license when it starts.
```

The full output looks like this:

```text
license 01960000-0000-7000-8000-000000000302, seq 1: server.elmlic
The server is not running; it reads the license when it starts.
```

The sequence number starts at 1 and goes up with each renewal: a server, and the service, can tell a newer file of the license from an older one. The activation itself stays in `server-data`, and the service counts it against the license's activations.

Open a third terminal in the tutorial folder, with `tools` on its `PATH`, and start the server there:

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

The log ends with the line `server.started`, as in [Start the server](../tutorials/use-a-floating-license.md#start-the-server) of the floating license tutorial, with the license ID of the activation.

## Renew online

With `service.url`, the server renews the license on its own. It asks the service when the file enters its last `refreshWindow` days, here 20 days before it expires. When a renewal fails, it tries again an hour later, or after the time that the service names, at most 24 hours, and keeps trying until the service takes the server for a copy or refuses its proof. Each new file goes to the path of `license`, and the server reads it while it runs, so apps keep their leases.

To renew at once, for example after the vendor added seats to the license, run `elm server refresh`. It needs `--service` too, since it does not read `server.json`:

<!-- steps:run capture=second-seq pattern="seq (2):" -->
```shell
elm server refresh --product acme-cad --data server-data --trust root.pub.pem --service http://127.0.0.1:8080 --license server.elmlic
```

`elm` prints the new file with the next sequence number, and that the running server read it:

<!-- steps:expect -->
```text
The server reloaded the license.
```

The full output looks like this:

```text
license 01960000-0000-7000-8000-000000000302, seq 2: server.elmlic
The server reloaded the license.
```

If the server keeps its license instead, `elm` prints `error: the server kept its license:` with the code and the reason, and ends with exit code 1; the new file is written all the same. One such code is `ELM_CLOCK_TAMPERED`: the server checks its clock against the times it has seen, and `elm`, a separate program, cannot tell the server the time that the service answered with. The server then reads the new file at its next online renewal, or when it starts again.

`elm server status --json` shows whether the server renews on its own:

<!-- steps:run -->
```shell
elm server status --data server-data --json
```

The last member of the answer is `autoRefresh`:

<!-- steps:expect -->
```text
  "autoRefresh": true
```

## Renew offline

A server that cannot reach the service renews with files instead: it writes a request, the vendor answers it in the service, and the server imports the answer. The server must not renew online at the same time, so remove `service` from `server.json`:

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  }
}
```

The server reads `server.json` only when it starts. Press Ctrl+C in the third terminal, and start the server there again:

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

The activation in `server-data` stays the same. Write the request file:

<!-- steps:run -->
```shell
elm server refresh-request --product acme-cad --data server-data --trust root.pub.pem --out refresh.elmreq
```

<!-- steps:expect -->
```text
request: refresh.elmreq
```

While `service.url` is set and the server runs, `elm server refresh-request` refuses with `ELM_INVALID_STATE`: such a server renews online, with `elm server refresh`.

The administrator sends `refresh.elmreq` to the vendor, who answers it with `elm offline process`, as for an app's offline request in [Activate a device without a network](activate-a-device-without-a-network.md):

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in refresh.elmreq --out refresh.elmresp
```

`elm` prints the activation, the license, the new sequence number, and the answer file:

```text
activation 01960000-0000-7000-8000-000000000402, license 01960000-0000-7000-8000-000000000302, seq 3: refresh.elmresp
```

The vendor sends `refresh.elmresp` back, and the administrator imports it:

<!-- steps:run capture=third-seq pattern="seq (3):" -->
```shell
elm server import refresh.elmresp --product acme-cad --data server-data --trust root.pub.pem --license server.elmlic
```

<!-- steps:expect -->
```text
The server reloaded the license.
```

As online, `elm` first prints `license 01960000-0000-7000-8000-000000000302, seq 3: server.elmlic`. Renew this way before the file expires: without `service.url`, the server does not renew on its own.

## Activate offline

A server that never reaches the service is activated with files too. `elm server activate-request` takes the license key and writes the request, the vendor answers it with `elm offline process`, and `elm server import` reads the answer and writes the license file. The server then renews offline, as above. [Server license commands](../reference/elm-cli.md#server-license-commands) lists every option, and `elm server deactivate` frees the activation when the server moves to another machine.

## See also

- [Renew a server license](renew-a-server-license.md): a server license that the vendor issues by hand.
- [Running `elm-server`](../explanation/floating-licenses.md#running-elm-server): what the server gets from the vendor, and how it renews.
- [Server license commands](../reference/elm-cli.md#server-license-commands) and [`service`](../reference/server-configuration.md#service): every option and setting.
