---
sidebar_position: 2
---

# トライアルを提供する

このガイドは、`elm-service` を運用するベンダーが、製品でトライアルを提供し、すでにトライアルを使ったマシンにもう一度トライアルを始めさせるためのものだ。たとえば、新しいバージョンを試したいと顧客に頼まれたときだ。トライアルは、デバイスがライセンスキーなしで受け取る、期間を限ったライセンスで、マシンごとに 1 回だけ受け取れる。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品があり、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)のアプリがある状態から始める。このチュートリアルは、これをすべて準備し、自分のマシンでトライアルも始める。サービスがトライアルをどう与えるかは[トライアル](../explanation/activation.md#トライアル)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。手順では、フォルダー `app` にあるチュートリアルのアプリを、ルート公開鍵 `root.pub.pem` を使って動かす。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。

例では、チュートリアルの値を使う。製品 `acme-cad`、その機能 `pro`、`http://127.0.0.1:8080` のサービスだ。自分の値に置き換える。下のファイルの `<product-id>` は製品の `id` で、次のコマンドで製品の `code` と一緒に一覧できる。

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

下の出力にある ID、フィンガープリント、日時は架空の値だ。手元のマシンでは毎回違う値になる。

## トライアルのポリシーを作る

トライアルは、製品のトライアルのポリシーの下にあるライセンスだ。トライアルのポリシーとは、`licenseKind` が `trial` のポリシーをいう。トライアルの期間は `trialDuration` に秒で指定する。1 日（86,400）から 365 日（31,536,000）までで、既定は 14 日だ。次のポリシーでは 30 日（2,592,000 秒）にする。トライアルのポリシーには猶予期間がないので、`gracePeriod` は 0 にする。

<!-- steps:file path=month-trial-policy.json -->
```json
{"productId":"<product-id>","name":"Trial, 30 days","licenseKind":"trial","gracePeriod":0,"trialDuration":2592000}
```

<!-- steps:run capture=month-trial-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body month-trial-policy.json --url http://127.0.0.1:8080
```

`elm` はポリシーを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Trial, 30 days","licenseKind":"trial","binding":"nodeLocked","maxActivations":1,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":null,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":0,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":2592000,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

トライアルのポリシーはノードロックで、マシンのフィンガープリントを照合する（`fingerprintMatching` が既定の `any` か、`all`）。サービスは、ほかの値を `ELM_INVALID_ARGUMENT` で拒否する。機能の一覧は要らない。トライアルは、始まった時点で製品が持つ機能をすべて受け取るからだ。

## 製品でトライアルを提供する

製品がトライアルを提供するのは、`trialPolicyId` にトライアルのポリシーを指定しているあいだだけだ。ポリシーの `id`、ここでは `<month-trial-policy-id>` を設定する。

<!-- steps:file path=offer-trial.json -->
```json
{"trialPolicyId":"<month-trial-policy-id>"}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body offer-trial.json --url http://127.0.0.1:8080
```

`elm` は、新しい `trialPolicyId` とともに製品を表示する。

```text
{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":"01960000-0000-7000-8000-000000000203","createdAt":"2026-01-15T09:30:00Z"}
```

これ以降、`StartTrialAsync` を呼ぶアプリは 30 日のトライアルを受け取る。製品が指定できるトライアルのポリシーは、一度に 1 つだ。すでに動いているトライアルは、始まったときに決まった期限のままだ。

## トライアルを使ったマシンでトライアルを始める

チュートリアルのアプリで、新しいデータフォルダー `trial-data` を使ってトライアルを始める。サービスから見ると、これはアプリの新しいインストールだが、マシンは同じだ。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem trial-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_USED: A fingerprint of this installation already had a trial of the product.
```

サービスは、各トライアルの記録を、そのトライアルを使ったマシンのフィンガープリントとともに残す。そして、どのデータフォルダーやインストール鍵から求められても、そのフィンガープリントのどれかを持つマシンには、その製品の 2 回目のトライアルを拒否する。フィンガープリントの元になるマシン ID をすべて変えた（Windows では 2 つとも）管理者は、使えるトライアルをもう一度始められる。新しい鍵と作ったフィンガープリントを送ってもトライアルは得られるが、そのライセンスファイルにはそのフィンガープリントが書かれるので、そのマシンでは使えない（[セキュリティモデル](../explanation/security-model.md#ライセンシーのマシン上)）。このマシンは、チュートリアルでトライアルを使った。この製品のトライアルを一度も使っていないマシンでは、このコマンドはすぐに成功して `trial: pro` を表示する。その場合も、下の手順でそのトライアルの記録を削除し、2 つ目のデータフォルダーで別のトライアルを始める。

## トライアルの記録を見る

製品のトライアルの記録を一覧する。

<!-- steps:run capture=trial-id pattern="\x22id\x22:\x22([0-9a-f]{8}-[0-9a-f-]{27})\x22" -->
```shell
elm admin request GET "/v1/admin/trials?productId=<product-id>" --url http://127.0.0.1:8080
```

`elm` は、記録の 1 ページを表示する。

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","fingerprints":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4","9b1e7c3a5d2f4e6081a7c9b3d5e7f901"],"createdAt":"2026-01-15T09:30:00Z"}]}
```

記録の `id` はトライアルのライセンスの ID で、下の `<trial-id>` だ。`createdAt` は、トライアルが始まった日時だ。1 ページに収まらないほど記録がある一覧には `next` がある。残りの取り方は[ページング](../reference/administration-api.md#ページング)で説明している。記録が多いときは、トライアルが始まった日時でマシンの記録を探す。アプリがマシンのフィンガープリントを表示するなら、そのフィンガープリントでも探せる（フィンガープリントを返す呼び出しは[オフラインのみ](../explanation/overview.md#オフラインのみ)に挙げている）。

## マシンにもう一度トライアルを始めさせる

マシンの記録を削除する。

<!-- steps:run -->
```shell
elm admin request DELETE /v1/admin/trials/<trial-id> --url http://127.0.0.1:8080
```

サービスは本文のないレスポンスを返すので、`elm` は何も表示しない。記録を削除しても、前のトライアルは終わらない。そのライセンスとアクティベーションは残る。そのアクティベーションを、トライアルの ID をライセンス ID として[管理者としてアクティベーションを解除する](deactivate-a-device.md#管理者としてアクティベーションを解除する)のとおりに解除すると、その枠が空き、次のトークンの更新が拒否される。ただし、デバイスにあるトライアルのライセンスファイルは、期限が切れるまで有効なままだ。

2 つ目の新しいデータフォルダー `second-trial-data` で、もう一度トライアルを始める。アクティベーションを保存しているデータフォルダーでは、トライアルを始められない（`ELM_INVALID_STATE`）。上の 1 回目の実行が成功したマシンでは、`trial-data` にそのトライアルが保存されている。

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem second-trial-data
```

アプリは 2 行を表示する。1 行目は次のとおりだ。

<!-- steps:expect -->
```text
trial: pro
```

2 行目は新しいトライアルの終わりで、新しいトライアルのポリシーにより、今から 30 日後の UTC の日時になる。たとえば `expires: 2026-02-14 09:30:00Z` のようになる。サービスはこのトライアルも記録するので、新しい記録を削除しない限り、このマシンは 3 回目のトライアルを受け取れない。

製品はトライアルを提供し続ける。やめるには、同じ `PATCH` で `trialPolicyId` を `null` にする。そのあとトライアルを求めるアプリは `ELM_TRIAL_NOT_OFFERED` を受け取る。製品 ID がどの製品も指していないアプリも同じだ。すでに動いているトライアルは終わるまで続き、記録は残る。

## 関連するページ

- [トライアル](../explanation/activation.md#トライアル): サービスがトライアルを与える仕組みと、新しいマシン ID で新しいトライアルを受け取れる理由。
- 管理 API の [`GET /v1/admin/trials`](../reference/administration-api.md#get-v1admintrials) と [`DELETE /v1/admin/trials/{id}`](../reference/administration-api.md#delete-v1admintrialsid)。
- [エラーコード](../reference/error-codes.md): `ELM_TRIAL_USED`、`ELM_TRIAL_NOT_OFFERED`、`ELM_FINGERPRINT_UNAVAILABLE`。
