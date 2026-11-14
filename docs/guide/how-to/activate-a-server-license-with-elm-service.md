---
sidebar_position: 22
---

# Activate a server license with elm-service

This guide is for a vendor who sells floating licenses through `elm-service`, and for the customer's administrator who runs `elm-server`. Instead of issuing the server license by hand, the vendor creates it in the service, and the administrator activates the server with its license key. The server then renews the license from the service: on its own when it can reach the service, or with request and answer files when it cannot. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md); the server is set up here, as in [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md).

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

In this guide, one machine plays both parts: you type the vendor's and the administrator's commands in the same first terminal, in the folder of the tutorial. In real use, the vendor works on its own machine, and the administrator on the server's.

- The `tools` folder of the tutorial must be on the `PATH` of each terminal you use. If you closed the terminal, add it again as in [Before you start](../tutorials/activate-an-app-online.md#before-you-start) of the tutorial.
- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.
- You need the ID of the product `acme-cad`, `<product-id>` below, which the tutorial printed when it created the product. If you no longer have it, `elm admin request GET /v1/admin/products --url http://127.0.0.1:8080` lists the products with their IDs.
- Port 7070 must be free: the server listens on it. If another program uses it, the server says so when it starts, as [Start the server](../tutorials/use-a-floating-license.md#start-the-server) of the floating license tutorial shows.
- The tutorial folder must be inside your home folder (your user folder on Windows), and on Linux and macOS it must not be writable by your group or by others: the server keeps its private key in a folder inside it ([Which folders the server accepts](../reference/server-configuration.md#which-folders-the-server-accepts)). `chmod go-w .` in the folder makes sure of the second. In this guide you run the server and the `elm server` commands as yourself; in real use, run the `elm server` license commands as the user that `elm-server` runs as ([elm server](../reference/elm-cli.md#elm-server)).
- The full path of the tutorial folder may be at most 84 bytes in UTF-8 on Windows and Linux, and 80 on macOS; a character outside ASCII takes 2 to 4 bytes. The server keeps a socket file in `server-data` inside the folder, and the path of a socket file has a limit ([Data folder](../reference/server-configuration.md#data-folder)). If the server prints `error: The path of the administration socket …` when it starts, move the folder to a shorter path inside your home folder.

Download `elm-server` from the same release as the tutorial's tools: the archive `elm-server-<version>-<RID>` on the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases), not the one whose name ends in `-symbols`. Check it as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`, and extract it into `tools`. In PowerShell on Windows, type `Expand-Archive elm-server-<version>-<RID>.zip -DestinationPath tools -Force`: `-Force` lets it overwrite the `LICENSE` file that is already there. On Linux and macOS, type `tar -xzf elm-server-<version>-<RID>.tar.gz -C tools`.

<!-- steps:prebuilt name=elm-server -->

## Create a server license in the service

A server license comes from a policy whose `licenseKind` is `server`. In such a policy, `binding` must be `nodeLocked`, the default: each license file is bound to the server's installation key. `floating` `onPremises` says that the customer's `elm-server` lends the seats. The other settings may keep their defaults:

- `maxActivations` 1: a license activates one server.
- `tokenLifetime` 30 days and `refreshWindow` 20 days: the service issues each file for 30 days, and the server renews it in the last 20.
- `leaseDuration` 10 minutes, and no `borrowLimit`: apps hold each lease for up to 10 minutes and cannot borrow. [Choose the settings](set-up-a-policy.md#choose-the-settings) lists them all.

Create the policy for the product of the tutorial, replacing `<product-id>` with its ID:

<!-- steps:file path=server-policy.json -->
```json
{"productId":"<product-id>","name":"Floating server","licenseKind":"server","floating":"onPremises"}
```

<!-- steps:run capture=server-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body server-policy.json --url http://127.0.0.1:8080
```

Its `id` is `<server-policy-id>` below. A server license needs `seats` for each feature: the number of copies of the app that may hold the feature at the same time. Create one with five seats of `pro`:

<!-- steps:file path=server-license.json -->
```json
{"policyId":"<server-policy-id>","name":"Customer server","features":[{"code":"pro","seats":5}]}
```

<!-- steps:run capture=server-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body server-license.json --url http://127.0.0.1:8080
```

`elm` prints the license. The `key` at the end is its license key, `<server-license-key>` below, which the vendor gives to the customer's administrator; the service shows it only in this answer. The `id` at the start is the license ID, which the server shows later:

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Customer server","features":[{"code":"pro","ver":null,"seats":5,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000S-ERVER-00000"}
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

- `trust.rootKeys` is the vendor's root public key, which certifies the service's signing key.
- `service.url` is the vendor's `elm-service`. With it, the server renews its license there on its own. Use the service's `https` address in real use; `http` is accepted on a loopback address only. The environment variable `ELM_SERVICE_URL` sets it too.
- `license` is where the license file goes. It does not exist yet: the activation writes it.

[elm-server configuration](../reference/server-configuration.md) lists every setting. Start the server once to make its installation key, the key the license is bound to:

<!-- steps:run exit=1 -->
```shell
elm-server --config server.json
```

The server stops at once with exit code 1, because it has no license yet, and prints one line that starts with `error: The server license 'server.elmlic' does not exist.` and names its installation key. That is expected; the key stays in `server-data`.

## Activate the server

Activate the server with the license key. The `elm server` commands that activate and renew do not read `server.json`, so each one below names the product, the data folder, and the root public key, and the ones that write a license file name its path:

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

The server keeps running and prints its log. Its last line is `server.started`, with the license ID of the activation:

```text
info: elm-server[1]
      server.started: listening on port 7070 as https://127.0.0.1:7070/, key FAKE-SERVER-KEY-00000000000000000000000000K, license 01960000-0000-7000-8000-000000000302, in 68 ms; reclaim window until 2026-01-15T09:41:00.0000000+00:00
```

## Renew online

With `service.url`, the server renews the license on its own. It asks the service when the file enters its last `refreshWindow` days, here 20 days before it expires. When a renewal fails, it tries again an hour later, or after the time that the service names, at most 24 hours. It stops trying when the service no longer knows the activation, or refuses the renewal because the server looks like a copy of another ([Clone detection with the sequence number](../explanation/activation.md#clone-detection-with-the-sequence-number)) or its [proof](../explanation/devices-and-proofs.md#proofs) does not verify. Each new file goes to the path of `license`, and the server reads it while it runs, so apps keep their leases.

To renew at once, for example after the vendor added seats to the license, run `elm server refresh`: it renews whenever you run it, also before the `refreshWindow`. It needs `--service` too, since it does not read `server.json`:

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

If the server keeps its old license instead, `elm` prints `error: the server kept its license:` with the code and the reason, and ends with exit code 1; the new file is written all the same. One such code is `ELM_CLOCK_TAMPERED`, when the server's clock was set forward and back again:

```text
error: the server kept its license: ELM_CLOCK_TAMPERED: The server license does not verify: The clock has been set back. This server's installation key is FAKE-SERVER-KEY-00000000000000000000000000K.
```

Every message of a license that does not verify ends with the server's installation key; the key is not the cause here. A renewal by the server itself hands the server the service's time, which clears the error; `elm` is a separate program and cannot. Correct the clock. The server then takes a new license at its next online renewal, or reads this file when it starts again.

`elm server status --json` shows whether the server renews on its own. Like the other `elm server` commands that ask the running server, it needs only the data folder:

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

A server that cannot reach the service renews with files instead: it writes a request, the vendor answers it in the service, and the server imports the answer. While `service.url` is set and the server runs, `elm server refresh-request` refuses with `ELM_INVALID_STATE`, since such a server renews online. So remove `service` from `server.json`, and make sure `ELM_SERVICE_URL` is not set in the server's terminal either:

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

The administrator sends `refresh.elmreq` to the vendor, who answers it with `elm offline process`, as for an app's offline request in [Activate a device without a network](activate-a-device-without-a-network.md). With `ELM_API_KEY` set, `elm` sends the request to the administration API, which writes it to the audit log; without it, to the service's public endpoint for devices:

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in refresh.elmreq --out refresh.elmresp
```

`elm` prints the activation ID, which the service gave the server when it activated, the license ID, the new sequence number, and the answer file:

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

## Activate offline, and move a server

A server that never reaches the service is activated with files too, with no `service` in `server.json`. Start the server once to make its key, as above. Then `elm server activate-request --key <server-license-key>` writes the request, the vendor answers it with `elm offline process`, and `elm server import` reads the answer and writes the license file; start the server after that. It then renews offline, as above.

To move the license to another server, stop the server first, and free its activation: `elm server deactivate` does it online. Offline, `elm server deactivate-proof` removes the activation and writes a deactivation proof, which the vendor processes with `elm offline process`; once the service has it, `elm server forget-proof` lets the server activate again. Do not just delete `server-data`: the service would keep counting the activation, and with `maxActivations` 1, the license could not activate another server. [Server license commands](../reference/elm-cli.md#server-license-commands) lists every option of these commands.

When you are done trying, press Ctrl+C in the third terminal to stop the server, and in the second terminal to stop the service.

## See also

- [Renew a server license](renew-a-server-license.md): a server license that the vendor issues by hand.
- [Running `elm-server`](../explanation/floating-licenses.md#running-elm-server): what the server gets from the vendor, and how it renews.
- [Server license commands](../reference/elm-cli.md#server-license-commands) and [`service`](../reference/server-configuration.md#service): every option and setting.
