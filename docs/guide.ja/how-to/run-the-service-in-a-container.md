---
sidebar_position: 19
---

# サービスをコンテナで動かす

このガイドは、Docker のある Linux ホストで `elm-service` をコンテナとして動かすベンダーの運用担当者に向けている。このガイドでは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定とシークレットを、サービスだけが読めるボリュームに入れ、データベースを 2 つ目のボリュームに置く。そのあと、最初の API キーを作り、サービスを起動し、サービスを動かしたままバックアップする。Windows や macOS では、Linux の仮想マシンか、Docker を入れた WSL の Linux ディストリビューションで進める。イメージが動かすのはチュートリアルのアーカイブと同じ `elm-service` で、ここでは SQLite を使う。[サービスを PostgreSQL で動かす](run-the-service-on-postgresql.md)では、これを PostgreSQL に移す。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。使うのは Docker のある Linux ホストだ。チュートリアルのサービスは、ポート 8080 で動かしたままでよい。下のコンテナは、ホストのポート 8090 で待ち受ける。

コマンドでは、イメージを `elm-service` という名前で使う。イメージはまだ公開されていない。最初のリリースまでは、`CONTRIBUTING.md` の [Build the container image](https://github.com/YUKIKEDA/ElasticLM/blob/main/CONTRIBUTING.md#build-the-container-image) のとおりにイメージをビルドする。そうすると、イメージにこの名前が付く。イメージが公開されたら、`ghcr.io/yukikeda/elm-service` からダイジェストを指定して取得し、この名前を付ける。ダイジェストは、タグと違って別のイメージに付け替えられない。`docker pull ghcr.io/yukikeda/elm-service@sha256:<digest>` を実行し、続けて `docker tag ghcr.io/yukikeda/elm-service@sha256:<digest> elm-service` を実行する。

<!-- steps:prebuilt name=image -->

イメージには `/app/elm-service` が入っている。アーカイブ `elm-service-<version>-linux-x64.tar.gz` や `-linux-arm64.tar.gz` にあるのと同じ実行ファイルで、ベースはシェルもパッケージマネージャーもないイメージだ。イメージはユーザー ID 1654 で動き、`http://0.0.0.0:8080` で待ち受け、データを `/data` に置く。

## コンテナ用の設定を書く

コンテナの中では、サービスは `/config` の下でファイルを探し、データベースを `/data` の下に置く。チュートリアルのフォルダーに、チュートリアルと違うファイルを入れる `container` という名前のフォルダーを作る。その中に、チュートリアルの設定とこれらのパスを書いた `container.json` という名前のファイルを作る。

<!-- steps:file path=container/container.json -->
```json
{
  "publicUrls": ["https://elm.example/"],
  "node": {
    "name": "node-1"
  },
  "database": {
    "provider": "sqlite",
    "connectionFile": "/config/database.connection"
  },
  "signing": {
    "key": "/config/signing.key.pem",
    "certificates": ["/config/signing.cert"]
  },
  "trust": {
    "rootKeys": ["/config/root.pub.pem"]
  },
  "nonce": {
    "secretFile": "/config/nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

- `publicUrls` は、デバイスがサービスに接続するときの `https://` のアドレスだ。サービスの前に置くリバースプロキシのアドレスなどがこれに当たる。コンテナでは、サービスはループバック以外のアドレスで待ち受けるので、`publicUrls` がないと `setup` もサービスも起動しない。`https://elm.example/` の代わりに自分のアドレスを使う。
- `node.name` は、このノードの変わらない名前だ。これがないと、名前はコンテナのホスト名になる。ホスト名はコンテナを作り直すたびに変わり、そうすると利用ログは別のノードのファイルを書き始める。

データベースの接続文字列は `container/database.connection` に書く。こうすると、チュートリアルの `database.connection` はそのまま残る。

<!-- steps:file path=container/database.connection -->
```text
Data Source=/data/service.db
```

## ファイルをボリュームに入れる

ボリュームを 2 つ作る。1 つは設定とシークレット用、もう 1 つはデータ用だ。

<!-- steps:run -->
```shell
docker volume create elm-config
docker volume create elm-data
```

シークレットは、ユーザー ID 1654 だけが読めるようにしなければならない。補助のコンテナが、シークレットを `elm-config` にコピーし、そのユーザーのものにする。補助のコンテナを、起動せず、ネットワークなしで作り、ファイルをコピーしてから起動して、ファイルの所有者を変える。

<!-- steps:run -->
```shell
docker create --name elm-config-helper --network none -v elm-config:/config busybox@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec chown -R 1654:1654 /config
docker cp container/container.json elm-config-helper:/config/
docker cp container/database.connection elm-config-helper:/config/
docker cp signing.key.pem elm-config-helper:/config/
docker cp signing.cert elm-config-helper:/config/
docker cp root.pub.pem elm-config-helper:/config/
docker cp nonce.secret elm-config-helper:/config/
docker start -a elm-config-helper
docker rm elm-config-helper
docker run --rm --network none -v elm-config:/config busybox@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec chmod -R u=rX,go= /config
```

これで `elm-config` にファイルが入り、所有者だけが読める状態になった。Kubernetes では、ポッドの `securityContext.fsGroup: 1654` でシークレットに同じ所有者を与えるか、シークレットの `defaultMode` で読めるようにする。

## 最初の API キーを作る

終わると自分を削除するコンテナで、`setup` を 1 回実行する。

<!-- steps:run capture=container-api-key env=ELM_API_KEY -->
```shell
docker run --rm -v elm-config:/config:ro -v elm-data:/data elm-service setup --config /config/container.json
```

チュートリアルと同じように、1 行目に新しい API キーが表示される。`elm-data` のデータベースは新しいので、チュートリアルのキーはそこでは使えない。[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)と同じ方法で、キーを `ELM_API_KEY` に読み込む。

## サービスを起動する

2 つ目のターミナルでサービスを起動する。そのターミナルでは、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、`ELM_SIGNING_PASSPHRASE` に署名鍵のパスフレーズが入っている。

<!-- steps:background ready=http://127.0.0.1:8090/health -->
```shell
docker run --rm --name elm-service -p 127.0.0.1:8090:8080 -v elm-config:/config:ro -v elm-data:/data -e ELM_SIGNING_PASSPHRASE elm-service --config /config/container.json
```

値を付けない `-e ELM_SIGNING_PASSPHRASE` は、このターミナルの変数をコンテナに渡す。それでも値は `docker inspect elm-service` に表示され、Kubernetes ではポッドの定義に出る。本番では、代わりにパスフレーズを `elm-config` のファイルに入れ、`signing.passphraseFile` でそのファイルを指定する。`-p 127.0.0.1:8090:8080` は、ホストのループバックアドレスにだけポートを公開する。その前にリバースプロキシか Ingress で TLS を置くか、サービスに `tls.certificate` と `tls.key` を与える。

サービスはチュートリアルと同じようにログを表示し、最後に `service.started` を出す。`/health` は、サービスがデータベースに接続できるあいだは 200 を返し、接続できないときは 503 を返す。イメージにはシェルがないので、`HEALTHCHECK` もない。`/health` はコンテナの外から確かめる。Kubernetes では、これを `readinessProbe`（`httpGet`、パス `/health`、ポート 8080）に使い、`livenessProbe` には使わない。そちらに使うと、データベースが止まっているあいだ、サービスを何度も再起動してしまう。

チュートリアルと同じように、1 つ目のターミナルから管理 API で製品を作る。

<!-- steps:run capture=container-product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8090
```

作った製品を読む。

<!-- steps:run capture=container-product-code pattern="(acme-cad)" -->
```shell
elm admin request GET /v1/admin/products/<container-product-id> --url http://127.0.0.1:8090
```

`elm` は製品 `acme-cad` を表示する。デバイスのプルーフは `publicUrls` のアドレスを指定していなければならないので、アプリは `http://127.0.0.1:8090` を通してはアクティベーションできない。アプリは、リバースプロキシを通して `https://elm.example/` でサービスに接続する。

## データベースをバックアップする

サービスが動いているあいだに、同じボリュームを使う 2 つ目のコンテナで `elm-service backup` を実行する。

<!-- steps:run capture=schema-version pattern="schema version ([0-9]+)" -->
```shell
docker run --rm -v elm-config:/config:ro -v elm-data:/data elm-service backup --config /config/container.json --out /data/backup-1.db
```

`backup: /data/backup-1.db, schema version 8` か、別のバージョン番号の行が表示される。2 つのコンテナが SQLite のデータベースを共有できるのは、ここのように同じホストのボリュームを通すときだけだ。ネットワークファイルシステム上のボリュームは、SQLite のロックを保たない。[サービスをバックアップして復元する](back-up-and-restore-the-service.md)にあるとおり、バックアップをボリュームからコピーして取り出し、ホストとは別の場所に置く。

<!-- steps:run -->
```shell
docker cp elm-service:/data/backup-1.db backup-1.db
```

## サービスを止める

`docker stop elm-service` でサービスを止める。Docker は SIGTERM を送り、サービスは実行中の処理を終えてから終了コード 0 で終わる。そのあと `--rm` がコンテナを削除する。ボリュームは残るので、同じ `docker run` の行でサービスをもう一度起動できる。このガイドで作ったものをすべて消すには、サービスが止まってから `docker volume rm elm-config elm-data` を実行する。

- ここのように、データは名前付きボリュームに置く。`-v elm-data:/data` がないと、Docker は匿名ボリュームを作り、コンテナを作り直すとデータが消える。
- 代わりにホストのフォルダーにデータを置くときは、先にユーザー ID 1654 をそのフォルダーの所有者にする。
- Kubernetes では、PersistentVolumeClaim はイメージの `/data` の所有者を引き継がない。`securityContext.fsGroup: 1654` を設定する。
- ルートファイルシステムを読み取り専用にするときは、`/tmp` にも書き込めるボリュームをマウントする。

## 関連するページ

- [elm-service の設定](../reference/service-configuration.md): すべての設定と、ファイルに入れられる[シークレット](../reference/service-configuration.md#シークレット)。
- [サービスをバックアップして復元する](back-up-and-restore-the-service.md)と[サービスをアップグレードする](upgrade-the-service.md): 同じ作業をコンテナの外でする方法。
- [サービスを PostgreSQL で動かす](run-the-service-on-postgresql.md): 複数のコンテナが共有するデータベース。
