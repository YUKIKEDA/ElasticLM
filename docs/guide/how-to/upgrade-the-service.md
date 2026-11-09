---
sidebar_position: 18
---

# Upgrade the service

This guide is for the vendor's operator who runs `elm-service` on SQLite, on one node, and moves it to a new version. A new version may add database migrations, which it applies when it starts, and there are no migrations back: once a newer version has started on the database, an older version refuses it. So the guide backs up first, then stops the service, replaces the executable, and starts it again. It starts where `elm-service` is running at `http://127.0.0.1:8080` with the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md).

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH` and the API key in `ELM_API_KEY`, as in the tutorial. The service runs in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service); if it is stopped, start it there again.

`elm-service` has no `--version` option: the version is in the name of the archive it came from, such as `elm-service-<version>-<RID>.zip`. Write down the version you run now, and keep its archive until the new version has run for a while.

Download the archive of the new version, `elm-service-<version>-<RID>`, from the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases), and check it as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`, as in the tutorial. Read the release notes for changes to the settings.

## Back up the database

Copy the database while the service still runs the old version:

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
elm-service backup --config service.json --out before-upgrade.db
```

`elm-service backup` prints the backup's file and its schema version, such as:

```text
backup: before-upgrade.db, schema version 8
```

Keep this file until you are sure that you stay on the new version: it is the only way back. [Back up and restore the service](back-up-and-restore-the-service.md) says where to keep it and what else to keep.

## Replace the executable and start the service

1. Press Ctrl+C in the second terminal to stop the service.
2. Extract the new archive into the `tools` folder, over the old files, as in the tutorial: on Windows, `Expand-Archive elm-service-<version>-<RID>.zip -DestinationPath tools -Force`; on Linux and macOS, `tar -xzf elm-service-<version>-<RID>.tar.gz -C tools`.
3. Start the service again in the second terminal, where `ELM_SIGNING_PASSPHRASE` is still set:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

If the new version has migrations, the service applies them when it starts, and its log says which, after the address it listens on:

```text
info: elm-service[3]
      Schema versions applied: 9
```

Without new migrations, the log has no such line. Either way it ends with the line `service.started`. The settings file stays as it is, unless the release notes say otherwise.

Check that the service answers with your data, for example by reading the product of the tutorial:

<!-- steps:run capture=product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<product-id> --url http://127.0.0.1:8080
```

`elm` prints the product `acme-cad`.

## Go back to the old version

There are no migrations back, and an older version does not use a database that a newer version has changed. Started on it, the older version ends with exit code 1 and the line:

```text
The database has schema version 9, which this elm-service does not know; it is older than the database.
```

To go back, stop the service, restore `before-upgrade.db` as in [Restore a backup](back-up-and-restore-the-service.md#restore-a-backup), put the old version's executable back into `tools`, and start it. Everything since the backup is then gone: [What a restore takes back](back-up-and-restore-the-service.md#what-a-restore-takes-back) lists it.

## See also

- [Back up and restore the service](back-up-and-restore-the-service.md): what a backup holds and how to restore it.
- [elm-service configuration](../reference/service-configuration.md#command-line-and-exit-codes): the exit codes of the service.
- [Logs](../reference/logs.md): the service's log entries, the applied schema versions included.
