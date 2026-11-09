---
sidebar_position: 17
---

# Back up and restore the service

This guide is for the vendor's operator who runs `elm-service` on SQLite. The service keeps its products, licenses, activations, API keys, and audit log in one database file; losing it loses every activation. The guide copies the database with `elm-service backup` while the service runs, says what else to keep, and restores a copy, with what a restore takes back. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md), whose database is the file `service.db`. A PostgreSQL database is backed up with PostgreSQL's own `pg_dump`, not with this command.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH` and the API key in `ELM_API_KEY`, as in the tutorial. The service runs in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service); if it is stopped, start it there again.

In the outputs below, the IDs are fake. On your machine they are different every time.

## Create a license before the backup

To see later what the backup holds, create a license now. Write its body, with the `<policy-id>` of the tutorial's policy `Standard`:

<!-- steps:file path=license-a.json -->
```json
{"policyId":"<policy-id>","name":"customer-a","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-a pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/licenses --body license-a.json --url http://127.0.0.1:8080
```

The `id` at the start of the answer is `<license-a>` below.

## Back up the database

Copy the database into the file `backup.db`, in the first terminal, while the service runs:

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
elm-service backup --config service.json --out backup.db
```

`elm-service backup` prints one line:

```text
backup: backup.db, schema version 8
```

The copy is the whole database at one moment, also while the service writes to it, and the service does not wait for it. The number is the newest schema version in the copy: an `elm-service` that is older than that version cannot use the copy.

`backup` reads the same settings file as the service, but only its database settings: it needs neither the signing key's passphrase nor a running service. It refuses to overwrite a file, so give each backup a new name, such as one with the date. It does not create folders.

Keep the backup where only you can read and write it. It holds the names and the metadata of your customers' licenses, the addresses in the audit log, and the hashes of the API keys. A copy that someone changed and that you then restore would also let them add an administrator or an API key, the same as writing to the database. On Linux and macOS the file can be read and written by its owner only; on Windows it takes the access rules of its folder, so put it in a folder that only you can open, which also keeps others from swapping the file while `backup` writes it. [Security model](../explanation/security-model.md#the-vendor) lists what the vendor must protect.

## Keep the files outside the database

The database does not hold the service's keys and settings. Keep these too, apart from the database's backups, and protect each as the [Secrets](../reference/service-configuration.md#secrets) of the configuration say:

- The signing key, its passphrase or `signing.passphraseFile`, and its certificates.
- The root public keys of `trust.rootKeys`. The root private key belongs offline, never with the service's backups: see [Keep the root key safe](keep-the-root-key-safe.md).
- The nonce secret.
- With the admin UI's sign-in, the key-encryption key of `dataProtection.kekFile` and the OIDC client secret. Without the same key-encryption key, the service cannot read the sign-in keys in a restored database and does not start.
- The TLS certificate and key, if the service serves https itself.
- The settings file and the connection string's file.
- The usage log folder, if `usageLog.directory` is set.
- The `elm-service` archive of the version that made the backup, so that you can start a version that knows the copy's schema.

## Create a license after the backup

Create a second license, which the backup does not hold:

<!-- steps:file path=license-b.json -->
```json
{"policyId":"<policy-id>","name":"customer-b","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-b pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/licenses --body license-b.json --url http://127.0.0.1:8080
```

Its `id` is `<license-b>` below.

## Start the service on the backup

To check a backup without touching the real database, point the service at the copy. Change `database.connection` to the backup:

<!-- steps:file path=database.connection -->
```text
Data Source=backup.db
```

Press Ctrl+C in the second terminal, and start the service there again:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

The service now runs on `backup.db`. Read the first license:

<!-- steps:run capture=license-a-name pattern="(customer-a)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-a> --url http://127.0.0.1:8080
```

`elm` prints the license `customer-a`. Read the second one:

<!-- steps:run exit=1 capture=license-b-missing stream=stderr pattern="(ELM_LICENSE_NOT_FOUND)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-b> --url http://127.0.0.1:8080
```

The service has no such license, and `elm` ends with exit code 1 and prints on standard error:

```text
error: the service answered 404.
{"type":"about:blank","title":"ELM_LICENSE_NOT_FOUND","status":404,"detail":"No license has this ID.","code":"ELM_LICENSE_NOT_FOUND"}
```

The API key in `ELM_API_KEY` still works, because the tutorial made it before the backup. When you have seen what you need, set `database.connection` back to `Data Source=service.db` and restart the service, or keep running on the copy if that is the restore you want.

## Restore a backup

To put a backup in the place of the database:

1. Stop the service.
2. Move `service.db` away, together with `service.db-wal` and `service.db-shm` beside it, if they are there. Do not leave an old `-wal` file: SQLite would read it as part of the restored file and damage it.
3. Copy the backup to `service.db`.
4. Start the service. It starts as usual, and puts the file back into the write-ahead log mode that it uses.

Start a version of `elm-service` that knows the copy's schema version, the version that made the backup or a newer one.

## What a restore takes back

A restore brings back the database as it was at the backup, and everything since is gone:

- A device that refreshed its license file since the backup sends a sequence number that the restored database has not seen. The service takes it as a copied installation: under the policy's default `cloneStrategy`, `deny`, it refuses every later refresh of the device with `ELM_CLONE_DETECTED`, and the device stops refreshing. An administrator then removes the activation, and the device activates again. See [Activation](../explanation/activation.md#clone-detection-with-the-sequence-number).
- An activation made since the backup is gone: its device's refreshes fail with `ELM_ACTIVATION_NOT_FOUND`, and the device must activate again.
- A license revoked or suspended, an API key revoked, or an administrator disabled since the backup is active again: a key you stopped works again.
- The audit log ends at the backup, and its check still passes, since the restored rows are whole.
- Cloud leases granted since the backup are gone, while the usage log, outside the database, still lists them.

So, before you restore, read the audit log of the database you replace while you still can, with [`GET /v1/admin/audit`](../reference/administration-api.md#get-v1adminaudit), and after the restore do again what it shows since the backup: revoke the same licenses and API keys, and disable the same administrators.

## See also

- [`elm-service backup`](../reference/service-configuration.md#command-line-and-exit-codes): every argument and exit code.
- [Upgrade the service](upgrade-the-service.md): back up before each upgrade.
- [Security model](../explanation/security-model.md): what the database and its backups protect.
