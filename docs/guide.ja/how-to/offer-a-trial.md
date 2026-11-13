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
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Trial, 30 days","licenseKind":"trial","binding":"nodeLocked","maxActivations":1,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":null,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":0,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":2592000,"trialDailyLimit":1000,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
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

## 製品が 24 時間に出すトライアルの数を決める

サービスに接続できる人なら誰でも、トライアルを求められる。トライアルは 1 件ごとに、サービスのデータベースに行を残す。マシンのフィンガープリントを持ち、そのマシンに 2 回目のトライアルを出さないためのトライアルの記録と、トライアルのライセンスとアクティベーションだ。どれも、トライアルが終わっても削除されない。新しいインストール鍵とフィンガープリントで何度も求めるスクリプトにデータベースを埋められないよう、製品が出すトライアルは、どの 24 時間をとっても `trialDailyLimit` 件までだ。これは、製品が `trialPolicyId` で指定したトライアルのポリシーの項目で、1 から 100,000、既定は 1,000 だ。上限をなくす値はない。サービスは、製品の直近 24 時間のトライアルを、製品のどのポリシーで作ったかを問わず、データベースで数える。そのため、サービスのどのノード（データベースを共有して動く `elm-service` のそれぞれ）も同じトライアルを数え、再起動しても数は戻らない。記録を削除したトライアル（[マシンにもう一度トライアルを始めさせる](#マシンにもう一度トライアルを始めさせる)を参照）は数えなくなる。そのライセンスとアクティベーションは残る。

製品が出しているトライアルの数は、監査ログの `trial.create` の行を 24 時間ぶん数えれば分かる。1 行が 1 件のトライアルで、`detail` に `productId` が入る（[ログ](../reference/logs.md#アクション)）。上限は、トライアルがいちばん多い 24 時間の数倍にする。大きくすると、止まるまでにスクリプトが足せる行が増える。小さくすると、本当の利用者で上限に達しやすくなる。次の例では 5,000 にする。

<!-- steps:file path=daily-trials.json -->
```json
{"trialDailyLimit":5000}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/policies/<month-trial-policy-id> --body daily-trials.json --url http://127.0.0.1:8080
```

`elm` は、`"trialDailyLimit":5000` になったポリシーを表示する。新しい値は、次のトライアルのリクエストから効く。直近 24 時間のトライアルの数より小さい値にすると、次のトライアルはすぐに断られる。

直近 24 時間に製品がその数のトライアルを出していれば、サービスは次のトライアルをステータス 429 と `ELM_RATE_LIMITED` で断り、何も書き込まない。メッセージは "The product has reached its limit of 5000 trials in 24 hours." のようになる。レスポンスの `Retry-After` ヘッダーは、枠が 1 つ空くまでの秒数で、長くても 24 時間だ。枠が空くのは、数えたトライアルのうち最も古いものを出してから 24 時間たったときだ。上限を数より小さく下げたあとなら、その差の分のトライアルも 24 時間たったときだ。空く枠は 1 つだけで、そのあいだに断られたアプリはどれもほぼ同じ時間を受け取るので、最初に求め直したアプリがその枠を使う。誰かが求め続けていれば、枠はすぐに埋まる。`StartTrialAsync` は、エラーと待つ時間（`RetryAfter`）を持つ `ActivationResult.Failed` を返す。この製品のトライアルを使ったマシンには、上限に達していても `ELM_TRIAL_USED` が返る。レスポンスを受け取れなかったトライアルを求め直すアプリは、上限に達していてもそのトライアルを受け取る。

各ノードは、製品が上限に達したとき、運用ログにも警告を書く。書くのは最初の 1 回と、そのノードがその製品について前に警告してから 24 時間たったときだけだ。ノードはこの時刻をメモリーに持つので、再起動のあとは、次に断ったときに警告を書く（[ログ](../reference/logs.md#運用ログ)）。

警告が本当の利用者ではなくスクリプトによるものなら、監査ログの `trial.create` の行の `ip` で、その送信元のアドレスを探す。プロキシの後ろでは、サービスにはプロキシのアドレスが見えるので、プロキシのログで探す。見つけたアドレスは、ファイアウォールかプロキシで断る。アドレスを変える送信元（IPv6 の /64 の多くのアドレスを使うものなど）は、この方法では止められない。上限を上げても、上限に達するまでの時間が延びるだけだ。そうした送信元に何ができるかは、[セキュリティモデル](../explanation/security-model.md#saas-型のサービスとその管理者)で説明している。

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

サービスは、各トライアルの記録を、そのトライアルを使ったマシンのフィンガープリントとともに残す。そして、どのデータフォルダーやインストール鍵から求められても、そのフィンガープリントのどれかを持つマシンには、その製品の 2 回目のトライアルを拒否する。このマシンは、チュートリアルでトライアルを使った。この製品のトライアルを一度も使っていないマシンでは、このコマンドはすぐに成功して `trial: pro` を表示する。その場合も、下の手順でそのトライアルの記録を削除し、2 つ目のデータフォルダーで別のトライアルを始める。

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
- [エラーコード](../reference/error-codes.md): `ELM_TRIAL_USED`、`ELM_TRIAL_NOT_OFFERED`、`ELM_FINGERPRINT_UNAVAILABLE`、`ELM_RATE_LIMITED`。
