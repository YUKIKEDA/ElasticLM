---
sidebar_position: 20
---

# Run the service on PostgreSQL

This guide is for the vendor's operator who runs `elm-service` on PostgreSQL: the database that several nodes of the service can share, where SQLite serves one node only. It prepares a database and a role for the service, moves the settings of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) to PostgreSQL, backs the database up with `pg_dump` and restores it with `pg_restore`, and says what several nodes share and in which order they move to a new version. To try it, the guide starts PostgreSQL in a container on a Linux host with Docker; on Windows or macOS, follow it in a Linux virtual machine, or in a Linux distribution of WSL with Docker in it. With a PostgreSQL server of your own, skip the container and run the same SQL there.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The service runs in a second terminal, as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service), with `ELM_SIGNING_PASSPHRASE` set there.

`elm-service` works with PostgreSQL 15 to 18. The service does not move the tutorial's data from SQLite: it starts on PostgreSQL with an empty database, and you create the products, policies, and licenses there again.

## Start a PostgreSQL server to try

Start PostgreSQL 18 in a container, on the host's loopback address only. The password is for trying only: never use such a value on a server you keep. If a PostgreSQL of your own already listens on port 5432, use another host port, such as `-p 127.0.0.1:5433:5432`, and that port in the connection string below.

<!-- steps:run -->
```shell
docker run -d --name elm-postgres -p 127.0.0.1:5432:5432 -e POSTGRES_PASSWORD=try-only-postgres postgres:18@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6
docker exec elm-postgres sh -c 'until pg_isready -q -h 127.0.0.1 -U postgres; do sleep 1; done'
```

The second line waits until the server takes connections over TCP: the container first prepares its data with a server that does not listen there.

## Create the role and the database

The service connects as its own role, which owns its database and nothing else. Write the SQL into a file, with a password of your own in place of `try-only-elm`:

<!-- steps:file path=create-elm.sql -->
```sql
CREATE ROLE elm LOGIN PASSWORD 'try-only-elm';
CREATE DATABASE elm OWNER elm;
CREATE DATABASE elm_restored OWNER elm;
```

The second database, `elm_restored`, is for trying a restore below. Run the file in the container with `psql`:

<!-- steps:run -->
```shell
docker cp create-elm.sql elm-postgres:/tmp/create-elm.sql
docker exec elm-postgres psql -U postgres -v ON_ERROR_STOP=1 -f /tmp/create-elm.sql
```

On a server of your own, run it with your administrator's role: `psql -h <host> -U <admin-role> -v ON_ERROR_STOP=1 -f create-elm.sql`. The service creates its tables in the database itself, when it runs `setup` or starts.

## Point the service at PostgreSQL

Replace the content of `database.connection` with the connection string of PostgreSQL. It holds the role's password, so on Linux and macOS make the file readable by its owner only with `chmod 600 database.connection`:

<!-- steps:file path=database.connection -->
```text
Host=127.0.0.1;Port=5432;Database=elm;Username=elm;Password=try-only-elm
```

In `service.json`, change `database.provider` to `postgres`:

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "postgres",
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
  }
}
```

The connection string is Npgsql's: [elm-service configuration](../reference/service-configuration.md#database) lists its usual keys, such as `SSL Mode` for a server you reach over a network.

Prepare the new database and make its first API key, as in the tutorial:

<!-- steps:run capture=pg-api-key env=ELM_API_KEY -->
```shell
elm-service setup --config service.json
```

`setup` creates the tables in `elm` and prints a new API key on its first line. Read it into `ELM_API_KEY` as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service): the tutorial's key lives in the SQLite database and does not work here.

Press Ctrl+C in the second terminal, and start the service there again; `ELM_SIGNING_PASSPHRASE` is still set in that terminal:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

Its line `service.started` now says `database Postgres`. Create the product again, in the first terminal:

<!-- steps:run capture=pg-product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8080
```

Its `id` is `<pg-product-id>` below.

## Back up with pg_dump

`elm-service backup` copies a SQLite database only; for PostgreSQL, use PostgreSQL's `pg_dump`, which takes a consistent copy while the service runs. Use a `pg_dump` of the server's major version or newer: here, the one in the server's container. Write the copy in the custom format (`-Fc`), which `pg_restore` reads, and copy it out of the container:

<!-- steps:run -->
```shell
docker exec elm-postgres pg_dump -U elm -d elm -Fc -f /tmp/elm.dump
docker cp elm-postgres:/tmp/elm.dump elm.dump
```

On a server of your own, run `pg_dump -h <host> -U elm -d elm -Fc -f elm.dump` on a machine with PostgreSQL's client tools. Keep the copy where only you can read it, as [Back up and restore the service](back-up-and-restore-the-service.md#back-up-the-database) says for the SQLite file: it holds the same data. Keep the files outside the database too, as that guide lists.

## Restore with pg_restore

Restore the copy into the second database. `--no-owner` makes the restoring role, `elm`, the owner of what it creates:

<!-- steps:run -->
```shell
docker exec elm-postgres pg_restore -U elm -d elm_restored --no-owner /tmp/elm.dump
```

To check it, point the service at `elm_restored`:

<!-- steps:file path=database.connection -->
```text
Host=127.0.0.1;Port=5432;Database=elm_restored;Username=elm;Password=try-only-elm
```

Press Ctrl+C in the second terminal, and start the service there again:

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

Read the product back:

<!-- steps:run capture=pg-product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<pg-product-id> --url http://127.0.0.1:8080
```

`elm` prints the product `acme-cad`. To restore in production, stop every node, restore into an empty database with the same name (drop and create it first, or restore into a new one and change the connection string of every node), and start the nodes again. A restore takes back everything since the backup, as [What a restore takes back](back-up-and-restore-the-service.md#what-a-restore-takes-back) lists; save the audit log first, as [Restore a backup](back-up-and-restore-the-service.md#restore-a-backup) says.

## Run several nodes

Every node of the service connects to the same database, and holds the same:

- nonce secret (`nonce.secretFile`), so that a nonce from one node passes on another;
- signing key and its certificates, with its passphrase;
- root public keys and revoked key IDs (`trust`);
- key-encryption key of the admin UI's sign-in (`dataProtection.kekFile`), when the admin UI's sign-in is on.

Each node has its own `node.name`, so that each writes its own usage log file. Put the nodes behind one load balancer whose address is in `publicUrls` of every node, and set the rate limits per node, as [Limit request rates](limit-request-rates.md#several-nodes-and-restarts) says.

## Upgrade several nodes

A new version applies its database migrations when it starts, under a lock of PostgreSQL that lets one node migrate while the others wait. After a migration, a node of the older version does not start on the database: it ends with exit code 1 and `The database has schema version <n>, which this elm-service does not know; it is older than the database.` So:

1. Back up the database with `pg_dump`, as above.
2. Stop every node.
3. Start one node with the new version, and wait for its `service.started`. It applies the migrations, and its log lists them under `Schema versions applied`.
4. Start the other nodes with the new version.

Stop every node of the older version before the first node of the new version starts, so that no node of the older version works on the database while it is migrated. To go back to the older version, restore the backup of step 1 into an empty database and start the older version, as [Upgrade the service](upgrade-the-service.md#go-back-to-the-old-version) says.

## See also

- [`database`](../reference/service-configuration.md#database) and [`node`](../reference/service-configuration.md#node) in the `elm-service` configuration.
- [Back up and restore the service](back-up-and-restore-the-service.md): what a backup holds and what a restore takes back.
- [Run the service in a container](run-the-service-in-a-container.md): the image, with the settings in a volume.
