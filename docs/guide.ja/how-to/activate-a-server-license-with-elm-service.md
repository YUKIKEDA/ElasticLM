---
sidebar_position: 22
---

# elm-service でサーバーライセンスをアクティベーションする

このガイドは、`elm-service` を通してフローティングライセンスを販売するベンダーと、`elm-server` を運用する顧客の管理者に向けている。サーバーライセンスを手作業で発行する代わりに、ベンダーがサービスでサーバーライセンスを作り、管理者がそのライセンスキーでサーバーをアクティベーションする。そのあとサーバーは、サービスからライセンスを更新する。サービスに接続できるときは自分で更新し、接続できないときはリクエストファイルとレスポンスファイルで更新する。このガイドは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定で、`elm-service` が `http://127.0.0.1:8080` で動いている状態から始める。サーバーは、[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)と同じように、ここで準備する。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

このガイドでは、1 台のマシンが両方の役を務める。ベンダーのコマンドも管理者のコマンドも、チュートリアルのフォルダーで、同じ 1 つ目のターミナルで打つ。実際の運用では、ベンダーは自分のマシンで、管理者はサーバーのマシンで作業する。

- 使うどのターミナルでも、チュートリアルの `tools` フォルダーが `PATH` に入っていなければならない。ターミナルを閉じたときは、チュートリアルの[始める前に](../tutorials/activate-an-app-online.md#始める前に)のとおりに、もう一度足す。
- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。
- 製品 `acme-cad` の ID が要る。チュートリアルで製品を作ったときに表示された ID で、下では `<product-id>` と書く。手元に残っていなければ、`elm admin request GET /v1/admin/products --url http://127.0.0.1:8080` で製品とその ID を一覧できる。
- ポート 7070 と 7071 が空いていなければならない。サーバーはこの両方で待ち受ける。ほかのプログラムが使っていれば、フローティングライセンスのチュートリアルの[サーバーを起動する](../tutorials/use-a-floating-license.md#サーバーを起動する)にあるとおり、サーバーは起動するときにそう表示する。
- Linux と macOS では、チュートリアルのフォルダーにグループやほかのユーザーが書き込めてはならない。サーバーが自分の秘密鍵を、この中のフォルダーに置くからだ。そのフォルダーで `chmod go-w .` と打てば、確実に書き込めなくなる。

`elm-server` は、チュートリアルのツールと同じリリースからダウンロードする。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)にあるアーカイブ `elm-server-<version>-<RID>` で、名前が `-symbols` で終わるほうではない。`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめてから、`tools` に展開する。Windows の PowerShell では `Expand-Archive elm-server-<version>-<RID>.zip -DestinationPath tools -Force` と打つ。`-Force` を付けると、すでにある `LICENSE` ファイルを上書きできる。Linux と macOS では `tar -xzf elm-server-<version>-<RID>.tar.gz -C tools` と打つ。

<!-- steps:prebuilt name=elm-server -->

## サービスでサーバーライセンスを作る

サーバーライセンスは、`licenseKind` が `server` のポリシーから作る。このようなポリシーでは、`binding` は既定の `nodeLocked` でなければならない。各ライセンスファイルは、サーバーのインストール鍵に紐付く。`floating` の `onPremises` は、顧客の `elm-server` がシートを貸し出すことを表す。ほかの設定は既定のままでよい。

- `maxActivations` 1: 1 つのライセンスでアクティベーションできるサーバーは 1 台だ。
- `tokenLifetime` 30 日と `refreshWindow` 20 日: サービスは各ファイルを 30 日有効にして発行し、サーバーは最後の 20 日のうちにそれを更新する。
- `leaseDuration` 10 分で、`borrowLimit` はなし: アプリは各リースを最長 10 分保持し、借用はできない。すべての設定は[設定を選ぶ](set-up-a-policy.md#設定を選ぶ)に挙げている。

チュートリアルの製品のポリシーを作る。`<product-id>` は、その ID に置き換える。

<!-- steps:file path=server-policy.json -->
```json
{"productId":"<product-id>","name":"Floating server","licenseKind":"server","floating":"onPremises"}
```

<!-- steps:run capture=server-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body server-policy.json --url http://127.0.0.1:8080
```

`id` が、下の `<server-policy-id>` だ。サーバーライセンスには、機能ごとに `seats` が要る。その機能を同時に持てるアプリのコピーの数だ。`pro` のシートを 5 つ持つライセンスを作る。

<!-- steps:file path=server-license.json -->
```json
{"policyId":"<server-policy-id>","name":"Customer server","features":[{"code":"pro","seats":5}]}
```

<!-- steps:run capture=server-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body server-license.json --url http://127.0.0.1:8080
```

`elm` はライセンスを表示する。最後の `key` がライセンスキーで、下の `<server-license-key>` だ。ベンダーは、これを顧客の管理者に渡す。サービスがこのキーを示すのは、このレスポンスだけだ。最初の `id` はライセンス ID で、あとでサーバーが表示する。

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Customer server","features":[{"code":"pro","ver":null,"seats":5,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000S-ERVER-00000"}
```

## サーバーを準備する

チュートリアルのフォルダーに `server.json` を作る。

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "service": {
    "url": "http://127.0.0.1:8080"
  }
}
```

- `trust.rootKeys` は、ベンダーのルート公開鍵だ。この鍵が、サービスの署名鍵を証明する。
- `service.url` は、ベンダーの `elm-service` だ。これがあると、サーバーはこのサービスから自分でライセンスを更新する。実際の運用では、サービスの `https` のアドレスを使う。`http` を受け付けるのは、ループバックアドレスのときだけだ。環境変数 `ELM_SERVICE_URL` でも設定できる。
- `license` は、ライセンスファイルを置く場所だ。ファイルはまだない。アクティベーションのときに書かれる。

すべての設定は [elm-server の設定](../reference/server-configuration.md)に挙げている。サーバーを一度起動して、インストール鍵を作らせる。ライセンスを紐付ける鍵だ。

<!-- steps:run exit=1 -->
```shell
elm-server --config server.json
```

サーバーはまだライセンスがないので、すぐに終了コード 1 で止まり、`error: The server license 'server.elmlic' does not exist.` で始まる 1 行を表示する。この行には、サーバーのインストール鍵も示されている。これは想定どおりだ。鍵は `server-data` に残る。

## サーバーをアクティベーションする

ライセンスキーでサーバーをアクティベーションする。アクティベーションと更新をする `elm server` のコマンドは `server.json` を読まない。そのため、下のどのコマンドにも製品、データフォルダー、ルート公開鍵を指定し、ライセンスファイルを書くコマンドには、そのパスも指定する。

<!-- steps:run capture=first-seq pattern="seq (1):" -->
```shell
elm server activate <server-license-key> --product acme-cad --data server-data --trust root.pub.pem --service http://127.0.0.1:8080 --license server.elmlic
```

`elm` は、ライセンス ID、ライセンスのシーケンス番号、書いたパスを表示し、続けて、サーバーがまだ動いていないことを表示する。

<!-- steps:expect -->
```text
The server is not running; it reads the license when it starts.
```

出力の全体は次のようになる。

```text
license 01960000-0000-7000-8000-000000000302, seq 1: server.elmlic
The server is not running; it reads the license when it starts.
```

シーケンス番号は 1 から始まり、更新のたびに増える。これを使って、サーバーもサービスも、同じライセンスの新しいファイルと古いファイルを見分けられる。アクティベーションそのものは `server-data` に残り、サービスはそれを、ライセンスのアクティベーションの数に含める。

チュートリアルのフォルダーで 3 つ目のターミナルを開き、`tools` をその `PATH` に足して、そこでサーバーを起動する。

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

サーバーは動き続け、ログを表示する。最後の行は `server.started` で、アクティベーションのライセンス ID が入っている。

```text
info: elm-server[1]
      server.started: listening on port 7070 as https://127.0.0.1:7070/, key FAKE-SERVER-KEY-00000000000000000000000000K, license 01960000-0000-7000-8000-000000000302, in 68 ms; reclaim window until 2026-01-15T09:41:00.0000000+00:00
```

## オンラインで更新する

`service.url` があると、サーバーは自分でライセンスを更新する。ファイルの期限まで残り `refreshWindow` 日になると（ここでは期限の 20 日前）、サービスに問い合わせる。更新に失敗したときは、1 時間後か、サービスが指定した時間のあとにもう一度試す。待つのは最長 24 時間だ。サービスがそのアクティベーションをもう知らないときと、サービスが拒否したときは、試すのをやめる。サービスが拒否するのは、サーバーが別のサーバーのコピーに見えるとき（[シーケンス番号によるクローン検出](../explanation/activation.md#シーケンス番号によるクローン検出)）と、サーバーの[プルーフ](../explanation/devices-and-proofs.md#プルーフ)（所有証明）が検証に通らないときだ。新しいファイルはどれも `license` のパスに書かれ、サーバーは動いたままそれを読むので、アプリはリースを保持したままでいられる。

ベンダーがライセンスにシートを足したあとなど、すぐに更新したいときは、`elm server refresh` を実行する。このコマンドは、`refreshWindow` の前でも、実行するたびに更新する。これも `server.json` を読まないので、`--service` が要る。

<!-- steps:run capture=second-seq pattern="seq (2):" -->
```shell
elm server refresh --product acme-cad --data server-data --trust root.pub.pem --service http://127.0.0.1:8080 --license server.elmlic
```

`elm` は、次のシーケンス番号を持つ新しいファイルと、動いているサーバーがそれを読んだことを表示する。

<!-- steps:expect -->
```text
The server reloaded the license.
```

出力の全体は次のようになる。

```text
license 01960000-0000-7000-8000-000000000302, seq 2: server.elmlic
The server reloaded the license.
```

サーバーが新しいファイルを読まずに古いライセンスを使い続けたときは、`elm` は `error: the server kept its license:` に続けてコードと理由を表示し、終了コード 1 で終わる。それでも新しいファイルは書かれる。そのようなコードの 1 つが `ELM_CLOCK_TAMPERED` で、サーバーの時計が進められてから戻されたときに、次のように出る。

```text
error: the server kept its license: ELM_CLOCK_TAMPERED: The server license does not verify: The clock has been set back. This server's installation key is FAKE-SERVER-KEY-00000000000000000000000000K.
```

ライセンスが検証に通らないときのメッセージは、どれもサーバーのインストール鍵で終わる。ここでは、鍵は原因ではない。サーバー自身の更新ならサービスの時刻がサーバーに渡り、このエラーが解消されるが、`elm` は別のプログラムなので渡せない。時計を直す。そのあとサーバーは、次のオンラインの更新で新しいライセンスを受け取るか、次に起動するときにこのファイルを読む。

`elm server status --json` で、サーバーが自分で更新するかどうかがわかる。動いているサーバーに問い合わせるほかの `elm server` のコマンドと同じく、指定するのはデータフォルダーだけでよい。

<!-- steps:run -->
```shell
elm server status --data server-data --json
```

レスポンスの最後のメンバーが `autoRefresh` だ。

<!-- steps:expect -->
```text
  "autoRefresh": true
```

## オフラインで更新する

サービスに接続できないサーバーは、代わりにファイルで更新する。サーバーがリクエストを書き、ベンダーがサービスでそれに応え、サーバーがそのレスポンスを取り込む。`service.url` があってサーバーが動いているあいだは、`elm server refresh-request` は `ELM_INVALID_STATE` で拒否する。そのようなサーバーはオンラインで更新するからだ。そこで、`server.json` から `service` を外し、サーバーのターミナルで `ELM_SERVICE_URL` も設定されていないことを確かめる。

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  }
}
```

サーバーが `server.json` を読むのは、起動するときだけだ。3 つ目のターミナルで Ctrl+C を押し、そこでもう一度サーバーを起動する。

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

`server-data` のアクティベーションは変わらない。リクエストファイルを書く。

<!-- steps:run -->
```shell
elm server refresh-request --product acme-cad --data server-data --trust root.pub.pem --out refresh.elmreq
```

<!-- steps:expect -->
```text
request: refresh.elmreq
```

管理者は `refresh.elmreq` をベンダーに送り、ベンダーは `elm offline process` でそれに応える。[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md)での、アプリのオフラインのリクエストと同じだ。`ELM_API_KEY` が設定されていれば、`elm` はリクエストを管理 API に送り、管理 API はそれを監査ログに書く。設定されていなければ、サービスのデバイス用の公開エンドポイントに送る。

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in refresh.elmreq --out refresh.elmresp
```

`elm` は、アクティベーション ID、ライセンス ID、新しいシーケンス番号、レスポンスファイルを表示する。アクティベーション ID は、サーバーをアクティベーションしたときに、サービスが割り当てたものだ。

```text
activation 01960000-0000-7000-8000-000000000402, license 01960000-0000-7000-8000-000000000302, seq 3: refresh.elmresp
```

ベンダーが `refresh.elmresp` を送り返し、管理者がそれを取り込む。

<!-- steps:run capture=third-seq pattern="seq (3):" -->
```shell
elm server import refresh.elmresp --product acme-cad --data server-data --trust root.pub.pem --license server.elmlic
```

<!-- steps:expect -->
```text
The server reloaded the license.
```

オンラインのときと同じく、`elm` は最初に `license 01960000-0000-7000-8000-000000000302, seq 3: server.elmlic` を表示する。ファイルの期限が切れる前に、この方法で更新する。`service.url` がなければ、サーバーは自分では更新しない。

## オフラインでアクティベーションし、サーバーを移す

サービスに一度も接続しないサーバーも、ファイルでアクティベーションする。このとき `server.json` には `service` を書かない。上と同じように、サーバーを一度起動して鍵を作らせる。そのあと、`elm server activate-request --key <server-license-key>` がリクエストを書き、ベンダーが `elm offline process` でそれに応え、`elm server import` がレスポンスを読んでライセンスファイルを書く。サーバーは、そのあとで起動する。以降は、上と同じようにオフラインで更新する。

ライセンスを別のサーバーに移すには、まずサーバーを止め、そのアクティベーションの枠を空ける。オンラインでは `elm server deactivate` で空ける。オフラインでは、`elm server deactivate-proof` がアクティベーションを削除してアクティベーション解除のプルーフを書き、ベンダーがそれを `elm offline process` で処理する。サービスがそれを受け取ったら、`elm server forget-proof` で、サーバーはもう一度アクティベーションできるようになる。`server-data` を削除するだけで済ませてはならない。サービスはそのアクティベーションを数え続け、`maxActivations` が 1 では、そのライセンスでほかのサーバーをアクティベーションできなくなる。これらのコマンドのすべてのオプションは、[サーバーライセンスのコマンド](../reference/elm-cli.md#サーバーライセンスのコマンド)に挙げている。

試し終えたら、3 つ目のターミナルで Ctrl+C を押してサーバーを止め、2 つ目のターミナルで Ctrl+C を押してサービスを止める。

## 関連するページ

- [サーバーライセンスを更新する](renew-a-server-license.md): ベンダーが手作業で発行するサーバーライセンス。
- [`elm-server` の運用](../explanation/floating-licenses.md#elm-server-の運用): サーバーがベンダーから受け取るものと、更新の方法。
- [サーバーライセンスのコマンド](../reference/elm-cli.md#サーバーライセンスのコマンド)と [`service`](../reference/server-configuration.md#service): すべてのオプションと設定。
