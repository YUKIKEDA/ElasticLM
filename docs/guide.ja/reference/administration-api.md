---
sidebar_position: 6
---

# 管理 API

このページには、2 つの HTTP の管理 API のすべてのルートを載せる。SaaS 型のアクティベーションサービスである `elm-service` の管理 API と、オンプレミスのフローティングライセンスサーバーである `elm-server` のローカルの管理 API だ。`elm-service` のパスは `/v1/admin/` で始まり、`elm-server` のパスは `/admin/v1/` で始まる。ルートごとに、権限、クエリパラメーター、リクエストの本文、例を添えたレスポンス、エラーコードを示す。読者は、販売・課金システムを `elm-service` につなぐ開発者と、どちらかのプログラムをスクリプトで操作する運用担当者だ。[elm コマンドリファレンス](elm-cli.md)の `elm admin request` と `elm server` のコマンドは、これらのルートを代わりに呼び出す。アプリとデバイスが呼び出すルートは[デバイスのプロトコル](device-protocol.md)にある。製品、ポリシー、ライセンスとは何かは[アクティベーション](../explanation/activation.md)で、リースの仕組みは[フローティングライセンス](../explanation/floating-licenses.md)で説明している。各プログラムの節は、そのルートの一覧から始まる。[`elm-service`](#ルートの一覧) と [`elm-server`](#elm-server-のローカルの管理-api) だ。

例は、両方のプログラムを動かして得たものだ。ID、鍵、ハッシュ、トークン、[サムプリント](../explanation/devices-and-proofs.md#インストール鍵)は同じ形の架空の値に置き換え、JSON は読みやすいように字下げしている。プログラム自身は JSON を 1 行で送る。本文を受け取るルートでは、例で送った本文をレスポンスの前に示す。どちらのプログラムも、JSON の文字列の中の一部の文字を `\uXXXX` のエスケープで書く。たとえば `>` は `\u003E`、`<` は `\u003C`、`"` は `\u0022`、`'` は `\u0027` になる。JSON のパーサーは、これらを元の文字として読む。

## `elm-service` の管理 API

### ルートの一覧

| ルート | 権限 | 内容 |
| --- | --- | --- |
| [`GET /health`](#get-health) | なし | サービスとそのデータベースが答えるかを確かめる |
| [`POST /v1/admin/api-keys`](#post-v1adminapi-keys) | `apikey.create` | API キーを作る |
| [`GET /v1/admin/api-keys`](#get-v1adminapi-keys) | `apikey.read` | API キーを一覧する |
| [`DELETE /v1/admin/api-keys/{id}`](#delete-v1adminapi-keysid) | `apikey.revoke` | API キーを失効させる |
| [`GET /v1/admin/audit`](#get-v1adminaudit) | `audit.read` | 監査ログを一覧する |
| [`POST /v1/admin/audit/verify`](#post-v1adminauditverify) | `audit.read` | 監査ログのハッシュのチェーンを確かめる |
| [`POST /v1/admin/products`](#post-v1adminproducts) | `product.write` | 製品を作る |
| [`GET /v1/admin/products`](#get-v1adminproducts) | `product.read` | 製品を一覧する |
| [`GET /v1/admin/products/{id}`](#get-v1adminproductsid) | `product.read` | 製品を取得する |
| [`PATCH /v1/admin/products/{id}`](#patch-v1adminproductsid) | `product.write` | 製品を変更する |
| [`POST /v1/admin/products/{id}/features`](#post-v1adminproductsidfeatures) | `product.write` | 機能を作る |
| [`GET /v1/admin/products/{id}/features`](#get-v1adminproductsidfeatures) | `product.read` | 製品の機能を一覧する |
| [`PATCH /v1/admin/products/{id}/features/{featureId}`](#patch-v1adminproductsidfeaturesfeatureid) | `product.write` | 機能を変更する |
| [`POST /v1/admin/policies`](#post-v1adminpolicies) | `policy.write` | ポリシーを作る |
| [`GET /v1/admin/policies`](#get-v1adminpolicies) | `policy.read` | ポリシーを一覧する |
| [`GET /v1/admin/policies/{id}`](#get-v1adminpoliciesid) | `policy.read` | ポリシーを取得する |
| [`PATCH /v1/admin/policies/{id}`](#patch-v1adminpoliciesid) | `policy.write` | ポリシーを変更する |
| [`GET /v1/admin/signing-key`](#get-v1adminsigning-key) | `signingkey.read` | 署名鍵の状態を取得する |
| [`POST /v1/admin/licenses`](#post-v1adminlicenses) | `license.create` | ライセンスとそのキーを作る |
| [`GET /v1/admin/licenses`](#get-v1adminlicenses) | `license.read` | ライセンスを一覧し、検索する |
| [`GET /v1/admin/licenses/{id}`](#get-v1adminlicensesid) | `license.read` | ライセンスを取得する |
| [`PATCH /v1/admin/licenses/{id}`](#patch-v1adminlicensesid) | `license.update` | ライセンスを変更する |
| [`POST /v1/admin/licenses/{id}/suspend`](#post-v1adminlicensesidsuspend) | `license.update` | ライセンスを一時停止する |
| [`POST /v1/admin/licenses/{id}/resume`](#post-v1adminlicensesidresume) | `license.update` | ライセンスを再開する |
| [`POST /v1/admin/licenses/{id}/extend`](#post-v1adminlicensesidextend) | `license.update` | ライセンスの有効期限を延ばす |
| [`POST /v1/admin/licenses/{id}/revoke`](#post-v1adminlicensesidrevoke) | `license.revoke` | ライセンスを失効させる |
| [`POST /v1/admin/licenses/{id}/file`](#post-v1adminlicensesidfile) | `license.file` | マシンに紐付けないライセンスのライセンスファイルを得る |
| [`GET /v1/admin/licenses/{id}/activations`](#get-v1adminlicensesidactivations) | `activation.read` | ライセンスのアクティベーションを一覧する |
| [`GET /v1/admin/activations/{id}`](#get-v1adminactivationsid) | `activation.read` | アクティベーションを取得する |
| [`POST /v1/admin/activations/{id}/deactivate`](#post-v1adminactivationsiddeactivate) | `activation.deactivate` | デバイスのアクティベーションを解除する |
| [`GET /v1/admin/trials`](#get-v1admintrials) | `trial.read` | トライアルの記録を一覧する |
| [`DELETE /v1/admin/trials/{id}`](#delete-v1admintrialsid) | `trial.delete` | トライアルの記録を削除する |
| [`POST /v1/admin/offline`](#post-v1adminoffline) | `activation.offline` | オフラインのリクエストファイルを処理する |
| [`GET /v1/admin/licenses/{id}/leases`](#get-v1adminlicensesidleases) | `lease.read` | ライセンスのクラウドのリースを一覧する |
| [`DELETE /v1/admin/leases/{id}`](#delete-v1adminleasesid) | `lease.release` | クラウドのリースを解放する |
| [`GET /v1/admin/usage-log/files`](#get-v1adminusage-logfiles) | `usagelog.read` | 利用ログのファイルを一覧する |
| [`GET /v1/admin/usage-log/files/{name}`](#get-v1adminusage-logfilesname) | `usagelog.read` | 利用ログのファイルを取得する |
| [`POST /v1/admin/usage-log/seal`](#post-v1adminusage-logseal) | `usagelog.seal` | 利用ログにすぐ署名する |
| [`GET /v1/admin/oidc`](#get-v1adminoidc) | なし | `elm admin login` 用の OIDC の設定を取得する |
| [`GET /v1/admin/admins`](#get-v1adminadmins) | `admin.read` | 管理者を一覧する |
| [`POST /v1/admin/admins/{id}/disable`](#post-v1adminadminsiddisable) | `admin.update` | 管理者を無効にする |
| [`POST /v1/admin/admins/{id}/enable`](#post-v1adminadminsidenable) | `admin.update` | 管理者を有効にする |
| [`GET /v1/admin/me`](#get-v1adminme) | 任意の資格情報 | 呼び出し元とその権限を取得する |
| [`GET /v1/admin/sessions`](#get-v1adminsessions) | `session.read` | 管理画面のセッションを一覧する |
| [`DELETE /v1/admin/sessions/{id}`](#delete-v1adminsessionsid) | `session.revoke` | 管理画面のセッションを終わらせる |

### リクエストとレスポンス

ルートは、`listen` のアドレス（[elm-service の設定](service-configuration.md#最上位の設定)を参照）の `/v1/admin/` の下にある。サービスの前にリバースプロキシを置いているときは、そのプロキシの公開 URL の下にある。本文は UTF-8 の JSON で、名前はキャメルケースだ。

#### ID

| 対象 | ID |
| --- | --- |
| 製品、機能、ポリシー、ライセンス、アクティベーション、トライアル、API キー、管理者、セッション | バージョン 7 の UUID。`01960000-0000-7000-8000-000000000501` のように、小文字の 36 文字で書く。トライアルの ID は、そのトライアルのライセンスの ID だ。 |
| クラウドのリース | `Xk2bQ9rT4mVn8sLp0wZa1g` のような、22 文字の base64url。`elm-server` のリースの ID も同じ形だ。 |
| 監査ログの行 | 行の番号 `n`。 |
| 利用ログのファイル | ファイル名。 |

#### 認証

`GET /v1/admin/oidc` と `GET /health` を除くすべてのルートで、次のどれかの資格情報が要る。

| 資格情報 | 送り方 | 受け入れる条件 |
| --- | --- | --- |
| API キー | `Authorization: Bearer elm_…`。`elm_` と 43 文字の base64url からなる。キーは `elm-service setup` と `POST /v1/admin/api-keys` で作る。 | 常に受け入れる。キーは、期限が切れるか失効するまで使える。`oidc.issuer` を設定したときは、その管理者が `apiKeys.inactiveAdminDays` 日サインインしていなくても使えなくなる。日数は、最後のサインイン、管理者の作成、キーの作成のうち、いちばん遅いものから数える。 |
| OIDC のアクセストークン | `Authorization: Bearer <token>`。値は `elm_` で始まらない。`elm admin login` で得る。 | `oidc.issuer` を設定したときだけ受け入れる。トークンは、8,192 文字以下の署名付き JWT（RS256、PS256、ES256 のいずれか）で、`typ` が `at+jwt`、`application/at+jwt`、`JWT` のいずれか、`iss` が `oidc.issuer` と等しく、`aud` が `oidc.audience` を含み、`exp` が有効（時計のずれは 60 秒まで許す）でなければならない。`iss` と、`oidc.subjectClaim` が指すクレーム（既定は `sub`）は、それぞれ制御文字を含まない 1 から 255 文字で、管理者を表す。そのクレームは 1 回だけ、文字列で入っていなければならない。`oidc.roleClaim`（値は 100 個まで）が、`oidc.roleMap` を通してロールを決める。これらの制限を外れたトークンは有効でない。 |
| 管理画面のセッション | Cookie の `__Host-elm-session`。`Authorization` ヘッダーは付けない。`GET` と `HEAD` 以外のリクエストには、ヘッダー `X-ELM-CSRF: 1` も要る。`Origin` ヘッダーがあるときは、管理画面のオリジンでなければならない。 | 管理画面のサインインが有効なとき（`oidc.webClientId` を設定したとき）だけ受け入れる。リクエストがないまま `session.idleMinutes` が過ぎるか、`session.maxHours` が過ぎると、セッションは終わる。 |

- スキーム名の `Bearer` は、大文字と小文字を区別しない。`Authorization` ヘッダーのあるリクエストを、Cookie で認証することはない。
- 資格情報がない、知らない資格情報である、資格情報が失効しているか期限が切れている、管理者が無効になっている、セッションが終わっている、のどれも同じレスポンスになる。`401` と `ELM_UNAUTHORIZED` だ。
- 有効なアクセストークンでも、ロールのクレームがどのロールにも対応しなければ、`403` と `ELM_FORBIDDEN` になる。トークンの `iss` と、`oidc.subjectClaim` のクレームの値が `bootstrapOwner`（最初の管理者。[elm-service の設定](service-configuration.md#bootstrapowner)で指定する）のものなら、クレームの内容にかかわらず、ロールは `owner` になる。
- CSRF 対策のヘッダーがないセッションのリクエストと、別のオリジンからのセッションのリクエストは、`403` と `ELM_FORBIDDEN` になる。

設定は [elm-service の設定](service-configuration.md#oidc)にある。サインインそのもの（`/auth/login`、`/auth/callback`、`/auth/logout`）は管理画面の一部で、この API には含まない。

#### ロールと権限

各ルートには 1 つの権限が要る。権限の名前は `resource.action` の形だ。管理者はロールを 1 つ持ち、ロールは次の表の権限を与える。API キーは自分の権限のリストを持つ。リクエストのたびに、そのうち管理者のロールも持つ権限だけが有効になる。アクセストークンとセッションは、そのロールのすべての権限を持つ。ロール `none` は、ロールのクレームがどのロールにも対応しなくなった管理者のロールで、権限を 1 つも持たない。

| 権限 | 許可すること | `owner` | `admin` | `support` | `viewer` |
| --- | --- | --- | --- | --- | --- |
| `apikey.read` | API キーを一覧する | ✓ | | ✓ | ✓ |
| `apikey.create` | API キーを作る | ✓ | | | |
| `apikey.revoke` | API キーを失効させる | ✓ | | | |
| `audit.read` | 監査ログを一覧し、検証する | ✓ | ✓ | ✓ | ✓ |
| `product.read` | 製品と機能を取得する | ✓ | ✓ | ✓ | ✓ |
| `product.write` | 製品と機能を作り、変更する | ✓ | ✓ | | |
| `policy.read` | ポリシーを取得する | ✓ | ✓ | ✓ | ✓ |
| `policy.write` | ポリシーを作り、変更する | ✓ | ✓ | | |
| `license.read` | ライセンスを取得する | ✓ | ✓ | ✓ | ✓ |
| `license.create` | ライセンスを作る | ✓ | ✓ | | |
| `license.update` | ライセンスを変更、一時停止、再開、延長する | ✓ | ✓ | | |
| `license.revoke` | ライセンスを失効させる | ✓ | ✓ | | |
| `license.file` | マシンに紐付けないライセンス（ポリシーの `binding` が `unbound` で、アクティベーションをしないライセンス）のライセンスファイルを得る | ✓ | ✓ | | |
| `signingkey.read` | 署名鍵の状態を取得する | ✓ | ✓ | ✓ | ✓ |
| `activation.read` | アクティベーションを取得する | ✓ | ✓ | ✓ | ✓ |
| `activation.deactivate` | 管理者としてデバイスのアクティベーションを解除する | ✓ | ✓ | ✓ | |
| `activation.offline` | オフラインのリクエストファイルを処理する | ✓ | ✓ | ✓ | |
| `trial.read` | トライアルを取得する | ✓ | ✓ | ✓ | ✓ |
| `trial.delete` | トライアルの記録を削除する | ✓ | ✓ | | |
| `lease.read` | [クラウドのリース](#リース)を取得する | ✓ | ✓ | ✓ | ✓ |
| `lease.release` | クラウドのリースと借用を解放する | ✓ | ✓ | ✓ | |
| `usagelog.read` | 利用ログのファイルを一覧し、取得する | ✓ | ✓ | ✓ | ✓ |
| `usagelog.seal` | 利用ログにすぐ署名する | ✓ | ✓ | | |
| `admin.read` | 管理者を一覧する | ✓ | | | |
| `admin.update` | 管理者を無効にし、有効にする | ✓ | | | |
| `session.read` | 管理画面のセッションを一覧する | ✓ | | | |
| `session.revoke` | 管理画面のセッションを終わらせる | ✓ | | | |

権限のない呼び出しは `403` と `ELM_FORBIDDEN` になり、メッセージに権限の名前が出る。

#### リクエストの本文

- リクエストの `Content-Type` ヘッダーは確かめない。どの型が送られても、本文は UTF-8 の JSON として読む。`POST /v1/admin/offline` の本文は、ファイルのテキストとして読む。
- 本文は 64 KiB までだ。`Content-Length` か、読んだバイト数で数える。それより大きい本文は `413` と `ELM_PAYLOAD_TOO_LARGE` になる。
- 本文は 1 つの JSON オブジェクトでなければならない。JSON でない本文、オブジェクトでない本文、型の違う項目、8 より深い入れ子、必須の項目がないか `null`、同じ項目が 2 回ある、対になっていないサロゲートを含む文字列、のどれかは `400` と `ELM_MALFORMED` になる。
- 型は正しいが、範囲や値の集合から外れた値は `400` と `ELM_INVALID_ARGUMENT` になる。本文の ID が何も指していないとき（どの製品のものでもない `productId` など）も同じだ。
- 知らない項目は無視する。新しいクライアントが古いサービスでも動くようにするためだ。
- 時刻の書き方は 1 つだけで、秒までの UTC で `2027-11-01T00:00:00Z` のように書く。レスポンスも同じ形を使う。ほかの形の時刻（秒の小数部があるもの、`+00:00` を使うもの）は `ELM_MALFORMED` になる。
- 文字数での長さは、バイトではなく Unicode の文字（スカラー値）で数える。表にバイトと書いてあるものは除く。
- `PATCH` は、送った項目だけを変える。送らなかった項目はそのままだ。`code` のように変えられない項目を送ると、`400` と `ELM_INVALID_ARGUMENT` になる。`null` の意味は項目ごとに違い、各ルートの本文の表に書いてある。値を取り除くもの、既定に戻すもの、`ELM_MALFORMED` になるものがある。
- 本文を受け取らないルートは、本文が送られても無視する。

#### ページング

一覧のルートは、どれも 1 ページを返す。ページはオブジェクトで、オブジェクトの配列である `items` と、次のページの `after` である `next` を持つ。ページの例は `GET /v1/admin/api-keys` にある。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit` | 1 ページの件数。1 から 200。既定は 50。 |
| `after` | この項目の次から始める。前のページの `next` を渡す。ルートに別の記載がなければ、項目は ID の順、つまり作成の順に並ぶ。どの項目も指していない `after` でもよい。その場合、ページはその並び順でその値が来る位置の次から始まる。 |

最後のページには `next` がない。形の正しくない `limit` や `after`、2 回指定したクエリパラメーターは `400` と `ELM_INVALID_ARGUMENT` になる。

#### エラー

エラーは、`application/problem+json` の型の problem 形式のレスポンス（RFC 9457）だ。

```json
{
  "type": "about:blank",
  "title": "ELM_CONFLICT",
  "status": 409,
  "detail": "The product code \u0027acme-cad\u0027 is in use.",
  "code": "ELM_CONFLICT"
}
```

`code` と `title` にはエラーコード、`status` には HTTP の状態、`detail` には英語のメッセージが入る。すべてのコードは[エラーコード](error-codes.md)にある。以下の各ルートのエラーのほかに、どのルートも次のレスポンスを返すことがある。

| 状態 | コード | 条件 |
| --- | --- | --- |
| 401 | `ELM_UNAUTHORIZED` | 有効な資格情報がない。[認証](#認証)を参照。 |
| 403 | `ELM_FORBIDDEN` | 権限がない、アクセストークンがどのロールにも対応しない、またはセッションの CSRF の確認に失敗した。 |
| 413 | `ELM_PAYLOAD_TOO_LARGE` | 本文が 64 KiB より大きい（本文を読むルート）。 |
| 429 | `ELM_RATE_LIMITED` | レート制限に達した。レスポンスには `Retry-After: 1` が付く。 |
| 500 | `ELM_INTERNAL_ERROR` | データベースでエラーが起きた。理由は運用ログ（[ログ](logs.md#運用ログ)）にある。 |

存在しないルートのパスは本文のない `404` になり、パスが受け付けないメソッドは本文のない `405` になる。パスの `{id}` が ID の形でないときは、知らない ID と同じレスポンスになる。

本文を読めないリクエストには、レスポンスを返さず、サービスが接続を閉じる。形が正しくない本文（たとえば `Transfer-Encoding: chunked` の不正なチャンク）と、届くのが遅すぎる本文がこれに当たる。その呼び出しも監査ログに記録し、`result` は `ELM_MALFORMED` か `ELM_NETWORK_ERROR` になる（[ログ](logs.md#行のフィールド)）。

#### レート制限

どのリクエストも、まず送信元のアドレス（IPv4 アドレス、または IPv6 の /64）ごとに `rateLimit.perIp` で制限する。認証されたリクエストは、さらに `rateLimit.perKey` でも制限する。API キーごと、またはアクセストークンとセッションでは管理者ごとに数える。確認の順序は、送信元ごとの制限、認証、キーごとの制限、権限、ルートそのもの、だ。設定は [elm-service の設定](service-configuration.md#ratelimit)にある。

#### キャッシュ

`/v1/admin/` の下のレスポンスには、エラーも含めてすべて `Cache-Control: no-store` が付く。

#### 監査ログ

認証された呼び出しは、参照と拒否も含めて、どれも監査ログに 1 行を書く。レスポンスを最後まで返せずに終わった呼び出しも、1 行を書く。アクションの名前は、以下の各ルートに書いてある。権限がなくて拒否した呼び出しは、`result` を `ELM_FORBIDDEN` として書く。有効な資格情報のない呼び出しは件数を数え、1 分ごとに 1 行の `auth.failed` として書く。レート制限した呼び出しは書かない。`GET /v1/admin/oidc` と、成功した `GET /v1/admin/me` は行を書かない。アクションと行のフィールドは[ログ](logs.md#監査ログ)にある。

### ヘルスチェック

#### `GET /health`

権限: なし。資格情報は要らない。送信元ごとの制限は適用する。

データベースが答えれば本文のない `200` を、答えなければ本文のない `503` を返す。レスポンスには `Cache-Control` ヘッダーが付かない。監査ログの行は書かない。

### オブジェクト

以下のオブジェクトは、複数のルートが返す。`null` になりうる項目には、そう書いてある。そうした項目も常にある。時刻はどれも `2027-11-01T00:00:00Z` の形だ。

#### 製品

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | 製品の ID。 |
| `code` | 文字列 | アプリとライセンスファイルが使う製品 ID。英小文字、数字、`-` からなる 1 から 64 文字。変わらない。 |
| `name` | 文字列 | 表示名。1 から 64 文字。 |
| `trialPolicyId` | 文字列または `null` | この製品のトライアルを与える、`licenseKind` が `trial` のポリシーの ID（[トライアル](../explanation/activation.md#トライアル)）。製品がトライアルを提供しないときは `null`。 |
| `createdAt` | 時刻 | 作成した時刻。 |

#### 機能

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | 機能の ID。 |
| `productId` | 文字列 | 機能が属する製品。変わらない。 |
| `code` | 文字列 | アプリとライセンスファイルが使う機能 ID。英小文字、数字、`-` からなる 1 から 64 文字で、製品の中で一意。変わらない。 |
| `name` | 文字列 | 表示名。1 から 64 文字。 |
| `createdAt` | 時刻 | 作成した時刻。 |

#### ポリシー

既定の値も含めて、すべての項目がある。各設定が何を決めるかは[アクティベーション](../explanation/activation.md#ポリシーの設定)にある。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | ポリシーの ID。 |
| `productId` | 文字列 | 製品。変わらない。 |
| `name` | 文字列 | 表示名。1 から 64 文字。 |
| `licenseKind` | 文字列 | `standard`、`trial`、`server` のいずれか。変わらない。 |
| `binding` | 文字列 | `unbound` か `nodeLocked`。変わらない。 |
| `maxActivations` | 数値 | 同時にアクティベーションを保持できるデバイスの台数。 |
| `deactivationLimit` | 数値または `null` | デバイスによるアクティベーション解除が許される回数の合計。`null` なら制限はない。 |
| `expirationBasis` | 文字列 | `fromIssue` か `fromFirstActivation`。 |
| `duration` | 数値または `null` | ライセンスが続く秒数。`null` なら有効期限はない。 |
| `tokenLifetime` | 数値 | 1 つのライセンスファイルが有効な秒数。 |
| `refreshWindow` | 数値 | ファイルの有効期限の何秒前から、アプリがトークンの更新をするか。 |
| `gracePeriod` | 数値 | ファイルの有効期限が切れたあと、アプリが動き続ける秒数。 |
| `fingerprintMatching` | 文字列 | `ignore`、`any`、`all` のいずれか。 |
| `cloneStrategy` | 文字列 | `deny` か `flag`。アクティベーション済みのインストールのコピーを検出したときに、サービスがすること（[シーケンス番号によるクローン検出](../explanation/activation.md#シーケンス番号によるクローン検出)）。`licenseKind` が `server` のポリシーでは、アクティベーション済みのインストールは `elm-server` で、この値はサーバーライセンスの `clone` クレームにもなる。つまり、コピーされたアプリのインストールが同じリースを更新したときに、そのサーバーがすることだ（[再試行とクローン](../explanation/floating-licenses.md#再試行とクローン)）。`floating` が `cloud` のポリシーでは、そうしたリースに対してサービスがすることでもある。 |
| `floating` | 文字列 | `none`、`onPremises`、`cloud` のいずれか。 |
| `leaseDuration` | 数値 | 1 つのリースが続く秒数。 |
| `overage` | 数値または `null` | 購入したシート数を超えて貸し出せるシートの割合（パーセント）。`null` なら超えて貸し出さない。 |
| `borrowLimit` | 数値または `null` | 借用の最長の期間（秒）。`null` なら借用できない。 |
| `borrowReturnLimit` | 数値 | 30 日間に借用を早期返却できる回数。 |
| `clientInfoStrategy` | 文字列 | `none` か `hostAndUser`。 |
| `trialDuration` | 数値 | トライアルが続く秒数。 |
| `createdAt` | 時刻 | 作成した時刻。 |
| `updatedAt` | 時刻 | 最後に変更した時刻。 |

#### ライセンス

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | ライセンス ID。ライセンスファイルにも入る。 |
| `productId` | 文字列 | ポリシーの製品。 |
| `policyId` | 文字列 | ポリシー。変わらない。 |
| `name` | 文字列 | 顧客名などの名前。1 から 256 文字。 |
| `features` | 配列 | 付与する機能。それぞれ次の項目を持つオブジェクト。 |
| `features[].code` | 文字列 | 機能の `code`。 |
| `features[].ver` | 文字列または `null` | `>=2.0 <3.0` のようなバージョンの範囲。`null` ならすべてのバージョン。 |
| `features[].seats` | 数値または `null` | サーバーライセンスか、クラウドのフローティングのライセンスのシート数（[リース](#リース)を参照）。ほかのライセンスでは `null`。 |
| `features[].expiresAt` | 時刻または `null` | 機能ごとの有効期限。ないときは `null`。 |
| `maxActivations` | 数値または `null` | ライセンスごとのアクティベーションの上限。ポリシーの `maxActivations` を使うときは `null`。 |
| `state` | 文字列 | `active`、`suspended`、`expired`、`revoked` のいずれか。複数に当てはまるときは、revoked が suspended より、suspended が expired より優先する。[ライセンスの状態](../explanation/activation.md#ライセンスの状態)を参照。 |
| `createdAt` | 時刻 | 作成した時刻。 |
| `expiresAt` | 時刻または `null` | 有効期限。ライセンスに有効期限がないとき、または `fromFirstActivation` で最初のアクティベーションの前は `null`。 |
| `suspendedAt` | 時刻または `null` | 一時停止した時刻。一時停止していなければ `null`。 |
| `revokedAt` | 時刻または `null` | 失効させた時刻。失効していなければ `null`。 |
| `meta` | オブジェクト | ライセンスファイルにコピーされる文字列の値。 |
| `deactivations` | 数値 | これまでのデバイスによるアクティベーション解除の回数。ポリシーの `deactivationLimit` に対して数える。管理者によるアクティベーション解除は数えない。 |
| `key` | 文字列 | `ELM-A1B2C-3D4E5-F6G7H-J8K9M` のようなライセンスキー。`POST /v1/admin/licenses` のレスポンスにだけ入る。サービスはキーのハッシュしか保存しないので、もう一度表示できない。 |

トライアルもライセンスの 1 つだ。名前は `trial` で、キーはない。

#### アクティベーション

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | アクティベーション ID。 |
| `licenseId` | 文字列 | ライセンス。 |
| `keyId` | 文字列 | デバイスのインストール鍵のサムプリント。 |
| `fingerprints` | 文字列の配列 | デバイスが送ったマシンフィンガープリント。8 個まで、それぞれ小文字の 16 進数 32 文字。 |
| `client` | オブジェクトまたは `null` | `host` と `user`。デバイスが送ったホスト名と OS のユーザー名で、`clientInfoStrategy` が `hostAndUser` のときだけ保存する。それ以外は `null`。 |
| `app` | 文字列または `null` | デバイスが送ったアプリのバージョン。送らなかったときは `null`。 |
| `seq` | 数値 | 最新のライセンスファイルのシーケンス番号。 |
| `blockedAt` | 時刻または `null` | `cloneStrategy` が `deny` のもとでクローンを検出して、アクティベーションを止めた時刻。止めていなければ `null`。 |
| `createdAt` | 時刻 | デバイスがアクティベーションした時刻。 |
| `updatedAt` | 時刻 | トークンの更新などで、最後に変わった時刻。 |

#### トライアル

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | トライアルの ID。トライアルのライセンスの ID と同じ。 |
| `productId` | 文字列 | 製品。 |
| `fingerprints` | 文字列の配列 | このトライアルを受けたマシンフィンガープリント。記録があるあいだ、これらのフィンガープリントでは、その製品の別のトライアルを始められない。 |
| `createdAt` | 時刻 | トライアルを始めた時刻。 |

#### リース

クラウドのリースとは、クラウドのフローティングのライセンスのシートを、`elm-server` ではなく `elm-service` 自身が貸し出したものだ。クラウドのフローティングのライセンスとは、ポリシーの `floating` が `cloud` のライセンスをいう。クラウドのリースの仕組みは[フローティングライセンス](../explanation/floating-licenses.md#saas-型のサービスも同じプロトコル)にある。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | リース ID。22 文字の base64url。 |
| `feature` | 文字列 | 機能の `code`。 |
| `seats` | 数値 | 保持しているシート数。 |
| `keyId` | 文字列 | デバイスのインストール鍵のサムプリント。 |
| `seq` | 数値 | 最新のリーストークンのシーケンス番号。 |
| `expiresAt` | 時刻 | リースの更新がなければ有効期限が切れる時刻。 |
| `borrow` | 真偽値 | 借用かどうか。 |
| `stopped` | 真偽値 | クローンを検出して止めたかどうか。止めたリースは更新されず、有効期限で終わる。 |
| `client` | オブジェクトまたは `null` | `host` と `user`。アクティベーションと同じ。 |
| `app` | 文字列または `null` | デバイスが送ったアプリのバージョン。 |
| `createdAt` | 時刻 | 貸し出した時刻。 |

#### API キー

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | キーの ID。監査ログは、この ID でキーを示す。 |
| `name` | 文字列 | 名前。1 から 64 文字。 |
| `prefix` | 文字列 | `elm_ExAm` のような、キーの先頭の 8 文字。キーを見分けるのに使う。 |
| `permissions` | 文字列の配列 | キーの権限。[ロールと権限](#ロールと権限)の表の順に並ぶ。リクエストのたびに、そのうち管理者のロールが持つ権限だけが有効になる。 |
| `createdBy` | 文字列 | キーが代理で操作する管理者の ID。 |
| `createdAt` | 時刻 | 作成した時刻。 |
| `expiresAt` | 時刻 | 使えなくなる時刻。 |
| `revokedAt` | 時刻または `null` | 失効させた時刻。失効していなければ `null`。 |
| `key` | 文字列 | キーそのもの。`POST /v1/admin/api-keys` のレスポンスにだけ入る。サービスはキーのハッシュしか保存しない。 |

#### 管理者

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | 管理者の ID。 |
| `issuer` | 文字列 | OIDC の発行者（`iss`）。 |
| `subject` | 文字列 | その発行者における、`oidc.subjectClaim` が指すクレームの値（既定は `sub`）。 |
| `role` | 文字列 | `owner`、`admin`、`support`、`viewer`、`none` のいずれか。 |
| `enabled` | 真偽値 | 管理者が認証できるかどうか。無効にした管理者の API キー、トークン、セッションは使えない。 |
| `createdAt` | 時刻 | 管理者を作成した時刻。`bootstrapOwner` なら `elm-service setup` が作成した時刻、それ以外なら最初のサインインの時刻。 |
| `lastLoginAt` | 時刻または `null` | アクセストークンでの最後のリクエスト、または最後の管理画面のサインインの時刻。書き込むのは 1 時間に 1 回まで。最初のリクエストやサインインの前は `null`。API キーでのリクエストは数えない。 |

#### セッション

管理画面のセッション。その Cookie と Cookie のハッシュは返さない。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | セッションの ID。 |
| `adminId` | 文字列 | 管理者。 |
| `createdAt` | 時刻 | サインインした時刻。 |
| `lastSeenAt` | 時刻 | 最後のリクエストの時刻。書き込むのは 1 分に 1 回まで。 |
| `expiresAt` | 時刻 | セッションが遅くとも終わる時刻（サインインから `session.maxHours` 後）。 |
| `ip` | 文字列または `null` | サインインしたときの送信元のアドレス。わからなかったときは `null`。 |

#### 監査ログの行

監査ログの 1 行。各フィールドの意味は[ログ](logs.md#行のフィールド)にある。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `n` | 数値 | 行の番号。 |
| `ts` | 時刻 | 時刻。 |
| `actorType` | 文字列 | `apikey`、`oidc`、`system`、`installation` のいずれか。 |
| `actorId` | 文字列または `null` | 操作した者。 |
| `action` | 文字列 | アクション。 |
| `target` | 文字列または `null` | 操作の対象。 |
| `result` | 文字列 | `ELM_OK` かエラーコード。レスポンスが終わる前に接続が閉じたかリセットされたとき（リクエストの本文の途中も含む）と、リクエストの本文が届くのが遅すぎたときは `ELM_NETWORK_ERROR`、接続が開いたままで、リクエストの本文の形が正しくなくて読めなかったときは `ELM_MALFORMED`、サービスの内部で操作が失敗したときは `ELM_INTERNAL_ERROR`。 |
| `ip` | 文字列または `null` | 送信元のアドレス。 |
| `detail` | 文字列または `null` | 詳細。JSON オブジェクトを、保存したとおりのテキストで返す。行のハッシュを計算し直せるようにするためだ。 |
| `prev` | 文字列 | 前の行のハッシュ。base64url で書く。 |
| `hash` | 文字列 | この行のハッシュ。base64url で書く。 |

### API キーのルート

#### `POST /v1/admin/api-keys`

呼び出し元の管理者の API キーを作る。

権限: `apikey.create`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `name` | 文字列 | はい | 1 から 64 文字。 |
| `permissions` | 文字列の配列 | はい | [ロールと権限](#ロールと権限)にある権限を 1 から 32 個。個数は重複を除く前に数え、2 回指定した権限は 1 つにまとめる。どの権限も、呼び出し元がいま持っているものでなければならない。 |
| `expiresInDays` | 数値 | いいえ | キーの有効期限までの日数。1 から 365。既定は 90。`null` は既定と同じ。 |

`201` で、`key` を含む [API キー](#api-キー)を返す。キーは保管しておくこと。もう一度表示することはない。

この例で送った本文:

```json
{
  "name": "billing",
  "permissions": ["license.read", "license.create", "license.update"],
  "expiresInDays": 30
}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000102",
  "name": "billing",
  "prefix": "elm_ExAm",
  "permissions": [
    "license.read",
    "license.create",
    "license.update"
  ],
  "createdBy": "01960000-0000-7000-8000-000000000001",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2026-12-01T11:27:59Z",
  "revokedAt": null,
  "key": "elm_ExAmPlEaPiKeY000000000000000000000000000001"
}
```

エラー: 名前、権限の個数、`expiresInDays` が範囲外のときと、知らない権限があるときは `ELM_INVALID_ARGUMENT`（400）。呼び出し元が持たない権限があるときは `ELM_FORBIDDEN`（403）。

監査ログのアクション: `apikey.create`。

#### `GET /v1/admin/api-keys`

すべての管理者の API キーを、失効したキーと期限切れのキーも含めて一覧する。

権限: `apikey.read`。

クエリパラメーター: `limit` と `after`（API キーの ID）。[ページング](#ページング)を参照。

`200` で、`key` を含まない [API キー](#api-キー)のページを返す。この例は `GET /v1/admin/api-keys?limit=1` のレスポンスで、`billing` のキーが後に続くので `next` がある。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000101",
      "name": "setup",
      "prefix": "elm_SeTu",
      "permissions": [
        "apikey.read",
        "apikey.create",
        "apikey.revoke",
        "audit.read",
        "product.read",
        "product.write",
        "policy.read",
        "policy.write",
        "license.read",
        "license.create",
        "license.update",
        "license.revoke",
        "license.file",
        "signingkey.read",
        "activation.read",
        "activation.deactivate",
        "activation.offline",
        "trial.read",
        "trial.delete",
        "lease.read",
        "lease.release",
        "usagelog.read",
        "usagelog.seal",
        "admin.read",
        "admin.update",
        "session.read",
        "session.revoke"
      ],
      "createdBy": "01960000-0000-7000-8000-000000000001",
      "createdAt": "2026-11-01T11:27:58Z",
      "expiresAt": "2027-01-30T11:27:58Z",
      "revokedAt": null
    }
  ],
  "next": "01960000-0000-7000-8000-000000000101"
}
```

エラー: `limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `apikey.list`。

#### `DELETE /v1/admin/api-keys/{id}`

API キーを失効させる。キーはすぐに使えなくなり、`revokedAt` を持って一覧に残る。失効したキーを失効させても何も変わらず、同じレスポンスを返す。呼び出しごとに監査ログの行を書く。

権限: `apikey.revoke`。

本文のない `204` を返す。

エラー: その ID のキーがないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `apikey.revoke`。

### 監査のルート

#### `GET /v1/admin/audit`

監査ログの行を一覧する。

権限: `audit.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit` | 1 から 200。既定は 50。 |
| `after` | 行の番号 `n`。`asc` ではその次から、`desc` ではそれより小さい番号からページが始まる。 |
| `order` | `asc`（既定）は古い順。`desc` は新しい順で、`after` がなければ最新の行から始まる。 |

`200` で、[監査ログの行](#監査ログの行)のページを返す。ここでは `next` は数値で、ページの最後の行の `n` だ。この例は `GET /v1/admin/audit?order=desc&limit=2` のレスポンスで、[ライセンス](#ライセンスのルート)と [`PATCH /v1/admin/policies/{id}`](#patch-v1adminpoliciesid) の例のあとに取得した。

```json
{
  "items": [
    {
      "n": 25,
      "ts": "2026-11-01T11:28:04Z",
      "actorType": "apikey",
      "actorId": "01960000-0000-7000-8000-000000000101",
      "action": "policy.update",
      "target": "01960000-0000-7000-8000-000000000401",
      "result": "ELM_OK",
      "ip": "127.0.0.1",
      "detail": "{\u0022deactivationLimit\u0022:5,\u0022duration\u0022:null}",
      "prev": "hAsHeXaMpLe00000000000000000000000000000024",
      "hash": "hAsHeXaMpLe00000000000000000000000000000025"
    },
    {
      "n": 24,
      "ts": "2026-11-01T11:28:04Z",
      "actorType": "apikey",
      "actorId": "01960000-0000-7000-8000-000000000101",
      "action": "license.resume",
      "target": "01960000-0000-7000-8000-000000000504",
      "result": "ELM_CONFLICT",
      "ip": "127.0.0.1",
      "detail": null,
      "prev": "hAsHeXaMpLe00000000000000000000000000000023",
      "hash": "hAsHeXaMpLe00000000000000000000000000000024"
    }
  ],
  "next": 24
}
```

エラー: `limit`、`after`、`order` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `audit.list`。

#### `POST /v1/admin/audit/verify`

監査ログのハッシュのチェーンを、残っているいちばん古い行から最新の行まで確かめる。この確認で何がわかり、何がわからないかは[ログ](logs.md#監査ログの検証)にある。

権限: `audit.read`。

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `intact` | 真偽値 | すべての行が前の行に続いているかどうか。 |
| `checked` | 数値 | 読んだ行の数。 |
| `firstBroken` | 数値または `null` | 前の行に続いていない最初の行の番号。チェーンが壊れていなければ `null`。 |

```json
{
  "intact": true,
  "checked": 72,
  "firstBroken": null
}
```

エラー: すべてのルートに共通のものだけ。

監査ログのアクション: `audit.verify`。

### 製品と機能のルート

製品、機能、ポリシーを削除するルートはない。

#### `POST /v1/admin/products`

製品を作る。

権限: `product.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `code` | 文字列 | はい | 英小文字、数字、`-` からなる 1 から 64 文字。ほかの製品が使っていないもの。 |
| `name` | 文字列 | はい | 1 から 64 文字。 |

`201` で[製品](#製品)を返す。新しい製品にはトライアルのポリシーがない。`PATCH` で設定する。

この例で送った本文:

```json
{"code": "acme-cad", "name": "Acme CAD"}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000201",
  "code": "acme-cad",
  "name": "Acme CAD",
  "trialPolicyId": null,
  "createdAt": "2026-11-01T11:27:59Z"
}
```

エラー: `code` か `name` が形に合わないときは `ELM_INVALID_ARGUMENT`（400）。ほかの製品がそのコードを持つときは `ELM_CONFLICT`（409）。

監査ログのアクション: `product.create`。

#### `GET /v1/admin/products`

製品を一覧する。

権限: `product.read`。

クエリパラメーター: `limit` と `after`（製品の ID）。

`200` で[製品](#製品)のページを返す。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000201",
      "code": "acme-cad",
      "name": "Acme CAD",
      "trialPolicyId": "01960000-0000-7000-8000-000000000402",
      "createdAt": "2026-11-01T11:27:59Z"
    }
  ]
}
```

エラー: `limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `product.list`。

#### `GET /v1/admin/products/{id}`

製品を 1 つ返す。

権限: `product.read`。

`200` で[製品](#製品)を返す。たとえば、上の一覧の例の項目と同じものだ。

エラー: その ID の製品がないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `product.get`。

#### `PATCH /v1/admin/products/{id}`

製品を変更する。

権限: `product.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `name` | 文字列 | いいえ | 1 から 64 文字。`null` は `ELM_MALFORMED`。 |
| `trialPolicyId` | 文字列または `null` | いいえ | この製品の、`licenseKind` が `trial` のポリシーの ID。製品は、このポリシーでトライアルを提供するようになる。`null` はトライアルのポリシーを取り除き、製品はトライアルを提供しなくなる。 |
| `code` | — | — | 変更できない。送ると `ELM_INVALID_ARGUMENT`。 |

`200` で、変更した[製品](#製品)を返す。この例では、`{"productId": "01960000-0000-7000-8000-000000000201", "name": "Trial", "licenseKind": "trial", "gracePeriod": 0}` で作ったトライアルのポリシーの ID を送った。

```json
{"trialPolicyId": "01960000-0000-7000-8000-000000000402"}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000201",
  "code": "acme-cad",
  "name": "Acme CAD",
  "trialPolicyId": "01960000-0000-7000-8000-000000000402",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

エラー: その ID の製品がないときは `ELM_NOT_FOUND`（404）。`name` が範囲外のとき、`trialPolicyId` がこの製品のトライアルのポリシーでないとき、`code` を送ったときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `product.update`。

#### `POST /v1/admin/products/{id}/features`

製品の機能を作る。

権限: `product.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `code` | 文字列 | はい | 英小文字、数字、`-` からなる 1 から 64 文字。製品のほかの機能が使っていないもの。 |
| `name` | 文字列 | はい | 1 から 64 文字。 |

`201` で[機能](#機能)を返す。この例で送った本文:

```json
{"code": "pro", "name": "Pro"}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000301",
  "productId": "01960000-0000-7000-8000-000000000201",
  "code": "pro",
  "name": "Pro",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

エラー: その ID の製品がないときは `ELM_NOT_FOUND`（404）。`code` か `name` が形に合わないときは `ELM_INVALID_ARGUMENT`（400）。製品のほかの機能がそのコードを持つときは `ELM_CONFLICT`（409）。

監査ログのアクション: `feature.create`。

#### `GET /v1/admin/products/{id}/features`

製品の機能を一覧する。

権限: `product.read`。

クエリパラメーター: `limit` と `after`（機能の ID）。

`200` で[機能](#機能)のページを返す。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000301",
      "productId": "01960000-0000-7000-8000-000000000201",
      "code": "pro",
      "name": "Pro",
      "createdAt": "2026-11-01T11:27:59Z"
    },
    {
      "id": "01960000-0000-7000-8000-000000000302",
      "productId": "01960000-0000-7000-8000-000000000201",
      "code": "export",
      "name": "Export",
      "createdAt": "2026-11-01T11:27:59Z"
    }
  ]
}
```

エラー: その ID の製品がないときは `ELM_NOT_FOUND`（404）。`limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `feature.list`。

#### `PATCH /v1/admin/products/{id}/features/{featureId}`

機能を変更する。

権限: `product.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `name` | 文字列 | いいえ | 1 から 64 文字。`null` は `ELM_MALFORMED`。 |
| `code`、`productId` | — | — | 変更できない。どちらかを送ると `ELM_INVALID_ARGUMENT`。 |

`200` で、変更した[機能](#機能)を返す。この例で送った本文:

```json
{"name": "Professional"}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000301",
  "productId": "01960000-0000-7000-8000-000000000201",
  "code": "pro",
  "name": "Professional",
  "createdAt": "2026-11-01T11:27:59Z"
}
```

エラー: 製品にその ID の機能がないときは `ELM_NOT_FOUND`（404）。`name` が範囲外のとき、`code` か `productId` を送ったときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `feature.update`。

### ポリシーのルート

#### `POST /v1/admin/policies`

ポリシーを作る。省いた設定は既定の値になる。

権限: `policy.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `productId` | 文字列 | はい | 製品の ID。 |
| `name` | 文字列 | はい | 1 から 64 文字。 |
| `licenseKind` | 文字列 | いいえ | `standard`（既定）、`trial`、`server` のいずれか。 |
| `binding` | 文字列 | いいえ | `unbound` か `nodeLocked`（既定）。 |
| `maxActivations` | 数値 | いいえ | 1 から 100,000。既定は 1。 |
| `deactivationLimit` | 数値または `null` | いいえ | 0 から 100,000。既定は `null` で、制限はない。 |
| `expirationBasis` | 文字列 | いいえ | `fromIssue`（既定）か `fromFirstActivation`。 |
| `duration` | 数値または `null` | いいえ | 秒数。86,400（1 日）から 3,162,240,000（366 日の年で 100 年）。既定は `null` で、有効期限はない。 |
| `tokenLifetime` | 数値 | いいえ | 秒数。3,600 から 3,162,240,000。既定は 2,592,000（30 日）。 |
| `refreshWindow` | 数値 | いいえ | 秒数。0 から 3,162,240,000 で、`tokenLifetime` より小さい値。既定は 1,728,000（20 日）。 |
| `gracePeriod` | 数値 | いいえ | 秒数。0 から 3,162,240,000。既定は 604,800（7 日）。 |
| `fingerprintMatching` | 文字列 | いいえ | `ignore`、`any`（既定）、`all` のいずれか。 |
| `cloneStrategy` | 文字列 | いいえ | `deny`（既定）か `flag`。[ポリシー](#ポリシー)の `cloneStrategy` を参照。 |
| `floating` | 文字列 | いいえ | `none`（既定）、`onPremises`、`cloud` のいずれか。 |
| `leaseDuration` | 数値 | いいえ | 秒数。120 から 86,400。既定は 600。 |
| `overage` | 数値または `null` | いいえ | パーセント。0 から 1,000。既定は `null` で、超えて貸し出さない。 |
| `borrowLimit` | 数値または `null` | いいえ | 秒数。1 から 3,162,240,000。既定は `null` で、借用できない。 |
| `borrowReturnLimit` | 数値 | いいえ | 0 から 1,000。既定は 2。 |
| `clientInfoStrategy` | 文字列 | いいえ | `none`（既定）か `hostAndUser`。 |
| `trialDuration` | 数値 | いいえ | 秒数。86,400 から 31,536,000（365 日）。既定は 1,209,600（14 日）。 |

`null` を取らない数値の項目と、文字列の項目では、`null` は `ELM_MALFORMED` になる。設定どうしも次のように合っていなければならず、合わなければ `ELM_INVALID_ARGUMENT` になる。

- `refreshWindow` が `tokenLifetime` より小さい。
- `trial` か `server` のポリシーは `nodeLocked` で、`floating` が `cloud` でない。
- `trial` のポリシーは、`fingerprintMatching` が `any` か `all` で、`gracePeriod` が 0。
- `unbound` のポリシーは、`expirationBasis` が `fromIssue`。

`licenseKind` と `floating` によって、作られるライセンスは次のようになる。

- `licenseKind` が `server` のポリシーは、`elm-server` が使うサーバーライセンスを作る。このポリシーは `nodeLocked` で、`floating` は `none` か `onPremises` だ。そのライセンスの各機能には `seats` が要る。`elm-server` がサーバーライセンスをどう使うかは[フローティングライセンス](../explanation/floating-licenses.md#サーバーライセンス-クライアントがオンプレミスのサーバーを信頼する仕組み)にある。
- `floating` が `cloud` のポリシーは、クラウドのフローティングのライセンスを作る。`elm-service` がそのシートを[クラウドのリース](#リース)として貸し出し、アクティベーションはしない。このポリシーの `licenseKind` は `standard` だ。
- `elm-service` は `onPremises` を `none` として扱う。`onPremises` の `standard` のポリシーが作るライセンスは、`none` のポリシーのライセンスと同じようにアクティベーションする。

`201` で[ポリシー](#ポリシー)を返す。この例で送った本文:

```json
{
  "productId": "01960000-0000-7000-8000-000000000201",
  "name": "Standard, 3 devices",
  "maxActivations": 3,
  "duration": 31622400
}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000401",
  "productId": "01960000-0000-7000-8000-000000000201",
  "name": "Standard, 3 devices",
  "licenseKind": "standard",
  "binding": "nodeLocked",
  "maxActivations": 3,
  "deactivationLimit": null,
  "expirationBasis": "fromIssue",
  "duration": 31622400,
  "tokenLifetime": 2592000,
  "refreshWindow": 1728000,
  "gracePeriod": 604800,
  "fingerprintMatching": "any",
  "cloneStrategy": "deny",
  "floating": "none",
  "leaseDuration": 600,
  "overage": null,
  "borrowLimit": null,
  "borrowReturnLimit": 2,
  "clientInfoStrategy": "none",
  "trialDuration": 1209600,
  "createdAt": "2026-11-01T11:27:59Z",
  "updatedAt": "2026-11-01T11:27:59Z"
}
```

エラー: `productId` がどの製品のものでもないとき、値が範囲や値の集合から外れたとき、設定どうしが合わないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `policy.create`。

#### `GET /v1/admin/policies`

ポリシーを一覧する。

権限: `policy.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit`、`after` | [ページング](#ページング)を参照。`after` はポリシーの ID。 |
| `productId` | この製品のポリシーだけ。 |

`200` で[ポリシー](#ポリシー)のページを返す。この例は `GET /v1/admin/policies?limit=1` のレスポンスで、ほかのポリシーが後に続くので `next` がある。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000401",
      "productId": "01960000-0000-7000-8000-000000000201",
      "name": "Standard, 3 devices",
      "licenseKind": "standard",
      "binding": "nodeLocked",
      "maxActivations": 3,
      "deactivationLimit": null,
      "expirationBasis": "fromIssue",
      "duration": 31622400,
      "tokenLifetime": 2592000,
      "refreshWindow": 1728000,
      "gracePeriod": 604800,
      "fingerprintMatching": "any",
      "cloneStrategy": "deny",
      "floating": "none",
      "leaseDuration": 600,
      "overage": null,
      "borrowLimit": null,
      "borrowReturnLimit": 2,
      "clientInfoStrategy": "none",
      "trialDuration": 1209600,
      "createdAt": "2026-11-01T11:27:59Z",
      "updatedAt": "2026-11-01T11:27:59Z"
    }
  ],
  "next": "01960000-0000-7000-8000-000000000401"
}
```

エラー: `limit`、`after`、`productId` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `policy.list`。

#### `GET /v1/admin/policies/{id}`

ポリシーを 1 つ返す。

権限: `policy.read`。

`200` で[ポリシー](#ポリシー)を返す。形は [`POST /v1/admin/policies`](#post-v1adminpolicies) のレスポンスと同じだ。

エラー: その ID のポリシーがないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `policy.get`。

#### `PATCH /v1/admin/policies/{id}`

ポリシーを変更する。変更は、サービスが次に発行するライセンスファイルやリースから反映される。発行済みのファイルとリースは、有効期限まで元の値のままだ。

権限: `policy.write`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `name` | 文字列 | いいえ | 1 から 64 文字。`null` は `ELM_MALFORMED`。 |
| `deactivationLimit` | 数値または `null` | いいえ | 0 から 100,000。`null` は制限を取り除く。 |
| `duration` | 数値または `null` | いいえ | 作成時と同じ。`null` なら有効期限はない。新しい値は、変更後に作るライセンスと、`fromFirstActivation` で有効期限のないライセンスの次のアクティベーションやクラウドのリースに適用する。すでに有効期限のあるライセンスは、その有効期限のままだ。 |
| `overage` | 数値または `null` | いいえ | 作成時と同じ。`null` にすると、購入したシート数を超えるシートを貸し出さない。 |
| `borrowLimit` | 数値または `null` | いいえ | 作成時と同じ。`null` にすると借用できなくなる。 |
| `maxActivations`、`expirationBasis`、`tokenLifetime`、`refreshWindow`、`gracePeriod`、`fingerprintMatching`、`cloneStrategy`、`floating`、`leaseDuration`、`borrowReturnLimit`、`clientInfoStrategy`、`trialDuration` | 作成時と同じ | いいえ | `POST /v1/admin/policies` の範囲。`null` は `ELM_MALFORMED`。 |
| `productId`、`licenseKind`、`binding` | — | — | 変更できない。どれかを送ると `ELM_INVALID_ARGUMENT`。 |

変更後のポリシー全体が、`POST /v1/admin/policies` のルールに合っていなければならない。

`200` で、変更した[ポリシー](#ポリシー)を返す。この例では、[ライセンス](#ライセンスのルート)のライセンスを作ったあとに、`POST` の例のポリシーを変更した。それらのライセンスの有効期限は変わらない。送った本文:

```json
{"deactivationLimit": 5, "duration": null}
```

レスポンスは、そのポリシーの次の項目が変わったものだ。

```json
{
  "id": "01960000-0000-7000-8000-000000000401",
  "deactivationLimit": 5,
  "duration": null,
  "updatedAt": "2026-11-01T11:28:04Z"
}
```

エラー: その ID のポリシーがないときは `ELM_NOT_FOUND`（404）。値が範囲外のとき、変更できない項目を送ったとき、設定どうしが合わないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `policy.update`。

### 署名鍵のルート

#### `GET /v1/admin/signing-key`

サービスの署名鍵の状態を返す。

権限: `signingkey.read`。

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `keyId` | 文字列 | 署名鍵の鍵 ID（`kid`）。 |
| `uses` | 文字列の配列 | サービスの起動時に、鍵の証明書が許可していた用途。`license`、`lease`、`log` の順に並ぶ。サービスが利用ログを書かないときでも、`log` が入ることがある。 |
| `expiresAt` | 時刻 | サービスに必要な用途のうち、最初に証明書の範囲から外れるものの時刻。必要な用途は `license` と `lease`、利用ログがあるときは `log` も含む。用途ごとに、いちばん遅く終わる証明書で判断する。 |
| `expiresSoon` | 真偽値 | `expiresAt` までの残りが 30 日より短いかどうか。 |

```json
{
  "keyId": "kIdExAmPlEsIgNiNgKeY0000000000000000000001A",
  "uses": [
    "license",
    "lease",
    "log"
  ],
  "expiresAt": "2027-11-01T11:27:44Z",
  "expiresSoon": false
}
```

エラー: すべてのルートに共通のものだけ。

監査ログのアクション: `signingkey.get`。

### ライセンスのルート

ライセンスの変更は、サービスが次に発行するライセンスファイルやリースから反映される。すでにデバイスにあるファイルは、有効期限まで元の値のままだ。

#### `POST /v1/admin/licenses`

ライセンスと、そのライセンスキーを作る。

権限: `license.create`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `policyId` | 文字列 | はい | ポリシーの ID。ライセンスは、そのポリシーの製品のものになる。 |
| `name` | 文字列 | はい | 1 から 256 文字。たとえば顧客名。 |
| `features` | 配列 | はい | 機能ごとに 1 つ、次の項目を持つオブジェクトを 1 から 256 個。 |
| `features[].code` | 文字列 | はい | ポリシーの製品の機能の `code`。同じコードは 1 回だけ。 |
| `features[].ver` | 文字列または `null` | いいえ | `>=2.0 <3.0` のような、64 文字以下のバージョンの範囲。省くか `null` なら、すべてのバージョン。 |
| `features[].seats` | 数値または `null` | `server` のポリシーか、`floating` が `cloud` のポリシーでは必須 | 1 から 1,000,000。それらのポリシーでは必須で、ほかのポリシーでは指定できない。 |
| `features[].expiresAt` | 時刻または `null` | いいえ | 機能ごとの有効期限。現在より後で、ライセンスの有効期限以前の時刻。省くか `null` なら、有効期限はない。`fromFirstActivation` では、時刻を指定できない。 |
| `maxActivations` | 数値または `null` | いいえ | 1 から 100,000。省くか `null` なら、ポリシーの `maxActivations` を使う。 |
| `expiresAt` | 時刻または `null` | いいえ | `fromIssue` では有効期限で、現在より後の時刻。省くか `null` なら、有効期限は現在にポリシーの `duration` を足した時刻になり、`duration` が `null` なら有効期限はない。`fromFirstActivation` では時刻を指定できず、サービスが最初のアクティベーションか最初のクラウドのリースのときに有効期限を決める。 |
| `meta` | 文字列のオブジェクト | いいえ | 32 個まで。UTF-8 で、各キーは 1 から 64 バイト、各値は 1,024 バイトまで。`null` は `ELM_MALFORMED`。 |

`201` で、`key` を含む[ライセンス](#ライセンス)を返す。キーはこの時点で顧客に渡すこと。もう一度表示することはない。

この例では、[`POST /v1/admin/policies`](#post-v1adminpolicies) の例のポリシーに次の本文を送った。そのポリシーの `expirationBasis` は `fromIssue` で、`duration` は 31,622,400 秒（366 日）だ。

```json
{
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {"code": "pro", "ver": ">=2.0 <3.0"},
    {"code": "export"}
  ],
  "meta": {"customer": "C-1042"}
}
```

`expiresAt` を送らなかったので、レスポンスの有効期限は `createdAt` に 366 日を足した時刻になる。

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {
      "code": "pro",
      "ver": "\u003E=2.0 \u003C3.0",
      "seats": null,
      "expiresAt": null
    },
    {
      "code": "export",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": null,
  "state": "active",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2027-11-02T11:27:59Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {
    "customer": "C-1042"
  },
  "deactivations": 0,
  "key": "ELM-A1B2C-3D4E5-F6G7H-J8K9M"
}
```

このルートは冪等キーを受け取らない。たとえばネットワークの障害でレスポンスが失われたあとに同じリクエストを送り直すと、別の ID とキーを持つ 2 つ目のライセンスができる。`meta` は送ったとおりに保存し、ライセンスと一緒に返す。`GET /v1/admin/licenses` は `q` で名前からライセンスを検索できるが、`meta` で絞り込む手段はない。

エラー: `policyId` がどのポリシーのものでもないとき、値が範囲外のとき、機能のコードがほかの製品のものか 2 回指定されたとき、`seats` が必須なのにないか指定できないのにあるとき、有効期限が上のルールに反するときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `license.create`。キーは監査ログに書かない。

#### `GET /v1/admin/licenses`

ライセンスを一覧する。絞り込みの条件は組み合わせられる。

権限: `license.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit`、`after` | [ページング](#ページング)を参照。`after` はライセンス ID。 |
| `productId` | この製品のライセンスだけ。 |
| `policyId` | このポリシーのライセンスだけ。 |
| `state` | この状態のライセンスだけ。`active`、`suspended`、`expired`、`revoked` のいずれか。 |
| `q` | 名前がこのテキストを含むライセンスだけ。制御文字を含まない 1 から 100 文字。ASCII の英字は大文字と小文字を区別せずに照合する。`%` と `_` は、その文字そのものとだけ一致する。 |
| `keyHash` | このライセンスキーのライセンスだけ。キーを大文字にして `-` を除いた形（`ELMA1B2C3D4E5F6G7HJ8K9M`）にしてから計算した SHA-256 を、base64url（43 文字）で書いたもの。キーが `ELM-A1B2C-3D4E5-F6G7H-J8K9M` なら `gT9XDOHEzUlMi2gDHAs39ChMHVRb1Nwvg1k0tupdFaY` になる。キーそのものは送らないので、URL のログにキーが残らない。 |

`200` で、`key` を含まない[ライセンス](#ライセンス)のページを返す。この例は `GET /v1/admin/licenses?limit=1&q=fabrikam` のレスポンスで、ほかに一致するライセンスがないので `next` はない。このライセンスは、`{"productId": "01960000-0000-7000-8000-000000000201", "name": "Server", "licenseKind": "server", "floating": "onPremises"}` で作った `duration` が `null` のポリシーのもとで、`{"policyId": "01960000-0000-7000-8000-000000000403", "name": "Fabrikam server", "features": [{"code": "pro", "seats": 5}]}` で作った。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000502",
      "productId": "01960000-0000-7000-8000-000000000201",
      "policyId": "01960000-0000-7000-8000-000000000403",
      "name": "Fabrikam server",
      "features": [
        {
          "code": "pro",
          "ver": null,
          "seats": 5,
          "expiresAt": null
        }
      ],
      "maxActivations": null,
      "state": "active",
      "createdAt": "2026-11-01T11:28:00Z",
      "expiresAt": null,
      "suspendedAt": null,
      "revokedAt": null,
      "meta": {},
      "deactivations": 0
    }
  ]
}
```

エラー: パラメーターが形に合わないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `license.list`。

#### `GET /v1/admin/licenses/{id}`

ライセンスを 1 つ返す。

権限: `license.read`。

`200` で、`key` を含まない[ライセンス](#ライセンス)を返す。[`POST /v1/admin/licenses`](#post-v1adminlicenses) の例で作ったライセンスでは、レスポンスはその例のレスポンスから `key` を除いたものだった。

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。

監査ログのアクション: `license.get`。

#### `PATCH /v1/admin/licenses/{id}`

ライセンスを変更する。

権限: `license.update`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `name` | 文字列 | いいえ | 1 から 256 文字。`null` は `ELM_MALFORMED`。 |
| `maxActivations` | 数値または `null` | いいえ | 1 から 100,000。`null` はライセンスごとの上限を取り除き、ポリシーの `maxActivations` がまた適用される。 |
| `meta` | 文字列のオブジェクト | いいえ | `meta` 全体を置き換える。制限は `POST /v1/admin/licenses` と同じ。`null` は `ELM_MALFORMED`。 |
| `features` | 配列 | いいえ | すべての機能を置き換える。ルールは `POST /v1/admin/licenses` と同じで、ライセンスの現在の有効期限で判断する。`null` は `ELM_MALFORMED`。 |
| `expiresAt` | — | — | ここでは変更しない。送ると `ELM_INVALID_ARGUMENT`。`POST /v1/admin/licenses/{id}/extend` を使う。 |
| `productId`、`policyId` | — | — | 変更できない。どちらかを送ると `ELM_INVALID_ARGUMENT`。 |

`200` で、変更した[ライセンス](#ライセンス)を返す。この例では、`POST` の例のライセンスを変更した。送った本文:

```json
{"maxActivations": 5, "meta": {"customer": "C-1042", "order": "SO-77"}}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Contoso Ltd.",
  "features": [
    {
      "code": "pro",
      "ver": "\u003E=2.0 \u003C3.0",
      "seats": null,
      "expiresAt": null
    },
    {
      "code": "export",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": 5,
  "state": "active",
  "createdAt": "2026-11-01T11:27:59Z",
  "expiresAt": "2027-11-02T11:27:59Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {
    "customer": "C-1042",
    "order": "SO-77"
  },
  "deactivations": 0
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。値がルールに反するとき、`expiresAt`、`productId`、`policyId` を送ったときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `license.update`。

#### `POST /v1/admin/licenses/{id}/suspend`

有効なライセンスか期限切れのライセンスを一時停止する。再開するまで、新しいライセンスファイルもリースも得られない。一時停止中のライセンスを一時停止しても何も変わらず、同じレスポンスを返す。

権限: `license.update`。

`200` で、`state` が `suspended` になり `suspendedAt` が入った[ライセンス](#ライセンス)を返す。この例では `PATCH` の例のライセンスを一時停止した。レスポンスは、そのライセンスの次の項目が変わったものだ。

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "state": "suspended",
  "suspendedAt": "2026-11-01T11:28:02Z"
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。ライセンスが失効しているときは `ELM_CONFLICT`（409）。

監査ログのアクション: `license.suspend`。

#### `POST /v1/admin/licenses/{id}/resume`

一時停止中のライセンスを再開する。状態は `active` になり、有効期限を過ぎていれば `expired` になる。一時停止していないライセンスを再開しても何も変わらず、同じレスポンスを返す。

権限: `license.update`。

`200` で、`suspendedAt` が `null` の[ライセンス](#ライセンス)を返す。この例では、上の例で一時停止したライセンスを再開した。レスポンスは `PATCH` の例のライセンスに戻り、次の項目を持つ。

```json
{
  "id": "01960000-0000-7000-8000-000000000501",
  "state": "active",
  "suspendedAt": null
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。ライセンスが失効しているときは `ELM_CONFLICT`（409）。

監査ログのアクション: `license.resume`。

#### `POST /v1/admin/licenses/{id}/extend`

有効なライセンスか期限切れのライセンスの有効期限を延ばす。期限切れのライセンスは、また有効になる。機能ごとの有効期限は変わらない。それらは `PATCH` で変更する。

権限: `license.update`。

| 名前 | 型 | 必須 | 範囲と意味 |
| --- | --- | --- | --- |
| `expiresAt` | 時刻 | はい | 新しい有効期限。現在の有効期限と現在時刻の両方より後。`null` は `ELM_MALFORMED`。 |

`200` で[ライセンス](#ライセンス)を返す。この例では別のライセンスを延長した。そのライセンスは、`POST` の例と同じポリシーのもとで `{"policyId": "01960000-0000-7000-8000-000000000401", "name": "Tailspin site", "features": [{"code": "pro"}]}` によって 2026-11-01T11:28:01Z に作ったもので、有効期限は 2027-11-02T11:28:01Z だった。送った本文:

```json
{"expiresAt": "2028-11-01T00:00:00Z"}
```

レスポンス:

```json
{
  "id": "01960000-0000-7000-8000-000000000504",
  "productId": "01960000-0000-7000-8000-000000000201",
  "policyId": "01960000-0000-7000-8000-000000000401",
  "name": "Tailspin site",
  "features": [
    {
      "code": "pro",
      "ver": null,
      "seats": null,
      "expiresAt": null
    }
  ],
  "maxActivations": null,
  "state": "active",
  "createdAt": "2026-11-01T11:28:01Z",
  "expiresAt": "2028-11-01T00:00:00Z",
  "suspendedAt": null,
  "revokedAt": null,
  "meta": {},
  "deactivations": 0
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。`expiresAt` が現在の有効期限と現在時刻の両方より後になっていないときは `ELM_INVALID_ARGUMENT`（400）。ライセンスが一時停止中か失効しているとき、または有効期限がないときは `ELM_CONFLICT`（409）。

監査ログのアクション: `license.extend`。

#### `POST /v1/admin/licenses/{id}/revoke`

ライセンスを完全に失効させる。新しいライセンスファイルもリースも得られなくなり、再開も延長もできない。失効したライセンスを失効させても何も変わらず、同じレスポンスを返す。

権限: `license.revoke`。

`200` で、`state` が `revoked` になり `revokedAt` が入った[ライセンス](#ライセンス)を返す。この例では、上の延長の例のライセンスを失効させた。レスポンスは、そのライセンスの次の項目が変わったものだ。

```json
{
  "id": "01960000-0000-7000-8000-000000000504",
  "state": "revoked",
  "revokedAt": "2026-11-01T11:28:04Z"
}
```

そのあとにこのライセンスを再開しようとすると、`409` と `ELM_CONFLICT` になった。[`GET /v1/admin/audit`](#get-v1adminaudit) の例に、そのことが出ている。

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。

監査ログのアクション: `license.revoke`。

#### `POST /v1/admin/licenses/{id}/file`

ポリシーの `binding` が `unbound` のライセンスについて、ライセンスファイルに署名して返す。このようなライセンスはアクティベーションをしないので、このファイルが顧客にライセンスを届ける手段になる。トークンには、インストール鍵、フィンガープリント、シーケンス番号が入らない。トークンの有効期限はライセンスの有効期限と同じで、ライセンスに有効期限がなければトークンにもない。有効期限があるときの猶予期間は、ポリシーの `gracePeriod` だ。

権限: `license.file`。

`200` でライセンスファイルを返す（`application/json` で、`Content-Disposition: attachment; filename="<license ID>.elmlic"` が付く）。そのメンバーは[トークンとファイルの形式](formats.md#ライセンスファイル-elmlic)に挙げ、何のためのファイルかは[ライセンスファイルと検証](../explanation/license-files-and-verification.md#ライセンスファイル)で説明している。

```json
{
  "format": "elasticlm-license",
  "version": 1,
  "license": "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU",
  "certificates": [
    "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"
  ]
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。ポリシーが `unbound` でないとき、ライセンスが一時停止中、失効、期限切れのときは `ELM_CONFLICT`（409）。たとえば起動後に鍵証明書の期間が終わったなどの理由で、サービスがいま署名できないときは `ELM_INTERNAL_ERROR`（500）。

監査ログのアクション: `license.file`。トークンの ID と有効期限も記録する。

### アクティベーションとトライアルのルート

#### `GET /v1/admin/licenses/{id}/activations`

ライセンスのアクティベーションを一覧する。

権限: `activation.read`。

クエリパラメーター: `limit` と `after`（アクティベーション ID）。

`200` で[アクティベーション](#アクティベーション)のページを返す。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000601",
      "licenseId": "01960000-0000-7000-8000-000000000501",
      "keyId": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "fingerprints": [
        "3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4"
      ],
      "client": null,
      "app": "2.1.0",
      "seq": 1,
      "blockedAt": null,
      "createdAt": "2026-11-01T11:29:19Z",
      "updatedAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。`limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `activation.list`。

#### `GET /v1/admin/activations/{id}`

アクティベーションを 1 つ返す。

権限: `activation.read`。

`200` で[アクティベーション](#アクティベーション)を返す。形は、上の一覧の例の項目と同じだ。

エラー: その ID のアクティベーションがないときは `ELM_ACTIVATION_NOT_FOUND`（404）。

監査ログのアクション: `activation.get`。

#### `POST /v1/admin/activations/{id}/deactivate`

デバイスを使わずにアクティベーションを取り除く。たとえば、デバイスを紛失したときに使う。ライセンスのアクティベーションの上限に、空きが 1 つできる。クローンとして止めたアクティベーションにも使え、ポリシーの `deactivationLimit` には数えない。デバイスにあるライセンスファイルは、有効期限まで有効なままだ（[アクティベーション](../explanation/activation.md#アクティベーション解除)を参照）。

権限: `activation.deactivate`。

本文のない `204` を返す。

エラー: その ID のアクティベーションがないときは `ELM_ACTIVATION_NOT_FOUND`（404）。

監査ログのアクション: `activation.deactivate`。ライセンス ID と、デバイスの鍵のサムプリントも記録する。

#### `GET /v1/admin/trials`

トライアルの記録を一覧する。

権限: `trial.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit`、`after` | [ページング](#ページング)を参照。`after` はトライアルの ID。 |
| `productId` | この製品のトライアルだけ。 |

`200` で[トライアル](#トライアル)のページを返す。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000701",
      "productId": "01960000-0000-7000-8000-000000000201",
      "fingerprints": [
        "9b1e7c3a5d2f4e6081a7c9b3d5e7f901"
      ],
      "createdAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

エラー: `limit`、`after`、`productId` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `trial.list`。

#### `DELETE /v1/admin/trials/{id}`

トライアルの記録を削除する。記録にあったフィンガープリントで、その製品のトライアルをまた始められるようになる。トライアルのライセンスとアクティベーションは残る。

権限: `trial.delete`。

本文のない `204` を返す。

エラー: その ID のトライアルがないときは、すでに削除したときも含めて `ELM_NOT_FOUND`（404）。

監査ログのアクション: `trial.delete`。

### オフラインのルート

#### `POST /v1/admin/offline`

デバイスがサービスに接続できない顧客のために、オフラインのリクエストファイルを処理する。ファイルは、アクティベーションかトークンの更新のリクエスト（`.elmreq`）か、アクティベーション解除のプルーフ（`.elmdeact`）だ。サービスは、デバイス自身のオンラインのリクエストと同じ確認をし、資格情報の要らない公開のルート [`POST /v1/offline`](device-protocol.md#post-v1offline) と同じレスポンスを返す。同じファイルをもう一度送っても、ライセンスとアクティベーションがまだそれを許すあいだは、同じレスポンスになる。オフラインのアクティベーションの仕組みは[アクティベーション](../explanation/activation.md#ファイルの受け渡しによるオフラインのアクティベーション)に、ファイルの形式は[トークンとファイルの形式](formats.md#オフラインのリクエスト-elm-actreqjws)にある。

権限: `activation.offline`。インストール鍵ごとのレート制限は適用しない。

本文は、ファイルの中身そのもの（リクエストファイルのテキスト）だ。`Content-Type` は確かめない。`elm offline process` は `application/jose` を送る。

レスポンス:

- アクティベーションかトークンの更新では、`activationId`（アクティベーション ID）と `license`（署名したライセンスファイル）を持つ `200`。`elm offline process` は、この本文をそのままレスポンスファイル（`.elmresp`）として保存し、顧客はそれをデバイスに取り込む。
- アクティベーション解除では、本文のない `204`。

```json
{
  "activationId": "01960000-0000-7000-8000-000000000602",
  "license": {
    "format": "elasticlm-license",
    "version": 1,
    "license": "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU",
    "certificates": [
      "eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"
    ]
  }
}
```

エラー:

| 状態 | コード | 条件 |
| --- | --- | --- |
| 400 | `ELM_MALFORMED` | 本文がリクエストファイルでもアクティベーション解除のプルーフでもない、クレームが有効でない、またはリクエストの発行時刻がサービスの時計より 5 分を超えて先にある。 |
| 400 | `ELM_PROOF_INVALID` | ファイルの署名を検証できない、またはファイルが、指定したアクティベーションのインストール鍵で署名されていない。 |
| 400 | `ELM_REQUEST_EXPIRED` | リクエストが、有効期間の 7 日を過ぎている。 |
| 400 | `ELM_WRONG_PRODUCT` | ライセンスキーが別の製品のものだ。 |
| 400 | `ELM_FINGERPRINT_UNAVAILABLE` | ポリシーがフィンガープリントを照合するのに、リクエストにフィンガープリントがない。 |
| 403 | `ELM_LICENSE_SUSPENDED`、`ELM_LICENSE_REVOKED`、`ELM_EXPIRED` | ライセンスが一時停止中、失効、期限切れのいずれか。 |
| 404 | `ELM_LICENSE_NOT_FOUND` | そのキーのライセンスがない。 |
| 404 | `ELM_ACTIVATION_NOT_FOUND` | トークンの更新やアクティベーション解除が指定した ID のアクティベーションがない。 |
| 409 | `ELM_ACTIVATION_LIMIT`、`ELM_DEACTIVATION_LIMIT` | ライセンスが、アクティベーションかアクティベーション解除の上限に達した。 |
| 409 | `ELM_CLONE_DETECTED` | インストールのクローンを検出した、またはアクティベーションがクローンとして止められている。 |
| 409 | `ELM_CONFLICT` | ライセンスがマシンに紐付けないライセンスか、クラウドのフローティングのライセンスで、アクティベーションをしない。 |
| 409 | `ELM_REPLAYED` | このリクエストのノンスを使った別のリクエストが、すでにあった。 |
| 500 | `ELM_INTERNAL_ERROR` | サービスがいまライセンスファイルに署名できない。 |

監査ログのアクション: `activation.offline`。拒否したファイルも含めて、ファイルごとに書く。アクティベーションやアクティベーション解除をするファイルは、デバイスの行（`activation.activate` か `activation.deactivate`）も書き、クローンを検出したときは `activation.clone` を書く。これらの行は、呼び出し元を操作した者とし、`channel` を `offline` とする。トークンの更新は、それ自身の行を書かない。

### リースのルート

#### `GET /v1/admin/licenses/{id}/leases`

ライセンスのクラウドのリースと借用のうち、有効期限が切れていないものを一覧する。オンプレミスのリースは顧客の `elm-server` が保持する。[`elm-server` のローカルの管理 API](#elm-server-のローカルの管理-api) を参照。

権限: `lease.read`。

クエリパラメーター: `limit` と `after`（リース ID）。リースはリース ID の順に並ぶ。

`200` で[リース](#リース)のページを返す。

```json
{
  "items": [
    {
      "id": "Xk2bQ9rT4mVn8sLp0wZa1g",
      "feature": "pro",
      "seats": 1,
      "keyId": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "seq": 1,
      "expiresAt": "2026-11-01T11:39:19Z",
      "borrow": false,
      "stopped": false,
      "client": {
        "host": "ws-12",
        "user": "alice"
      },
      "app": "2.1.0",
      "createdAt": "2026-11-01T11:29:19Z"
    }
  ]
}
```

エラー: その ID のライセンスがないときは `ELM_LICENSE_NOT_FOUND`（404）。`limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `lease.list`。

#### `DELETE /v1/admin/leases/{id}`

デバイスを使わずにクラウドのリースか借用を解放し、そのシートを空ける。デバイスは次のリースの更新（[`POST /v1/leases/{id}/renew`](device-protocol.md#post-v1leasesidrenew)）でそれを知り、更新は `ELM_UNKNOWN_LEASE` で失敗する。

権限: `lease.release`。

本文のない `204` を返す。

エラー: その ID を持つ有効期限内のリースがないときは `ELM_UNKNOWN_LEASE`（404）。

監査ログのアクション: `lease.release`。ライセンス ID、機能、シート数も記録する。利用ログが有効なら、`by` が `admin` の `lease.released` か `borrow.returned` の行を利用ログに書く（[ログ](logs.md#elm-service-のイベント)）。

### 利用ログのルート

これらのルートは、リクエストに答えたノードの利用ログを対象にする。ノードとは、実行中の `elm-service` のインスタンス 1 つのことで、`node.name` で名前を付ける（[elm-service の設定](service-configuration.md#node)）。各ノードは自分のファイルを書く。すべてのノードのファイルを集めるのは運用者の役目だ。`usageLog.directory` のないノードでは、3 つのルートはどれも `409` と `ELM_CONFLICT` を返す。ログそのものは[ログ](logs.md#利用ログ)で説明している。

#### `GET /v1/admin/usage-log/files`

このノードの利用ログのファイルを、古い順に一覧する。

権限: `usagelog.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit` | 1 から 200。既定は 50。 |
| `after` | 1 から 255 文字のファイル名。ページは、その名前の次の名前から始まる。 |

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `node` | 文字列 | このノードの名前（`node.name`）。 |
| `items` | 配列 | ファイル。`name`、`size`（バイト数）、`modifiedAt`（最後に書き込んだ時刻）を持つ。 |
| `next` | 文字列 | 次のページの `after`。最後のページにはない。 |

```json
{
  "node": "node-1",
  "items": [
    {
      "name": "usage-node-1-20261101T112758915Z.jsonl",
      "size": 1749,
      "modifiedAt": "2026-11-01T11:28:58Z"
    }
  ]
}
```

エラー: ノードが利用ログを書かないときは `ELM_CONFLICT`（409）。`limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `usagelog.list`。

#### `GET /v1/admin/usage-log/files/{name}`

このノードの利用ログのファイルを 1 つ、そのまま返す。

権限: `usagelog.read`。

`200` で、ファイルの行（`application/x-ndjson`）を返す。返すのは、ダウンロードを始めた時点のファイルの長さまでで、`Content-Length` はその長さだ。転送が途中で失敗すると、サービスはレスポンスを終わらせずに接続を切る。クライアントが受け取るバイトは `Content-Length` より少なくなるので、クライアントは、その一部をファイルの全体と取り違えずに、エラーにする。たとえば、ファイルの最初の数行は次のようになる。

```text
{"type":"chain.start","ts":1793532478,"stream":"usage","node":"node-1","certificates":["eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"]}
{"type":"auth","ts":1793532478,"sig":"eyJhbGciOiJFUzI1NiJ9.eyJleGFtcGxlIjp0cnVlfQ.ZmFrZS1zaWduYXR1cmU"}
{"type":"service.started","ts":1793532478,"node":"node-1"}
```

エラー: ノードが利用ログを書かないときは `ELM_CONFLICT`（409）。このノードにその名前のファイルがないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `usagelog.get`。

#### `POST /v1/admin/usage-log/seal`

それまでに数えた拒否を書き込み、まだ署名していない行にすぐ署名する。タイマーが 1 分ごとにしているのと同じ処理だ。

権限: `usagelog.seal`。

本文のない `204` を返す。

エラー: ノードが利用ログを書かないときは `ELM_CONFLICT`（409）。

監査ログのアクション: `usagelog.seal`。

### 管理者とセッションのルート

管理者は、`elm-service setup` が作る（[`bootstrapOwner`](service-configuration.md#bootstrapowner) の場合）ほか、OIDC での最初のサインインのときに作られる。管理者を作るルートも削除するルートもない。

#### `GET /v1/admin/oidc`

`elm admin login` に必要な OIDC の設定を返す。シークレットは返さない。

権限: なし。資格情報は要らない。適用するのは送信元ごとの制限だけだ。

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `issuer` | 文字列 | `oidc.issuer`。 |
| `clientId` | 文字列 | `oidc.cliClientId`。 |
| `scopes` | 文字列の配列 | `oidc.cliScopes`。 |

```json
{
  "issuer": "https://idp.example.com",
  "clientId": "elm-cli",
  "scopes": [
    "openid",
    "offline_access"
  ]
}
```

エラー: `oidc.issuer` を設定していないときは `ELM_NOT_FOUND`（404）。`ELM_RATE_LIMITED`（429）。

監査ログの行は書かない。

#### `GET /v1/admin/admins`

管理者を一覧する。

権限: `admin.read`。

クエリパラメーター: `limit` と `after`（管理者の ID）。

`200` で[管理者](#管理者)のページを返す。

```json
{
  "items": [
    {
      "id": "01960000-0000-7000-8000-000000000001",
      "issuer": "https://idp.example.com",
      "subject": "owner-1",
      "role": "owner",
      "enabled": true,
      "createdAt": "2026-11-01T11:27:58Z",
      "lastLoginAt": null
    }
  ]
}
```

エラー: `limit` か `after` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `admin.list`。

#### `POST /v1/admin/admins/{id}/disable`

管理者を無効にする。その管理者の API キー、アクセストークン、セッションはすぐに使えなくなり、管理画面のセッションは削除される。無効にした管理者を無効にしても何も変わらず、同じレスポンスを返す。

権限: `admin.update`。

本文のない `204` を返す。

エラー: その ID の管理者がないときは `ELM_NOT_FOUND`（404）。呼び出し元自身の管理者、`bootstrapOwner`、有効な最後の `owner` を指定したときは `ELM_CONFLICT`（409）。

監査ログのアクション: `admin.disable`。

#### `POST /v1/admin/admins/{id}/enable`

管理者を有効にする。有効な管理者を有効にしても何も変わらず、同じレスポンスを返す。

権限: `admin.update`。

本文のない `204` を返す。

エラー: その ID の管理者がないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `admin.enable`。

#### `GET /v1/admin/me`

呼び出し元の管理者と、このリクエストの権限を返す。

権限: 認証された呼び出し元なら誰でもよい。

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `id` | 文字列 | 管理者の ID。 |
| `issuer` | 文字列 | OIDC の発行者。 |
| `subject` | 文字列 | その発行者における、`oidc.subjectClaim` が指すクレームの値（既定は `sub`）。 |
| `role` | 文字列 | `owner`、`admin`、`support`、`viewer`、`none` のいずれか。 |
| `permissions` | 文字列の配列 | このリクエストが持つ権限。[ロールと権限](#ロールと権限)の順に並ぶ。API キーでは、キーの権限のうちロールも持つものだ。 |

```json
{
  "id": "01960000-0000-7000-8000-000000000001",
  "issuer": "https://idp.example.com",
  "subject": "owner-1",
  "role": "owner",
  "permissions": [
    "apikey.read",
    "apikey.create",
    "apikey.revoke",
    "audit.read",
    "product.read",
    "product.write",
    "policy.read",
    "policy.write",
    "license.read",
    "license.create",
    "license.update",
    "license.revoke",
    "license.file",
    "signingkey.read",
    "activation.read",
    "activation.deactivate",
    "activation.offline",
    "trial.read",
    "trial.delete",
    "lease.read",
    "lease.release",
    "usagelog.read",
    "usagelog.seal",
    "admin.read",
    "admin.update",
    "session.read",
    "session.revoke"
  ]
}
```

エラー: すべてのルートに共通のものだけ。

成功した呼び出しは監査ログの行を書かない。拒否した呼び出しは `admin.me` を書く。

#### `GET /v1/admin/sessions`

まだ終わっていない管理画面のセッションを一覧する。アイドル時間か終了時刻を過ぎたセッションは、削除される前でも一覧に出ない。

権限: `session.read`。

| クエリパラメーター | 範囲と意味 |
| --- | --- |
| `limit`、`after` | [ページング](#ページング)を参照。`after` はセッションの ID。 |
| `adminId` | この管理者のセッションだけ。 |

`200` で[セッション](#セッション)のページを返す。この例は管理画面のサインインが無効なサービスで得たもので、セッションはない。

```json
{
  "items": []
}
```

エラー: `limit`、`after`、`adminId` が正しくないときは `ELM_INVALID_ARGUMENT`（400）。

監査ログのアクション: `session.list`。

#### `DELETE /v1/admin/sessions/{id}`

管理画面のセッションを終わらせる。そのセッションを持つブラウザーは、次のリクエストでサインアウトされる。

権限: `session.revoke`。

本文のない `204` を返す。

エラー: その ID を持つ終わっていないセッションがないときは `ELM_NOT_FOUND`（404）。

監査ログのアクション: `session.revoke`。

## `elm-server` のローカルの管理 API

`elm-server` のローカルの管理 API は、サーバーのマシン上で `elm server` のコマンドが使う。この API がリクエストに答えるのは、サーバーのデータフォルダーにあるソケットのファイル `admin.sock`（Unix ドメインソケット。同じマシンのプログラムどうしが、ポートの代わりに接続に使うファイルだ）で受けたときだけで、そのソケットの上で平文の `http` を使う。Windows、Linux、macOS のどれでも同じだ。TCP のポートも設定もない（[elm-server の設定](server-configuration.md#ポート)を参照）。接続はマシンの外に出ないので、暗号化しない。接続を守るのは、データフォルダーのアクセス権と管理者トークンだ（[セキュリティモデル](../explanation/security-model.md#顧客の管理者向け)を参照）。ここではリースのプロトコルを提供しない。また、サーバーの `https` のアドレスで `/admin/…` にリクエストすると、本文のない `404` になる。

認証は `Authorization: Bearer <token>` だ。トークンは管理者トークンで、サーバーのデータフォルダーの `admin.token` に保存した 32 バイトの乱数を、パディングなしの base64url（43 文字）で書いたものだ。サーバーは、起動のたびに新しいトークンを作ってファイルを置き換える。そのため、自分のプログラムでは、サーバーを起動するたびにファイルを読み直す。前の起動のトークンは `401` になる。Linux と macOS では、ファイルは 32 バイトをそのまま持ち、所有者だけが読み書きできる。Windows では、ファイルは DPAPI でマシンに対して保護したバイト列を持つので、スクリプトはそのバイト列をそのままでは使えない。下を参照。ヘッダーは、ここに書いたとおり `Bearer` と 1 つの空白で始まらなければならない。スキーム名は大文字と小文字を区別する。トークンがないか正しくないときは `401` と `ELM_UNAUTHORIZED` になる。このファイルを読める人は、サーバーを管理できる（[elm-server の設定](server-configuration.md#データフォルダー)を参照）。

Linux と macOS では、自分のプログラムからは、サーバーのユーザーとして、ソケットのファイルを通して API を呼び出す。curl では次のようになる。1 行目はファイルからトークンを作り、2 行目はリクエストを送る。

```text
token=$(base64 < "<data folder>/admin.token" | tr '+/' '-_' | tr -d '=\n')
curl --unix-socket "<data folder>/admin.sock" -H "Authorization: Bearer $token" http://elm-server.invalid/admin/v1/status
```

curl は、リクエストをソケットのファイルに送る。URL のホストは使われない。`elm-server.invalid` は、リクエストの `Host` ヘッダーに入るだけで、サーバーはこのヘッダーを見ない。

Windows では、スクリプトからは、代わりに `elm server` のコマンドに `--json` を付けて使う（[サーバー管理のオプション](elm-cli.md#サーバー管理のオプション)）。このとき `elm server status`、`elm server leases`、`elm server reload` は、API のレスポンスを受け取ったまま表示する。

どのルートにも、次のことが当てはまる。

- ロールもページングもない。`allow` と `deny` の設定と `rateLimit.perIp` は適用しない。
- 本文を読むルートはない。
- エラーは problem 形式のレスポンス（`application/problem+json`）で、項目は[エラー](#エラー)で説明したものだ。
- 正しいトークンのあるリクエストは、どれも利用ログに `admin.action` の行を書く。正しいトークンのないリクエストは、1 分ごとに件数を数えて `admin.denied` の行に書く（[ログ](logs.md#elm-server-のイベント)）。
- 時刻は Unix 秒で表す。

どのルートにもトークンが要る。

| ルート | 内容 |
| --- | --- |
| [`GET /admin/v1/status`](#get-adminv1status) | サーバーの状態を取得する |
| [`GET /admin/v1/leases`](#get-adminv1leases) | リースと借用を一覧する |
| [`DELETE /admin/v1/leases/{id}`](#delete-adminv1leasesid) | リースを解放する |
| [`POST /admin/v1/license/reload`](#post-adminv1licensereload) | サーバーライセンスのファイルを読み直す |
| [`POST /admin/v1/log/seal`](#post-adminv1logseal) | 利用ログにすぐ署名する |

### `GET /admin/v1/status`

サーバーの状態を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `product` | 文字列 | 製品 ID（設定 `product`）。 |
| `license` | 文字列 | サーバーライセンスの ID。 |
| `licenseToken` | 文字列 | 使用中のサーバーライセンスのファイルのトークン ID（`jti`）。22 文字の base64url。 |
| `licenseExpires` | 数値 | サーバーライセンスの有効期限。Unix 秒で表す。ないときは項目がない。 |
| `key` | 文字列 | サーバーの鍵 ID。サーバー自身のインストール鍵のサムプリント。 |
| `recovering` | 真偽値 | サーバーが、起動後のリースの再登録の期間にあるかどうか。この期間に、クライアントがリースを再登録する（[フローティングライセンス](../explanation/floating-licenses.md#再起動とリースの再登録)）。 |
| `recoveryEnds` | 数値 | リースの再登録の期間が終わる時刻。Unix 秒で表す。 |
| `leases` | 数値 | 保持しているリースと借用の数。有効期限が切れたが、サーバーがまだ取り除いていないものも数える。 |
| `features` | 配列 | サーバーライセンスの機能ごと、およびライセンスがもう付与していないがリースがまだ保持している機能ごとに、`feature`（機能 ID）、`seats`（購入したシート数）、`allowed`（超過分を含めたシート数）、`inUse`（借用を含めて保持しているシート数）、`borrowed`（借用が保持しているシート数）を持つ。 |
| `autoRefresh` | 真偽値 | `elm server activate` が保持するアクティベーションを通して、サーバーが `elm-service` からサーバーライセンスを自分で更新するかどうか。 |

`200` を返す。

```json
{
  "product": "acme-cad",
  "license": "01960000-0000-7000-8000-000000000502",
  "licenseToken": "jTiExAmPlE000000000001",
  "licenseExpires": 1796124632,
  "key": "sErVeRkEyExAmPlE00000000000000000000000001A",
  "recovering": false,
  "recoveryEnds": 1793533250,
  "leases": 2,
  "features": [
    {
      "feature": "pro",
      "seats": 5,
      "allowed": 5,
      "inUse": 3,
      "borrowed": 2
    }
  ],
  "autoRefresh": false
}
```

エラー: `ELM_UNAUTHORIZED`（401）。

### `GET /admin/v1/leases`

サーバーが保持しているリースと借用を一覧する。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `leases` | 配列 | リースごとに 1 つ、次の項目を持つオブジェクト。 |
| `leases[].id` | 文字列 | リース ID。 |
| `leases[].feature` | 文字列 | 機能 ID。 |
| `leases[].seats` | 数値 | 保持しているシート数。 |
| `leases[].key` | 文字列 | クライアントのインストール鍵のサムプリント。 |
| `leases[].expires` | 数値 | リースの更新がなければ有効期限が切れる時刻。Unix 秒で表す。 |
| `leases[].borrow` | 真偽値 | 借用かどうか。 |
| `leases[].stopped` | 真偽値 | クローンを検出して止めたかどうか。 |

`200` を返す。

```json
{
  "leases": [
    {
      "id": "Xk2bQ9rT4mVn8sLp0wZa1g",
      "feature": "pro",
      "seats": 1,
      "key": "q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M",
      "expires": 1793533857,
      "borrow": false,
      "stopped": false
    },
    {
      "id": "Bw7nR2cK5pQx9LmT3vYz0A",
      "feature": "pro",
      "seats": 2,
      "key": "r8W2zXd1mKn3oSuB9tLeF5gHiVjPqXaY2cO6wD8fZ7N",
      "expires": 1793619657,
      "borrow": true,
      "stopped": false
    }
  ]
}
```

エラー: `ELM_UNAUTHORIZED`（401）。

### `DELETE /admin/v1/leases/{id}`

サーバー側でリースを解放し、そのシートを空ける。解放してもクライアントは止まらない。クライアントの次のリースの更新（[`POST /v1/leases/{id}/renew`](device-protocol.md#elm-server-の-post-v1leasesidrenew)）は `ELM_UNKNOWN_LEASE` を受け取り、クライアントはリースを再登録する（[`POST /v1/leases/reclaim`](device-protocol.md#post-v1leasesreclaim)）。シートが空いていれば、再登録でまたシートを得る。借用はこの方法では解放できない。借用は、有効期限で終わるか、クライアントが返却したときに終わる。

本文のない `204` を返す。

エラー: `ELM_UNAUTHORIZED`（401）。サーバーがその ID のリースを保持していないときは `ELM_UNKNOWN_LEASE`（404）。借用には `ELM_RETURN_NOT_ALLOWED`（409）。

```json
{
  "type": "about:blank",
  "title": "ELM_RETURN_NOT_ALLOWED",
  "status": 409,
  "detail": "A borrow is not released by the administrator; it ends at its expiry.",
  "code": "ELM_RETURN_NOT_ALLOWED"
}
```

### `POST /admin/v1/license/reload`

再起動せずに、サーバーにライセンスファイル（設定 `license`）を読み直させ、それを使わせる。`elm server activate`、`refresh`、`import` は、新しいファイルを書いたあとにこのルートを呼び出す。

`200` を返す。

| 項目 | 型 | 意味 |
| --- | --- | --- |
| `license` | 文字列 | いま使っているサーバーライセンスの ID。 |
| `licenseToken` | 文字列 | いま使っているサーバーライセンスのファイルのトークン ID（`jti`）。 |

```json
{
  "license": "01960000-0000-7000-8000-000000000502",
  "licenseToken": "jTiExAmPlE000000000001"
}
```

エラー: `ELM_UNAUTHORIZED`（401）。新しいファイルを使えないとき、サーバーはそれまでのライセンスを使い続け、失敗した確認のコードを `409` で返す。そのコードがほかの場面でどの状態を持つかには関係しない。[エラーコード](error-codes.md)では、これらのコードの状態に「`elm-server` のライセンスの再読み込みでは 409」と書いている。新しいファイルには、起動時と同じ確認をする。`product` の製品のサーバーライセンスで、このサーバーのインストール鍵に紐付き、ライセンスがマシンフィンガープリントを指定していればそれにも紐付いていなければならない（[ライセンスファイルと検証](../explanation/license-files-and-verification.md#確かめる順序)を参照）。

| コード | 条件 |
| --- | --- |
| `ELM_LICENSE_NOT_FOUND` | ファイルがない。 |
| `ELM_MALFORMED` | ファイルを読めない、64 KiB より大きい、または有効なライセンスファイルでない。 |
| `ELM_BAD_SIGNATURE`、`ELM_UNTRUSTED_KEY`、`ELM_KEY_REVOKED`、`ELM_CERTIFICATE_EXPIRED` | 署名か証明書が、サーバーの信頼の設定で検証に失敗する。 |
| `ELM_CLOCK_TAMPERED` | ライセンスの発行時刻が未来にある、またはサーバーの時計が戻された。 |
| `ELM_EXPIRED` | ライセンスが有効期限と猶予期間を過ぎている。 |
| `ELM_WRONG_PRODUCT`、`ELM_WRONG_KIND` | ライセンスが別の製品のものか、サーバーライセンスでない。 |
| `ELM_WRONG_INSTALLATION` | ライセンスが別のインストール鍵に紐付いている。 |
| `ELM_FINGERPRINT_UNAVAILABLE`、`ELM_FINGERPRINT_MISMATCH` | マシンフィンガープリントを計算できない、またはライセンスと一致しない。 |
| `ELM_INTERNAL_ERROR` | 借用の記録に新しいライセンスを記録できない。 |

### `POST /admin/v1/log/seal`

サーバーの利用ログのまだ署名していない行に、すぐ署名する。

本文のない `204` を返す。

エラー: `ELM_UNAUTHORIZED`（401）。正しいトークンがなければ、この API のどのルートも次のレスポンスを返す。

```json
{
  "type": "about:blank",
  "title": "ELM_UNAUTHORIZED",
  "status": 401,
  "detail": "The administrator token is missing or wrong.",
  "code": "ELM_UNAUTHORIZED"
}
```
