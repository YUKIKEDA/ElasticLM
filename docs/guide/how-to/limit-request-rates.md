---
sidebar_position: 16
---

# Limit request rates

This guide is for the vendor's operator who runs `elm-service`. The service refuses requests that come faster than its rate limits allow, so that one caller cannot keep it busy for everyone else. The guide sets the four limits in the settings file, shows what the service does with a value it does not accept, and shows what a caller sees when it is refused. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md).

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The service runs in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service); if it is stopped, start it there again.

## What each limit counts

The service has four limits, each a number of requests per second. A caller may also send that many requests at once after a quiet second: the burst is the same number. A source is the address a request comes from: one IPv4 address, or one IPv6 /64 network.

| Setting | Default | What it counts |
| --- | --- | --- |
| `rateLimit.perIp` | `20` | Requests from one source to every path other than the device endpoints: the administration API, the admin UI, and `/health`. Counted before the caller is authenticated. |
| `rateLimit.perKey` | `10` | Administration API requests with one API key, or of one administrator who signed in with OIDC or in the admin UI. |
| `rateLimit.clientPerIp` | `20` | Requests from one source to the device endpoints, which apps call: activations, trials, nonces, offline files, and leases. Counted before the request's proof is checked. |
| `rateLimit.clientPerKey` | `2` | Device requests of one installation key, that is, of one installation of an app. |

One activation or refresh is two device requests, the nonce and the request itself, so the default `clientPerIp` lets one source activate ten devices a second. Each value is a whole number from 1 to 10000. Keep `perKey` at or below `perIp`: an API key's requests from one source count against both, so for such a key a larger `perKey` never takes effect.

## Set the limits

Add `rateLimit` to `service.json`. Leave out a limit to keep its default. This example allows more requests per source, as for a service whose administrators share one address, and keeps the defaults per key:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  },
  "rateLimit": {
    "perIp": 100,
    "perKey": 10,
    "clientPerIp": 100,
    "clientPerKey": 2
  }
}
```

The service reads its settings only when it starts. Press Ctrl+C in the second terminal, and start the service there again; `ELM_SIGNING_PASSPHRASE` is still set in that terminal:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

The log ends with the line `service.started`, as in the tutorial; it does not show the limits. You can also give a limit as an environment variable, such as `ELM_RATELIMIT_PERIP=100`, which takes the place of the value in the file; [elm-service configuration](../reference/service-configuration.md#how-settings-are-given) says how.

## When a value is out of range

A value outside 1 to 10000 is a setting error: the service does not start. To see it without stopping the running service, write the settings to a second file with `perIp` set to `0`:

<!-- steps:file path=rate-check.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  },
  "rateLimit": {
    "perIp": 0
  }
}
```

Start a service with it in the first terminal:

<!-- steps:run exit=2 capture=bad-setting stream=stderr pattern="(rateLimit\.perIp) must be between 1 and 10000\." -->
```shell
elm-service --config rate-check.json
```

The service checks its settings before it reads the signing key, so it needs no passphrase for this. It ends at once with exit code 2 and prints on standard error:

```text
rateLimit.perIp must be between 1 and 10000.
```

A value that is not a whole number, such as `2.5` or `"20"`, also stops the service with exit code 2, with a line that starts with `The settings file 'rate-check.json' is not valid:` and names the setting as `$.rateLimit.perIp`. Delete `rate-check.json` afterwards.

## What a refused caller sees

The service answers a request over a limit with the status 429, the error code `ELM_RATE_LIMITED`, and the header `Retry-After: 1`: the caller may try again after one second. The answer is a problem document whose `detail` names the limit:

| `detail` | Limit |
| --- | --- |
| `Too many requests from this address.` | `perIp` or `clientPerIp` |
| `Too many requests with this API key.` | `perKey`, for an API key |
| `Too many requests from this administrator.` | `perKey`, for an OIDC access token or an admin UI session |
| `Too many requests with this installation key.` | `clientPerKey` |

`elm admin request` prints such an answer on standard error and ends with exit code 1:

```text
error: the service answered 429.
{"type":"about:blank","title":"ELM_RATE_LIMITED","status":429,"detail":"Too many requests with this API key.","code":"ELM_RATE_LIMITED"}
```

A script that calls the administration API directly waits for the `Retry-After` time and sends the request again; one that uses `elm admin request` does not see the header, since `elm` does not print it: it sees exit code 1 and `ELM_RATE_LIMITED` on standard error, and waits at least one second. In an app, `ActivationClient` returns `ActivationResult.Failed` with the error `ELM_RATE_LIMITED` and its `RetryAfter`, and does not send the request again by itself; [Handle errors](handle-errors.md) shows an app that tells its user to try later, and prints:

```text
retry later: ELM_RATE_LIMITED
retry after: 1 s
reason: Too many requests with this installation key.
```

In the background refresh, the client tries again an hour later.

## Behind a proxy

The service takes the source of a request from the connection, and does not read `X-Forwarded-For` or `Forwarded`: those headers can be written by any caller. Behind a reverse proxy or a load balancer, every request then comes from the proxy's address, and `perIp` and `clientPerIp` count all callers together. Raise both to what all your callers send together, and limit each caller at the proxy, if it can. A load balancer's health checks of `/health` count against `perIp` of its address too. The audit log then names the proxy's address as well.

## Several nodes and restarts

Each `elm-service` process counts by itself, in memory: with several nodes behind a load balancer, a caller may send up to the limit to each node. Set the limits per node. The counts start again from zero when the service restarts.

## See also

- [`rateLimit`](../reference/service-configuration.md#ratelimit) in the `elm-service` configuration.
- [`ELM_RATE_LIMITED`](../reference/error-codes.md) in the error codes.
- [Handle errors](handle-errors.md): what an app does with an error that asks it to try later.
