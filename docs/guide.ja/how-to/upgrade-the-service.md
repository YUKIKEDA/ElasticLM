---
sidebar_position: 18
---

# サービスをアップグレードする

このガイドは、`elm-service` を 1 つのノードと SQLite で動かしていて、それを新しいバージョンに移すベンダーの運用担当者に向けている。新しいバージョンにはデータベースのマイグレーションが加わっていることがあり、サービスは起動時にそれを適用する。マイグレーションを元に戻す手段はない。新しいバージョンがデータベースにマイグレーションを適用すると、古いバージョンはそのデータベースを拒否する。そのため、このガイドでは、まずバックアップを取り、それからサービスを止め、実行ファイルを置き換えて、もう一度起動する。このガイドは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定で、`elm-service` が `http://127.0.0.1:8080` で動いている状態から始める。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。

`elm-service` には `--version` オプションがない。バージョンは、`elm-service-<version>-<RID>.zip` のような、取り出したアーカイブの名前にある。今動かしているバージョンを書き留め、新しいバージョンをしばらく動かすまで、そのアーカイブを取っておく。

新しいバージョンのアーカイブ `elm-service-<version>-<RID>` を [Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)からダウンロードし、チュートリアルと同じように、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめる。設定の変更がないか、リリースノートを読む。このガイドで置き換えるのは `elm-service` だけだ。同じリリースの `elm` ツールは別のアーカイブ `elm-<version>-<RID>` に入っていて、同じ方法で置き換える。

## データベースをバックアップする

サービスがまだ古いバージョンで動いているうちに、データベースをコピーする。

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
elm-service backup --config service.json --out before-upgrade.db
```

`elm-service backup` は、次のように、バックアップのファイルとそのスキーマのバージョンを表示する。

```text
backup: before-upgrade.db, schema version 10
```

新しいバージョンのままでよいと確かめられるまで、このファイルを取っておく。古いバージョンに戻す手段はこれしかない。どこに置くか、ほかに何を取っておくかは、[サービスをバックアップして復元する](back-up-and-restore-the-service.md)で説明している。

## 実行ファイルを置き換えてサービスを起動する

1. 2 つ目のターミナルで Ctrl+C を押して、サービスを止める。
2. チュートリアルと同じように、新しいアーカイブを、古いファイルを上書きして `tools` フォルダーに展開する。Windows では `Expand-Archive elm-service-<version>-<RID>.zip -DestinationPath tools -Force`、Linux と macOS では `tar -xzf elm-service-<version>-<RID>.tar.gz -C tools` だ。
3. `ELM_SIGNING_PASSPHRASE` がまだ設定されている 2 つ目のターミナルで、サービスをもう一度起動する。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

新しいバージョンにマイグレーションがあれば、サービスは起動時にそれを適用し、どれを適用したかをログに出す。この行は、Web ホストのエントリー（アドレス、`Application started`、`Content root path`）のあとに出る。

```text
info: elm-service[3]
      Schema versions applied: 11
```

新しいマイグレーションがなければ、ログにこの行は出ない。どちらの場合も、ログは `service.started` の行で終わる。リリースノートに別の指示がない限り、設定ファイルはそのままでよい。

チュートリアルの製品を読むなどして、サービスがこれまでのデータを返すことを確かめる。

<!-- steps:run capture=product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<product-id> --url http://127.0.0.1:8080
```

`elm` は製品 `acme-cad` を表示する。

SQLite で動かせるのは 1 つのノードだけだ。PostgreSQL で複数のノードを動かすときは、サービスをまた使い始める前に、すべてのノードを新しいバージョンに移す。新しいバージョンがマイグレーションしたデータベースでは、古いバージョンのノードは起動しない。

## 古いバージョンに戻す

マイグレーションを元に戻す手段はない。新しいバージョンが、古いバージョンの知らないマイグレーションをデータベースに適用すると、古いバージョンはそのデータベースでは起動しない。終了コード 1 で終わり、次のような行を表示する。

```text
The database has schema version 11, which this elm-service does not know; it is older than the database.
```

マイグレーションのないアップグレードなら、データベースは古いバージョンが知っている形のままなので、古いバージョンはこれまでどおりそのデータベースで起動する。マイグレーションのあとで戻すには、サービスを止め、[バックアップを復元する](back-up-and-restore-the-service.md#バックアップを復元する)のとおりに `before-upgrade.db` を復元し、古いバージョンの実行ファイルを `tools` に戻して起動する。そうすると、バックアップの後のことはすべてなくなる。何がなくなるかは[復元で取り消されるもの](back-up-and-restore-the-service.md#復元で取り消されるもの)に挙げている。

## 関連するページ

- [サービスをバックアップして復元する](back-up-and-restore-the-service.md): バックアップに何が入るかと、その復元のしかた。
- [elm-service の設定](../reference/service-configuration.md#コマンドラインと終了コード): サービスの終了コード。
- [ログ](../reference/logs.md): サービスのログのエントリー。適用したスキーマのバージョンも含む。
