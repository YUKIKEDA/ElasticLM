---
sidebar_position: 16
---

# リクエストのレートを制限する

このガイドは、`elm-service` を動かすベンダーの運用担当者に向けている。サービスは、レート制限が許すより速く来るリクエストを拒否する。1 つの呼び出し元がサービスをふさぎ、ほかの呼び出し元を待たせることがないようにするためだ。このガイドでは、設定ファイルで 4 つの上限を設定し、サービスが受け付けない値をどう扱うかと、拒否された呼び出し元に何が返るかを示す。このガイドは、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)の設定で、`elm-service` が `http://127.0.0.1:8080` で動いている状態から始める。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。サービスは、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに 2 つ目のターミナルで動かす。止まっていたら、そこでもう一度起動する。

## それぞれの上限が数えるもの

サービスには 4 つの上限があり、どれも 1 秒あたりのリクエスト数だ。一度に送れる数（バースト）も同じで、リクエストのなかった 1 秒のあとなら、呼び出し元はその数のリクエストを一度に送れる。送信元とは、リクエストが来るアドレスのことで、1 つの IPv4 アドレスか、1 つの IPv6 の /64 のネットワークだ。

| 設定 | 既定 | 数えるもの |
| --- | --- | --- |
| `rateLimit.perIp` | `20` | 1 つの送信元から、デバイス用のエンドポイント以外のすべてのパスへのリクエスト。管理 API、管理画面、`/health` がこれに当たる。呼び出し元を認証する前に数える。 |
| `rateLimit.perKey` | `10` | 管理 API のリクエストのうち、1 つの API キーによるもの、または OIDC か管理画面でサインインした 1 人の管理者によるもの。 |
| `rateLimit.clientPerIp` | `20` | 1 つの送信元から、アプリが呼び出すデバイス用のエンドポイントへのリクエスト。アクティベーション、トライアル、ノンス、オフラインのファイル、リースがこれに当たる。リクエストのプルーフを確かめる前に数える。 |
| `rateLimit.clientPerKey` | `2` | 1 つのインストール鍵、つまりアプリの 1 つのインストールによるデバイスのリクエスト。 |

アプリの 1 回のアクティベーションやトークンの更新では、デバイスのリクエストが 2 つになる。ノンスのリクエストと、リクエストそのものだ。そのため、既定の `clientPerIp` では、1 つの送信元が 1 秒に 10 台のデバイスをアクティベーションできる。ただし、ノンスは 300 秒のあいだ使えるので、1 つのノンスを使い続けるスクリプトは、アクティベーションやトライアルの 1 件ごとにリクエストを 1 つ送り、1 秒に 20 件になる。トライアルは、製品ごとにも、どの 24 時間でも、製品のトライアルのポリシーの `trialDailyLimit` で制限される（[トライアルを提供する](offer-a-trial.md#製品が-1-日に出すトライアルの数を決める)）。それぞれの値は 1 から 10000 の整数だ。`perKey` は `perIp` 以下にしておく。1 つの送信元からの API キーのリクエストは両方の上限で数えられるので、そうしたキーでは、`perKey` を送信元ごとの上限より大きくしても効かない。

## 上限を設定する

`service.json` に `rateLimit` を足す。書かなかった上限は既定の値のままになる。次の例では、管理者が 1 つのアドレスを共有するサービスのように、送信元ごとのリクエストを多く許し、キーごとの上限は既定の値のままにしている。

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
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
  },
  "rateLimit": {
    "perIp": 100,
    "perKey": 10,
    "clientPerIp": 100,
    "clientPerKey": 2
  }
}
```

サービスは設定を起動時にしか読まない。2 つ目のターミナルで Ctrl+C を押し、そこでサービスをもう一度起動する。そのターミナルには `ELM_SIGNING_PASSPHRASE` がまだ設定されている。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

ログは、チュートリアルと同じく `service.started` の行で終わる。ログに上限は出ない。上限は、`ELM_RATELIMIT_PERIP=100` のように環境変数でも渡せる。環境変数は、ファイルにある値の代わりに使われる。渡し方は [elm-service の設定](../reference/service-configuration.md#設定の渡し方)で説明している。

## 値が範囲外のとき

1 から 10000 の範囲外の値は設定のエラーで、サービスは起動しない。動いているサービスを止めずにこれを確かめるため、`perIp` を `0` にした設定を 2 つ目のファイルに書く。

<!-- steps:file path=rate-check.json -->
```json
{
  "database": {
    "provider": "sqlite",
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
  },
  "rateLimit": {
    "perIp": 0
  }
}
```

1 つ目のターミナルで、この設定でサービスを起動する。

<!-- steps:run exit=2 capture=bad-setting stream=stderr pattern="(rateLimit\.perIp) must be between 1 and 10000\." -->
```shell
elm-service --config rate-check.json
```

サービスは署名鍵を読む前に設定を確かめるので、ここではパスフレーズは要らない。すぐに終了コード 2 で終わり、標準エラー出力に次のように表示する。

```text
rateLimit.perIp must be between 1 and 10000.
```

`2.5` や `"20"` のような整数でない値でも、サービスは終了コード 2 で止まる。そのときの行は `The settings file 'rate-check.json' is not valid:` で始まり、設定を `$.rateLimit.perIp` として示す。確かめ終わったら `rate-check.json` を削除する。

## 拒否された呼び出し元に返るもの

サービスは、上限を超えたリクエストに、ステータス 429、エラーコード `ELM_RATE_LIMITED`、ヘッダー `Retry-After: 1` で応える。呼び出し元は 1 秒後に再試行してよい。レスポンスは problem 形式で、その `detail` がどの上限かを示す。

| `detail` | 上限 |
| --- | --- |
| `Too many requests from this address.` | `perIp` か `clientPerIp` |
| `Too many requests with this API key.` | `perKey`（API キーの場合） |
| `Too many requests from this administrator.` | `perKey`（OIDC のアクセストークンか管理画面のセッションの場合） |
| `Too many requests with this installation key.` | `clientPerKey` |

`elm admin request` は、このようなレスポンスを標準エラー出力に表示し、終了コード 1 で終わる。

```text
error: the service answered 429.
{"type":"about:blank","title":"ELM_RATE_LIMITED","status":429,"detail":"Too many requests with this API key.","code":"ELM_RATE_LIMITED"}
```

管理 API を直接呼び出すスクリプトは、`Retry-After` の時間だけ待ってから、リクエストをもう一度送る。`elm admin request` を使うスクリプトには、ヘッダーは見えない。`elm` がヘッダーを表示しないからだ。そのスクリプトに見えるのは、終了コード 1 と標準エラー出力の `ELM_RATE_LIMITED` だけなので、少なくとも 1 秒待つ。アプリでは、`ActivationClient` が、エラー `ELM_RATE_LIMITED` とその `RetryAfter` を持つ `ActivationResult.Failed` を返し、自分ではリクエストを送り直さない。[エラーを処理する](handle-errors.md)では、あとで再試行するようユーザーに伝えるアプリを示している。そのアプリは次のように表示する。

```text
retry later: ELM_RATE_LIMITED
retry after: 1 s
reason: Too many requests with this installation key.
```

バックグラウンドでのトークンの更新では、クライアントは 1 時間後に再試行する。

## プロキシの後ろに置くとき

サービスは、リクエストの送信元を接続から取り、`X-Forwarded-For` や `Forwarded` は読まない。これらのヘッダーは、どの呼び出し元でも書けるからだ。そのため、リバースプロキシやロードバランサーの後ろでは、すべてのリクエストがプロキシのアドレスから来ることになり、`perIp` と `clientPerIp` はすべての呼び出し元をまとめて数える。両方を、すべての呼び出し元が合わせて送る数まで引き上げ、プロキシでできるなら、そこで呼び出し元ごとに制限する。ロードバランサーが `/health` に送るヘルスチェックも、そのアドレスの `perIp` で数えられる。監査ログにも、プロキシのアドレスが記録される。

## 複数のノードと再起動

`elm-service` の各プロセスは、それぞれ自分のメモリーで数える。ロードバランサーの後ろに複数のノードがあると、呼び出し元はノードごとに上限までリクエストを送れる。上限はノードごとに設定する。サービスを再起動すると、数は 0 に戻る。

## 関連するページ

- `elm-service` の設定の [`rateLimit`](../reference/service-configuration.md#ratelimit)。
- エラーコードの [`ELM_RATE_LIMITED`](../reference/error-codes.md)。
- [エラーを処理する](handle-errors.md): あとで再試行するよう求めるエラーを、アプリがどう扱うか。
