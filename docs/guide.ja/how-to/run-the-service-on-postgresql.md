---
sidebar_position: 20
---

# サービスを PostgreSQL で動かす

このガイドは、`elm-service` を PostgreSQL で動かすベンダーの運用担当者に向けている。PostgreSQL は、サービスの複数のノードが共有できるデータベースだ。SQLite は 1 つのノードにしか使えない。このガイドでは、サービス用のデータベースとロールを用意し、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定を PostgreSQL に移し、`pg_dump` でデータベースをバックアップして `pg_restore` で復元する。そして、複数のノードが何を共有し、どの順で新しいバージョンに移るかを示す。試すために、このガイドでは Docker のある Linux ホストのコンテナで PostgreSQL を起動する。Windows や macOS では、Linux の仮想マシンか、Docker を入れた WSL の Linux ディストリビューションで進める。自分の PostgreSQL サーバーがあるなら、コンテナは使わず、同じ SQL をそのサーバーで実行する。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。サービスは、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに 2 つ目のターミナルで動かし、そのターミナルで `ELM_SIGNING_PASSPHRASE` を設定しておく。

`elm-service` は PostgreSQL 15 から 18 で動く。サービスは、チュートリアルのデータを SQLite から移さない。PostgreSQL では空のデータベースで起動するので、製品、ポリシー、ライセンスはそこで作り直す。

## 試すための PostgreSQL サーバーを起動する

ホストのループバックアドレスでだけ待ち受けるように、コンテナで PostgreSQL 18 を起動する。パスワードは試すためだけのものだ。使い続けるサーバーには、このような値を決して使わない。ポートが空いていれば、`ss -ltn 'sport = :5432'` は見出しの行だけを表示する。自分の PostgreSQL がすでにポート 5432 で待ち受けているなら、`-p 127.0.0.1:5433:5432` のようにホスト側の別のポートを使い、下の接続文字列にもそのポートを書く。

<!-- steps:run -->
```shell
docker run -d --name elm-postgres -p 127.0.0.1:5432:5432 -e POSTGRES_PASSWORD=try-only-postgres postgres:18@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6
docker exec elm-postgres sh -c 'until pg_isready -q -h 127.0.0.1 -U postgres; do sleep 1; done'
```

2 行目は、サーバーが TCP で接続を受け付けるようになるまで待つ。コンテナは最初に、TCP では待ち受けないサーバーでデータを準備するからだ。

## ロールとデータベースを作る

サービスは自分専用のロールで接続する。このロールはサービスのデータベースを所有し、それ以外は何も所有しない。SQL をファイルに書く。`try-only-elm` の代わりに、自分で決めたパスワードを使う。下の接続文字列にも、同じパスワードを使う。

<!-- steps:file path=create-elm.sql -->
```sql
CREATE ROLE elm LOGIN PASSWORD 'try-only-elm';
CREATE DATABASE elm OWNER elm;
CREATE DATABASE elm_restored OWNER elm;
```

2 つ目のデータベース `elm_restored` は、下で復元を試すためのものだ。このファイルを、コンテナの中で `psql` で実行する。

<!-- steps:run -->
```shell
docker cp create-elm.sql elm-postgres:/tmp/
docker exec elm-postgres psql -U postgres -v ON_ERROR_STOP=1 -f /tmp/create-elm.sql
```

自分のサーバーでは、管理者のロールで `psql -h <host> -U <admin-role> -v ON_ERROR_STOP=1 -f create-elm.sql` を実行する。データベースのテーブルは、サービスが `setup` を実行するときか起動するときに、自分で作る。

## サービスの接続先を PostgreSQL にする

`database.connection` の中身を、PostgreSQL の接続文字列に置き換える。このファイルにはロールのパスワードが入るので、Linux と macOS では `chmod 600 database.connection` で所有者だけが読めるようにする。

<!-- steps:file path=database.connection -->
```text
Host=127.0.0.1;Port=5432;Database=elm;Username=elm;Password=try-only-elm
```

`service.json` で、`database.provider` を `postgres` に変える。

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "postgres",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

接続文字列は、[elm-service の設定](../reference/service-configuration.md#database)にあるとおり Npgsql の形式だ。ネットワーク越しに接続するサーバーでは、Npgsql の接続文字列のパラメーターの一覧（https://www.npgsql.org/doc/connection-string-parameters.html）にあるとおり、Npgsql の `SSL Mode` キーを足す。

チュートリアルと同じように、新しいデータベースを準備し、最初の API キーを作る。

<!-- steps:run capture=pg-api-key env=ELM_API_KEY -->
```shell
elm-service setup --config service.json
```

`setup` は `elm` にテーブルを作り、1 行目に新しい API キーを表示する。[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに、キーを `ELM_API_KEY` に読み込む。チュートリアルのキーは SQLite のデータベースにあるので、ここでは使えない。

2 つ目のターミナルで Ctrl+C を押し、そこでサービスをもう一度起動する。そのターミナルには `ELM_SIGNING_PASSPHRASE` がまだ設定されている。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

`service.started` の行には、今度は `database Postgres` と出る。1 つ目のターミナルで、製品をもう一度作る。

<!-- steps:run capture=pg-product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8080
```

その `id` が、下の `<pg-product-id>` だ。

## pg_dump でバックアップする

`elm-service backup` がコピーできるのは SQLite のデータベースだけだ。PostgreSQL では、PostgreSQL の `pg_dump` を使う。これは、サービスが動いているあいだでも整合性の取れたコピーを取る。サーバーと同じメジャーバージョンか、それより新しい `pg_dump` を使う。ここでは、サーバーのコンテナにあるものを使う。`pg_restore` が読めるカスタム形式（`-Fc`）で書き出し、コンテナの外にコピーする。

<!-- steps:run -->
```shell
docker exec elm-postgres pg_dump -U elm -d elm -Fc -f /tmp/elm.dump
docker cp elm-postgres:/tmp/elm.dump elm.dump
```

自分のサーバーでは、PostgreSQL のクライアントツールがあるマシンで `pg_dump -h <host> -U elm -d elm -Fc -f elm.dump` を実行する。[サービスをバックアップして復元する](back-up-and-restore-the-service.md#データベースをバックアップする)が SQLite のファイルについて書いているとおり、コピーは自分だけが読める場所に置く。SQLite のファイルと同じデータが入っているからだ。そのガイドに挙げているとおり、データベースの外にあるファイルも取っておく。

## pg_restore で復元する

コピーを 2 つ目のデータベースに復元する。`--no-owner` を付けると、復元するロール `elm` が、作ったものの所有者になる。

<!-- steps:run -->
```shell
docker exec elm-postgres pg_restore -U elm -d elm_restored --no-owner /tmp/elm.dump
```

確かめるには、サービスの接続先を `elm_restored` にする。

<!-- steps:file path=database.connection -->
```text
Host=127.0.0.1;Port=5432;Database=elm_restored;Username=elm;Password=try-only-elm
```

2 つ目のターミナルで Ctrl+C を押し、そこでサービスをもう一度起動する。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

製品を読む。

<!-- steps:run capture=pg-product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<pg-product-id> --url http://127.0.0.1:8080
```

`elm` は製品 `acme-cad` を表示する。本番で復元するときは、すべてのノードを止め、空のデータベースに復元し（データベースを削除して作り直すか、新しいデータベースを作ってすべてのノードの接続文字列を変える）、ノードをもう一度起動する。[復元で取り消されるもの](back-up-and-restore-the-service.md#復元で取り消されるもの)に挙げているとおり、復元するとバックアップの後のことはすべて取り消される。[バックアップを復元する](back-up-and-restore-the-service.md#バックアップを復元する)のとおりに、先に監査ログを保存しておく。

試し終えたら、`database.connection` を `Database=elm` に戻し、サービスを再起動する。または、`Data Source=service.db` と `"provider": "sqlite"` で、チュートリアルの SQLite に戻す。

## 複数のノードを動かす

サービスのすべてのノードは同じデータベースに接続し、次のものを同じにしておく。

- ノンスのシークレット（`nonce.secretFile`）。あるノードが出したノンスを、別のノードでも受け付けられるようにするためだ。
- 署名鍵とその鍵証明書、そのパスフレーズ。
- ルート公開鍵と、失効させた鍵 ID（`trust`）。
- 管理画面のサインインを有効にしているときは、管理画面のサインインに使う鍵暗号化鍵（`dataProtection.kekFile`）。

各ノードには別々の `node.name` を付け、それぞれが自分の利用ログのファイルを書くようにする。ノードは 1 つのロードバランサーの後ろに置き、そのアドレスをすべてのノードの `publicUrls` に入れる。[リクエストのレートを制限する](limit-request-rates.md#複数のノードと再起動)にあるとおり、レート制限はノードごとに設定する。

## 複数のノードをアップグレードする

新しいバージョンは、起動するときにデータベースのマイグレーションを適用する。そのとき PostgreSQL のロックを使うので、1 つのノードがマイグレーションするあいだ、ほかのノードは待つ。マイグレーションのあと、古いバージョンのノードはそのデータベースでは起動しない。終了コード 1 で終わり、`The database has schema version <n>, which this elm-service does not know; it is older than the database.` を表示する。そのため、次の順で進める。

1. 上と同じように、`pg_dump` でデータベースをバックアップする。
2. すべてのノードを止める。
3. 新しいバージョンでノードを 1 つ起動し、`service.started` が出るまで待つ。そのノードがマイグレーションを適用し、ログの `Schema versions applied` に適用したものを挙げる。
4. ほかのノードを新しいバージョンで起動する。

新しいバージョンの最初のノードが起動する前に、古いバージョンのノードをすべて止めておく。そうすれば、マイグレーションのあいだに古いバージョンのノードがデータベースで処理をすることはない。古いバージョンに戻すには、[サービスをアップグレードする](upgrade-the-service.md#古いバージョンに戻す)にあるとおり、手順 1 のバックアップを空のデータベースに復元し、古いバージョンを起動する。

## PostgreSQL のコンテナを止めて削除する

このガイドのコンテナは試すためだけのものだ。まずサービスを止め、次にコンテナを止めて、そのデータボリュームと一緒に削除する。データボリュームには、データベースとロールのパスワードのハッシュが入っている。`docker stop elm-postgres` を実行し、続けて `docker rm -v elm-postgres` を実行する。チュートリアルのフォルダーの `elm.dump` と `create-elm.sql` も削除する。

## 関連するページ

- `elm-service` の設定の [`database`](../reference/service-configuration.md#database) と [`node`](../reference/service-configuration.md#node)。
- [サービスをバックアップして復元する](back-up-and-restore-the-service.md): バックアップに何が入り、復元で何が取り消されるか。
- [サービスをコンテナで動かす](run-the-service-in-a-container.md): 設定をボリュームに入れて、イメージで動かす方法。
