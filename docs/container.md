# Running elm-service in a container

`ghcr.io/yukikeda/elm-service` is the hosted activation service as a container image (design §20.6). Nothing has been
published yet: the first image comes with the first release (roadmap row V1e).

## What the image holds

- `/app/elm-service`: the NativeAOT executable of the release, byte for byte the one in
  `elm-service-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz`. `/app/LICENSE`.
- `/data`: an empty folder owned by UID 1654, declared as a volume, and the working folder.
- The base is `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled`: glibc, OpenSSL, and the CA certificates. It
  has no shell and no package manager.
- It runs as UID 1654 and listens on `http://0.0.0.0:8080` (`ELM_LISTEN`). Platforms: `linux/amd64` and `linux/arm64`.

## Pull it by digest

A tag (`<version>`, `latest`) can be moved to another image; a digest cannot. Pull the image by its digest, and check it
before you pin it (see "Checking the image"):

```sh
docker pull ghcr.io/yukikeda/elm-service@sha256:<digest>
```

## Settings and secrets

`elm-service` reads its settings from environment variables and from a settings file (`--config`, design §14.8). Give
secrets as files: an environment variable shows in `docker inspect` and in a pod's definition. Every secret has a file
setting: `database.connectionFile`, `nonce.secretFile`, `signing.passphraseFile`, `oidc.webClientSecretFile`, and
`dataProtection.kekFile`. The signing key, its certificates, and the root keys are files already.

The files must be readable by UID 1654. With Kubernetes secrets, set `defaultMode` or the pod's `fsGroup`.

Two values have no default that works in a container, so set them:

- `ELM_PUBLICURLS` (`publicUrls`): the `https://` addresses devices reach the service under. The service listens on a
  non-loopback address in the container, and without them neither `setup` nor the service starts.
- `ELM_NODE_NAME` (`node.name`): a stable name for this node. Without it the name is the container's host name, which
  changes when the container is made again, and the usage log starts a file of another node.

## Data

The SQLite database and the usage log need a folder the service can write as a whole (SQLite keeps `-wal` and `-shm` files
beside the database).

- Docker: mount a named volume on `/data`. A new named volume takes the folder's owner from the image. If you mount
  nothing, Docker makes an anonymous volume, and the data is lost when the container is made again.
- A folder of the host: change its owner to UID 1654 first.
- Kubernetes: a persistent volume claim does not copy the image's folder; set `securityContext.fsGroup: 1654`.
- A read-only root file system: mount a writable volume on `/tmp` as well.

## First run and start

```sh
docker volume create elm-data
docker run --rm -v elm-data:/data -v "$PWD/config:/config:ro" -e ELM_PUBLICURLS=https://licenses.example.com/ \
  ghcr.io/yukikeda/elm-service@sha256:<digest> setup --config /config/elm-service.json
docker run -d --name elm-service -p 8080:8080 -v elm-data:/data -v "$PWD/config:/config:ro" \
  -e ELM_PUBLICURLS=https://licenses.example.com/ -e ELM_NODE_NAME=node-1 \
  ghcr.io/yukikeda/elm-service@sha256:<digest> --config /config/elm-service.json
```

`setup` prints the first API key once. Put TLS in front of the service (a reverse proxy or an ingress), or give it
`tls.certificate` and `tls.key` and listen on `https`.

`docker stop` sends SIGTERM; the service finishes its running work and exits with code 0.

## Health

`GET /health` answers 200 while the service reaches its database and 503 when it does not. There is no `HEALTHCHECK` in
the image (it has no shell or `curl`).

- Kubernetes: use it as the `readinessProbe` (`httpGet`, path `/health`, port 8080). Do not use it as the
  `livenessProbe`: a database outage would restart the service again and again.
- Docker: check `/health` from outside the container.

## Checking the image

The release workflow attests the image's index digest. The command to check it from the registry is tried with the first
release (V1e) and then written here and in `SECURITY.md`.
