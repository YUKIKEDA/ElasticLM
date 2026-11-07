---
sidebar_position: 4
---

# Issue a license and verify it in a C app

In this tutorial you create a vendor's keys, issue a license with them, and write a small C program that links `libelasticlm`, the ElasticLM library for C, and checks the license on your machine, with no network. It is for anyone new to ElasticLM who can use a terminal and build a C program. [Issue a license and verify it in a .NET app](verify-a-license-offline.md) does the same in .NET.

## Before you start

You need a C compiler:

- On Windows, Visual Studio or Build Tools for Visual Studio with the workload "Desktop development with C++".
- On Linux, `cc`, which is gcc or clang.
- On macOS, the Xcode Command Line Tools (`xcode-select --install`).

To check the files you download, you also need the [GitHub CLI](https://cli.github.com/), `gh`; nothing else in this tutorial uses it.

On Windows, open "x64 Native Tools Command Prompt" from the Start menu, then type `powershell` in it, and type every command of this page in that PowerShell. The prompt sets up the compiler `cl` for 64-bit x64 programs, and PowerShell keeps that setup. Do not use "Developer Command Prompt": it builds 32-bit x86 programs, which cannot link the x64 library. On a Windows ARM64 machine, open "ARM64 Native Tools Command Prompt" instead and take the `win-arm64` archives below. On Linux and macOS, use any terminal.

Create an empty folder for this tutorial. Open a terminal in that folder and type every command of this page there.

You download the `elm` tool and the library from a GitHub prerelease. Open the [Releases page](https://github.com/YUKIKEDA/ElasticLM/releases) and take the newest release marked "Pre-release". The release that GitHub marks "Latest" is never a prerelease, so do not go by that mark.

From the release's Assets, save these three files in the tutorial folder, where `<RID>` names your system: one of `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-arm64` (a Mac with Apple silicon), and `osx-x64` (a Mac with an Intel processor):

- The archive `elm-<version>-<RID>`, a `.zip` file for Windows and a `.tar.gz` file for the others.
- The archive `libelasticlm-<version>-<RID>`, of the same kind.
- `SHA256SUMS`, the list of the files' hashes.

Take the archives whose names do not end in `-symbols`: those hold only debug symbols.

Before you extract or use anything, check the two archives in the tutorial folder as described in [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) in `SECURITY.md`. That section also says which Linux and macOS versions the programs need.

Extract the `elm` archive into a folder named `tools`. In PowerShell on Windows, type `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools`; on Linux and macOS, type `mkdir -p tools` and then `tar -xzf elm-<version>-<RID>.tar.gz -C tools`. `tools` then holds `elm` (`elm.exe` on Windows), the command-line tool that creates keys and issues licenses, and `LICENSE`.

<!-- steps:prebuilt name=elm -->

Add the `tools` folder to your `PATH` in this terminal, so that the command `elm` runs it. In PowerShell on Windows, type `$env:PATH = "$PWD/tools;$env:PATH"`; in Bash or zsh, type `export PATH="$PWD/tools:$PATH"`.

Extract the `libelasticlm` archive into a folder named `app`, where you also write and build your program. The files are at the top of the archive, so extracting it as it is would put them in the tutorial folder itself. In PowerShell on Windows, type `Expand-Archive libelasticlm-<version>-<RID>.zip -DestinationPath app`; on Linux and macOS, type `mkdir -p app` and then `tar -xzf libelasticlm-<version>-<RID>.tar.gz -C app`. `app` then holds the library (`elasticlm.dll` and the import library `elasticlm.lib` on Windows, `libelasticlm.so` on Linux, `libelasticlm.dylib` on macOS), the header `elasticlm.h`, and `LICENSE`.

<!-- steps:prebuilt name=libelasticlm -->

## Create the keys

You need two keys: a root key, which your app trusts, and a signing key, which signs licenses. The root key certifies the signing key, so that your app accepts what the signing key signs.

`elm` keeps each private key in a file encrypted with a passphrase. When it creates a key, it asks for a new passphrase, at least 12 characters long, and asks you to repeat it. When a later command uses the key, it asks for that passphrase again. Nothing is shown while you type it. Use the same passphrase for both keys in this tutorial. If the environment variable `ELM_KEY_PASSPHRASE` is set, `elm` takes the passphrase from it and does not ask, so the lines that ask for it in the outputs below do not appear. To set it in this terminal, type `$env:ELM_KEY_PASSPHRASE = '<passphrase>'` in PowerShell, or `export ELM_KEY_PASSPHRASE='<passphrase>'` in Bash or zsh. This is for scripts and terminals where nobody can type; in real use, do not keep a passphrase in a script. See [Passphrases](../reference/elm-cli.md#passphrases) in the `elm` reference.

In the outputs below, the key IDs, the token ID, and the dates are fake. On your machine they are different every time.

Create the root key:

<!-- steps:run -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
```

`elm` prints:

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
private key (encrypted): root.key.pem
public key: root.pub.pem
```

`root.key.pem` is the private key and `root.pub.pem` is its public key. The key ID is a short name computed from the public key.

Create the signing key:

<!-- steps:run -->
```shell
elm key signing create --out signing.key.pem --public-out signing.pub.pem
```

`elm` prints:

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
private key (encrypted): signing.key.pem
public key: signing.pub.pem
```

Certify the signing key with the root key, for signing licenses:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
```

`elm` prints:

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` is the key certificate. It is valid for one year: during that time the signing key may sign new licenses. The dates do not limit how long the licenses it signed stay valid; [Key certificates](../explanation/trust-chain.md#key-certificates) explains this under "Validity and the issue time".

## Issue a license

Issue a license for the product `acme-cad` with the feature `pro`, which expires at 00:00 UTC on 31 December 2099:

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
```

`elm` prints:

```text
Passphrase for 'signing.key.pem':
license file: first.elmlic
license ID: first-license
token ID: FAKEjti000000000000001
```

`first.elmlic` is the license file. It holds the signed license and the signing key's certificate. With `--trust`, `elm` verifies the new license against the root public key and writes the file only if it passes. The license is not bound to a device, so it is valid on any machine.

## Create the app

Create the file `app/app.c` with this code:

<!-- sample: samples/VerifyOfflineC/app.c -->
<!-- steps:file path=app/app.c -->
```c
/*
 * Verifies a license file offline and prints whether it is valid.
 *
 *   app <root-public-key.pem> <license.elmlic>
 *
 * A real app embeds the root public key instead of reading it from a file.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "elasticlm.h"

#define PRODUCT_ID "acme-cad"
#define FEATURE_ID "pro"

/* Reads a whole file and ends it with a NUL, so that a PEM file can be used as a string; NULL when it cannot. */
static char* read_file(const char* path, size_t* length)
{
    FILE* file = fopen(path, "rb");
    char* bytes = NULL;
    long size;
    if (file == NULL) {
        return NULL;
    }
    if (fseek(file, 0, SEEK_END) == 0 && (size = ftell(file)) >= 0 && fseek(file, 0, SEEK_SET) == 0) {
        bytes = (char*)malloc((size_t)size + 1);
        if (bytes != NULL && fread(bytes, 1, (size_t)size, file) == (size_t)size) {
            bytes[size] = '\0';
            *length = (size_t)size;
        } else {
            free(bytes);
            bytes = NULL;
        }
    }
    fclose(file);
    return bytes;
}

int main(int argc, char** argv)
{
    elm_verifier_options options = {0};
    elm_requirement requirement = {0};
    elm_verifier* verifier = NULL;
    elm_license* license = NULL;
    const char* root_keys[1];
    char* root_key;
    char* file;
    size_t root_key_length;
    size_t file_length;
    int32_t error;

    if (argc != 3) {
        fprintf(stderr, "usage: app <root-public-key.pem> <license.elmlic>\n");
        return 2;
    }
    root_key = read_file(argv[1], &root_key_length);
    if (root_key == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[1]);
        return 2;
    }
    file = read_file(argv[2], &file_length);
    if (file == NULL) {
        fprintf(stderr, "error: cannot read %s\n", argv[2]);
        free(root_key);
        return 2;
    }

    root_keys[0] = root_key;
    options.struct_size = sizeof options;
    options.root_keys_pem = root_keys;
    options.root_key_count = 1;
    error = elm_verifier_create(&options, &verifier);
    if (error != ELM_OK) {
        /* The first argument is not a public key in PEM. */
        fprintf(stderr, "error: %s\n", elm_last_error_message());
        free(file);
        free(root_key);
        return 2;
    }

    requirement.struct_size = sizeof requirement;
    requirement.product_id = PRODUCT_ID;
    requirement.kind = ELM_KIND_STANDARD;
    requirement.feature_id = FEATURE_ID;
    error = elm_verifier_verify(verifier, (const uint8_t*)file, file_length, &requirement, &license);
    if (error == ELM_OK) {
        printf("valid: license %s, product %s, feature %s\n", elm_license_id(license), elm_license_product(license), FEATURE_ID);
    } else {
        printf("invalid: %d: %s\n", (int)error, elm_last_error_message());
    }

    elm_license_free(license);
    elm_verifier_destroy(verifier);
    free(file);
    free(root_key);
    return error == ELM_OK ? 0 : 1;
}
```

The program makes a verifier from the root public key, the one key it trusts. `struct_size` tells the library which version of each structure the program was built with, and the fields left at zero take their defaults: no revoked key IDs, and no clock guard, so the program does not notice a clock set back ([Clock rollback detection and its limits](../explanation/license-files-and-verification.md#clock-rollback-detection-and-its-limits) explains the guards that do). It then verifies the license file: the signatures, the certificate, the expiry, the product `acme-cad`, and the feature `pro`. The requirement's `installation` is `NULL`, which says that the program uses only licenses that are not bound to a device. It prints `valid` with the license's ID, or `invalid` with the error number and the reason.

This program reads the root public key from a file to keep the tutorial short; a real app embeds the text of `root.pub.pem` in its code, for example as a string constant.

## Build the app

The library is in `app`, beside the program you build, which is where the program looks for it when it starts: Windows looks for a DLL in the program's folder first, and the option `-Wl,-rpath,...` below tells Linux and macOS to look there.

On Windows, build with `cl` and link the import library `elasticlm.lib`. `cl` also writes the object file `app.obj` in the tutorial folder:

<!-- steps:run os=windows -->
```shell
cl /nologo /std:c11 app/app.c app/elasticlm.lib /Fe:app/app.exe
```

On Linux, `-L app -lelasticlm` links `libelasticlm.so` from `app`, and `$ORIGIN` names the program's folder. Keep the single quotes: they keep the shell from reading `$ORIGIN` as a variable, in Bash, zsh, and PowerShell alike:

<!-- steps:run os=linux -->
```shell
cc -std=c11 -o app/app app/app.c -L app -lelasticlm '-Wl,-rpath,$ORIGIN'
```

On macOS, first remove the quarantine attribute from the files in `app`, then build. A web browser puts that attribute on each file it downloads, and `tar` gives it to every file it extracts from such an archive; `gh release download` and `curl` do not put it. Gatekeeper does not open a file that carries it and is not notarized, as [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) says. `xattr -dr` goes through the whole folder and ends without an error also where there is no attribute. `@executable_path` names the program's folder, and `cc` builds for the Mac you are on, so the archive's `<RID>` must match it:

<!-- steps:run os=macos -->
```shell
xattr -dr com.apple.quarantine app
cc -std=c11 -o app/app app/app.c -L app -lelasticlm -Wl,-rpath,@executable_path
```

[C API](../reference/c-api.md) lists the files, options, and search paths of each system.

## Run the app

Run the program with the root public key and the license file. The line is the same on every system; on Windows, PowerShell runs `app/app.exe`:

<!-- steps:run -->
```shell
app/app root.pub.pem first.elmlic
```

The program prints:

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

If it prints `invalid:` instead, the number after it says what failed. `elasticlm.h` defines each number with the name of its error code:

| Number | Code | Likely cause in this tutorial | What to do |
| --- | --- | --- | --- |
| `3` | `ELM_UNTRUSTED_KEY` | The first argument is a public key other than `root.pub.pem`, such as `signing.pub.pem`. | Pass `root.pub.pem`. |
| `7` | `ELM_WRONG_PRODUCT` | The license was issued with a `--product` other than `acme-cad`. | Issue the license again with `--product acme-cad`, and add `--force` to overwrite `first.elmlic`. |
| `11` | `ELM_FEATURE_NOT_LICENSED` | The license was issued without `--feature pro`. | Issue the license again with `--feature pro` and `--force`. |
| `2` | `ELM_MALFORMED` | The second argument is not a license file, such as `signing.cert`. | Pass `first.elmlic`. |

If the program prints a line that starts with `error: A root key is not a P-256 public key in PEM.`, the first argument is not a public key file: check the order of the two arguments. An `error: cannot read` line names the file it could not read. [Error codes](../reference/error-codes.md) lists every code.

If the program does not start at all, it did not find the library: check that `elasticlm.dll`, `libelasticlm.so`, or `libelasticlm.dylib` is in `app`, beside the program.

## What you made

You created a root key and a signing key, certified the signing key, issued a license, and verified it offline in a C program linked with `libelasticlm`. Keep `root.key.pem` offline in a real product: it only certifies signing keys, and anyone who holds it can make licenses that your app accepts.

To learn more:

- [C API](../reference/c-api.md): every function of `libelasticlm`, and the files, options, and search paths of each system.
- [License files and verification](../explanation/license-files-and-verification.md): what the license file holds and every check the program made.
- [Trust chain](../explanation/trust-chain.md): the root key, signing keys, and how to keep them safe.
