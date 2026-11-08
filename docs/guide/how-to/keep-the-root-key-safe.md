---
sidebar_position: 5
---

# Keep the root key safe

This guide is for the vendor who holds the root key, the key that certifies every signing key. Anyone who has the root key file and its passphrase can certify a signing key of their own and make licenses that every app trusting the root key accepts. And if both copies of the file, or the passphrase, are lost, you can certify no new signing key. Both cases need an app update to recover from. This guide says where to keep the root key, how to keep copies, and how to check that a copy still works. It starts where you have a root key `root.key.pem`, its public key `root.pub.pem`, and the public key `signing.pub.pem` of a signing key that the root key certified. [Issue a license and verify it in a .NET app](../tutorials/verify-a-license-offline.md) creates them; [Root key](../explanation/trust-chain.md#root-key) explains what the root key does.

<!-- steps:after page=docs/guide/tutorials/verify-a-license-offline.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. Replace the file names with your own.

In the outputs below, the key IDs and the dates are fake. On your machine they are different.

## Record the root key's ID

The key ID is a short name computed from the public key. It names the root key in every key certificate, so it tells you which key a file holds. Print it:

<!-- steps:run -->
```shell
elm key thumbprint root.pub.pem
```

`elm` prints the key ID:

```text
FAKE-ROOT-KEY-ID-000000000000000000000000R1
```

Write it down where you record your copies of the root key. The public key and its ID are not secrets: every app holds the public key in its trust store.

## Keep the key offline

- Keep `root.key.pem` on a machine that has no network, or on removable media such as a USB drive that you connect only when you use the key. Never put it on the machine that runs `elm-service`, in a repository, in a container image, or in a server's backup.
- Use it only for `elm key certify`: to certify a new signing key, as in [Rotate the signing key](rotate-the-signing-key.md), or to renew the certificate of a signing key. No other command and no program of ElasticLM reads the root key.
- `elm` keeps the key encrypted with its passphrase, and asks for the passphrase each time it uses the key. Type it when `elm` asks. Do not write it in a script, and do not keep it in `ELM_KEY_PASSPHRASE` in a file or a terminal profile. Keep it apart from the key file, so that whoever finds one does not also have the other.

## Keep two copies

Copy `root.key.pem` with the tools of your system, such as `Copy-Item` in PowerShell or `cp` in Bash, to two removable media, and keep them in two different places, so that a fire or a theft does not take both. Each copy is as secret as the original, and is encrypted with the same passphrase. Keep the passphrase so that it can be found again too: a copy without its passphrase is as lost as no copy.

If every copy of the key, or its passphrase, is lost, you can certify no new signing key, and the certificates of your signing keys run out within their periods. The only way back is a new root key, which needs an app update; [Replacing a root key](../explanation/trust-chain.md#replacing-a-root-key) gives the order. If a copy may have been stolen together with its passphrase, see [When the root key leaks](../explanation/trust-chain.md#when-the-root-key-leaks).

## Check that a copy works

After you make a copy, and from time to time, check that the copy can still be read with the passphrase: certify the signing key's public key with it. Pass the path of the copy to `--root` instead of `root.key.pem`. `--out check.cert` names a new file, since `elm` does not overwrite a file that exists without `--force`, and `--days 1` keeps the certificate's period short:

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --out check.cert --days 1
```

`elm` asks for the passphrase and prints the certificate:

```text
Passphrase for 'root.key.pem':
certificate: check.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license
valid: 2026-01-15 09:30:00Z to 2026-01-16 09:30:00Z
```

<!-- steps:expect -->
```text
certificate: check.cert
uses: license
```

Compare `root key ID:` with the key ID you recorded. When they are the same, the copy is the root key and the passphrase is right. `elm` cannot tell a root key from another private key: if you pass the file of a signing key, it certifies with that key, and `root key ID:` shows that key's ID instead.

Then delete `check.cert`. Nothing uses it: the signing key already has its own certificate.

| What `elm` prints | What happened | What to do |
| --- | --- | --- |
| `error: The key 'root.key.pem' is not an encrypted P-256 private key, or the passphrase is wrong.` | The passphrase is wrong, or the file is not a private key, such as `root.pub.pem`. | Type the passphrase again. If it still fails, the copy is damaged: use the other copy, and make a new one from it. |
| `error: The file 'root.key.pem' does not exist.` | The path does not name the copy. | Check the path of the copy, and that its media is connected. |
| `error: The file 'check.cert' exists. Pass --force to overwrite it.` | A file from an earlier check is still there. | Delete it, and certify again. |

`elm` ends with exit code 2 in each case. [elm command reference](../reference/elm-cli.md#exit-codes) lists the exit codes.

## See also

- [Trust chain](../explanation/trust-chain.md): the root key, signing keys, key certificates, and what to do when a key leaks.
- [Security model](../explanation/security-model.md#the-vendor): what the vendor must do to keep licenses safe.
- [`elm key certify`](../reference/elm-cli.md#elm-key-certify) and [`elm key thumbprint`](../reference/elm-cli.md#elm-key-thumbprint): every option.
