---
sidebar_position: 8
---

# Revoke a signing key

This guide is for a vendor whose signing key has leaked, or may have, and for the developers and administrators who must act with them. A thief with the key can sign licenses that every app accepts, also with an issue time in the past, so neither the key's certificate nor revoking licenses stops them. Only revoking the key ID does: every license and cloud lease that the key signed then fails with `ELM_KEY_REVOKED`, the legitimate ones too, wherever the list of revoked key IDs reaches. The guide replaces the key in `elm-service` and revokes the old one there in one restart, shows what the revocation does with a license file, and says what must reach the apps, the license servers, and the customers. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the root key, the signing key, and the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md). [When a signing key leaks](../explanation/trust-chain.md#when-a-signing-key-leaks) explains the reasons for each step.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).

The key to revoke here is the one the service signs with now: the file that `signing.key` names in `service.json`, whose key ID is the `keyId` of [`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key). The commands below use the file names of the tutorial: the root key `root.key.pem`, the root public key `root.pub.pem`, the service's signing key `signing.key.pem` with its public key `signing.pub.pem` and its certificate `signing.cert`, and the settings file `service.json`. If you have replaced the key since, for example with [Rotate the signing key](rotate-the-signing-key.md), use the files of the key the service uses now in place of `signing.key.pem`, `signing.pub.pem`, and `signing.cert`, such as `signing-2.key.pem`, and give the new key file names that no key has used yet, such as `signing-3.key.pem`, `signing-3.pub.pem`, and `signing-3.cert`.

In the outputs below, the key IDs, the token ID, and the dates are fake. On your machine they are different every time.

## Plan the order

Revoking the key ID works only where the list reaches, and it stops the legitimate licenses of that key too. Take the steps in this order:

1. Replace the key in `elm-service`, and revoke the old one there, as below. `elm-service` does not start when its own signing key is revoked, so both changes go in at the same restart.
2. Give every customer a license signed by the new key. A device that refreshes online gets one at its next refresh, and a device without a network when its refresh files are carried, as in [Refresh the license file](activate-a-device-without-a-network.md#refresh-the-license-file). Issue again every license file that you gave by hand: files from `elm license issue`, unbound license files, and server licenses made with `elm`. A license server activated through `elm-service` gets a new server license at its next refresh, online or by refresh files.
3. Add the old key ID to `trust.revokedKeyIds` of the license servers you run, and restart each one, since `elm-server` reads the setting only when it starts; tell your customers to do the same on theirs. Do it only once the server has a server license signed by the new key: with a server license signed by a revoked key, `elm-server` does not start.
4. Ship an app version whose trust store lists the old key ID as revoked. Until a device runs it, the device accepts every license of the old key, forged ones included. Once it runs it, the device refuses an old-key license file it still keeps, also in its grace period: ship it after your devices have refreshed, or have the app refresh when it sees `ELM_KEY_REVOKED`, as below.

## A license file that the key signed

To see what the revocation does, issue a license file with the current key now, as a vendor issues a file by hand. This one has no expiry: such a file can be stopped only by revoking its key.

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id old-key-license --feature pro --expires never --trust root.pub.pem --out old-key.elmlic
```

`elm` asks for the signing key's passphrase, warns about the missing expiry, and prints the license file:

```text
Passphrase for 'signing.key.pem':
warning: The license has no expiry. It cannot be revoked once delivered.
license file: old-key.elmlic
license ID: old-key-license
token ID: FAKEjti000000000000002
```

The warning says that this file cannot be revoked on its own. The service has no record of a file issued by hand, so there is nothing to revoke there, and without an expiry the file never stops being valid. Revoking the key that signed it stops it in every app and server whose trust store lists the key as revoked, as below.

## Find the key ID

Print the key ID of the key to revoke, from its public key:

<!-- steps:run capture=old-key-id -->
```shell
elm key thumbprint signing.pub.pem
```

`elm` prints it, `<old-key-id>` below:

```text
FAKE-SIGNING-KEY-ID-000000000000000000000S1
```

It is the `signing key` that the service's line `service.started` names, and the `keyId` of [`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key).

## Replace the key and revoke it in the service

Create a new signing key and certify it, as in [Create the new signing key](rotate-the-signing-key.md#create-the-new-signing-key). In short: remove `ELM_KEY_PASSPHRASE` from the terminal first, so that `elm` asks, and give the new key a new passphrase, which differs from the root key's and from the leaked key's. Create the key on the machine that runs the service, and run `elm key certify` on the offline machine that holds the root key: carry the new public key there, and carry back only the certificate. Never bring the root key to the machine that runs the service. Add `log` to `--use` when your settings set `usageLog.directory`:

<!-- steps:run -->
```shell
elm key signing create --out signing-2.key.pem --public-out signing-2.pub.pem
elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2.cert
```

<!-- steps:expect -->
```text
certificate: signing-2.cert
uses: license, lease
```

In `service.json`, change `signing` to the new key and its certificate, and list the old key ID in `trust.revokedKeyIds`. Replace `<old-key-id>` with the key ID that `elm key thumbprint` printed:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing-2.key.pem",
    "certificates": ["signing-2.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"],
    "revokedKeyIds": ["<old-key-id>"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

Restart the service: press Ctrl+C in the second terminal, read the new key's passphrase into `ELM_SIGNING_PASSPHRASE` there, and start it again, as in [Restart the service with the new key](rotate-the-signing-key.md#restart-the-service-with-the-new-key), which also lists what the service prints when it does not start:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

The line `service.started` of its log names the new key. The service now signs with the new key only, and it refuses to start with the old one: with `signing` set back to the old key, it ends with exit code 1 and the line `No certificate that the trust store vouches for lets the signing key FAKE-SIGNING-KEY-ID-000000000000000000000S1 sign for License, Lease at 2026-01-15T09:30:00Z.`

Once the service runs with the new key, take the old key's files out of use, as in [Take the old key out of use](rotate-the-signing-key.md#take-the-old-key-out-of-use): keep its public key `signing.pub.pem`, or the key ID you printed, to tell others what to revoke; keep its certificate out of `signing.certificates`; and delete the leaked private key `signing.key.pem` and every copy of it, so that no one gives it to the service or signs with it again.

## See what the revocation does

The service's list reaches no app. An app whose trust store lists no revoked key still accepts the old key's file. `elm license verify` checks a file with the same verifier as an app; without `--revoked`, it shows what such an app sees:

<!-- steps:run -->
```shell
elm license verify old-key.elmlic --trust root.pub.pem --product acme-cad --feature pro
```

<!-- steps:expect -->
```text
valid: license old-key-license, product acme-cad, kind standard
expires: never
features: pro
```

With `--revoked` and the old key ID, it shows what an app version that lists the key sees:

<!-- steps:run exit=1 -->
```shell
elm license verify old-key.elmlic --trust root.pub.pem --revoked <old-key-id> --product acme-cad --feature pro
```

<!-- steps:expect -->
```text
invalid: ELM_KEY_REVOKED: The license was signed by a revoked key.
```

The verifier checks the token's key ID against the revoked key IDs before its certificates, its signature, and its payload, so the file fails even though it has no expiry and its signature is correct.

## Revoke the key in the apps

The app developer adds the old key ID to the revoked key IDs of the app's trust store, and ships the new version:

- In .NET, the `revokedKeyIds` of [`TrustStore.Create`](../reference/dotnet-api.md#truststore).
- In C, `revoked_key_ids` of [`elm_verifier_options`](../reference/c-api.md#elm_verifier_options), and of `elm_activation_options` and `elm_client_options`.

The list reaches a device only with that version; there is no revocation list fetched over the network. A device that still keeps a license file of the old key then gets `ELM_KEY_REVOKED` from `Check`. An app that activates online can refresh at that point, with `RefreshAsync` in .NET or `elm_activation_refresh` in C: the service answers with a license file signed by the new key, and the app checks again. A device without a network needs its refresh files carried, so ship the version to such devices only after they have refreshed.

## Revoke the key on license servers

On each `elm-server`, add the old key ID to `trust.revokedKeyIds` (see [elm-server configuration](../reference/server-configuration.md#trust)), and restart the server: it reads the setting only when it starts. Do it only after the server has a server license signed by the new key, since with a server license signed by the old key the server then does not start. From then on it refuses a server license signed by the old key. Tell your customers the key ID, so that they do the same on their servers.

## See also

- [When a signing key leaks](../explanation/trust-chain.md#when-a-signing-key-leaks): why each step is needed, and what a revocation does not reach.
- [Rotate the signing key](rotate-the-signing-key.md): each step of replacing the key, and checking that devices keep working.
- [`trust`](../reference/service-configuration.md#trust) in the `elm-service` configuration.
- [`elm license verify`](../reference/elm-cli.md#elm-license-verify) and [`elm key thumbprint`](../reference/elm-cli.md#elm-key-thumbprint): every option.
