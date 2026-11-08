---
sidebar_position: 12
---

# Verify the usage log

This guide is for a customer's administrator, or an auditor, who checks that the usage log of an `elm-server` is complete and unchanged: before handing it to the vendor, for an audit, or after a doubt about the server. The server writes every lease, borrow, return, and administrator action to the log, with the refusals counted each minute, and signs it with its installation key; [Logs](../reference/logs.md#usage-log) describes its files. The guide starts where you have finished [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md), whose `elm-server` has written a log.

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the server's data folder `server-data`, which holds the log, and the root public key `root.pub.pem`, which verifies the server license that the log starts with.

To verify the log, the server does not need to run: `elm log verify` reads the files only. To sign the newest lines first, as below, it must run. If it is stopped, start it again in a second terminal, as in [Start the server](../tutorials/use-a-floating-license.md#start-the-server).

The examples use the values of the tutorial: the product `acme-cad` and the data folder `server-data`. Replace them with your own; the log is in the data folder unless the setting `log.usage` puts it elsewhere.

## Sign the newest lines

The server signs its log in blocks: after 100 lines, after 60 seconds, and when it stops. Make it sign the lines that it has not signed yet:

<!-- steps:run -->
```shell
elm server seal --data server-data
```

`elm` prints nothing and ends with exit code 0. The server goes on writing to the same file. The count of refusals of the current minute is written at the end of the minute, not by this step. Without this step, the last lines of the newest file may not be signed yet, and the check below shows them as a warning.

## Verify the log

Verify the usage log in the data folder. `--product` names the server license's product, and `--trust` the root public key that the vendor's server license is checked against:

<!-- steps:run -->
```shell
elm log verify server-data --product acme-cad --trust root.pub.pem
```

`elm` checks each file, oldest first, and the chain from each file to the next, and prints:

```text
intact: 1 file(s), 9 event line(s)
```

The numbers are those of your log: the server starts a new file at each start and at each UTC day. Exit code 0 means intact, also when the output has `warning:` lines. To get the result as JSON, for a script:

<!-- steps:run -->
```shell
elm log verify server-data --product acme-cad --trust root.pub.pem --json
```

<!-- steps:expect -->
```text
  "intact": true,
```

The JSON also has `files` and `entries`, the numbers of files and of event lines, and `issues`, the list of issues, each with its `file`, `line`, `severity`, and `message`.

The server also keeps a record of its borrows in the same folder, signed in the same way. Verify it with `--stream borrows`:

<!-- steps:run -->
```shell
elm log verify server-data --product acme-cad --trust root.pub.pem --stream borrows
```

It prints `intact:` in the same form.

## Read a failed check

When a check fails, `elm` prints one line for each issue, with the file and the line where it found it, then `NOT INTACT:`, and ends with exit code 1. For example, after a line before a signature was edited:

```text
error: usage-20260115T093000000Z.jsonl:2: The lines before the signature were changed, added, or removed.
NOT INTACT: 1 file(s), 0 event line(s)
```

Other errors are, for example, a missing file between two files, a file that another server's key signed, and a server license that does not verify. Warnings do not fail the check, but say what to look at: lines after the last signature, which a crash or an edit can leave, and a chain that starts again with `chain.reset`, with its reason. [Verifying the usage log](../reference/logs.md#verifying-the-usage-log) lists every error and warning. Exit code 2 means that the command could not run, for example because a path is not a file or folder.

## Keep copies of the finished files

The check cannot find that the newest file was deleted, or cut after its last signature: there is no later file to compare it with. Nor can it find that the oldest files were deleted: it takes the first file it is given as the start of the chain. Each file before the newest one is finished, and the next file's first line holds the hash of its last signature, so a change to it shows. Copy the finished files regularly to a place that the server's machine cannot change, such as a share where files cannot be overwritten, or hand them to whoever audits them. A copy that is verified there, and compared with the server's files later, shows a file that was deleted or changed afterwards, also among the oldest ones.

This does not protect against an administrator with root on the server: with the server's key, they can rewrite the log before it is copied. [Security model](../explanation/security-model.md#on-the-customers-license-server) lists this. The server deletes no log file itself; delete the old ones once you have kept the copies that you need.

## See also

- [Usage log](../reference/logs.md#usage-log): its files, lines, events, signatures, and chain.
- [`elm log verify`](../reference/elm-cli.md#elm-log-verify) and [`elm server seal`](../reference/elm-cli.md#elm-server-seal): every option.
- [Running `elm-server`](../explanation/floating-licenses.md#running-elm-server): what the log is for.
