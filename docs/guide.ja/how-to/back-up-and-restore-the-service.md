---
sidebar_position: 17
---

# サービスをバックアップして復元する

このガイドは、`elm-service` を SQLite で動かすベンダーの運用担当者に向けている。サービスは、製品、ライセンス、アクティベーション、API キー、監査ログを 1 つのデータベースファイルに置いている。このファイルを失うと、すべてのアクティベーションを失う。このガイドでは、サービスが動いているあいだに `elm-service backup` でデータベースをコピーし、ほかに何を取っておくかを示す。そのあと、バックアップでサービスを動かして確かめ、バックアップを復元し、復元で何が取り消されるかを示す。このガイドは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定で、`elm-service` が `http://127.0.0.1:8080` で動いている状態から始める。そのデータベースはファイル `service.db` だ。PostgreSQL のデータベースは、このコマンドではなく、PostgreSQL の `pg_dump` でバックアップする。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。

下のライセンスでは `<policy-id>` を使う。チュートリアルのポリシー `Standard` の `id` だ。控えていなければ、`elm admin request GET /v1/admin/policies --url http://127.0.0.1:8080` で、ポリシーを名前と一緒に一覧できる。

## バックアップの前にライセンスを作る

バックアップに何が入っているかをあとで確かめるため、ここでライセンスを作る。

<!-- steps:file path=license-a.json -->
```json
{"policyId":"<policy-id>","name":"customer-a","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-a pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/licenses --body license-a.json --url http://127.0.0.1:8080
```

レスポンスの最初にある `id` が、下の `<license-a>` だ。

## データベースをバックアップする

サービスが動いているあいだに、1 つ目のターミナルで、データベースをファイル `backup.db` にコピーする。

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
elm-service backup --config service.json --out backup.db
```

`elm-service backup` は 1 行を表示する。

```text
backup: backup.db, schema version 8
```

サービスがデータベースに書き込んでいるあいだでも、コピーはある時点のデータベース全体になる。サービスはコピーが終わるのを待たない。数字は、コピーにある最も新しいスキーマのバージョンだ。そのバージョンより古い `elm-service` は、このコピーを使えない。バックアップの時刻を書き留めておく。復元すると、それより後のことはすべて取り消される。何が取り消されたかは、監査ログでわかる。

`backup` は、サービスと同じ設定ファイルを読んで確かめ、そこで指定したシークレットのファイルも読む。ただし、署名鍵、その鍵証明書、ルート公開鍵、署名鍵のパスフレーズは読まない。`ELM_SIGNING_PASSPHRASE` も、動いているサービスも要らない。レート制限が範囲外であるなどの設定のエラーがあると、サービスと同じように終了コード 2 で止まる。ファイルは上書きせず、フォルダーも作らない。バックアップには、日付を入れた名前のように、毎回新しい名前を付ける。その名前のファイルがすでにあると、終了コード 1 で終わり、`The backup file 'backup.db' exists.` を表示する。存在しないフォルダーを指定すると、終了コード 1 で終わり、`The backup file '<path>' cannot be written:` で始まる行を表示する。この行には、作れなかった一時ファイル `<path>.<random hex>.tmp` の名前が出る。

バックアップは、自分だけが読み書きできる場所に置く。バックアップには、顧客のライセンスの名前とメタデータ、監査ログにあるアドレス、API キーのハッシュが入っている。誰かが書き換えたコピーを復元すると、データベースに書き込まれたときと同じように、その人が管理者や API キーを足せてしまう。Linux と macOS では、ファイルは所有者だけが読み書きできる。Windows では、ファイルは置いたフォルダーのアクセス権を引き継ぐので、自分のアカウントだけが開けるフォルダーに置く。こうすると、`backup` が書いているあいだに、ほかの人がファイルを差し替えることも防げる。ベンダーが守らなければならないものは、[セキュリティモデル](../explanation/security-model.md#ベンダー)に挙げている。

## データベースの外にあるファイルを取っておく

データベースには、サービスの鍵と設定は入っていない。次のものも、データベースのバックアップとは別に取っておき、設定の[シークレット](../reference/service-configuration.md#シークレット)のとおりにそれぞれを守る。

- 署名鍵とその鍵証明書。そのパスフレーズか `signing.passphraseFile` は、鍵のファイルとは別にしておく。
- `trust.rootKeys` のルート公開鍵。ルート秘密鍵はオフラインに置くもので、サービスのバックアップと一緒にはしない。[ルート鍵を安全に保管する](keep-the-root-key-safe.md)を読む。
- ノンスのシークレット。
- 管理画面のサインインを使うときは、`dataProtection.kekFile` の鍵暗号化鍵と、`oidc.webClientSecretFile` の Web クライアントのシークレット。同じ鍵暗号化鍵がないと、サービスは復元したデータベースにあるサインイン用の鍵を読めず、起動しない。
- サービスが自分で https を提供するときは、TLS の証明書と鍵。
- 設定ファイルと、接続文字列のファイル。
- `usageLog.directory` を設定しているときは、利用ログのフォルダー。
- バックアップを作ったバージョンの `elm-service` のアーカイブ。コピーのスキーマを知っているバージョンを起動できるようにするためだ。

## バックアップの後にライセンスを作る

バックアップに入っていない 2 つ目のライセンスを作る。

<!-- steps:file path=license-b.json -->
```json
{"policyId":"<policy-id>","name":"customer-b","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-b pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/licenses --body license-b.json --url http://127.0.0.1:8080
```

その `id` が、下の `<license-b>` だ。

## バックアップでサービスを起動して確かめる

バックアップが使えることを確かめるには、それでサービスを起動する。サービスは、動かしているファイルに監査ログも含めて書き込むので、そのあとファイルは `backup` が書いたときのままではなくなる。このガイドの `backup.db` は試すためだけのものだ。取っておくバックアップなら、まずシステムのツールでコピーし（PowerShell では `Copy-Item backup.db check.db`、Bash か zsh では `cp backup.db check.db`）、そのコピーでサービスを起動する。

`database.connection` をバックアップに変える。

<!-- steps:file path=database.connection -->
```text
Data Source=backup.db
```

2 つ目のターミナルで Ctrl+C を押し、そこでサービスをもう一度起動する。そのターミナルには `ELM_SIGNING_PASSPHRASE` がまだ設定されている。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

`service.started` の前に `Schema versions applied: 1, 2, …` がログに出たら、サービスはファイルを見つけられず、新しい空のデータベースを作っている。`database.connection` の名前を確かめる。空のデータベースで動くサービスは、API キーを 1 つも持たないので、すべての API キーを 401 で拒否する。

1 つ目のライセンスを読む。

<!-- steps:run capture=license-a-name pattern="(customer-a)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-a> --url http://127.0.0.1:8080
```

`elm` はライセンス `customer-a` を表示する。2 つ目を読む。

<!-- steps:run exit=1 capture=license-b-missing stream=stderr pattern="(ELM_LICENSE_NOT_FOUND)" -->
```shell
elm admin request GET /v1/admin/licenses/<license-b> --url http://127.0.0.1:8080
```

バックアップにはそのライセンスがないので、`elm` は終了コード 1 で終わり、標準エラー出力に次のように表示する。

```text
error: the service answered 404.
{"type":"about:blank","title":"ELM_LICENSE_NOT_FOUND","status":404,"detail":"No license has this ID.","code":"ELM_LICENSE_NOT_FOUND"}
```

バックアップの監査ログを確かめる。

<!-- steps:run capture=audit-intact pattern="intact.:\s*(true)" -->
```shell
elm admin request POST /v1/admin/audit/verify --url http://127.0.0.1:8080
```

レスポンスには `"intact":true` が入っている。監査ログは欠けずにそろっている。`ELM_API_KEY` の API キーがここで使えるのは、チュートリアルがバックアップの前にそのキーを作ったからだ。終わったら、`database.connection` を `Data Source=service.db` に戻し、サービスを再起動する。これが望む復元なら、代わりにこのファイルでサービスを動かし続けてもよい。そのときは新しいバックアップを作る。このファイルが、いま使われているデータベースになったからだ。

## バックアップを復元する

バックアップをデータベースの代わりに置くには、次のようにする。

1. 置き換えるデータベースでサービスがまだ動いているうちに、[`GET /v1/admin/audit`](../reference/administration-api.md#get-v1adminaudit) でその監査ログを保存する。バックアップにないことの記録は、これしかない。
2. Ctrl+C でサービスを止める。
3. `service.db` を、隣に `service.db-wal` と `service.db-shm` があればそれらと一緒に、別の場所へ移す。Ctrl+C で止めたサービスはふつう `-wal` ファイルを残さないが、強制終了したりクラッシュしたりしたサービスは残す。SQLite は古い `-wal` を復元したファイルの一部として読み、ファイルを壊してしまう。PowerShell では `New-Item -ItemType Directory replaced | Out-Null` と打ってから `Move-Item service.db* replaced` と打つ。Bash か zsh では `mkdir replaced` と打ってから `mv service.db* replaced/` と打つ。
4. バックアップを `service.db` にコピーする。PowerShell では `Copy-Item backup.db service.db`、Bash か zsh では `cp backup.db service.db` だ。`backup.db` はそのまま残しておく。
5. コピーのスキーマのバージョンを知っている `elm-service` で、サービスを起動する。バックアップを作ったバージョンか、それより新しいバージョンだ。サービスは、ファイルを自分の使う WAL（先行書き込みログ）モードに戻す。ログに `Schema versions applied: 1, 2, …` が出たら、サービスは `service.db` を見つけられず、空のデータベースを作っている。サービスを止めて、手順 4 をやり直す。
6. バックアップを確かめたときと同じように、復元したデータベースを確かめる。知っているライセンスを読み、`POST /v1/admin/audit/verify` で監査ログを確かめる。
7. 保存した監査ログにある、バックアップの時刻より後の操作をもう一度する。同じライセンスを失効させたり一時停止したりし、同じ API キーを失効させ、同じ管理者を無効にする。

## 復元で取り消されるもの

復元すると、データベースはバックアップの時点の状態に戻り、それより後のことはすべてなくなる。

- バックアップの後に作ったライセンスや API キーはなくなる。そうしたライセンスのライセンスキーを持つ顧客は `ELM_LICENSE_NOT_FOUND` を受け取る。ライセンスを作り直し、その新しいライセンスキーを顧客に渡す。
- バックアップの後にトークンを更新してライセンスファイルを新しくしたデバイスは、復元したデータベースが知らないシーケンス番号を送る。サービスはこれを、コピーされたインストールとして扱う。ポリシーの `cloneStrategy` が既定の `deny` なら、サービスはそのデバイスのそれ以降のトークンの更新をすべて `ELM_CLONE_DETECTED` で拒否し、デバイスのトークンの更新は止まる。そのあと管理者が[管理者としてアクティベーションを解除する](deactivate-a-device.md#管理者としてアクティベーションを解除する)のとおりにそのアクティベーションを解除し、デバイスはもう一度アクティベーションする。[アクティベーション](../explanation/activation.md#シーケンス番号によるクローン検出)を読む。
- バックアップの後にしたアクティベーションはなくなる。そのデバイスのトークンの更新は `ELM_ACTIVATION_NOT_FOUND` で失敗し、デバイスはもう一度アクティベーションしなければならない。
- バックアップの後に失効させたか一時停止したライセンス、失効させた API キー、無効にした管理者は、また有効になる。止めたキーは、手順 7 のとおりにもう一度止めるまで、また使える。
- 監査ログはバックアップの時点で終わる。復元した行は欠けていないので、監査ログの確認は通る。
- バックアップの後に与えたクラウドのリースはなくなる。アプリが次にリースを更新すると `ELM_UNKNOWN_LEASE` が返り、リースは失われる。`usageLog.directory` を設定していれば、利用ログにはそれらのリースが残っている。

## 関連するページ

- [`elm-service backup`](../reference/service-configuration.md#コマンドラインと終了コード): すべての引数と終了コード。
- [サービスをアップグレードする](upgrade-the-service.md): アップグレードのたびに、その前にバックアップする。
- [セキュリティモデル](../explanation/security-model.md): データベースとそのバックアップが何を守るか。
