# V1d4b（Issue #415）: 管理画面のサインインの経路が、リクエストの文字を運用のログに書くことを直す

## Grill の記録

- 測った事実（Grill、`origin/main` の 5ce2174、ASP.NET Core 10.0.15 の共有フレームワーク、Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.14）:
  - 既定の段で、`OpenIdConnectHandler` の誤り（イベント 12「Message contains error: …」）が、`/auth/callback` のクエリの `error`、`error_description`、`error_uri` を本物の CR LF ごと書く。警告（イベント 16）は、書き換えた相関の Cookie の値を書く。`Information`（イベント 4）と `elm-service` 自身の `SignInRefused`（イベント 16。`ElmService.Web.cs` の `OnRemoteFailureAsync` が `failure?.Message` を渡す）も、同じ文を書く。1 つの要求で約 18.6 KB になった。
  - 段を `Warning` や `Error` に上げても、イベント 12 と 16 は残る。
  - 読んだソース（dotnet/aspnetcore v10.0.14）: `src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs`（115、254、265、275、289 行）と同じフォルダーの `LoggingExtensions.cs`、`src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs`（723〜746 行: 相関が通り、クエリの `error` が空でなければ、コードを交換せずに `OpenIdConnectProtocolException` で止まる。1509〜1521 行）と同じフォルダーの `LoggingExtensions.cs`。分類の名前は `Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler`（70 文字）。
- 持ち主が決めたこと（2026-11-14、Issue #415 のコメント「V1d4b の Grill」。すべて推奨案 A）:
  - 問 4b-1: その分類を、V1d4a の `HostRequestLog` と同じ形（構成のすべての規則のあとに足す規則）で、コードで `None` にする。運用に要る理由は `elm-service` のイベント 16 と 17 に残る（測った: クエリの誤り、`access_denied`、正しくないコード（`invalid_grant`）、正しくないクライアントの秘密（`invalid_client`）、ログインのときと戻りのときに IdP に届かない、`state` がないか壊れている、相関の Cookie がない）。失うのは、IdP の HTTP 状態コード（イベント 52）と例外のスタック（イベント 17）。
  - 問 4b-2: クエリの `error` のとき、`SignInRefused` は、登録された誤りのコード（RFC 6749 §4.1.2.1 の 7 つと OpenID Connect Core §3.1.2.6 の 9 つ）だけを「the identity provider answered <code>」と書き、それ以外は決まった文を書く。`error_description` と `error_uri` は書かない。ほかの理由（トークンのエンドポイントの答え、ID トークンの確かめ、「Correlation failed.」）と、テスト `ElmServiceWebTests.Callback_IdTokenNotSigned_IsBadRequest` は、そのままにする。
  - 問 4b-3: トークンのエンドポイントの答え（`invalid_client`、`invalid_grant` とその説明）は、今のまま書く。
  - 問 4b-4: §18.3 と `logs.md`（英語と日本語）の OIDC の例外の文（表の行と `Trace` の文）を消し、`Hosting.Diagnostics` と同じ形の段落にする。`elm-service` の行に、拒否したサインインの理由がリクエストの文字を持たないこと（登録されたコードか決まった文）を足す。V1d4a の問 4a-3 が挙げた 3 つの分類は、2 つになる。
- こちらが決めたこと:
  - コードの置き場所: 型を足さずに、両方の製品の `HostRequestLog` を一般にした。`HostRequestLog.Raise(logging, category, floor)` が、分類と下限の段を引数に取る。`elm-server` は `Hosting.Diagnostics` と `Warning` で、`elm-service` はそれに加えて `OpenIdConnectHandler` と `None` で呼ぶ。2 つの `HostRequestLog.cs` は、名前空間のほかは同じままにした（§18.3 の「同じ形の `HostRequestLog.cs`」を「名前空間だけが違う同じ `HostRequestLog.cs`」にし、`elm-server` はこの分類だけに使うと書いた）。規則の選び方を写したコードを 2 つの型に分けると、`LoggerRuleSelector` の写しが 3 つになるためである。足す規則の名前は、分類、`*`、分類（`OpenIdConnectHandler` では 141 文字）。
  - 分類の名前は `typeof(OpenIdConnectHandler).FullName` で得る（`ElmService.SignInHandlerCategory`）。ハンドラーは `GetType().FullName` の分類でロガーを作る。テストは、名前を文字列としても確かめる。
  - クエリの誤りの見分け方（`ElmService.RefusalReason`）: 失敗が `OpenIdConnectProtocolException` で、要求のクエリが `error` の名前を持つとき。持ち主の決定は「クエリの `error` が空でない」だったが、空の値も数えた。`error` が 2 つあるクエリのように、ハンドラーが空でないと読み、こちらが空と読む形があると、ハンドラーの文がそのまま書かれるためである。空の `error` とともにトークンのエンドポイントが断ったときは、その答えの代わりに決まった文を書く（読み手に失うものはその場合だけ）。`/auth/callback` は GET だけを受ける（`RefuseWebPath`）ので、フォームの `error` は届かない。
  - 理由に書くのは、受けた文字列ではなく、一覧の文字列。比べ方は大文字と小文字を区別する（`StringComparer.Ordinal`）。
  - `CA1873`（ログの引数の評価）を避けるため、理由はいったん変数に入れてから `SignInRefused` に渡す。
  - V1d4a のテストの名前 `RaiseToWarning_…` は、`Raise_…` にした（両方のテストのプロジェクト）。
- テスト:
  - `ElmServiceWebTests.Callback_ErrorOrCorrelationCookie_LeavesNoRequestTextInTheLog`（`Information` と `Trace` の 2 つの場合）: `error`、`error_description`、`error_uri`（後ろの 2 つは CR LF と偽の行を持つ）で戻る。`error` は `server_error`、`login_required`、`Server_Error`、目印と CR LF を持つ文字列の 4 つ。もう 1 つは、相関の Cookie を書き換えて戻る。理由がそれぞれ「the identity provider answered server_error」「… login_required」、決まった文 2 回、「Correlation failed.」であること。どの分類の項目にも目印と偽の行がなく、`OpenIdConnectHandler` の分類の項目がないこと。
  - `ElmServiceWebTests.Callback_TokenEndpointRefusesTheClient_KeepsItsAnswerInTheReason`: 正しくないクライアントの秘密で、理由が「Message contains error: 'invalid_client', error_description: 'error_description is null', error_uri: 'error_uri is null'.」のままであること（一度走らせて読んだ文。状態コードは文に入らない）。
  - `HostRequestLogTests.Raise_SignInHandlerEachSetting_WritesNothing`（18 の場合）と `Raise_SignInHandlerConsoleSettings_WritesNothing`（6 の場合）: V1d4a と同じ形の設定（設定なし、`Default`、分類、`*` を前、後ろ、途中に置いた名前と 141 文字の名前、提供者を問わないものと提供者のもの、別名 `Console` と完全な名前）で、`Trace` から `Critical` のどの段も書かないこと。`SignInHandlerCategory_IsTheHandlersLogCategory`: 分類の名前。
  - 既存の `Callback_IdTokenNotSigned_IsBadRequest` と `Callback_Refusals_AreBadRequests` は、変えずに通った。
  - 手元（Windows 11、Release）: `ElasticLM.Service.Tests` は合計 483、成功 399、スキップ 84（PostgreSQL の場合）。`ElasticLM.Server.Tests` は合計 238、成功 237、スキップ 1。
- 変異の確かめ（すべてテストが落ちた。ただし 3 の `OpenIdConnectHandler` の側は下のとおり）:
  1. `ElmService.Build` から `OpenIdConnectHandler` の規則を外す: `Callback_ErrorOrCorrelationCookie_…` の 2 つの場合が落ちた。
  2. その規則の下限を `Warning` にする: 同じ 2 つが落ちた。
  3. 別名をまとめない（両方の `HostRequestLog.cs` で）: `Raise_ConsoleSettings_KeepsTheRequestLinesOut` が落ちた。`OpenIdConnectHandler` のテストは落ちない。下限が `None` なので、足す規則はどの提供者の名前でも `None` になり、別名をまとめてもまとめなくても結果は同じだからである（違いの出る入力がない）。
  4. 足す規則の名前を分類そのものにする（両方で）: `*` を含む長い名前の場合が落ちた。
  5. 提供者を問わない規則だけを足す（両方で）: 提供者の設定の場合が落ちた。
  6. 理由に `error_description` を足す: `Callback_ErrorOrCorrelationCookie_…` が落ちた。
  7. どの `error` の文字列もコードとして書く: 同じテストが落ちた（`Server_Error` と目印の場合）。
  8. コードの比べ方で大文字と小文字を区別しない: 同じテストが落ちた（`Server_Error` の場合）。
  9. クエリの `error` を見ない: `Callback_TokenEndpointRefusesTheClient_…` が落ちた。
- 同じ種類を探した範囲: `elm-service` の `ServiceLog` で、要求から来た文字列を理由として書くもの。`SignInRefused` のほかの理由（`OnTokenValidated` などの決まった文、ライブラリーの IDX の文。IdentityModel は PII を伏せる）と `SignInUnavailable`（届かないときの例外の文）は、要求のクエリを持たない。`Trace` の場合のテストで、`OpenIdConnectHandler` のほかの分類が目印を書かないことも確かめた。
- 行の外で見つけたこと: なし。
