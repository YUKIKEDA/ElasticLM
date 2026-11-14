---
sidebar_position: 17
---

# Back up and restore the service

This guide is for the vendor's operator who runs `elm-service` on SQLite. The service keeps its products, licenses, activations, API keys, and audit log in one database file; losing it loses every activation. The guide copies the database with `elm-service backup` while the service runs, says what else to keep, checks a backup by running the service on it, and restores a backup, with what a restore takes back. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md), whose database is the file `service.db`. A PostgreSQL database is backed up with PostgreSQL's own `pg_dump`, not with this command.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`.

- If the service is stopped, start it again in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service).
- If `ELM_API_KEY` is not set, for example because you closed the terminal, make a new API key with `elm-service setup --config service.json`, and read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service). `elm-service setup` makes a new key each time it runs, also while the service runs, and the keys it made before stay valid.

The licenses below use `<policy-id>`, the `id` of the tutorial's policy `Standard`. If you did not keep it, `elm admin request GET /v1/admin/policies --url http://127.0.0.1:8080` lists the policies with their names.

## Create a license before the backup

To see later what the backup holds, create a license now:

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
backup: backup.db, schema version 10
```

The copy is the whole database at one moment, also while the service writes to it, and the service does not wait for it. The number is the newest schema version in the copy: an `elm-service` older than that version cannot use the copy. Write down the time of the backup: a restore takes back everything after it, and the audit log is where you find what that was.

`backup` reads and checks the same settings file as the service, and reads the secret files it names, but not the signing key, its certificates, the root public keys, or the signing key's passphrase: it needs neither `ELM_SIGNING_PASSPHRASE` nor a running service. A setting error, such as a rate limit out of range, stops it with exit code 2, as it stops the service. It never overwrites a file and does not create folders; give each backup a new name, such as one with the date. With a file of that name already there, it ends with exit code 1 and `The backup file 'backup.db' exists.`; with a folder that does not exist, it ends with exit code 1 and a line that starts with `The backup file '<path>' cannot be written:` and names the temporary file it could not create, `<path>.<random hex>.tmp`.

Keep the backup where only you can read and write it. It holds the names and the metadata of your customers' licenses, the addresses in the audit log, and the hashes of the API keys. A copy that someone changed and that you then restore would also let them add an administrator or an API key, the same as writing to the database. On Linux and macOS the file can be read and written by its owner only; on Windows it takes the access rules of its folder, so put it in a folder that only your account can open, which also keeps others from swapping the file while `backup` writes it. [Security model](../explanation/security-model.md#the-vendor) lists what the vendor must protect.

## Keep the files outside the database

The database does not hold the service's keys and settings. Keep these too, apart from the database's backups, and protect each as the [Secrets](../reference/service-configuration.md#secrets) of the configuration say:

- The signing key and its certificates, and its passphrase or `signing.passphraseFile`, kept apart from the key file.
- The root public keys of `trust.rootKeys`. The root private key belongs offline, never with the service's backups: see [Keep the root key safe](keep-the-root-key-safe.md).
- The nonce secret.
- With the admin UI's sign-in, the key-encryption key of `dataProtection.kekFile` and the web client secret of `oidc.webClientSecretFile`. Without the same key-encryption key, the service cannot read the sign-in keys in a restored database and does not start.
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

## Check the backup by starting the service on it

To know that a backup works, start the service on it. The service writes to the file it runs on, its audit log included, so the file is then no longer as `backup` wrote it. This guide's `backup.db` is only for trying; with a backup you keep, copy it first with the tools of your system, such as `Copy-Item backup.db check.db` in PowerShell or `cp backup.db check.db` in Bash or zsh, and start the service on the copy.

Change `database.connection` to the backup:

<!-- steps:file path=database.connection -->
```text
Data Source=backup.db
```

Press Ctrl+C in the second terminal, and start the service there again; `ELM_SIGNING_PASSPHRASE` is still set in that terminal:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

If the log shows `Schema versions applied: 1, 2, …` before `service.started`, the service did not find the file and made a new, empty database: check the name in `database.connection`. A service on an empty database refuses every API key with 401, since it has none.

Read the first license:

<!-- steps:run capture=license-a-name pattern="(customer-a)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-a> --url http://127.0.0.1:8080
```

`elm` prints the license `customer-a`. Read the second one:

<!-- steps:run exit=1 capture=license-b-missing stream=stderr pattern="(ELM_LICENSE_NOT_FOUND)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-b> --url http://127.0.0.1:8080
```

The backup has no such license, and `elm` ends with exit code 1 and prints on standard error:

```text
error: the service answered 404.
{"type":"about:blank","title":"ELM_LICENSE_NOT_FOUND","status":404,"detail":"No license has this ID.","code":"ELM_LICENSE_NOT_FOUND"}
```

Check the audit log of the backup:

<!-- steps:run capture=audit-intact pattern="intact.:\s*(true)" -->
```shell
elm admin request POST /v1/admin/audit/verify --url http://127.0.0.1:8080
```

The answer holds `"intact":true`: the audit log is whole. The API key in `ELM_API_KEY` works here because the tutorial made it before the backup. When you are done, set `database.connection` back to `Data Source=service.db`, and restart the service; or keep it running on this file, if that is the restore you want, and then make a new backup, since the file is now the live database.

## Restore a backup

To put a backup in the place of the database:

1. While the service still runs on the database you replace, save its audit log with [`GET /v1/admin/audit`](../reference/administration-api.md#get-v1adminaudit). It is the only record of what the backup does not hold.
2. Stop the service with Ctrl+C.
3. Move `service.db` away, together with `service.db-wal` and `service.db-shm` beside it, if they are there. A service stopped with Ctrl+C usually leaves no `-wal` file, but one that was killed or crashed does, and SQLite would read an old `-wal` as part of the restored file and damage it. In PowerShell, type `New-Item -ItemType Directory replaced | Out-Null` and then `Move-Item service.db* replaced`; in Bash or zsh, `mkdir replaced` and then `mv service.db* replaced/`.
4. Copy the backup to `service.db`: `Copy-Item backup.db service.db` in PowerShell, `cp backup.db service.db` in Bash or zsh. Keep `backup.db` as it is.
5. Start the service, with a version of `elm-service` that knows the copy's schema version: the version that made the backup, or a newer one. It puts the file back into the write-ahead log mode that it uses. If its log shows `Schema versions applied: 1, 2, …`, it found no `service.db` and made an empty one: stop it, and repeat step 4.
6. Check the restored database as you checked the backup: read a license you know, and check the audit log with `POST /v1/admin/audit/verify`.
7. Do again what the saved audit log shows after the time of the backup: revoke and suspend the same licenses, revoke the same API keys, and disable the same administrators.

## What a restore takes back

A restore brings back the database as it was at the backup, and everything since is gone:

- A license or an API key created since the backup is gone. A customer who has the license key of such a license gets `ELM_LICENSE_NOT_FOUND`: create the license again and give the customer its new license key.
- A device that refreshed its license file since the backup sends a sequence number that the restored database has not seen. The service takes it as a copied installation: under the policy's default `cloneStrategy`, `deny`, it refuses every later refresh of the device with `ELM_CLONE_DETECTED`, and the device stops refreshing. An administrator then deactivates the activation, as in [Deactivate as the administrator](deactivate-a-device.md#deactivate-as-the-administrator), and the device activates again. See [Activation](../explanation/activation.md#clone-detection-with-the-sequence-number).
- An activation made since the backup is gone: its device's refreshes fail with `ELM_ACTIVATION_NOT_FOUND`, and the device must activate again.
- A license revoked or suspended, an API key revoked, or an administrator disabled since the backup is active again: a key you stopped works again, until you stop it again as in step 7.
- The audit log ends at the backup, and its check still passes, since the restored rows are whole.
- Cloud leases granted since the backup are gone: the app's next renewal gets `ELM_UNKNOWN_LEASE`, and the lease is lost. The usage log, if `usageLog.directory` is set, still lists them.

## See also

- [`elm-service backup`](../reference/service-configuration.md#command-line-and-exit-codes): every argument and exit code.
- [Upgrade the service](upgrade-the-service.md): back up before each upgrade.
- [Security model](../explanation/security-model.md): what the database and its backups protect.
