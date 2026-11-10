# The elm-service container image

`ghcr.io/yukikeda/elm-service` is the hosted activation service as a container image. Nothing has been
published yet: the first image comes with the first release that is not a prerelease, such as `v1.0.0`; a prerelease
such as `v1.0.0-rc.1` pushes no image.

This file is for the people who build and check the image. To run it, read
[Run the service in a container](https://github.com/YUKIKEDA/ElasticLM/blob/main/docs/guide/how-to/run-the-service-in-a-container.md)
in the user documentation.

## What the image holds

- `/app/elm-service`: the NativeAOT executable of the release, byte for byte the one in
  `elm-service-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz`. `/app/LICENSE`.
- `/data`: an empty folder owned by UID 1654, declared as a volume, and the working folder.
- The base is `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled`: glibc, OpenSSL, and the CA certificates. It
  has no shell and no package manager.
- It runs as UID 1654 and listens on `http://0.0.0.0:8080` (`ELM_LISTEN`). Platforms: `linux/amd64` and `linux/arm64`.

## How it is built

The release workflow's `image` job builds both platforms from the release archives with
`eng/container/Build-Image.ps1` into an OCI layout, builds it a second time to compare the index digest, and
`image-test` starts it on each platform with `eng/container/Test-Image.ps1`. The job that follows runs the steps of the
container and PostgreSQL how-to guides on that image. To build one platform on your machine, follow
[Build the container image](../CONTRIBUTING.md#build-the-container-image) in `CONTRIBUTING.md`.

## Checking the image

The release workflow attests the image's index digest. The command to check it from the registry is tried with the first
release and then written here and in `SECURITY.md`.
