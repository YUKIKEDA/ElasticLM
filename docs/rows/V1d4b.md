# V1d4b（Issue #415）: 管理画面のサインインの経路が、リクエストの文字を運用のログに書くことを直す

## Grill の記録

- 測った事実（Grill、`origin/main` の 5ce2174、ASP.NET Core 10.0.15 の共有フレームワーク、Microsoft.AspNetCore.Authentication.OpenIdConnect 10.0.14）:
  - 既定の段で、`OpenIdConnectHandler` の誤り（イベント 12「Message contains error: …」）が、`/auth/callback` のクエリの `error`、`error_description`、`error_uri` を本物の CR LF ごと書く。警告（イベント 16）は、書き換えた相関の Cookie の値を書く。`Information`（イベント 4）と `elm-service` 自身の `SignInRefused`（イベント 16。`ElmService.Web.cs` の `OnRemoteFailureAsync` が `failure?.Message` を渡す）も、同じ文を書く。1 つの要求で約 18.6 KB になった。
  - 段を `Warning` や `Error` に上げても、イベント 12 と 16 は残る。
  - 読んだソース（dotnet/aspnetcore v10.0.14）: `src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs`（115、254、265、275、289 行）と同じフォルダーの `LoggingExtensions.cs`、`src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs`（723〜746 行: 相関が通り、クエリの `error` が空でなければ、コードを交換せずに `OpenIdConnectProtocolException` で止まる。1509〜1521 行）と同じフォルダーの `LoggingExtensions.cs`。分類の名前は `Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler`（70 文字）。
- 持ち主が決めたこと（2026-11-14、Issue #415 のコメント「V1d4b の Grill」。すべて推奨案 A）:
  - 問 4b-1: `OpenIdConnectHandler` の分類を、V1d4a の `HostRequestLog` と同じ形（構成のすべての規則のあとに足す規則）で、コードで `None` にする。運用に要る理由は `elm-service` のイベント 16 と 17 に残る（測った: クエリの誤り、`access_denied`、正しくないコード（`invalid_grant`）、正しくないクライアントの秘密（`invalid_client`）、ログインのときと戻りのときに IdP に届かない、`state` がないか壊れている、相関の Cookie がない）。失うのは、IdP の HTTP 状態コード（ハンドラーのイベント 52）と例外のスタック（ハンドラーのイベント 17）。
  - 問 4b-2: クエリの `error` のとき、`SignInRefused` は、登録された誤りのコード（RFC 6749 §4.1.2.1 の 7 つと OpenID Connect Core §3.1.2.6 の 9 つ）だけを「the identity provider answered <code>」と書き、それ以外は決まった文を書く。`error_description` と `error_uri` は書かない。ほかの理由（トークンのエンドポイントの答え、ID トークンの確かめ、「Correlation failed.」）と、テスト `ElmServiceWebTests.Callback_IdTokenNotSigned_IsBadRequest` は、そのままにする。
  - 問 4b-3: トークンのエンドポイントの答え（`invalid_client`、`invalid_grant` とその説明）は、今のまま書く。
  - 問 4b-4: §18.3 と `logs.md`（英語と日本語）の、OIDC を例外とする文（表の行と `Trace` の文）を消し、`Hosting.Diagnostics` と同じ形の段落にする。`elm-service` の行に、拒否したサインインの理由がリクエストの文字を持たないこと（登録されたコードか決まった文）を足す。V1d4a の問 4a-3 が挙げた 3 つの分類は、2 つになる。
- 持ち主が決めたこと（2026-11-14、PR #432 の実装とレビューのあと。Issue #415 のコメント。どれも推奨案）:
  - 問 4b-2 の「クエリの `error` が空でないとき」を、「クエリに `error` という名前の引数があるとき（値が空でも）」に広げる。ハンドラーと `Request.Query` は、クエリを違うふうに読む。ハンドラーのメッセージ（Microsoft.IdentityModel 8.19.2 の `OpenIdConnectMessage.cs`）は、名前の大文字と小文字を区別し、同じ名前の値のうち null でない最初のものを取る。`Request.Query` は大文字と小文字を区別しない。そのため、`?error=&error=…` のように、ハンドラーとこちらで `error` の読み方が分かれる形があると、ハンドラーの文（クエリの文字を含む）が書かれる。読み手が失うのは、ハンドラーが `error` を読まない形（値が空か、名前の大文字と小文字が違う）で、その先の確かめかトークンのエンドポイントが断ったときの答えである。そのときの理由は、決まった文か「answered <コード>」になる。`/auth/callback` は GET だけを受ける（`RefuseWebPath`）ので、フォームの `error` は届かない。
  - `ServiceLog.SignInRefused` を `Warning` に上げる。ハンドラーの行を止めたあと、断ったサインインの理由は `elm-service` の行だけに残るので、`Logging__LogLevel__Default=Warning` の運用者にも残す。
  - クエリの誤りでない失敗では、理由に内側の例外の `Message` の連鎖を足す（トークンのエンドポイントへの TLS の失敗の理由、IDX の「see inner exception」の中身）。問 4b-1 で示した「失うもの」は、HTTP の状態コードと例外のスタックだけで、内側の例外の文を言っていなかった。
  - 実装のあと、こちらが広げた 2 つを、持ち主が承認した（2026-11-14、Issue #415 の最後のコメント）。`SignInUnavailable`（ログインのときと戻りのとき）にも同じつなぎ方を使う。`access_denied` は、コードの一覧（`RegisteredErrors`、15 個）に入れない。ハンドラーはクエリの `access_denied` を `OnAccessDenied` に渡し、そこで同じ形の理由「the identity provider answered access_denied」を書くので、一覧を引くことはない（一覧に残すと、どの入力でも結果の変わらない項目になる）。テストは 16 のコードすべてで理由を確かめる。
- こちらが決めたこと:
  - コードの置き場所: 型を足さずに、両方の製品の `HostRequestLog` を一般にし、分類と下限の段を引数で受ける形にした（`HostRequestLog.Raise(logging, category, floor)`）。`elm-server` は `Hosting.Diagnostics` と `Warning` で、`elm-service` はそれに加えて `OpenIdConnectHandler` と `None` で呼ぶ。2 つの `HostRequestLog.cs` は、名前空間のほかは同じままにした（§18.3 の「同じ形の `HostRequestLog.cs`」を「名前空間だけが違う同じ `HostRequestLog.cs`」にし、`elm-server` は `Raise` をこの分類だけに使うと書いた）。規則の選び方を写したコードを 2 つの型に分けると、`LoggerRuleSelector` の写しが 3 つになるためである。足す規則の名前は、分類、`*`、分類で、分類の長さを n 文字とすると 2n + 1 文字（`Hosting.Diagnostics` は 81 文字、`OpenIdConnectHandler` は 141 文字）。
  - 分類の名前は `typeof(OpenIdConnectHandler).FullName` で得る（`ElmService.SignInHandlerCategory`）。ハンドラーは `GetType().FullName` の分類でロガーを作る。テストは、名前を文字列としても確かめる。
  - 理由に書くのは、受けた文字列ではなく、サービス自身の一覧の文字列。比べ方は大文字と小文字を区別する（`StringComparer.Ordinal`）。
  - V1d9a は、`ServiceLog.RequestUnreadable`（イベント 21）の段を「画面のログインの拒否（イベント 16）と同じ `Information`」とした（`docs/rows/V1d9a.md`）。イベント 16 が `Warning` になったので、この比べ方はもう当たらない。イベント 21 は `Information` のままにした（呼び出した側の誤りで、サービスの故障ではないという V1d9a の理由は変わらない）。
  - `Default=Warning` でも、認証のない誰かの戻りは 1 回ごとに `Warning` の行を 1 つ書く。その数は `rateLimit.perIp`（既定は毎秒 20 件）で抑えられる。`GuardAsync` は認証より前に、`/auth/callback` への要求も数える（§18.3）。
  - クエリが `error` の名前を持つときは、どこで起きた `OpenIdConnectProtocolException` もこの形の理由になる。`Data["error"]` が一覧にあれば「answered <コード>」になる（トークンのエンドポイントも `invalid_request`、`unauthorized_client`、`invalid_scope`、`server_error` を答えうる）。なければ決まった文になる（`invalid_grant`、`invalid_client`、`Data["error"]` を持たない IDX21323（nonce）、`c_hash` と `at_hash` の確かめ、IDX21334（コードがない））。
  - 内側の例外のつなぎ方は、`Exception.ToString` と同じ「 ---> 」で、スタックは書かない（`ElmService.WithInnerMessages`）。
  - `CA1873`（ログの引数の評価）を避けるため、理由はいったん変数に入れてからログに渡す。
  - V1d4a のテストの名前 `RaiseToWarning_…` は、`Raise_…` にした（両方のテストのプロジェクト）。
- テスト:
  - `ElmServiceWebTests.Callback_ErrorOrCorrelationCookie_LeavesNoRequestTextInTheLog`（`Information` と `Trace` の 2 つの場合）: 戻りのクエリは、どれも `error_description` と `error_uri`（CR LF と偽の行を持つ）を持つ。`error` は、16 の登録されたコード（「the identity provider answered <コード>」）と、`Server_Error`、`LOGIN_REQUIRED`、後ろに空白のある `login_required `、`invalid_grant`、目印と CR LF を持つ文字列、`?error=&error=<目印>`、`?Error=<目印>`（どれも決まった文）。もう 1 つは、相関の Cookie を書き換えて戻る（「Correlation failed.」）。理由はどれも `Warning` で、数は場合の数と同じ。どの分類の項目にも目印と偽の行がなく、`OpenIdConnectHandler` の分類の項目がないこと。
  - `ElmServiceWebTests.RefusalReason_InnerExceptions_AreJoinedUnlessTheQueryHasAnError`: 内側の例外を 2 つ持つ例外の理由は「outer ---> middle ---> inner」、クエリに `error` があるときの `OpenIdConnectProtocolException`（内側の例外つき）の理由はコードだけ、失敗がないときは「the sign-in failed」。
  - `ElmServiceWebTests.SignIn_ProviderOutOfReach_LogsTheInnerReasons`: ログインのとき（discovery の文書が読めない）と戻りのとき（トークンのエンドポイントが接続を断る）の 2 つの `Warning` が「 ---> 」を持ち、ログインのときは IDX20803 のあとに「 ---> IDX20804: 」が続く（IDX20803 の文は、内側の例外を型の名前つきで持つので、それだけでは区別できない）。
  - `ElmServiceWebTests.Callback_TokenEndpointRefusesTheClient_KeepsItsAnswerInTheReason`: 正しくないクライアントの秘密で、理由が「Message contains error: 'invalid_client', error_description: 'error_description is null', error_uri: 'error_uri is null'.」のままで、`Warning` であること（一度走らせて読んだ文。状態コードは文に入らない）。
  - `HostRequestLogTests.Raise_SignInHandlerEachSetting_WritesNothing`（18 の場合）と `Raise_SignInHandlerConsoleSettings_WritesNothing`（6 の場合）: V1d4a と同じ形の設定（設定なし、`Default`、分類、`*` を前、後ろ、途中に置いた名前と 141 文字の名前、提供者を問わないものと提供者のもの、別名 `Console` と完全な名前）で、`Trace` から `Critical` のどの段も書かないこと。`SignInHandlerCategory_IsTheHandlersLogCategory`: 分類の名前。
  - 既存の `Callback_IdTokenNotSigned_IsBadRequest` と `Callback_Refusals_AreBadRequests` は、変えずに通った。
  - 手元の数は、PR #432 の本文にある。
- 変異の確かめ（すべてテストが落ちた。ただし 3 の `OpenIdConnectHandler` の側は下のとおり）:
  1. `ElmService.Build` から `OpenIdConnectHandler` の規則を外す: `Callback_ErrorOrCorrelationCookie_…` の 2 つの場合が落ちた。
  2. その規則の下限を `Warning` にする: 同じ 2 つが落ちた。
  3. 別名をまとめない（両方の `HostRequestLog.cs` で）: `Raise_ConsoleSettings_KeepsTheRequestLinesOut` が落ちた。`OpenIdConnectHandler` のテストは落ちない。下限が `None` なので、足す規則はどの提供者の名前でも `None` になり、別名をまとめてもまとめなくても結果は同じだからである（違いの出る入力がない）。
  4. 足す規則の名前を分類そのものにする（両方で）: `*` を含む長い名前の場合が落ちた。
  5. 提供者を問わない規則だけを足す（両方で）: 提供者の設定の場合が落ちた。
  6. 理由に `error_description` を足す: `Callback_ErrorOrCorrelationCookie_…` が落ちた。
  7. どの `error` の文字列もコードとして書く: 同じテストが落ちた。
  8. コードの比べ方で大文字と小文字を区別しない: 同じテストが落ちた。
  9. クエリの `error` を見ない: `Callback_TokenEndpointRefusesTheClient_…` が落ちた。
  10. 最初の `error` の値が空でないときだけ数える: `Callback_ErrorOrCorrelationCookie_…` が落ちた（`?error=&error=…`）。
  11. `SignInRefused` を `Information` に戻す: `Callback_ErrorOrCorrelationCookie_…` と `Callback_TokenEndpointRefusesTheClient_…` が落ちた。
  12. 内側の例外の文をつながない: `RefusalReason_InnerExceptions_…` と `SignIn_ProviderOutOfReach_…` が落ちた。
  13. 戻りのときの `SignInUnavailable` だけ、ログインのときの `SignInUnavailable` だけ、`SignInRefused` だけを、内側の例外なしにする: それぞれ `SignIn_ProviderOutOfReach_…`、同じテスト、`RefusalReason_InnerExceptions_…` が落ちた。ログインのときだけの変異は、初めは生き残った。IDX20803 の `Message` が、内側の例外を「 ---> 」と型の名前つきで持つので、「 ---> 」があることだけでは区別できなかった。テストに「 ---> IDX20804: 」を確かめる行を足して、落ちることを確かめた。
  15. `OpenIdConnectProtocolException` のときは内側の例外をつながない: `RefusalReason_InnerExceptions_…` が落ちる（クエリに `error` のない要求の `OpenIdConnectProtocolException("outer", 内側 "inner")` が「outer ---> inner」になることを確かめる）。
  14. 一覧から `registration_not_supported` を外す: `Callback_ErrorOrCorrelationCookie_…` が落ちた。
- レビュー（PR #432 の head bab36bf の正しさ、読み手、日本語のレビュー）のあとに直したこと: 上の持ち主の決定 3 つ、16 のコードと近い文字列とクエリの読み方の違いのテスト、`access_denied` を一覧から外したこと、`Data["error"]` による理由の文の書き直し、覆う名前の長さ 2n + 1、`logs.md` の表の行を短くして理由を表の下の段落に移したこと（2 つの警告の文、一覧の範囲、IdP の文であること、どのエントリーにもないものと IdP のログを見ること、ハンドラーの説明）。
- 同じ種類を探した範囲: `elm-service` の `ServiceLog` で、要求から来た文字列を理由として書くもの。`SignInRefused` のほかの理由（`OnTokenValidated` などの決まった文、ライブラリーの IDX の文。IdentityModel は PII を伏せる）と `SignInUnavailable`（届かないときの例外の文）は、要求のクエリを持たない。`Trace` の場合のテストで、`OpenIdConnectHandler` のほかの分類が目印を書かないことも確かめた。
- 行の外で見つけたこと: なし。
