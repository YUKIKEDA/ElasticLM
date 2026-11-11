---
sidebar_position: 8
---

# 署名鍵を失効させる

このガイドは、署名鍵が漏れたか、漏れたおそれがあるベンダーと、ベンダーと一緒に対応しなければならない開発者と管理者に向けている。鍵を盗んだ人は、すべてのアプリが受け入れるライセンスに、過去の発行時刻でも署名できる。そのため、鍵の鍵証明書でも、ライセンスの失効でも止められない。止められるのは、鍵 ID の失効だけだ。鍵 ID を失効させると、失効した鍵 ID の一覧が届く先では、その鍵が署名したライセンスとクラウドのリースが、正規のものも含めてすべて `ELM_KEY_REVOKED` で失敗する。このガイドでは、`elm-service` の鍵を入れ替えると同時に、1 回の再起動で古い鍵をサービスで失効させ、失効がライセンスファイルに何をするかを確かめ、アプリ、ライセンスサーバー、顧客に何を届けなければならないかを説明する。このガイドは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)のルート鍵、署名鍵、設定を使って、`elm-service` が `http://127.0.0.1:8080` で動いている状態から始める。それぞれの手順が要る理由は[署名鍵が漏れたとき](../explanation/trust-chain.md#署名鍵が漏れたとき)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。

ここで失効させるのは、サービスが今署名に使っている鍵だ。`service.json` の `signing.key` が指すファイルで、その鍵 ID は [`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key) の `keyId` だ。下のコマンドは、チュートリアルのファイル名を使う。ルート鍵 `root.key.pem`、ルート公開鍵 `root.pub.pem`、サービスの署名鍵 `signing.key.pem` とその公開鍵 `signing.pub.pem` と鍵証明書 `signing.cert`、設定ファイル `service.json` だ。[署名鍵を入れ替える](rotate-the-signing-key.md)などで鍵をすでに入れ替えているなら、`signing.key.pem`、`signing.pub.pem`、`signing.cert` の代わりに、`signing-2.key.pem` のような、サービスが今使っている鍵のファイルを使う。新しい鍵には、`signing-3.key.pem`、`signing-3.pub.pem`、`signing-3.cert` のように、どの鍵もまだ使っていないファイル名を付ける。

下の出力にある鍵 ID、トークン ID、日時は架空の値だ。手元のマシンでは毎回違う値になる。

## 順番を決める

鍵 ID の失効が効くのは一覧が届く先だけで、その鍵の正規のライセンスも止まる。手順は次の順に進める。

1. 下のとおりに、`elm-service` の鍵を入れ替え、古い鍵をサービスで失効させる。`elm-service` は、自分の署名鍵が失効していると起動しないので、2 つの変更は同じ再起動で入れる。
2. すべての顧客に、新しい鍵が署名したライセンスを渡す。オンラインでトークンを更新するデバイスは次の更新で受け取り、ネットワークにつながらないデバイスは、[ライセンスファイルを更新する](activate-a-device-without-a-network.md#ライセンスファイルを更新する)のように更新のファイルが運ばれたときに受け取る。手作業で渡したライセンスファイルは、すべて発行し直す。`elm license issue` のファイル、マシンに紐付けないライセンスファイル、`elm` で作ったサーバーライセンスがこれに当たる。`elm-service` でアクティベーションしたライセンスサーバーは、次の更新（オンラインでも更新のファイルでもよい）で新しいサーバーライセンスを受け取る。
3. 自分が動かしているライセンスサーバーの `trust.revokedKeyIds` に古い鍵 ID を足し、それぞれを再起動する。`elm-server` はこの設定を起動時にしか読まないからだ。顧客にも、自分のサーバーで同じことをするよう伝える。これは、サーバーが新しい鍵の署名したサーバーライセンスを持ってからにする。失効した鍵が署名したサーバーライセンスでは、`elm-server` は起動しない。
4. トラストストアで古い鍵 ID を失効として載せたアプリのバージョンをリリースする。デバイスがそのバージョンを動かすまでは、デバイスは古い鍵のライセンスを、偽造されたものも含めてすべて受け入れる。そのバージョンを動かすと、デバイスは、まだ保存している古い鍵のライセンスファイルを、猶予期間中でも拒否する。デバイスがトークンを更新し終えてからリリースするか、下のとおり、`ELM_KEY_REVOKED` を受け取ったらアプリがトークンを更新するようにする。

## 鍵が署名したライセンスファイル

失効が何をするかを見るため、ベンダーが手作業でファイルを発行するように、今の鍵でライセンスファイルを発行しておく。このファイルには期限がない。こうしたファイルは、鍵を失効させないと止められない。

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id old-key-license --feature pro --expires never --trust root.pub.pem --out old-key.elmlic
```

`elm` は署名鍵のパスフレーズを尋ね、期限がないことを警告し、ライセンスファイルを表示する。

```text
Passphrase for 'signing.key.pem':
warning: The license has no expiry. It cannot be revoked once delivered.
license file: old-key.elmlic
license ID: old-key-license
token ID: FAKEjti000000000000002
```

警告は、このファイルだけを失効させることはできない、という意味だ。手作業で発行したファイルの記録はサービスにないので、サービスには失効させるものがない。また、期限がないので、ファイルが無効になることもない。下のとおり、署名した鍵を失効させれば、トラストストアにその鍵を失効として載せているすべてのアプリとサーバーで、このファイルを止められる。

## 鍵 ID を調べる

失効させる鍵の鍵 ID を、その公開鍵から表示する。

<!-- steps:run capture=old-key-id -->
```shell
elm key thumbprint signing.pub.pem
```

`elm` が表示する鍵 ID が、下の `<old-key-id>` だ。

```text
FAKE-SIGNING-KEY-ID-000000000000000000000S1
```

これは、サービスの `service.started` の行に出る `signing key` で、[`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key) の `keyId` でもある。

## サービスで鍵を入れ替えて失効させる

[新しい署名鍵を作る](rotate-the-signing-key.md#新しい署名鍵を作る)のとおりに、新しい署名鍵を作って証明する。要点は次のとおりだ。まずターミナルから `ELM_KEY_PASSPHRASE` を消し、`elm` がパスフレーズを尋ねるようにする。新しい鍵には、ルート鍵とも漏れた鍵とも違う新しいパスフレーズを付ける。鍵はサービスを動かすマシンで作り、`elm key certify` はルート鍵を置いたオフラインのマシンで実行する。新しい公開鍵をそこへ運び、持ち帰るのは鍵証明書だけにする。ルート鍵を、サービスを動かすマシンに持ち込んではならない。設定で `usageLog.directory` を指定しているときは、`--use` に `log` を足す。

<!-- steps:run -->
```shell
elm key signing create --out signing-2.key.pem --public-out signing-2.pub.pem
elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2.cert
```

<!-- steps:expect -->
```text
certificate: signing-2.cert
uses: license, lease
```

`service.json` で、`signing` を新しい鍵とその鍵証明書に変え、古い鍵 ID を `trust.revokedKeyIds` に載せる。`<old-key-id>` は、`elm key thumbprint` が表示した鍵 ID に置き換える。

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing-2.key.pem",
    "certificates": ["signing-2.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"],
    "revokedKeyIds": ["<old-key-id>"]
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

サービスを再起動する。2 つ目のターミナルで Ctrl+C を押し、そこで新しい鍵のパスフレーズを `ELM_SIGNING_PASSPHRASE` に読み込み、もう一度起動する。手順は[新しい鍵でサービスを再起動する](rotate-the-signing-key.md#新しい鍵でサービスを再起動する)と同じで、サービスが起動しないときに何を表示するかもそこに挙げている。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

サービスのログの `service.started` の行に、新しい鍵が出る。これでサービスは新しい鍵だけで署名し、古い鍵では起動しなくなる。`signing` を古い鍵に戻すと、終了コード 1 で終わり、`No certificate that the trust store vouches for lets the signing key FAKE-SIGNING-KEY-ID-000000000000000000000S1 sign for License, Lease at 2026-01-15T09:30:00Z.` の行が出る。

サービスが新しい鍵で動いたら、[古い鍵を使わないようにする](rotate-the-signing-key.md#古い鍵を使わないようにする)のとおりに、古い鍵のファイルを使わないようにする。何を失効させるかをほかの人に伝えるため、公開鍵 `signing.pub.pem` か、表示した鍵 ID を残しておく。鍵証明書は `signing.certificates` に入れないままにする。漏れた秘密鍵 `signing.key.pem` は、すべてのコピーとともに削除し、誰かがもう一度サービスに渡したり、それで署名したりしないようにする。

## 失効が何をするかを確かめる

サービスの一覧は、どのアプリにも届かない。トラストストアに失効した鍵を載せていないアプリは、古い鍵のファイルを受け入れ続ける。`elm license verify` は、アプリと同じ検証器でファイルを確かめる。`--revoked` を付けなければ、そうしたアプリから見た結果になる。

<!-- steps:run -->
```shell
elm license verify old-key.elmlic --trust root.pub.pem --product acme-cad --feature pro
```

<!-- steps:expect -->
```text
valid: license old-key-license, product acme-cad, kind standard
expires: never
features: pro
```

`--revoked` に古い鍵 ID を付けると、その鍵を載せたアプリのバージョンから見た結果になる。

<!-- steps:run exit=1 -->
```shell
elm license verify old-key.elmlic --trust root.pub.pem --revoked <old-key-id> --product acme-cad --feature pro
```

<!-- steps:expect -->
```text
invalid: ELM_KEY_REVOKED: The license was signed by a revoked key.
```

検証側は、鍵証明書、署名、ペイロードより先に、トークンの鍵 ID を失効した鍵 ID と照らし合わせる。そのため、期限がなく署名が正しいファイルでも失敗する。

## アプリで鍵を失効させる

アプリの開発者は、アプリのトラストストアの失効した鍵 ID に古い鍵 ID を足し、新しいバージョンをリリースする。

- .NET では、[`TrustStore.Create`](../reference/dotnet-api.md#truststore) の `revokedKeyIds`。
- C では、[`elm_verifier_options`](../reference/c-api.md#elm_verifier_options) 、`elm_activation_options`、`elm_client_options` の `revoked_key_ids`。

一覧がデバイスに届くのは、そのバージョンが届いたときだけだ。ネットワークから取得する失効リストはない。そのあと、古い鍵のライセンスファイルをまだ保存しているデバイスは、`Check` から `ELM_KEY_REVOKED` を受け取る。オンラインでアクティベーションするアプリは、そこでトークンを更新できる。.NET では `RefreshAsync`、C では `elm_activation_refresh` を使う。サービスは新しい鍵が署名したライセンスファイルを返し、アプリはもう一度確かめる。ネットワークにつながらないデバイスは更新のファイルを運んでもらう必要があるので、そうしたデバイスには、トークンを更新し終えてから新しいバージョンを届ける。

## ライセンスサーバーで鍵を失効させる

それぞれの `elm-server` で、`trust.revokedKeyIds` に古い鍵 ID を足し（[elm-server の設定](../reference/server-configuration.md#trust)を参照）、サーバーを再起動する。サーバーは、この設定を起動時にしか読まない。これは、サーバーが新しい鍵の署名したサーバーライセンスを持ってからにする。古い鍵が署名したサーバーライセンスのままだと、サーバーは起動しなくなる。これ以降、サーバーは古い鍵が署名したサーバーライセンスを拒否する。顧客にも鍵 ID を伝え、顧客のサーバーで同じことをしてもらう。

## 関連するページ

- [署名鍵が漏れたとき](../explanation/trust-chain.md#署名鍵が漏れたとき): それぞれの手順が要る理由と、失効が届かないもの。
- [署名鍵を入れ替える](rotate-the-signing-key.md): 鍵を入れ替える各手順と、デバイスが動き続けることの確かめ方。
- `elm-service` の設定の [`trust`](../reference/service-configuration.md#trust)。
- [`elm license verify`](../reference/elm-cli.md#elm-license-verify) と [`elm key thumbprint`](../reference/elm-cli.md#elm-key-thumbprint): すべてのオプション。
