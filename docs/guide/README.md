# ElasticLM documentation

ElasticLM lets the vendor of a .NET or C/C++ application issue licenses, check them on their customers' machines, and count concurrent use. This documentation is for the people who issue the licenses, the developers who build the checks into an app, and the administrators who run a license server. It assumes you know your own platform; it does not assume you have read the design.

> **Status:** 1.0 is not released yet. These pages describe the `main` branch. The tutorials are still being written: [Issue a license and verify it in a .NET app](tutorials/verify-a-license-offline.md) and [Run a license server and lease a floating license in a .NET app](tutorials/use-a-floating-license.md) are ready. The how-to guides are still being written. The reference is partly written: the `elm` commands, the configuration of `elm-server` and `elm-service`, the administration API, the device protocol, the token and file formats, the error codes, the logs, the .NET API, and the C API. The explanation pages below explain how ElasticLM works.

## How the documentation is organized

Each page is one of four kinds. Pick the kind that fits what you need now.

| Kind | Read it when |
| --- | --- |
| Tutorials | You are new and want to get something working, step by step. |
| How-to guides | You have a task in mind, such as rotating a signing key. |
| Reference | You need the exact name of a command, setting, field, or error code. |
| Explanation | You want to understand how a part works, why, and what it protects against. |

## Where to start

Read [Overview](explanation/overview.md) first, whoever you are. It names the parts and who runs each one. Then:

**If you issue licenses (the vendor).** You hold the keys that make every license valid, and you may run the hosted service.

1. [Issue a license and verify it in a .NET app](tutorials/verify-a-license-offline.md): a tutorial that creates the keys, issues a license, and checks it.
2. [Run a license server and lease a floating license in a .NET app](tutorials/use-a-floating-license.md): a tutorial that issues a server license with seats and runs the server that lends them.
3. [Trust chain](explanation/trust-chain.md): the root key, signing keys, and how to keep them safe.
4. [License files and verification](explanation/license-files-and-verification.md): what you issue and what an app checks.
5. [Activation](explanation/activation.md): how the hosted service binds licenses to devices.
6. [Floating licenses](explanation/floating-licenses.md): seats, leases, and server licenses.
7. [Security model](explanation/security-model.md): what is protected, what is not, and your duties.

**If you build ElasticLM into an app (the developer).** Your app embeds the root public keys and checks licenses, activations, or leases.

1. [Issue a license and verify it in a .NET app](tutorials/verify-a-license-offline.md): a tutorial that builds a .NET app that verifies a license offline.
2. [Run a license server and lease a floating license in a .NET app](tutorials/use-a-floating-license.md): a tutorial that builds a .NET app that leases and borrows seats from a license server.
3. [License files and verification](explanation/license-files-and-verification.md)
4. [Devices and proofs](explanation/devices-and-proofs.md): the installation key that identifies a device.
5. [Activation](explanation/activation.md) or [Floating licenses](explanation/floating-licenses.md), whichever your product uses.
6. [Security model](explanation/security-model.md): what your app must check and what it cannot prevent.
7. [.NET API](reference/dotnet-api.md) or [C API](reference/c-api.md): every type, function, and option of the library your app embeds.

**If you run a license server for your organization (the customer's administrator).** You run `elm-server`, which lends your organization's seats.

1. [Run a license server and lease a floating license in a .NET app](tutorials/use-a-floating-license.md): a tutorial that sets up `elm-server` with a server license and checks what it lends.
2. [Floating licenses](explanation/floating-licenses.md): leases, borrowing, and what happens when the server restarts.
3. [Devices and proofs](explanation/devices-and-proofs.md): how clients prove who they are.
4. [Security model](explanation/security-model.md): what the server's key and logs protect.

**If you connect a sales or billing system (the vendor's systems).** Your system calls the hosted service's administration API with an API key.

1. [Activation](explanation/activation.md): products, policies, licenses, and devices as the service sees them.
2. [Security model](explanation/security-model.md): API keys, roles, and the audit log.
3. [Administration API](reference/administration-api.md): every route your system calls, with its request and answer.

To understand why ElasticLM is built the way it is, read [Design choices](explanation/design-choices.md).

**Reference.** To look up an exact name, use [elm command reference](reference/elm-cli.md), [elm-server configuration](reference/server-configuration.md), [elm-service configuration](reference/service-configuration.md), [Administration API](reference/administration-api.md), [Device protocol](reference/device-protocol.md), [Token and file formats](reference/formats.md), [Error codes](reference/error-codes.md), [Logs](reference/logs.md), [.NET API](reference/dotnet-api.md), or [C API](reference/c-api.md).

## Elsewhere

- Reporting a vulnerability, and checking the files you download: [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md).
- Running the `elm-service` container image: [docs/container.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/container.md).
- The design, in Japanese: [docs/design.ja.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/design.ja.md).
