#!/bin/sh
# Publishes elm, elm-server, elm-service, and libelasticlm for one runtime identifier with NativeAOT (design §20.5).
# POSIX sh, for the almalinux:8 container of the glibc release builds, which has no PowerShell (the arm64 runner's needs
# glibc 2.33). It does what Publish-Rid.ps1 does: one folder for each under <out>, the output of each publish in
# <logs>/<name>.log, and a failure when a publish fails. Test-Publish.ps1 counts the warnings in the logs on the runner.
# When DOTNET_SDK_VERSION is set, the SDK in use must be that version.
# Usage: sh publish-rid.sh <rid> <version suffix or ""> <out> <logs>
set -u

rid=$1
suffix=$2
out=$3
logs=$4
root=$(cd "$(dirname "$0")/../.." && pwd)

sdk=$(dotnet --version)
if [ -n "${DOTNET_SDK_VERSION:-}" ] && [ "$sdk" != "$DOTNET_SDK_VERSION" ]; then
    echo "The .NET SDK is $sdk, not DOTNET_SDK_VERSION $DOTNET_SDK_VERSION." >&2
    exit 1
fi

mkdir -p "$logs"
status=0
for target in Cli:elm Server:elm-server Service:elm-service Native:libelasticlm; do
    project="$root/src/ElasticLM.${target%%:*}"
    name=${target#*:}
    set -- publish "$project" -c Release -r "$rid" -o "$out/$name" -nologo
    if [ -n "$suffix" ]; then set -- "$@" "-p:VersionSuffix=$suffix"; fi
    if dotnet "$@" > "$logs/$name.log" 2>&1; then
        cat "$logs/$name.log"
    else
        cat "$logs/$name.log"
        echo "The NativeAOT publish of $name for $rid failed." >&2
        status=1
    fi
done

[ $status -eq 0 ] && echo "Published elm, elm-server, elm-service, and libelasticlm for $rid with SDK $sdk into $out."
exit $status
