# V1d4a（Issue #375）: Web ホストが、要求のパスの文字を運用のログに書くことを直す

## Grill の記録

- 測った事実（Grill、`origin/main` の 528918e、Windows 11、Debug のビルド）: 既定の段（`Information`）で、`Microsoft.AspNetCore.Hosting.Diagnostics` が、どの待ち受け（`elm-server` の https と `admin.sock`、`elm-service`）でも、要求ごとに「Request starting」と「Request finished」を書く。2 行とも、パスを復号し、`Host` ヘッダーをそのまま書く。経路に合わない要求には、イベント 16 も書く。1 行は約 7 KB まで、1 つの要求で 3 行までになる。パスの `%0A` は本物の改行として書かれ、Windows のコンソールで偽の行を差し込めた。コードで `AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", Warning)` にすると 0 行になり、環境変数 `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics` では戻せない。`docs/guide/reference/logs.md` の 24 行は、`Microsoft.*` をアドレスと起動と停止だけと書いていた。
- 持ち主が決めたこと（2026-11-13。どちらも推奨案 A）:
  - 問 4a-1: その分類を、コードで `Warning` に上げる（要求ごとの行はなくなる）。
  - 問 4a-2: 利用者の文書に、既定の段で書く `Microsoft.*` の分類を 1 つずつ挙げる。
- こちらが決めたこと:
  - 規則の足し方（§18.3）。単に `AddFilter(分類, Warning)` とすると、2 つの点で設計の文と合わなかった。実装の head（`origin/main` の a2a3c91 に変更を足したもの）で、`WebApplication.CreateSlimBuilder` の構成に規則を入れて、その分類の `ILogger.IsEnabled` を見て測った。
    - `Logging:LogLevel:Default` を `None` か `Error` にしても、この分類の `Warning` が有効のままだった（コードの規則が、いちばん詳しい分類として選ばれるため）。利用者の文書は、`None` で何も書かないと言っている。
    - `Logging:Console:LogLevel:Default=Debug` で、この分類の `Information` が有効に戻った（提供者の規則が、提供者を問わない規則より先に選ばれるため。`LoggerRuleSelector.cs` の `IsBetter`）。
  - そこで、構成のすべての規則のあとに（`PostConfigure`）、提供者ごとに、それまでの規則が与える段と `Warning` の高いほうの規則を足す（`HostRequestLog.RaiseToWarning`）。同じ測り方で、`Default` が `None` と `Error` のときは `Warning` も無効、`Default=Debug` に分類そのものを `Trace` とした設定（提供者を問わないものと `Console` のもの）でも `Information` は無効になった。
  - コードの置き場所: 同じ形の `HostRequestLog.cs` を `ElasticLM.Server` と `ElasticLM.Service` にそれぞれ置き、テストもそれぞれに置く。名前空間はアセンブリ名に合わせる（`docs/conventions.md`、`.cursor/rules/csharp.mdc`）ので、1 つのファイルを両方のプロジェクトに結び付ける（`Compile Include` の `Link`）と、片方のアセンブリにもう片方の名前空間の型ができる。2 つの製品が共に参照するプロジェクト（`ElasticLM.Leasing`、`ElasticLM.Issuing`）は、ログに依存しない。
  - Tutorial 3（`use-a-floating-license.md`、英語と日本語）は、サーバーの端末に「Request finished … 503」の行が出ると書いていた。その行は出なくなるので、`Executing endpoint` の項目とリースの行で書き直した。
  - 設計 §14.5 の `keyHash` の理由（要求の URL がサービスのログに残る）を、プロキシのログに残る、に直した。
- 既定の段で書く `Microsoft.*` の分類の測定（実装の head、Windows 11、Debug のビルド、同じプロセス。テストの提供者で `Information` から上を記録した）:
  - 送ったもの: `elm-server` の https、ソケットのファイル（管理者トークンありとなし）、`elm-service` の http、`elm-service` の管理画面のサインイン（`FakeIdp`）。長いパス（`%0A` を含む）、長いクエリ、長い `Host` ヘッダー（60 文字のラベル 15 個）。正しい要求、経路に合わない要求、認証のない要求、操作が例外で終わる要求（時計の読み取りを失敗させた）、待ち受けられない起動、`/auth/callback` の `error` と `error_description` と `error_uri`、書き換えた相関の Cookie、形の悪いアクセストークン 6 つ。
  - 直す前: 上の 3 つの雛形の行が、すべての待ち受けで、パス、クエリ、`Host` を持っていた。
  - 直したあと: `Microsoft.Hosting.Lifetime`（`Information`）、`Microsoft.Extensions.Hosting.Internal.Host`（`Error`、「Hosting failed to start」）、`Microsoft.AspNetCore.Routing.EndpointMiddleware`（`Information`、経路の形だけ）、`Microsoft.AspNetCore.Server.Kestrel`（`Error`、イベント 13、接続と要求の ID だけ）、`elm-service` の画面のログインで `Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager`（`Information` の「Creating key」と `Warning` の「No XML encryptor configured」）と `Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler`（`Information`、`Warning`、`Error`）、OIDC の管理者で `Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler`（`Information`、トークンは伏せられていた）。`Microsoft.AspNetCore.Hosting.Diagnostics` の行は 0 だった。
  - Kestrel が 400 で断る要求（`Host` の 1 つのラベルが 263 文字）は、`Information` から上に何も書かなかった（208 文字は通った）。
  - `Microsoft.AspNetCore.Hosting.dll`、`…Server.Kestrel.Core.dll`、`…Routing.dll`、`…Authentication.dll`、`…Http.Extensions.dll`（10.0.15）のメッセージの雛形のうち、パス、ホスト、クエリ、値を持つものを読んだ。Hosting は上の 3 つ。Kestrel の「bad request data」と Routing の要求のパスを持つ雛形は、どれも上の測定で `Information` から上に出なかった。
- テスト:
  - `HostRequestLogTests.Requests_LongPathQueryAndHost_LeaveNoRequestTextInTheLog`（`ElasticLM.Server.Tests`: https、ソケットのファイルのトークンありとなし。`ElasticLM.Service.Tests`: http）。長いパス、クエリ、`Host` の文字が、どの分類の項目にもないこと、`Microsoft.AspNetCore.Hosting.Diagnostics` の項目がないこと。対照として、製品自身の項目（`server.started`、`admin.action: status (null) -> ELM_OK`、`service.started`）と、Web ホストの `Information` の項目（`Executing endpoint 'HTTP: GET /v1/nonce'` など）があること。
  - `HostRequestLogTests.RaiseToWarning_EachSetting_KeepsTheRequestLinesOut`（両方のテストのプロジェクト、18 の場合）: 設定なし、`Default=None`、`Default=Error`、`Default=Debug` と分類の `Trace`、提供者の `Default=Trace`、提供者の分類の `Trace`、`Microsoft.AspNetCore.Hosting.*=Trace`、`Microsoft.*=None`、`Default=None` と提供者のほかの分類の `Trace`、`Default=Critical` と分類の `Information`。分類を覆う、分類より長い名前（`*` を後ろ、前、途中に置いた 41 文字と、分類、`*`、分類の 81 文字）の `Trace` を、提供者を問わないものと提供者のもので 8 つ。
  - `HostRequestLogTests.RaiseToWarning_ConsoleSettings_KeepsTheRequestLinesOut`（両方のテストのプロジェクト、7 の場合）: 本物の `ConsoleLoggerProvider` で、別名 `Console` と完全な名前の規則。`Default=Trace`、`Default=None`、41 文字の名前の `Trace`、81 文字の名前の `Trace`、片方の名前で分類を `None` にしてもう片方で `Default=Trace` にしたもの（2 つの向き）、`Default=Trace` と分類の `Error`。
  - `BorrowAndAdminTests.AdminApi_ReleaseOfAnIdOfAnotherForm_IsRefusedAndNotLogged` は、Web ホストの要求の行を除いて `elm-server` の分類だけを見ていた。すべての分類を見るように直した。
- 変異の確かめ（初めの 10 個と、独立レビューのあとの 6 個。すべてテストが落ちた）: 2 つの製品の `Build` から `RaiseToWarning` の呼び出しを外す（それぞれの待ち受けのテストが落ちる）。2 つの `HostRequestLog.cs` のそれぞれで、段を常に `Warning` にする、提供者を問わない規則だけを足す、`*` を読まない、提供者の規則が分類を覆わないときも足す（それぞれ、設定の場合のテストが落ちる）。独立レビューのあとは、2 つの `HostRequestLog.cs` のそれぞれで、足す規則の名前を分類そのもの（40 文字）にする（10 の場合が落ちた）、分類と `*`（41 文字）にする（81 文字の名前の 3 つの場合が落ちた）、別名をまとめない（`Console` で分類を `None` にした場合が落ちた）。
- 独立レビュー（PR #416）のあとに直したこと:
  - 足す規則の名前を、分類（40 文字）から、分類、`*`、分類の 81 文字にした（§18.3）。`LoggerRuleSelector` は `*` を含めた名前の長さで選ぶので、`Microsoft.AspNetCore.Hosting.Diagnostics*` などの、分類を覆うより長い名前の設定で要求の行が戻っていた。レビューは 41 文字を勧めたが、覆う名前は前と後の部分が重なってよく、81 文字まであるので、81 文字にした。
  - 別名 `Console` の規則を、完全な名前の規則と同じ提供者の規則として数える（§18.3）。
  - 既定より低い段で要求の文字を書く分類を、同じ head と方法で `Trace` で測った（`elm-server` の 3 つの待ち受け、形の悪い要求 9 つ、`elm-service` の http とサインイン）。`Debug` の `Microsoft.AspNetCore.Routing.Matching.DfaMatcher`（イベント 1000、1001、1005）と `Microsoft.AspNetCore.Server.Kestrel.BadRequests`（イベント 17）、`Trace` の `OpenIdConnectHandler`（イベント 24）が書いた。`Microsoft.AspNetCore.Hosting.Diagnostics` は `Trace` でも 0 行だった。利用者の文書の「どのエントリーも要求の文字を持たない」を既定の段に限り、これらの分類を挙げた。
- 同じ種類を探した範囲: 既定の段で書く `Microsoft.*` の分類のすべて（上の測定）。Routing は経路の形だけを書き、Kestrel の誤りは ID だけを書くので、そのまま残す。利用者の文書では、`Request starting` と `Request finished` を引いていた Tutorial 3 を直した。
- 行の外で見つけたこと（Issue #415）: `elm-service` の管理画面のサインインで、`/auth/login` で正しい `state` と相関の Cookie を得た誰でも、`/auth/callback` のクエリの `error`、`error_description`、`error_uri` の文字（`%0A` の改行を含む）を、`OpenIdConnectHandler` の `Error` と `Information` の項目と、`elm-service` 自身の `SignInRefused` の項目（`Information`）に書ける。相関の Cookie の値も、`OpenIdConnectHandler` の `Warning` の項目に書かれる。直すには、`ElmService.Web.cs`（V1d9a が変える）の `SignInRefused` に渡す文と、`Microsoft.AspNetCore.Authentication.OpenIdConnect` の分類の扱いを決める必要がある。

## 退けた案

- **決定:** Web ホストの要求ごとの行は、分類 `Microsoft.AspNetCore.Hosting.Diagnostics` を、コードで `Warning` より下では書かないことで止める（問 4a-1）。
- **理由:** 要求の文字が行に入る道が、その分類の 3 つの雛形だけで、段を上げれば道ごとなくなる。どの設定でも戻らない。
- **却下した案:** (B) Web ホストの行を、自前の 1 行（メソッド、経路の形、状態、時間）に置き換える。(C) パスを短くし、制御文字を逃がして書く。(実装の中で) 単に `AddFilter(分類, Warning)` とする: `Default=None` でもこの分類の警告と誤りが出て、`Logging__Console__LogLevel__Default=Debug` で要求の行が戻った。

- **決定:** 利用者の文書の運用のログの表に、既定の段で書く `Microsoft.*` の分類を 1 つずつ挙げ、要求の行を書かないことを書く（問 4a-2）。
- **理由:** どの分類が何を書くかが、読み手に分かる。
- **却下した案:** (B) `Microsoft.*` の 1 行のまま、「パスは書かない」を足す。
