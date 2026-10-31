#!/bin/sh
# Builds SQLite from the pinned amalgamation in this folder on Linux and macOS (design §14.2, decision 0.72); build.ps1 does
# the same on Windows. POSIX sh, so it runs where PowerShell does not: the almalinux:8 container of the arm64 release build
# (design §20.5) and the .NET SDK's Alpine image, which has no bash.
#
# Output, for the runtime identifier given:
#   <out>/static/libe_sqlite3.a              the static library NativeAOT links into elm-service
#   <out>/shared/libe_sqlite3.so or .dylib  the shared library JIT runs (tests, dotnet run) load
# Usage: sh build.sh <rid> <out>
set -eu

rid=$1
out=$2
here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

# The amalgamation is checked against the hashes recorded when it was added, so an edited copy is not built.
for required in sqlite3.c sqlite3.h; do
    if ! grep -q "  $required\$" SHA256SUMS; then
        echo "vendor/sqlite/SHA256SUMS does not list $required." >&2
        exit 1
    fi
done
if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c SHA256SUMS >/dev/null
else
    shasum -a 256 -c SHA256SUMS >/dev/null
fi

flags=""
while IFS= read -r line; do
    case "$line" in
        "" | "#"*) ;;
        *) flags="$flags -D$line" ;;
    esac
done < defines.txt

# design §20.5: on macOS the target comes from the RID, with the minimum version NativeAOT links for (12.0), so osx-x64
# builds on an arm64 runner and the object agrees with the executable.
target=""
case "$rid" in
    osx-x64) target="--target=x86_64-apple-macos12.0" ;;
    osx-arm64) target="--target=arm64-apple-macos12.0" ;;
esac

case "$rid" in
    osx-*) extension=dylib; libraries="" ;;
    *) extension=so; libraries="-lpthread -ldl -lm" ;;
esac

mkdir -p "$out/static" "$out/shared"
# shellcheck disable=SC2086 # the flags are separate words on purpose
clang -c -O2 -fPIC $target $flags -o "$out/sqlite3.o" sqlite3.c
ar rcs "$out/static/libe_sqlite3.a" "$out/sqlite3.o"
# shellcheck disable=SC2086
clang -shared $target -o "$out/shared/libe_sqlite3.$extension" "$out/sqlite3.o" $libraries
echo "SQLite built for $rid in $out."
