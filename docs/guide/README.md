# ElasticLM documentation

ElasticLM lets the vendor of a .NET or C/C++ application issue licenses, check them on their customers' machines, and count concurrent use. This documentation is for the people who issue the licenses, the developers who build the checks into an app, and the administrators who run a license server. It assumes you know your own platform; it does not assume you have read the design.

> **Status:** 1.0 is not released yet. These pages describe the `main` branch. The tutorials and how-to guides are still being written. The reference is partly written: the `elm` commands, the configuration of `elm-server` and `elm-service`, the error codes, and the logs. The explanation pages below explain how ElasticLM works.

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

1. [Trust chain](explanation/trust-chain.md): the root key, signing keys, and how to keep them safe.
2. [License files and verification](explanation/license-files-and-verification.md): what you issue and what an app checks.
3. [Activation](explanation/activation.md): how the hosted service binds licenses to devices.
4. [Floating licenses](explanation/floating-licenses.md): seats, leases, and server licenses.
5. [Security model](explanation/security-model.md): what is protected, what is not, and your duties.

**If you build ElasticLM into an app (the developer).** Your app embeds the root public keys and checks licenses, activations, or leases.

1. [License files and verification](explanation/license-files-and-verification.md)
2. [Devices and proofs](explanation/devices-and-proofs.md): the installation key that identifies a device.
3. [Activation](explanation/activation.md) or [Floating licenses](explanation/floating-licenses.md), whichever your product uses.
4. [Security model](explanation/security-model.md): what your app must check and what it cannot prevent.

**If you run a license server for your organization (the customer's administrator).** You run `elm-server`, which lends your organization's seats.

1. [Floating licenses](explanation/floating-licenses.md): leases, borrowing, and what happens when the server restarts.
2. [Devices and proofs](explanation/devices-and-proofs.md): how clients prove who they are.
3. [Security model](explanation/security-model.md): what the server's key and logs protect.

**If you connect a sales or billing system (the vendor's systems).** Your system calls the hosted service's administration API with an API key.

1. [Activation](explanation/activation.md): products, policies, licenses, and devices as the service sees them.
2. [Security model](explanation/security-model.md): API keys, roles, and the audit log.

To understand why ElasticLM is built the way it is, read [Design choices](explanation/design-choices.md).

**Reference.** To look up an exact name, use [elm command reference](reference/elm-cli.md), [elm-server configuration](reference/server-configuration.md), [elm-service configuration](reference/service-configuration.md), [Error codes](reference/error-codes.md), or [Logs](reference/logs.md).

## Elsewhere

- Reporting a vulnerability, and checking the files you download: [SECURITY.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md).
- Running the `elm-service` container image: [docs/container.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/container.md).
- The design, in Japanese: [docs/design.ja.md](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/design.ja.md).
