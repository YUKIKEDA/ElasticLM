---
sidebar_position: 10
---

# サーバーライセンスを更新する

このガイドは、`elm` でサーバーライセンスを手作業で発行するベンダーと、新しいファイルを `elm-server` に入れる顧客の管理者に向けている。サーバーライセンスの期限が切れる前や、シートや機能が変わるときに使う。サーバーは動いたまま新しいファイルを読むので、アプリはリースを保持したままでいられる。このガイドは、[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)を終えた状態から始める。`elm license issue` で作ったサーバーライセンスを持つ `elm-server` がある状態だ。サーバーライセンスを `elm-service` から受け取ったサーバーは、更新の方法が違う。[elm-service でアクティベーションしたサーバー](#elm-service-でアクティベーションしたサーバー)を参照。

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、署名鍵 `signing.key.pem` とその鍵証明書 `signing.cert`、ルート公開鍵 `root.pub.pem`、サーバーのデータフォルダー `server-data` を使う。チュートリアルでは 1 台のマシンが両方の役を務めるが、実際の運用では、ベンダーが自分のマシンでファイルを発行し、管理者に送る。

サーバーが止まっていたら、[サーバーを起動する](../tutorials/use-a-floating-license.md#サーバーを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。

例では、チュートリアルの値を使う。製品 `acme-cad`、ライセンス ID `floating-server`、シート 2 つの機能 `pro` だ。自分の値に置き換える。

## 今のサーバーライセンスを見る

サーバーが何を保持しているかを尋ねる。

<!-- steps:run -->
```shell
elm server status --data server-data
```

`elm` は、サーバーライセンスとそのシートを表示する。

```text
product: acme-cad
license: floating-server (token FAKEjti000000000000002)
expires: 2099-12-31 00:00:00Z
key: FAKE-SERVER-KEY-00000000000000000000000000K
serving
leases: 1
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

<!-- steps:expect -->
```text
expires: 2099-12-31 00:00:00Z
```

`license` は使用中のファイルのライセンス ID とトークン ID で、`expires` はその期限だ。`key` はサーバーのインストール鍵のサムプリントで、新しいファイルはこの鍵に紐付けなければならない。

## 新しいファイルを発行する

ベンダーとして、`elm license issue` でサーバーライセンスをもう一度発行する。変えるのは更新の目的のもの（ここでは期限）だけにし、ほかのオプションはすべて前と同じに指定する。同じ `--license-id`、`--bind-key`、`--feature`、`--lease-minutes`、`--borrow-days` だ。省いたオプションは既定の値に戻る。たとえば、リースの期間は 10 分に、借用はなしに、猶予期間は 7 日になる。`--license-id` を新しくすると、アプリが知らないライセンスになる。アプリはそれぞれ、最初に確かめたサーバーライセンスの ID を保持しているので、そのサーバーを `ELM_SERVER_PIN_MISMATCH` で拒否する。すべてのオプションとその既定の値は [`elm license issue`](../reference/elm-cli.md#elm-license-issue) に挙げている。

`<server-key>` は、上の `key:` のあとのサムプリントに置き換える。43 文字すべてを正確に写す。ここではベンダーのマシンがサーバーのマシンでもあるので、コマンドはサーバーの `license` のファイルに直接書き込み、`--force` で古いファイルを上書きする。サーバーが新しいファイルを拒否したときに戻せるように、先に古いファイルのコピーを取っておく。PowerShell では `Copy-Item server.elmlic server.elmlic.old` と打ち、Bash か zsh では `cp server.elmlic server.elmlic.old` と打つ。そのあと発行する。

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id floating-server --kind server --feature "pro;seats=2" --expires 2100-12-31T00:00:00Z --bind-key <server-key> --lease-minutes 1 --borrow-days 1 --trust root.pub.pem --out server.elmlic --force
```

`elm` は署名鍵のパスフレーズを尋ね、次のように表示する。

```text
Passphrase for 'signing.key.pem':
license file: server.elmlic
license ID: floating-server
token ID: FAKEjti000000000000003
```

発行するときは、署名鍵の鍵証明書が有効でなければならない。新しいファイルにはその鍵証明書が入るからだ。実際の運用では、新しいファイルを顧客の管理者に送る。管理者は、古いファイルのコピーを取っておき、新しいファイルをサーバーの設定の `license` のパスに置く。

下でサーバーが新しいファイルを読んだら、記録として要るのでなければ `server.elmlic.old` を削除する。サーバーに古いファイルを二度と渡してはならない。

## サーバーに読み込ませる

動いているサーバーに、ライセンスファイルを読み直させる。

<!-- steps:run -->
```shell
elm server reload --data server-data
```

`elm` は、新しいファイルのライセンス ID とトークン ID を表示する。

```text
reloaded: license floating-server, token FAKEjti000000000000003
```

このトークン ID を、`elm license issue` が表示したものと比べる。両者は同じだ。サーバーは、起動するときと同じように新しいファイルを確かめる。製品、自分のインストール鍵、マシンフィンガープリント、トラストストアだ。期限を確かめる。

<!-- steps:run -->
```shell
elm server status --data server-data
```

<!-- steps:expect -->
```text
expires: 2100-12-31 00:00:00Z
```

サーバーが貸し出したリースと借用は、そのまま残る。

新しいファイルが確認に通らないときは、サーバーは古いライセンスを使い続ける。`elm` は `error:`、エラーコード、理由を表示し、終了コード 1 で終わる。たとえば、別のインストール鍵に紐付いたファイルでは次のようになる。

```text
error: ELM_WRONG_INSTALLATION: The server license does not verify (ELM_WRONG_INSTALLATION): The license is bound to another installation key. This server's installation key is FAKE-SERVER-KEY-00000000000000000000000000K.
```

確認に通るファイルが手に入るまで、`license` のパスに古いファイルを戻しておく。ここでは `server.elmlic.old` から戻す。サーバーは次に起動するときにファイルを読み直し、検証に通らないファイルでは起動しない。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

## シートや機能を変える

更新では、ライセンスが与えるものも変えられる。`--feature` に新しいシートや機能を指定する。新しいライセンスのシートが減ったり、機能がなくなったりしても、サーバーは貸し出したリースを取り上げない。ある機能で保持されているシートが新しい数より多いあいだは、十分な数のリースの期限が切れるまで、その機能のリースの更新をすべて `ELM_NO_SEATS` で拒否する。借用も保持されているものとして数えるので、これは借用の期限が切れるまで続くことがある。なくなった機能のリースの更新は `ELM_FEATURE_NOT_LICENSED` で拒否するので、そのリースはリースの期間 1 回分のうちに終わる。借用は期限が切れるまで有効なままで、こうした変更の前に貸し出した借用の早期返却は `ELM_RETURN_NOT_ALLOWED` で拒否する。どの変更がこれに当たるかは[早期返却](../explanation/floating-licenses.md#早期返却)で説明している。

## 新しいファイルでは取り上げられないもの

手作業で読み込ませたファイルを、サーバーは置き換える前のファイルと比べない。`elm license issue` で発行したファイルには、比べるためのシーケンス番号がないからだ。そのため、シートを減らす更新のあとでも、管理者は古いファイルをもう一度読み込ませることができ、サーバーはその期限が切れるまでそれを使う。手作業で発行したライセンスファイルは失効させられない。

ベンダーとして、サーバーが与えるものを減らせるようにしておく必要があるなら、期限を短くしてファイルを発行するか、サーバーを `elm-service` でアクティベーションしてオンラインで更新させる（`service.url`）。期限を短くすれば、更新のたびに内容を変える機会がある。オンラインで更新するこのサーバーは、シーケンス番号を使って更新する。同じライセンスの古いファイルを手作業で読み込ませても、1 時間以内に最新のライセンスに戻る。オフラインでアクティベーションしたサーバーは、管理者が次の更新を取り込むまで、手作業で読み込ませたファイルを使い続ける。このことは、顧客のサーバーでのほかの限界と一緒に、[セキュリティモデル](../explanation/security-model.md#顧客のライセンスサーバー上)に挙げている。

## elm-service でアクティベーションしたサーバー

`elm server activate` で `elm-service` からサーバーライセンスを受け取ったサーバーは、データフォルダーにアクティベーションを保存していて、それを使ってライセンスを更新する。

- オンライン。`service.url` にベンダーの `elm-service` を指定しているときは、ライセンスの期限が切れる前にサーバーが自分で更新する。`elm server refresh` を使えば、すぐに更新する。
- オフライン。`elm server refresh-request` でリクエストファイルを書き、ベンダーがそれを `elm-service` で処理し、`elm server import` でそのレスポンスを取り込む。

このようなサーバーに、手作業で発行したファイルを読み込ませない。アクティベーションを更新すると、読み込ませたファイルはそのアクティベーションのライセンスで上書きされる。`service.url` を指定しているときは、読み込ませたファイルが同じライセンスの古いものなら、サーバーは 1 時間以内にアクティベーションのライセンスに戻る。どちらの方法も、[elm-service でサーバーライセンスをアクティベーションする](activate-a-server-license-with-elm-service.md)で手順を追って示している。コマンドは[サーバーライセンスのコマンド](../reference/elm-cli.md#サーバーライセンスのコマンド)に、設定は [`service`](../reference/server-configuration.md#service) に挙げている。

## 関連するページ

- [`elm-server` の運用](../explanation/floating-licenses.md#elm-server-の運用): サーバーがベンダーから受け取るものと、更新の方法。
- [`elm license issue`](../reference/elm-cli.md#elm-license-issue)、[`elm server reload`](../reference/elm-cli.md#elm-server-reload)、[`elm server status`](../reference/elm-cli.md#elm-server-status): すべてのオプション。
- [借用を制限する](limit-borrowing.md): サーバーライセンスとサーバーでの借用の上限。
