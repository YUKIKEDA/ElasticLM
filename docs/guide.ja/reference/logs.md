---
sidebar_position: 5
---

# ログ

このページには、ElasticLM のログに何が入るかを載せる。対象は、`elm-server` と `elm-service` の署名付きの利用ログ、`elm-server` の署名付きの借用の記録、`elm-service` のハッシュでつながった監査ログだ。ファイル、ファイルの行とデータベースの行の形式、すべてのイベントとアクション、各ログの検証方法を示す。読者は、これらのログを読む、保管する、引き渡す管理者と、ログの改ざんを調べる人だ。署名が何を防ぎ、何を防がないかは、[セキュリティモデル](../explanation/security-model.md)で説明している。最初に、署名のない運用ログを載せる。

## 運用ログ

`elm-server` と `elm-service` は、動いているあいだ、自分の処理についてのメッセージを .NET のコンソールロガーで標準出力に書く。このログには署名がなく、プログラムはこのログをファイルに残さない。残すには、プログラムを起動するサービスマネージャーやコンテナランタイムなどで、標準出力を記録する。

各メッセージは 2 行からなる。1 行目はレベル（`dbug`、`info`、`warn`、`fail`、`crit` のいずれか）、カテゴリ、イベント番号で、2 行目は字下げしたメッセージだ。たとえば次のようになる。

```text
info: elm-server[2]
      server.stopped: 0 leases were not returned
```

| カテゴリ | メッセージ |
| --- | --- |
| `elm-server` | 起動（ポート、公開 URL、鍵 ID、サーバーライセンス ID、リースの再登録の期間の終わり）と停止。リースの取得、返却、再登録のそれぞれ。見つかったクローン。管理リクエスト。ライセンスの読み直しと、ライセンスの更新の失敗。書き込めない利用ログと、起動時にチェーンを続けられなかった利用ログ。利用ログのファイルや借用の記録の名前を持つが、実在する日時を表していないファイル。書き込めない借用の記録。借用の記録の退避。片付けの処理の失敗。 |
| `elm-service` | 起動（アドレス、データベース、署名鍵と、その証明書の期限）と停止。適用したデータベースのスキーマのバージョン。30 日以内に期限が切れる署名鍵の鍵証明書。利用ログの場所。削除した監査ログの行と管理画面のセッション。定期的な処理とデータベースへのリクエストの失敗。署名できなかったライセンスファイルやリース。書き込めない利用ログと、起動時にチェーンを続けられなかった利用ログ。利用ログのファイルの名前を持つが、実在する日時を表していないファイル。管理画面のサインインに使う鍵を作ったこと、または作らなかったこと。長くサインインしていない管理者の API キーを拒否したこと。拒否した管理画面のサインイン。接続できない ID プロバイダー。`Debug` レベルでは、アクセストークンを拒否した理由。 |
| `Microsoft.Hosting.Lifetime` | Web ホストの起動と停止。待ち受けるアドレスとソケットのファイルのそれぞれ（`Now listening on: …`）、`Application started. Press Ctrl+C to shut down.`、ホスティング環境、コンテンツのルートのパス、`Application is shutting down...`。 |
| `Microsoft.Extensions.Hosting.Internal.Host` | プログラムが待ち受けられないときのエラー `Hosting failed to start` と、その例外。 |
| `Microsoft.AspNetCore.Routing.EndpointMiddleware` | エンドポイントに届いたリクエストごとに、そのメソッドとルートのパターンを、処理の前とあとに書く。たとえば `Executing endpoint 'HTTP: GET /v1/nonce'` と `Executed endpoint 'HTTP: GET /v1/nonce'` だ。パスの中の値は、`HTTP: DELETE /admin/v1/leases/{id}` の `{id}` のように、プレースホルダーのまま書く。 |
| `Microsoft.AspNetCore.Server.Kestrel` | リクエストがプログラムの中で失敗したときのエラー `An unhandled exception was thrown by the application.`。接続とリクエストの ID と、例外を添える。 |
| `Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager` | 管理画面のサインインを使う `elm-service` だけ。サインイン用の鍵を作る起動で、`Creating key {…} …` と、警告 `No XML encryptor configured. Key {…} may be persisted to storage in unencrypted form.` を書く。[OIDC でサインインする](../how-to/sign-in-with-oidc.md)で説明しているとおり、サービスはこの鍵を保存する前に自分で暗号化する。 |
| `Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler` | OIDC の管理者を使う `elm-service` だけ。アクセストークンを拒否した理由を、`Failed to validate the token.` と `elm-oidc was not authenticated. Failure message: …` として書く。 |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler` | 管理画面のサインインを使う `elm-service` だけ。ID プロバイダーに送ったサインインのそれぞれ（`AuthenticationScheme: elm-web was challenged.`）と、`/auth/callback` への戻りを拒否した理由を書く。これらのエントリーは、そのリクエストの文字を引用することがある。エラー `Message contains error: '…', error_description: '…', error_uri: '…'.` とエントリー `Error from RemoteAuthentication: …` は、クエリの `error`、`error_description`、`error_uri` を引用する。警告 `The correlation cookie value '…' did not match the expected value '…'.` は、リクエストが送った Cookie の値を引用する。拒否したサインインについての `elm-service` のエントリーも、同じ理由を引用する。 |

リクエストごとの Web ホスト自身のエントリー `Request starting …` と `Request finished …` は書かない。これらはリクエストのパス、クエリ文字列、`Host` ヘッダーを含むので、プログラムは、そのカテゴリ `Microsoft.AspNetCore.Hosting.Diagnostics` を `Warning` より下では書かない。`Logging` の下の設定は、このカテゴリを指すものでも、このカテゴリに当たる `*` のパターンでも、プロバイダー `Console` のものでも、`Default` でも、これを下げられない。`None` のように `Warning` より上のレベルを求める設定は、このカテゴリにもそのまま効く。

既定のレベルでは、最後の行にある `elm-service` のサインインのエントリーを除き、リクエストのパス、クエリ文字列、ヘッダーを含むエントリーはない。`Debug` では、さらに 2 つのカテゴリがリクエストを引用する。`Microsoft.AspNetCore.Routing.Matching.DfaMatcher` は、`No candidates found for the request path '…'` のように、リクエストごとにパスを書く。`Microsoft.AspNetCore.Server.Kestrel.BadRequests` は、`Invalid request line: '…'` や `Invalid request header: '…'` のように、Kestrel が拒否したリクエストの部分を書く。制御文字は `\x0D` のように書く。`Trace` では、`Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler` も、`/auth/callback` へのリクエストごとに、そのクエリを `MessageReceived: '…'` として書く。

レベルの既定は `Information` だ。変えるには、環境変数 `Logging__LogLevel__Default` に `Trace`、`Debug`、`Information`、`Warning`、`Error`、`Critical`、`None` のどれかを設定する。たとえば `Logging__LogLevel__Default=Warning` にすると、警告とエラーだけを書く。

起動する前にプログラムを止めたエラーは、このログには入らない。`elm-server` は `error:` と理由を、`elm-service` は理由を、標準エラー出力に書く。終了コードは [elm-server の設定](server-configuration.md#コマンドラインと終了コード)と [elm-service の設定](service-configuration.md#コマンドラインと終了コード)にある。

## 利用ログ

利用ログには、リース、借用、拒否と、書き込むプログラム自身のイベント（起動や停止など）を記録する。監査や課金の問い合わせのための記録で、シートの数え方はこのログに依存しない。書き込みに失敗しても、そのことを運用ログに書くだけで、リクエストは失敗させない。

### 書き込むプログラムと場所

| プログラム | 書き込む条件 | ファイル | 設定 |
| --- | --- | --- | --- |
| `elm-server` | 常に書き込む | `log.usage`（環境変数 `ELM_LOG_USAGE`）のパスと同じ場所の `<name>-<UTC time>.jsonl`。既定の `usage.jsonl` なら `usage-<UTC time>.jsonl` になる | `log.usage`。相対パスは、サーバーのデータフォルダー（`dataDirectory`）からのパスになる。 |
| `elm-service` | `usageLog.directory` を設定したノードだけ | そのフォルダーの `usage-<node>-<UTC time>.jsonl`。チェーンはノードごとに 1 本。書き込んでいるあいだ、ノードは `usage-<node>.lock` も保持する | `usageLog.directory`（`ELM_USAGELOG_DIRECTORY`）。ノード名は `node.name`（`ELM_NODE_NAME`）。 |

- 名前の UTC 時刻は `yyyyMMddTHHmmssfffZ` の形で、たとえば `usage-20270125T093000123Z.jsonl` になる。名前を並べ替えると時刻の順になる。時計が戻っても、新しいファイルの名前が最新のファイルより前に来ることはない。
- 起動のたびと、UTC で日付が変わるたびに、新しいファイルを始める。
- 名前がこの形でも、数字が実在する日時を表していないファイル（たとえば 13 月を指す `usage-20271301T000000000Z.jsonl`）は、ログのファイルではない。プログラムはそのファイルを読まず、次のファイルの名前を決めるときにも使わない。起動のたびと、動いているあいだは UTC の 1 日に 1 度、運用ログに警告 `The file <name> has the name of a usage log file but no real date and time; it is not read as one.` を書く。プログラムはファイルをそのまま残す。警告を止めるには、ファイルをフォルダーの外へ移すか、削除する。`elm log verify` は、フォルダーの中のそういうファイルを検証せず、警告 `warning: <name>:0: The file's name has the form of a log file's name but no real date and time; the file was not verified.` でそのファイルを示す。ファイルのパスを直接指定すれば、検証する。名前を変えても、削除より多くは隠せない。こうして検証から外れるのは、削除したときと同じく、いちばん新しいファイルだけである。途中のファイルの名前を変えると、そのあとのファイルとのつながりが切れ、`elm log verify` はそれを誤りとして報告する。
- 時刻 `99991231T235959999Z` より後の名前は付けられない。最新のファイルの名前がこの時刻のとき、プログラムは起動しない。エラーは、利用ログに書き込めないことを伝え、`No file name comes after <name>, the newest file of the log.` で終わる。そのファイルをフォルダーの外へ移してから、プログラムを起動し直す。
- 同じ名前の別のノードがすでにそのフォルダーに書き込んでいると、`elm-service` のノードは起動しない。各ノードは自分のファイルだけを書く。すべてのノードのファイルを集めるのは運用者の役目だ。
- 古いファイルは、運用者が削除するまで残る。
- `elm-server` は借用の記録も保持する。データフォルダーの `borrows-<UTC time>.jsonl` だ。これは別のログで、[elm-server の借用の記録](#elm-server-の借用の記録)で説明する。

### 行の形式

各行は、1 つの JSON オブジェクトと改行からなる（JSON Lines）。どの行も、次の項目で始まる。

| 項目 | 値 |
| --- | --- |
| `type` | イベントまたは行の種類。 |
| `ts` | 時刻。Unix 秒で表す。 |

ほかの項目は、種類によって異なる。たとえば、`elm-server` がリースを貸し出したときの行は次のようになる（値は架空のもの）。

```json
{"type":"lease.acquired","ts":1800000000,"lic":"LIC-0042","feat":"pro","n":1,"key":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","lease":"Xk2bQ9rT4mVn8sLp0wZa1g","host":"ws-12","user":"alice","app":"2.1.0"}
```

### 署名に関わる行とチェーン

| 種類 | 位置 | 項目 |
| --- | --- | --- |
| `chain.start` | 前のファイルのチェーンを続けるファイルの最初の行 | `stream`（`usage`）。`prev`: `file`（前のファイルの名前）と `auth`（前のファイルの最後の `auth` 行の SHA-256 を base64url で表したもの）。前のファイルがなければ省く。そのあとに、後述の鍵の項目が続く。 |
| `chain.reset` | チェーンを続けられないファイルの最初の行。前のファイルを読めない、前のファイルの最後の 1 MiB に署名がない、または書き込みに失敗した場合に使う | `stream`、`reason`、そのあとに後述の鍵の項目。署名の番号は 1 から振り直す。 |
| `key.changed` | `elm-server` だけ。動作中にサーバーライセンスが変わったとき | `license`（新しいサーバーライセンスのファイル）、`key`（JWK で表した新しい公開鍵）。次の署名はまだ古い鍵で作り、それより後の署名は新しい鍵で作る。 |
| `auth` | 各ファイルの冒頭の行のあと、100 行ごと、書き込み待ちの行があれば 60 秒ごと、ファイルを閉じる前、管理者の指示があったとき | `sig`: `elm-log+jws` のトークン。 |

起動時、プログラムは前のファイルの最後の 1 MiB を末尾から読み、最初に見つけた `auth` 行からチェーンを続ける。その行より後の行は、中身が何であっても読み飛ばす。途中で切れた行、JSON でないテキスト、`auth` 行でない JSON が、これに当たる。`chain.reset` で始めるしかないときは、理由を添えた警告を運用ログにも書く。

`chain.reset` の `reason` は、ファイルを名前だけで示し、フォルダーは含まない。

| `reason` | 場合 |
| --- | --- |
| `the previous file <name> cannot be read` | 前のファイルを開けないか、読めない。 |
| `no signature was found at the end of the previous file <name>` | 前のファイルの最後の 1 MiB に `auth` 行がない。 |
| `a write to <name> failed` | ファイル `<name>` に行か署名を書き込めず、次の書き込みでこのファイルを始めた。原因は運用ログにある。ライセンスの読み直しを借用の記録に書き込めなかったときは、読み直しへのレスポンスにある。 |

最初の行にある鍵の項目は、どの鍵がそのファイルに署名するかを示す。

| プログラム | 項目 |
| --- | --- |
| `elm-server` | `license`（サーバーライセンスのファイル）と `key`（ここでは、[サムプリント](../explanation/devices-and-proofs.md#インストール鍵)ではなく、JWK で表したサーバーの公開鍵）。サーバーはインストール鍵で署名する。 |
| `elm-service` | `node`（ノード名）と `certificates`（サービスのログ用の署名鍵の鍵証明書。`elm-cert+jws` を 1〜4 個）。 |

`auth` トークンのペイロードは次のとおり。

| 項目 | 値 |
| --- | --- |
| `ver` | `1`。 |
| `iat` | 署名した時刻。Unix 秒で表す。 |
| `jti` | 署名の一意な ID。 |
| `stream` | `usage` か `borrows`。 |
| `n` | ここでは、シート数ではなく、チェーンの中でのこの署名の番号。1 から始まる。 |
| `prev` | 前の `auth` 行の SHA-256（改行を含む）を base64url で表したもの。新しいチェーンの最初では空。 |
| `digest` | この署名が対象にする行の SHA-256（改行を含む）を base64url で表したもの。 |
| `lines` | 対象にする行の数。 |

すぐに署名させる管理者の指示は、`elm-server` では `elm server seal` か `POST /admin/v1/log/seal`、`elm-service` では `POST /v1/admin/usage-log/seal` だ。

### `elm-server` のイベント

リースの行には、イベントで分かる範囲で、`lic`（サーバーライセンスの ID）、`feat`（機能）、`n`（シート数）、`key`（クライアントのインストール鍵のサムプリント）、`lease`（リース ID）が入る。`code` と `result` には、`ELM_NO_SEATS` のようなエラーコードの名前が入る。コードの一覧は[エラーコード](error-codes.md)にある。

| 種類 | 書き込むとき | 項目 |
| --- | --- | --- |
| `lease.acquired` | リースを貸し出したとき | `lic`、`feat`、`n`、`key`、`lease`。クライアントが送った場合は `host` と `user`。送られた場合は `app` |
| `borrow.started` | 借用を貸し出したとき | `lease.acquired` と同じ |
| `overage` | 貸し出しによって、使用中のシート数がライセンスのシート数を超えたとき | `lic`、`feat`、`n`、`key`、`lease`、`inUse` |
| `lease.renewed` | リースを更新したとき | `lic`、`key`、`lease`、`cloneFlagged` |
| `lease.released` | クライアントがリースを返却したとき、または管理者がリースを強制的に返却したとき | `lic`、`feat`、`n`、`key`、`lease` |
| `borrow.returned` | クライアントが借用を早期返却したとき | `lic`、`feat`、`n`、`key`、`lease` |
| `lease.expired` | リースが更新されずに期限切れになったとき | `lic`、`feat`、`n`、`key`、`lease` |
| `borrow.expired` | 借用が終わったとき | `lic`、`feat`、`n`、`key`、`lease` |
| `lease.reclaimed` | サーバーの再起動のあと、クライアントがリースを再登録したとき | `lic`、`key`、`lease`、`cloneFlagged` |
| `lease.denied` | 拒否したリースの取得、リースの更新、リースの再登録、早期返却。60 秒ごとと停止時に件数を書く | `feat`（早期返却ではリースの機能。リースの取得では、リクエストが求めた機能で、サーバーライセンスが付与している場合だけ付く）、`code`、`count` |
| `admin.denied` | 正しいトークンのない管理リクエスト。60 秒ごとと停止時に件数を書く | `code`（`ELM_UNAUTHORIZED`）、`count` |
| `admin.action` | 正しいトークンのある管理リクエスト | `action`（`status`、`leases`、`release`、`reload`、`seal` のいずれか）、`target`（`release` のリース ID。リクエストの ID がリース ID の形でなければ付かない）、`result`（エラーコードの名前。レスポンスが終わる前に接続が閉じたときは `ELM_NETWORK_ERROR`、サーバーの内部で操作が失敗したときは `ELM_INTERNAL_ERROR`） |
| `server.started` | サーバーが起動したとき | `lic`、`key`（ここではサーバーの鍵 ID。クライアントのものではなく、サーバー自身のインストール鍵のサムプリント）、`borrows`（復元した借用） |
| `server.stopped` | サーバーが停止したとき | `leases`（返却されていないリース） |
| `records.set_aside` | `--set-aside-broken-records` で動かしたサーバーが、起動中、待ち受けを始める前に、壊れた借用の記録を退避したとき。そのあと待ち受けに失敗した起動でも、この行は残る | `file` |
| `license.refreshed` | サーバーライセンスを `elm-service` から更新したとき | `lic`、`token`（トークン ID）、`seq` |
| `license.refresh_failed` | サーバーライセンスの更新に失敗したとき | `stage`（`start`、`write`、`reload`、`service` のいずれか）、`code` |

拒否の行は、期間内の 1 つの種類（機能とコードの組）の件数を表す。1 つの期間に数える種類は 100 までで、それを超えた分は機能を区別せずに数える。リクエストが求めた機能のうち、サーバーライセンスが付与していないものの名前は書かないので、そうした拒否は、コードごとに 1 つの種類にまとまる。

### `elm-service` のイベント

リースの行には、`lic`（ライセンス ID）、`prd`（製品の `code`。アプリやライセンスファイルが使う `acme-cad` のような製品 ID で、管理 API での製品の `id` ではない）、`feat`、`n`（シート数）、`key`（デバイスのインストール鍵のサムプリント）、`lease` が入る。`exp` はリースの有効期限で、Unix 秒で表す。`code` にはエラーコードの名前が入る。[エラーコード](error-codes.md)を参照。ライセンスキーは書き込まない。アクティベーション、管理操作、クローン検出は、このログではなく監査ログに入る。

| 種類 | 書き込むとき | 項目 |
| --- | --- | --- |
| `lease.acquired` | リースを貸し出したとき | `lic`、`prd`、`feat`、`n`、`key`、`lease`、`exp`。ポリシーで残す設定なら `host` と `user`。送られた場合は `app` |
| `borrow.started` | 借用を貸し出したとき | `lease.acquired` と同じ |
| `lease.renewed` | リースを更新したとき（最後のトークンをもう一度返す再試行は書かない） | `lic`、`prd`、`feat`、`n`、`key`、`lease`、`exp` |
| `lease.released` | リースが返却されたとき | `lic`、`prd`、`feat`、`n`、`key`、`lease`、`by`（`device` か `admin`） |
| `borrow.returned` | 借用が早期返却されたとき、または管理者が借用を解放したとき | `lease.released` と同じ |
| `lease.expired` | 期限切れのリースを、片付けの処理が削除したとき | `lic`、`prd`、`feat`、`n`、`key`、`lease` |
| `borrow.expired` | 終わった借用を、片付けの処理が削除したとき | `lic`、`prd`、`feat`、`n`、`key`、`lease` |
| `lease.denied` | リースのルールで拒否したリースの取得、リースの更新、返却。60 秒ごと、停止時、seal の指示のときに件数を書く。プルーフの不備、レート制限（`429`）、本文のサイズ（`413`）による拒否は数えない | `feat`（リースの機能。リースの取得では、リクエストが求めた機能で、ライセンスが付与している場合だけ付く）、`code`、`count` |
| `service.started` | ノードが起動したとき。リクエストを受け付ける前に書く | `node` |
| `service.stopped` | ノードが停止したとき、または `service.started` を書いたあとで起動に失敗したとき（たとえば、待ち受けられないとき） | `node` |

行は、リクエストによる変更をコミットしたあとに書く。期限切れのリースは、片付けの処理でそのリースを削除したノードが 1 回だけ書く。

### 利用ログの検証

```text
elm log verify <files or folders>... [--product <ID>] [--node <name>] [--stream usage|borrows] [--trust <file>]... [--revoked <kid>]... [--json]
```

すべてのオプションは [`elm log verify`](elm-cli.md#elm-log-verify) に載せている。利用ログについては次のとおり。

- ログのファイル、またはそれを入れたフォルダーを指定する。ファイルは名前の順に、古いものから検証する。フォルダーを指定すると、その中にあるそのストリームのファイルを指定したことになる。
- `--product` は、`elm-server` のログでは必要で、`elm-service` のログでは要らない。
- `--node` は、`elm-service` の利用ログのフォルダーから、そのノードの `usage-<node>-*.jsonl` のファイルを選ぶ。ファイルのパスと一緒には使えない。
- `--json` は、`intact`、`files`、`entries`、`issues`（それぞれ `file`、`line`、`severity`、`message` を持つ）を書き出す。

このコマンドは、各ファイルの最初の行にある鍵の情報を、その行の時刻でのトラストストアと照らし合わせる。`elm-server` ならサーバーライセンス、`elm-service` ならログ用の鍵証明書だ。次に、すべての `auth` 行、各ファイルの中のチェーン、ファイルからファイルへのチェーンを確かめる。テキストの出力は、問題ごとに `error: <file>:<line>: <message>` か `warning: ...` を 1 行ずつ出し、最後に `intact: <files> file(s), <lines> event line(s)` か `NOT INTACT: ...` を出す。

| 終了コード | 意味 |
| --- | --- |
| 0 | 完全。エラーがない。警告はあってもよい。 |
| 1 | 完全でない。エラーが 1 つ以上ある。 |
| 2 | 入力が正しくない。たとえば、ファイルでもフォルダーでもないパスを指定した、ログファイルが 1 つも見つからない。 |

エラーになるのは次のものだ。読めないファイルや空のファイル、JSON オブジェクトでない行（JSON でないテキストや、`[]`、`123` のような JSON）や 1 MiB より長い行、行の `type`、最初の行、`auth` 行、`key.changed` 行にある、Unicode として正しくない文字列（たとえば、対になる相手のないエスケープ `\ud800`）、`chain.start` でも `chain.reset` でもない最初の行、最初の行より後にあるチェーンの行、別のストリーム、検証に通らない鍵の情報、ファイルの鍵によらない署名、一致しない `digest` や行数、`prev` や `n` の途切れ、`key.changed` のあとに古い鍵の署名がないこと、ファイルとファイルのあいだで欠けたファイルや途中で切られたファイル、別のライセンスのもとで、前のファイルの最後の鍵とは違う鍵で続くファイル、1 つのチェーンに混ざったサーバーのファイルと `elm-service` のファイル。`elm-service` では、別のノードを名乗るファイルやノードを名乗らないファイルと、`key.changed` の行もエラーになる。

警告になるのは次のものだ。理由を添えた `chain.reset`、最後の署名より後の行、途中で切れた最後の行、移動したサーバー（同じサーバーライセンスの別の鍵）、置き換えられた `elm-service` の署名鍵、フォルダーの中にあって、名前がログのファイルの形でも実在する日時を表していないファイル（検証しない）。

次のファイルがない最新のファイルを削除したり途中で切ったりしても、検出されない。

## elm-server の借用の記録

借用中のクライアントはネットワークから離れているので、サーバーの再起動のあとにリースを再登録できない。そこで `elm-server` は、借用と早期返却をすべて借用の記録に書き込み、起動時に読み戻す。終わっていない借用は、再起動をまたいでシートを保持し続ける。記録にはこれ以外のものは入らない。サーバーはデータベースを持たない。

### ファイル

- サーバーのデータフォルダー（`dataDirectory`）の `borrows-<UTC time>.jsonl`。時刻の形は利用ログと同じで、たとえば `borrows-20270125T093000123Z.jsonl` になる。利用ログと違い、専用の設定はない。
- 起動のたびと、UTC で日付が変わるたびに、新しいファイルを始める。新しいファイルでは、チェーンの行の次に、期限の切れていない借用と、過去 30 日の早期返却を書き写す。そのため、最新のファイルだけで全体の状態が分かる。書き写した行の `ts` は、書き写した時刻になる。
- 古いファイルは、運用者が削除するまで残る。サーバーが読むのは最新のファイルだけだ。
- 利用ログと同じく、名前がこの形でも実在する日時を表していないファイルは、記録のファイルではない。運用ログの警告は、これを `a borrow record file` と呼ぶ。最新のファイルの名前が時刻 `99991231T235959999Z` のとき、サーバーは起動しない。エラーは、借用の記録を読み書きできないことを伝える。そのファイルをデータフォルダーの外へ移してから、サーバーを起動し直す。
- `--set-aside-broken-records` で退避したファイルは、`borrows-broken-<UTC time>.jsonl` という名前になる。この時刻は `yyyyMMddTHHmmssZ` の形だ。サーバーはこのファイルを二度と読まない。

### 行の形式と署名

行は JSON Lines で、どの行も利用ログと同じく `type` と `ts`（Unix 秒）で始まる。記録には、`elm-server` の利用ログと同じチェーンで署名する。違いは次のとおり。

- ストリームは `borrows` で、最初の行とすべての `auth` トークンに入る。
- イベントの行ごとに `auth` 行が続く。各 `auth` 行は、借用や返却が反映される前にディスクへ書き出す。
- 最初の行は、`license` と `key` を持つ `chain.start` か `chain.reset` で、サーバーはインストール鍵で署名する。動作中にサーバーライセンスが変わると、利用ログと同じく `key.changed` の行を書く。その行を書けなかったときは、記録は古いライセンスのままになる。
- 行を書けなかったときは、借用なら `ELM_INTERNAL_ERROR`、早期返却なら `ELM_RETURN_NOT_ALLOWED` で拒否する。運用ログには、エラー `The borrow record cannot be written; the borrow or the early return is refused.` が、原因とともに残る。次の行からは、`chain.reset` で始まる新しいファイルになる。
- `--set-aside-broken-records` のあとは、次のファイルが `chain.reset` で始まり、その `reason` に退避したファイルの名前が入る。

### イベント

| 種類 | 書き込むとき | 項目 |
| --- | --- | --- |
| `borrow.started` | 借用を貸し出す前と、新しいファイルに期限の切れていない借用を書き写すとき | `lease`、`lic`、`seats`、`borrow`、`fp` |
| `borrow.returned` | 早期返却が反映される前と、新しいファイルに過去 30 日の返却を書き写すとき | `id`、`key`、`fp`、`at` |

| 項目 | 値 |
| --- | --- |
| `lease` | 借用のリーストークン全体。 |
| `lic` | 借用した時点のサーバーライセンス ID。 |
| `seats` | 借用した時点で、サーバーライセンスにある、借用した機能のシート数。 |
| `borrow` | 借用した時点の、サーバーライセンスの借用の上限。単位は秒。 |
| `fp` | クライアントが送ったマシンフィンガープリント。最大 8 個の文字列の配列。それ以外の値は、フィンガープリントなしとして読む。 |
| `id` | 返却した借用のリース ID。 |
| `key` | その借用をしたクライアントのインストール鍵のサムプリント。 |
| `at` | 返却した時刻。Unix 秒で表す。 |

たとえば、早期返却の行は次のようになる（値は架空のもの）。

```json
{"type":"borrow.returned","ts":1800003600,"id":"Xk2bQ9rT4mVn8sLp0wZa1g","key":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","fp":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4"],"at":1800003600}
```

`lic`、`seats`、`borrow` があるので、サーバーライセンスが変わったあとの早期返却をサーバーは拒否できる。過去 30 日の返却は、インストール鍵とフィンガープリントのそれぞれに許される早期返却の回数に数える。利用ログにも同じ名前のイベントがあるが、項目は違う。2 つのログは別のものだ。

### 起動時の処理

1. サーバーは、最新のファイルを `elm log verify` と同じ方法で検証する。ファイルの最後の鍵は、サーバー自身の鍵か、同じサーバーライセンスの鍵（サーバーを別のマシンに移した場合）でなければならない。別のサーバーの記録は、壊れているとみなす。
2. 記録が壊れていると、サーバーは終了コード 1 で起動を止め、メッセージで問題を示す。`--set-aside-broken-records` を付けると、サーバーはファイルの名前を変えて退避し、その借用なしで起動し、ファイル名を入れた `records.set_aside` を利用ログに書く。
3. 完全なファイルからは、サーバーが署名したリーストークンを持ち、期限の切れていない借用を、返却済みのものを除いて復元する。`borrow.started` や `borrow.returned` の行のうち、項目が上の表のとおりでないものと、Unicode として正しくない文字列を持つものは、読み飛ばす。

退避、削除、書き換えをされたファイルにある借用は、その借用が終わるまで数に入らない。データフォルダーに書き込める管理者は、この方法で借用中のシートを空けられる。痕跡として残るのは、ログの `records.set_aside` と `chain.reset`、または欠けたファイルだ。

#### 記録を自分で読む

書き込みの直後に停止すると、最後の `auth` 行より後にイベントの行が残ることがある。起動時、サーバーはそのような行が `borrow.started` なら読むが、`borrow.returned` なら読まない。その返却をしたクライアントには、返却が成功したと伝わっていないからだ。

### 借用の記録の検証

```text
elm log verify <files or folders>... --product <ID> --stream borrows [--trust <file>]... [--revoked <kid>]... [--json]
```

オプション、出力、終了コード、エラーと警告は、[利用ログの検証](#利用ログの検証)と同じだ。`--product` は必須で、指定しないと各ファイルの最初の行がエラーになる。`--node` は使えない。フォルダーを指定すると、その中の `borrows-<UTC time>.jsonl` のファイルを指定したことになり、退避したファイルは含まない。退避したファイルを確かめるには、そのパスを指定する。

## 監査ログ

`elm-service` は、管理操作とデバイスの操作を、データベースの追記専用のテーブルに記録する。各行は、SHA-256 で前の行とつながっている。

### 記録するもの

- 管理 API の認証済みの呼び出しすべて。参照と拒否も含み、呼び出しのアクションと一緒に記録する。レスポンスを最後まで返せずに終わった呼び出しも記録する。成功した `GET /v1/admin/me` は行を書かない。
- 有効な認証のない呼び出し。件数を数え、1 分ごとに 1 行の `auth.failed` として書く。
- 管理画面のサインイン、サインアウト、セッションの変更。
- デバイスからのアクティベーション、アクティベーション解除、トライアル、見つかったクローン。トークンの更新と、拒否したデバイスのリクエストは記録しない。

API キーのような秘密は書き込まない。

### 行のフィールド

| フィールド | 値 |
| --- | --- |
| `n` | ここでは、シート数ではなく、行の番号。 |
| `ts` | 時刻。Unix 秒で保存し、管理 API は `yyyy-MM-ddTHH:mm:ssZ` の形で返す。 |
| `actorType` | `apikey`、`oidc`、`system`、`installation` のいずれか。 |
| `actorId` | `apikey` なら API キーの ID。`oidc` なら管理者の ID。`system` なら `setup` か `auth`。`installation` ならデバイスのインストール鍵のサムプリント。 |
| `action` | アクション。次の表のどれか。 |
| `target` | アクションの対象。ライセンス、アクティベーション、リースの ID など。または `null`。 |
| `result` | `ELM_OK`、または拒否したときのエラーコードの名前（[エラーコード](error-codes.md)を参照）。レスポンスを最後まで返せずに終わった呼び出しでは、レスポンスが終わる前に接続が閉じたときは `ELM_NETWORK_ERROR`、サービスの内部で操作が失敗したときは `ELM_INTERNAL_ERROR` になる。すでに保存された変更の行は、`ELM_OK` のまま残る。サービスが接続の切断に気づけるのは、レスポンスをすべて渡し終えるまでだ。そのあとで、呼び出し元がレスポンスを読まずに接続を閉じたときは、行の `result` は操作そのものの結果になる。 |
| `ip` | 送信元のアドレス、または `null`。 |
| `detail` | アクションの詳細を入れた JSON オブジェクト、または `null`。 |
| `prev` | 前の行のハッシュ（32 バイト）。 |
| `hash` | この行のハッシュ（32 バイト）。管理 API は、`prev` と `hash` を base64url で返す。 |

### アクション

| アクション | 記録するもの |
| --- | --- |
| `admin.create` | OIDC での最初のサインインで管理者を作った。`detail` には `issuer`、`subject`（`oidc.subjectClaim` が指すクレームの値）、`role` が入る。 |
| `admin.role` | サインインのときに管理者のロールが変わった。`detail` には `from`、`to` が入る。 |
| `admin.list` | `GET /v1/admin/admins`。 |
| `admin.disable` | `POST /v1/admin/admins/{id}/disable`。 |
| `admin.enable` | `POST /v1/admin/admins/{id}/enable`。 |
| `admin.me` | 拒否した `GET /v1/admin/me`。 |
| `session.create` | 管理画面のサインイン。`detail` には、置き換えたセッション（`replaced`）と追い出したセッション（`evicted`）が入る。拒否したサインインは、`issuer` と `subject`（`oidc.subjectClaim` が指すクレームの値）を入れた `ELM_FORBIDDEN` になる。 |
| `session.list` | `GET /v1/admin/sessions`。`detail` には `adminId` が入る。 |
| `session.revoke` | `DELETE /v1/admin/sessions/{id}`。 |
| `session.logout` | 管理画面のサインアウト。 |
| `apikey.create` | `POST /v1/admin/api-keys` と、セットアップ時の最初のキー（`actorType` は `system`、`actorId` は `setup`）。`detail` には `name`、`permissions`、`expiresAt` が入り、セットアップ時は `owner` も入る。 |
| `apikey.list` | `GET /v1/admin/api-keys`。 |
| `apikey.revoke` | `DELETE /v1/admin/api-keys/{id}`。 |
| `audit.list` | `GET /v1/admin/audit`。 |
| `audit.verify` | `POST /v1/admin/audit/verify`。`detail` には `intact`、`checked` が入る。 |
| `auth.failed` | 直前の 1 分間に失敗した認証（`actorType` は `system`、`actorId` は `auth`、`result` は `ELM_UNAUTHORIZED`）。`detail` には `count` が入る。 |
| `product.create` | `POST /v1/admin/products`。 |
| `product.list` | `GET /v1/admin/products`。 |
| `product.get` | `GET /v1/admin/products/{id}`。 |
| `product.update` | `PATCH /v1/admin/products/{id}`。 |
| `feature.create` | `POST /v1/admin/products/{id}/features`。 |
| `feature.list` | `GET /v1/admin/products/{id}/features`。 |
| `feature.update` | `PATCH /v1/admin/products/{id}/features/{featureId}`。 |
| `policy.create` | `POST /v1/admin/policies`。 |
| `policy.list` | `GET /v1/admin/policies`。 |
| `policy.get` | `GET /v1/admin/policies/{id}`。 |
| `policy.update` | `PATCH /v1/admin/policies/{id}`。 |
| `signingkey.get` | `GET /v1/admin/signing-key`。 |
| `license.create` | `POST /v1/admin/licenses`。 |
| `license.list` | `GET /v1/admin/licenses`。 |
| `license.get` | `GET /v1/admin/licenses/{id}`。 |
| `license.update` | `PATCH /v1/admin/licenses/{id}`。 |
| `license.suspend` | `POST /v1/admin/licenses/{id}/suspend`。 |
| `license.resume` | `POST /v1/admin/licenses/{id}/resume`。 |
| `license.revoke` | `POST /v1/admin/licenses/{id}/revoke`。 |
| `license.extend` | `POST /v1/admin/licenses/{id}/extend`。 |
| `license.file` | `POST /v1/admin/licenses/{id}/file`。 |
| `activation.activate` | デバイスがアクティベーションまたは再アクティベーションした（`actorType` は `installation`）。`detail` には `licenseId`、`seq`、`reactivated` が入り、オフラインのファイルでは `channel` に `offline` が入る。 |
| `activation.deactivate` | デバイスがアクティベーションを解除した（`detail` には `licenseId`、`deactivations` が入る）。または `POST /v1/admin/activations/{id}/deactivate`（`detail` には `licenseId`、`keyId` が入る）。 |
| `activation.clone` | アクティベーションのクローンが見つかった。`result` は、`deny` なら `ELM_CLONE_DETECTED`、`flag` なら `ELM_OK`。`detail` には `licenseId`、`operation`、`seq`、`lastSeq`、`strategy` が入る。 |
| `activation.offline` | `POST /v1/admin/offline`。拒否したファイルも含む。`detail` には `operation` と、`offline` を値とする `channel` が入る。 |
| `activation.list` | `GET /v1/admin/licenses/{id}/activations`。 |
| `activation.get` | `GET /v1/admin/activations/{id}`。 |
| `trial.create` | デバイスがトライアルを始めた（`actorType` は `installation`）。`detail` には `productId`、`activationId`、`expiresAt` が入る。 |
| `trial.list` | `GET /v1/admin/trials`。 |
| `trial.delete` | `DELETE /v1/admin/trials/{id}`。 |
| `lease.clone` | リースのクローンが見つかった。`result` は `activation.clone` と同じ。`detail` には `licenseId`、`feature`、`seq`、`lastSeq`、`strategy` が入る。 |
| `lease.list` | `GET /v1/admin/licenses/{id}/leases`。 |
| `lease.release` | `DELETE /v1/admin/leases/{id}`。`detail` には `licenseId`、`feature`、`seats` が入る。 |
| `usagelog.list` | `GET /v1/admin/usage-log/files`。 |
| `usagelog.get` | `GET /v1/admin/usage-log/files/{name}`。ダウンロードを始めた呼び出しの `detail` は `{"sent": ..., "size": ...}` で、レスポンスに書き出したバイト数と、ダウンロードを始めた時点のファイルの長さを表す。最後まで届いたダウンロードでは `sent` と `size` が等しい。途中で終わったダウンロードでは、`sent` は呼び出し元が受け取れた量の上限になる。 |
| `usagelog.seal` | `POST /v1/admin/usage-log/seal`。 |

権限がなくて拒否した呼び出しは、そのアクション、`result` の `ELM_FORBIDDEN`、`detail` の `{"permission": ...}` で記録する。そのレスポンスを書く前に接続が閉じていたときは、`result` が `ELM_NETWORK_ERROR` になる。`detail` は変わらない。

レスポンスを最後まで返せずに終わった呼び出しでは、`target` と `detail` が `null` になることがある。サービスがそれらを知る前に、操作が止まったときだ。権限がなくて拒否した呼び出し、ダウンロードを始めた `usagelog.get`、ファイルを発行または処理したあとの `license.file` と `activation.offline` は、いつも、そのアクションの `target` と `detail` を持つ。

### ハッシュのチェーン

各行は、前の行のハッシュ（`prev`）と自分のハッシュ（`hash`）を持つ。そのため、行を書き換えたり、削除したり、挿入したりすると、チェーンが切れる。サービスは、リクエストがあるとチェーンを確かめる。[監査ログの検証](#監査ログの検証)を参照。

#### チェーンを自分で確かめる

サービスを使わずに行を確かめるには、各行のハッシュを、`n`、`ts`、`actorType`、`actorId`、`action`、`target`、`result`、`ip`、`detail`、`prev` の順に並べた列の SHA-256 として計算する。各列は、4 バイトのビッグエンディアンの長さと、それに続く UTF-8 のバイト列で表す。数値は 10 進の文字列で、`ts` は Unix 秒だ。`prev` は、長さ `0x00000020` のあとに生の 32 バイトを置く。`NULL` は長さ `0xFFFFFFFF` だけでバイト列を持たないので、空文字列とは区別される。最初の行の `prev` は、0 のバイトを 32 個並べたものだ。

### 保持期間

| 設定 | 環境変数 | 値 | 既定 |
| --- | --- | --- | --- |
| `audit.retentionDays` | `ELM_AUDIT_RETENTIONDAYS` | 1 から 36600 日 | 省略した場合は、すべての行を残す。 |

保持期間を設定すると、サービスは 1 時間ごとに古い行を削除する。削除するのは、期間内にある最初の行より前の行だ。最後に削除した行のハッシュはチェーンの始まりとして残るので、チェーンは引き続き検証に通る。

### 監査ログの検証

| 呼び出し | 権限 | レスポンス |
| --- | --- | --- |
| `POST /v1/admin/audit/verify` | `audit.read` | `intact`（true か false）、`checked`（読んだ行の数）、`firstBroken`（つながらない最初の行の番号、または `null`）。 |
| `GET /v1/admin/audit` | `audit.read` | `items`（上のフィールドを持つ行）と `next`。クエリは `limit`（1 から 200、既定は 50）、`after`（行の番号）、`order`（既定の `asc` か、`desc`）。 |

検証では、チェーンの始まりから 1 つのスナップショットで行を読み、次のことを確かめる。番号が連続していること、各 `prev` が前の行のハッシュであること、各ハッシュがその行と一致すること、最後の行の番号とハッシュが、サービスが追記のたびに行とは別に保存している番号とハッシュと一致すること。そのため、最後の行が削除されていても、その後ろに行が足されていても見つかる。データベースに書き込める者は、行とそのハッシュを書き換えられる。チェーンではそれを検出できない。
