# SQLite

The SQLite amalgamation that `elm-service` links in (design §14.2, decision 0.72).

| Item | Value |
| --- | --- |
| Version | 3.54.1 |
| Source | https://sqlite.org/2026/sqlite-amalgamation-3540100.zip |
| SHA3-256 of the zip (as sqlite.org publishes it) | `92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30` |
| Files taken | `sqlite3.c`, `sqlite3.h` (SHA-256 in `SHA256SUMS`) |
| License | Public domain (https://sqlite.org/copyright.html) |

`build.ps1` checks `SHA256SUMS`, then builds a static library for NativeAOT and a shared library for JIT runs with the platform's C compiler. The `elm-service` project runs it before it builds.

To update: download the new amalgamation, compare its SHA3-256 with https://sqlite.org/download.html, replace the two files, update `SHA256SUMS` and this table, and record the change in the PR.
