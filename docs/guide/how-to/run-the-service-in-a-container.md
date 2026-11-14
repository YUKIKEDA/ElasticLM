---
sidebar_position: 19
---

# Run the service in a container

This guide is for the vendor's operator who runs `elm-service` as a container on a Linux host with Docker. It puts the settings and secrets of [Run the hosted activation service and activate a .NET app online](../tutorials/activate-an-app-online.md) into a volume that only the service can read, keeps the database in a second volume, makes the first API key, starts the service, and backs it up while it runs. On Windows or macOS, follow it in a Linux virtual machine, or in a Linux distribution of WSL with Docker in it. The image runs the same `elm-service` as the archive of the tutorial, with SQLite here; [Run the service on PostgreSQL](run-the-service-on-postgresql.md) moves it to PostgreSQL.

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`, on a Linux host with Docker. If you followed the tutorial on Windows or macOS, `tools` needs the tutorial's Linux archives (`-linux-x64.tar.gz` or `-linux-arm64.tar.gz`) there. The tutorial's service may keep running on port 8080: the container below listens on port 8090 of the host.

The commands use the image under the name `elm-service`. No image has been published yet: until the first release, build it as described in [Build the container image](https://github.com/YUKIKEDA/ElasticLM/blob/main/CONTRIBUTING.md#build-the-container-image) in `CONTRIBUTING.md`, which gives it that name. Once an image is published, pull it from `ghcr.io/yukikeda/elm-service` by its digest, which the release names, which cannot be moved to another image as a tag can, and give it the name: `docker pull ghcr.io/yukikeda/elm-service@sha256:<digest>`, then `docker tag ghcr.io/yukikeda/elm-service@sha256:<digest> elm-service`.

<!-- steps:prebuilt name=image -->

The image holds `/app/elm-service`, the same executable as the archive `elm-service-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz`, on a base with no shell and no package manager. It runs as the user ID 1654, listens on `http://0.0.0.0:8080`, and keeps its data in `/data`.

## Write the settings for the container

Inside the container, the service finds its files under `/config` and keeps its database under `/data`. Create a folder named `container` in the tutorial folder for the files that differ from the tutorial's, and in it a file named `container.json`, with the tutorial's settings and these paths:

<!-- steps:file path=container/container.json -->
```json
{
  "publicUrls": ["https://elm.example/"],
  "node": {
    "name": "node-1"
  },
  "database": {
    "provider": "sqlite",
    "connectionFile": "/config/database.connection"
  },
  "signing": {
    "key": "/config/signing.key.pem",
    "certificates": ["/config/signing.cert"]
  },
  "trust": {
    "rootKeys": ["/config/root.pub.pem"]
  },
  "nonce": {
    "secretFile": "/config/nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

- `publicUrls` is the `https://` address under which devices reach the service, such as the address of the reverse proxy in front of it. In a container the service listens on an address other than loopback, and without `publicUrls` neither `setup` nor the service starts. Use your own address in place of `https://elm.example/`.
- `node.name` is a name for this node that stays the same. Without it, the name is the container's host name, which changes each time the container is made again; with `usageLog.directory` set, the usage log would then start the file of another node.

The database's connection string goes into `container/database.connection`, so that the tutorial's `database.connection` stays as it is:

<!-- steps:file path=container/database.connection -->
```text
Data Source=/data/service.db
```

## Put the files into a volume

Create two volumes, one for the settings and secrets, and one for the data:

<!-- steps:run -->
```shell
docker volume create elm-config
docker volume create elm-data
```

The secrets must be readable by the user ID 1654 and by no one else. A helper container copies them into `elm-config` and gives them to that user. Create it, without starting it and without a network, copy the files in, and then start it to change their owner:

<!-- steps:run -->
```shell
docker create --name elm-config-helper --network none -v elm-config:/config busybox@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec chown -R 1654:1654 /config
docker cp container/container.json elm-config-helper:/config/
docker cp container/database.connection elm-config-helper:/config/
docker cp signing.key.pem elm-config-helper:/config/
docker cp signing.cert elm-config-helper:/config/
docker cp root.pub.pem elm-config-helper:/config/
docker cp nonce.secret elm-config-helper:/config/
docker start -a elm-config-helper
docker rm elm-config-helper
docker run --rm --network none -v elm-config:/config busybox@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec chmod -R u=rX,go= /config
```

`elm-config` now holds the files, readable by their owner only. With Kubernetes, make the secrets readable by the user ID 1654 instead: the pod's `securityContext.fsGroup: 1654` gives them the group 1654, and the secret's `defaultMode`, such as `0440`, makes them readable by that group only.

## Make the first API key

Run `setup` once, in a container that removes itself at the end:

<!-- steps:run capture=container-api-key env=ELM_API_KEY -->
```shell
docker run --rm -v elm-config:/config:ro -v elm-data:/data elm-service setup --config /config/container.json
```

It prints a new API key on the first line, as in the tutorial; the database in `elm-data` is new, so the tutorial's key does not work there. Read the key into `ELM_API_KEY` the same way as in [Set up the service](../tutorials/activate-an-app-online.md#set-up-the-service).

## Start the service

Start the service in another terminal where `ELM_SIGNING_PASSPHRASE` holds the signing key's passphrase, set as in [Start the service](../tutorials/activate-an-app-online.md#start-the-service): a third one, if the tutorial's service still runs in the second.

<!-- steps:background ready=http://127.0.0.1:8090/health -->
```shell
docker run --rm --name elm-service -p 127.0.0.1:8090:8080 -v elm-config:/config:ro -v elm-data:/data -e ELM_SIGNING_PASSPHRASE elm-service --config /config/container.json
```

`-e ELM_SIGNING_PASSPHRASE` without a value hands the variable of this terminal to the container. The value still shows in `docker inspect elm-service`, and in a pod's definition when the value is written there: in production, put the passphrase in a file of `elm-config` and name it with `signing.passphraseFile` instead. `-p 127.0.0.1:8090:8080` publishes the port on the host's loopback address only; put TLS in front of it with a reverse proxy or an ingress, or give the service `tls.certificate` and `tls.key` and an https address with `-e ELM_LISTEN=https://0.0.0.0:8080`: the image sets `ELM_LISTEN`, which overrides a `listen` in `container.json`.

The service prints its log as in the tutorial, ending with `service.started`. `/health` answers 200 while the service reaches its database, and 503 when it does not. The image has no `HEALTHCHECK`, since it has no shell: check `/health` from outside the container. With Kubernetes, use it as the `readinessProbe` (`httpGet`, path `/health`, port 8080), not as the `livenessProbe`, which would restart the service again and again while the database is down.

Create a product through the administration API, from the first terminal, as in the tutorial:

<!-- steps:run capture=container-product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8090
```

Read it back:

<!-- steps:run capture=container-product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<container-product-id> --url http://127.0.0.1:8090
```

`elm` prints the product `acme-cad`. Apps cannot activate through `http://127.0.0.1:8090`, since a device's proof must name an address of `publicUrls`; they reach the service under `https://elm.example/`, through the reverse proxy.

## Back up the database

Run `elm-service backup` in a second container on the same volumes, while the service runs:

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
docker run --rm -v elm-config:/config:ro -v elm-data:/data elm-service backup --config /config/container.json --out /data/backup-1.db
```

It prints `backup: /data/backup-1.db, schema version 9`, or another version number. Two containers may share the SQLite database only through a volume on the same host, as here; a volume on a network file system does not keep SQLite's locks. Copy the backup out of the volume, and keep it apart from the host, as [Back up and restore the service](back-up-and-restore-the-service.md) says:

<!-- steps:run -->
```shell
docker cp elm-service:/data/backup-1.db backup-1.db
```

## Stop the service

Stop the service with `docker stop elm-service`. Docker sends SIGTERM, and the service finishes the work it runs and ends with exit code 0; `--rm` then removes the container. The volumes stay: start the service again with the same `docker run` line. To remove everything this guide made, run `docker volume rm elm-config elm-data` after the service has stopped.

- Keep the data in a named volume, as here. Without `-v elm-data:/data`, Docker makes an anonymous volume, and the data is gone when the container is made again.
- To keep the data in a folder of the host instead, make the user ID 1654 its owner first.
- With Kubernetes, a persistent volume claim does not take the owner of the image's `/data`: set `securityContext.fsGroup: 1654`.
- With a read-only root file system, mount a writable volume on `/tmp` too.

## See also

- [elm-service configuration](../reference/service-configuration.md): every setting, and the [Secrets](../reference/service-configuration.md#secrets) that a file can hold.
- [Back up and restore the service](back-up-and-restore-the-service.md) and [Upgrade the service](upgrade-the-service.md): the same work outside a container.
- [Run the service on PostgreSQL](run-the-service-on-postgresql.md): a database that several containers share.
