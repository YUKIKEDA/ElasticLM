---
sidebar_position: 1
---

# ポリシーを設定する

このガイドは、`elm-service` を運用するベンダーが、販売するライセンスの規則を決めるためのものだ。決めるのは、1 つのライセンスを何台のデバイスで使えるか、アプリがライセンスファイルを更新できないときにどれだけ動き続けるか、ライセンスをデバイスに紐付けるか、それともフローティングのシートを貸し出すか、だ。これらの規則はポリシーが持ち、その下で作るライセンスはすべてその規則に従う。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。製品、ポリシー、ライセンスについては[アクティベーション](../explanation/activation.md)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。

例では、チュートリアルの値を使う。製品 `acme-cad`、その機能 `pro`、`http://127.0.0.1:8080` のサービスだ。自分の値に置き換える。下のファイルの `<product-id>` は製品の `id` で、次のコマンドで製品の `code` と一緒に一覧できる。

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

下の出力にある ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。

## 設定を選ぶ

どの設定にも既定の値があるので、ポリシーに要るのは製品と名前だけだ。販売するものに合わせて、次の設定を決める。すべての設定は[ポリシーの設定](../explanation/activation.md#ポリシーの設定)に挙げていて、それぞれの範囲は [`POST /v1/admin/policies`](../reference/administration-api.md#post-v1adminpolicies) にある。

| 決めること | 設定 | 既定 |
| --- | --- | --- |
| 1 つのライセンスのアクティベーションを、同時に何台のデバイスが保持できるか | `maxActivations` | 1 |
| ライセンスの有効期間と、その期間をライセンスの作成時から数えるか、最初のアクティベーションから数えるか | `duration`、`expirationBasis` | 期限なし、`fromIssue` |
| ネットワークにつながらないときなど、ライセンスファイルの期限が切れたあと、アプリが動き続けられる期間 | `gracePeriod` | 7 日 |
| 1 つのライセンスファイルの有効期間と、アプリがそれを更新する時期 | `tokenLifetime`、`refreshWindow` | 30 日、期限の 20 日前 |
| デバイスがライセンスのアクティベーションを解除できる回数の合計 | `deactivationLimit` | 無制限 |
| デバイスごとにアクティベーションする（ノードロック）か、その時点でアプリを動かしているデバイスにサービスがシートを貸し出す（クラウドのフローティング）か | `floating` | `none`: ノードロック |

時間は秒で指定する。どの設定も、[ポリシーを変える](#ポリシーを変える)のとおり、あとで変えられる。

## ノードロックのポリシーを作る

ノードロックのポリシーは、各ライセンスを、それをアクティベーションしたデバイスに紐付ける。次のポリシーでは、1 つのライセンスを 3 台のデバイスで使え、ライセンスは作成から 1 年（31,622,400 秒、366 日）有効で、ライセンスファイルの期限が切れたあともアプリが 3 日（259,200 秒）動き続けられる。

<!-- steps:file path=three-devices-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, 3 devices","maxActivations":3,"duration":31622400,"gracePeriod":259200}
```

<!-- steps:run capture=three-devices-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body three-devices-policy.json --url http://127.0.0.1:8080
```

`elm` は、既定の値を含むすべての設定とともにポリシーを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Standard, 3 devices","licenseKind":"standard","binding":"nodeLocked","maxActivations":3,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":31622400,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":259200,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"serverBorrowReturnLimit":10000,"clientInfoStrategy":"none","trialDuration":1209600,"trialDailyLimit":1000,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

`id` が、下の `<three-devices-policy-id>` だ。このポリシーのライセンスは、チュートリアルのライセンスと同じようにアクティベーションする。ライセンスを作る。

<!-- steps:file path=three-devices-license.json -->
```json
{"policyId":"<three-devices-policy-id>","name":"Second customer","features":[{"code":"pro"}]}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses --body three-devices-license.json --url http://127.0.0.1:8080
```

`elm` はライセンスを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Second customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":"2027-01-16T09:30:00Z","suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00001"}
```

`expiresAt` は、ポリシーの `duration` により、作成の 366 日後になる。`maxActivations` は `null` で、ポリシーの値が使われる。より多くのデバイスを購入した顧客には、ファイルに `"maxActivations":5` と書くなどして、ライセンスに独自の値を設定することもできる。

## クラウドのフローティングのポリシーを作る

`floating` を `cloud` にすると、ライセンスをアクティベーションしない。代わりにサービスは、アプリを動かす各デバイスに、シートのリースを短い期間だけ貸し出す。アプリは、動いているあいだリースを更新する。ライセンスは、機能ごとにシートの数を持つ。リースについては[フローティングライセンス](../explanation/floating-licenses.md)で説明している。次のポリシーは、10 分（600 秒、既定の値）のリースを作る。

<!-- steps:file path=floating-policy.json -->
```json
{"productId":"<product-id>","name":"Floating","floating":"cloud","leaseDuration":600}
```

<!-- steps:run capture=floating-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body floating-policy.json --url http://127.0.0.1:8080
```

レスポンスでは、`floating` が `cloud` になり、`binding` は既定の `nodeLocked` のままだ。クラウドのフローティングのポリシーでは、デバイスがライセンスをどう保持するか（リースで保持する）を `floating` が決め、`binding` は使われない。`id` が、下の `<floating-policy-id>` だ。このポリシーのライセンスの各機能には、同時にリースを保持できるデバイスの台数である `seats` が要る。`pro` のシートを 5 つ持つライセンスを作る。

<!-- steps:file path=floating-license.json -->
```json
{"policyId":"<floating-policy-id>","name":"Team","features":[{"code":"pro","seats":5}]}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses --body floating-license.json --url http://127.0.0.1:8080
```

`elm` は、機能に `"seats":5` を持つライセンスを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000303","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000204","name":"Team","features":[{"code":"pro","ver":null,"seats":5,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00002"}
```

アプリは、`ActivationClient` ではなく `LicenseClient` でリースを受ける。どちらも [.NET API](../reference/dotnet-api.md) で説明している。

## ポリシーを変える

変える設定だけを送る。次のファイルで、ノードロックのポリシーのデバイスを 5 台にし、猶予期間を 7 日（604,800 秒）にする。

<!-- steps:file path=policy-change.json -->
```json
{"maxActivations":5,"gracePeriod":604800}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/policies/<three-devices-policy-id> --body policy-change.json --url http://127.0.0.1:8080
```

`elm` は、変わったポリシーを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000203","productId":"01960000-0000-7000-8000-000000000101","name":"Standard, 3 devices","licenseKind":"standard","binding":"nodeLocked","maxActivations":5,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":31622400,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":604800,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"serverBorrowReturnLimit":10000,"clientInfoStrategy":"none","trialDuration":1209600,"trialDailyLimit":1000,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:31:00Z"}
```

変更は、サービスが次に発行するライセンスファイルかリースから、そのポリシーのすべてのライセンスに反映される。発行済みのファイルとリースは、期限が切れるまで元の値のままだ。新しい `duration` は、変更のあとに作ったライセンスに反映される。`fromFirstActivation` のときは、期限のないライセンスにも、次のアクティベーションかクラウドのリースのときに反映される。すでに期限のあるライセンスは、その期限のままだ。`productId`、`licenseKind`、`binding` は変えられず、変えようとするとサービスは `ELM_INVALID_ARGUMENT` を返す。これらを変えるには、別のポリシーを作り、その下で新しいライセンスを作る。

製品のポリシーを、その設定とともに見るには、次のコマンドを打つ。

<!-- steps:run -->
```shell
elm admin request GET "/v1/admin/policies?productId=<product-id>" --url http://127.0.0.1:8080
```

レスポンスは 1 行の JSON で、ポリシーは `items` に入っている。各リクエストは、[監査ログ](../reference/administration-api.md#監査ログ)のとおり、サービスの監査ログに残る。

## 関連するページ

- [ポリシーの設定](../explanation/activation.md#ポリシーの設定): 各設定が決めること。
- 管理 API の[ポリシーのルート](../reference/administration-api.md#ポリシーのルート): すべてのメンバー、その範囲、互いに整合していなければならない設定。
- [トライアルを提供する](offer-a-trial.md): トライアル用のポリシー。
