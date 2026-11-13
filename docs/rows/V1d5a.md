# V1d5a（Issue #379）: クライアントが、オンラインのアクティベーション、更新、トライアルの答えを、頼んだリクエストに照らさないことを確かめて直す

## Grill の記録

- 測った事実（Issue #379 のコメント「V1d5a の Grill」。`origin/main` 528918e、SQLite、`cloneStrategy` は `deny`）。中継が、取っておいた本物の答えを別の要求に返すと、次のことが起きた。
  - R1: 更新の答えに古い更新の答えを返すと、クライアントの `seq` が戻り、次の本物の更新が複製と判定されてブロックされた。
  - R2: ライセンス 2 の有効化に、ライセンス 1 の有効化の古い答えを返すと、サービスには持ち主のいない有効化が残って台数を使い、有効化し直すと複製でブロックされた。
  - R3: 購入したライセンスの有効化に、試用の答えを返しても、R2 と同じことが起きた。
  - R5: 署名の外にある有効化 ID を書き換えた答えも通り、クライアントは書き換えた ID を保存して、有効化を失った。
  - サービスは、どの有効化にも、最後のファイルを出した要求の ID（`LastProof`）を持っている。
- 持ち主が決めたこと（2026-11-13。どれも推奨案 A）:
  - 問 5a-1: オンラインでもオフラインでも、有効化のライセンストークンに `areq`（サービスの `Device.RequestId`）を入れる。クライアントは、`areq` がこの要求の `jti`、`retry` か `pending` の一覧、待っているオフラインの要求の `nonce` のどれかであるときだけ受け取る。更新では `seq` が増えていることも見る。
  - 問 5a-2: トークンに `act` を入れ、クライアントは署名した値を使う。M4b の Grill の「有効化 ID をトークンのクレームにする」の却下を戻す（下の「退けた案」）。
  - 問 5a-3: 合わない答えは `ELM_REQUEST_MISMATCH`（38）。
  - 問 5a-4: 合わなかった要求の `jti` は、答えのない一覧に残す。
  - 問 5a-5: `areq` か `act` のない答えは断る。保存済みのファイルは、いまのまま `Check` を通る。
- 独立レビュー（PR #418）のあとに持ち主が決めたこと（2026-11-13。どちらも推奨案 A）:
  - 問 5a-7: 有効化と試用では、`pending` の `jti` のうち、同じ向け先（製品とライセンスキーの SHA-256、または製品と試用）の要求のものだけを受け取る。待っているオフラインの有効化の要求の `nonce` も、同じ向け先のときだけである。本文の `retry` には一覧のすべてを入れ続け、サービスの複製の判定は変えない。レビューは、ライセンス 1 の有効化（または試用）の答えを中継が止めると、その `jti` が `pending` に残り、そのあとのライセンス 2 の有効化に中継がその答えを返すと、クライアントがライセンス 1（または試用）を保存することを、2 つのテストで走らせて示した（R2 と R3 がもう一度起きる）。
  - 問 5a-6: `flag` の複製のあとの有効化の次の `seq` を、最後に出した値と要求の `seq` の大きいほう + 1 にする（リースの `LeaseRules.NextSequence` と同じ）。サービスのデータベースを古いバックアップから戻して、サービスの `seq` S が端末の C より小さいと、`flag` の更新の答えは S + 1 になり、問 5a-1 の `seq` の比べで端末が断る。差 d = C − S が 15 以下なら d + 1 回の更新で戻るが、16 以上なら答えのない一覧が 16 件で埋まり、管理者の解除でしか戻らない。この行が持ち込む後退である。次の `seq` を作れない要求（`seq` が 64 ビットの整数の最大）は、複製を記録せずに `ELM_MALFORMED` で断る（リースと同じ）。
- こちらが決めたこと:
  - .NET の名前。`VerifiedLicense.OfflineRequestNonce` と `LicenseSpec.OfflineRequestNonce` を `AnsweredRequestId` に改め、両方に `ActivationId` を足した。値はオンラインの要求の `jti` にもなるので、前の名前は意味に合わない。公開した版はまだない（V1d5 の記録と同じ）ので、前の名前は残さない（`.cursor/rules/no-dead-code.mdc`）。
  - 形。`areq` は 1〜64 文字（リーストークンの `areq` と同じ `Limits.IsProofId`）、`act` は `Guid.TryParseExact` の `D` の形（`Limits.IsActivationId`）。発行する側と検証器の両方が `LicensePayloadRules` で確かめる。どちらも任意のクレームで、ないトークンは検証に通る（問 5a-5 の後半）。
  - サービスが `areq` に入れる値は、有効化の `LastProof` と同じ値にした（`ElmService.IssueFile` の引数。更新では `next.LastProof`）。再試行に返す `LastFile` の `areq` が、`retry` で名指された `jti` とそのまま一致する。
  - 比べる順と文（設計 §12.6.2 の「受け取ったファイル」）: `act` と応答の `activationId`、更新では `act` と保存した有効化 ID、更新では `seq`、最後に `areq`。§12.7.5 と同じく、項目を `areq` より先にした。別の要求への本物の答えは `areq` も違うので、`areq` を先に比べると、どの項目が違っても同じ文になり、項目ごとの検査を外した変異を見分けられない。
  - 合わない答えの扱い。届かなかった答えと同じにした（リースの §12.7.3 と同じ）。要求の `jti` は一覧に残し（問 5a-4）、`unkept` のハッシュは書かない。保存したファイル、有効化 ID、`seq` は変えない。更新では状態を `RefreshFailing` にし、有効化と試用では状態を変えない。更新で `RefreshFailing` にするのは、設計の表の「成功の応答を保存できない」と同じ扱いで、`elm-server` が自分の更新の失敗を運用の利用ログ（`license.refresh_failed`）に書けるためである。
  - オフラインの応答の取り込みは、`act` と応答の `activationId` だけを足して比べ、保存した有効化の ID と `seq` とは比べない。待っている要求の `nonce` が有効化と `seq` を決めるので、本物の応答でこの 2 つが外れる道はない（M4c2 の変異の確かめと同じ理由。`.cursor/rules/testing.mdc` の、届かない検査は置かない規則）。`activationId` は応答を運ぶ人が書き換えられるので、`act` との比べは届く。
- テスト:
  - クライアント（`tests/ElasticLM.Client.Tests/ActivationClientTests.Answers.cs`。`DelegatingHandler` の中継が、要求をサービスに渡したあとで答えを差し替える）: R1（`Refresh_ReplayedEarlierAnswer_IsRefusedAndTheRetryGetsTheServicesFile`。断ったあとの更新が、断った要求の `jti` を `areq` に持つサービスの最後のファイルを受け取ること、つまり再試行の答えを受け取ることも確かめる）、R2（`Activate_ReplayedAnswerOfAnotherLicense_IsRefusedAndTheRetryGetsItsOwn`）、R3 と試用（`Trial_ReplayedTrialAnswer_IsRefused`）、R5（`Answer_UnsignedActivationIdChanged_IsRefused`、有効化と更新）、署名鍵で署名したファイルのクレームを 1 つずつ変えたもの（`Refresh_SignedFileWithOtherClaims_IsRefusedWithItsMessage`。`areq` なし、別の `areq`、`act` なし、別の `act`、同じ `seq`、正しいもの）、オフラインの取り込みで `activationId` を書き換えたもの（`Import_UnsignedActivationIdChanged_IsRefused`）、`areq` と `act` のない保存済みのファイル（`KeptFile_WithoutTheAnswersClaims_StillChecksAndRefreshes`）。
  - 独立レビューのあと: 答えのない別の要求への答え（`Activate_AnswerToAnotherPendingRequest_IsRefused`。別のキー、試用、待っているオフラインの別のキーの要求の 3 つ。どれも直す前のコードで落ちることを走らせて確かめた）、向け先が置き換えたオフラインの要求・解除の証明・オフラインの更新の取り込みを越えて残ること（`Activate_AimKeptThroughOtherSteps_TakesTheFileOfTheEarlierRequest`）、`aims` の形（`ActivationStoreTests.Read_EachOtherForm_IsDamaged`）、試用の更新を挟んだ購入の送り直し（`Trial_ThenPurchase_ReplacesTheTrial`）、`flag` のサービスを差 1 と差 20 だけ戻したときの更新（`Refresh_ServiceRestoredBehindUnderFlag_GetsALaterFile`）、`flag` の要求の `seq` 9 に 10 を返すこと、64 ビットの最大の `seq` が `flag` では複製を記録せずに `ELM_MALFORMED`、`deny` では `ELM_CLONE_DETECTED` になること（`Sequence_EachBranch_IsJudged`）。
  - C の関数（`tests/ElasticLM.Native.Tests/ActivationTests.cs` の `Refresh_ReplayedAnswerOfAnotherRequest_Returns38AndKeepsTheActivation`）と、`elm-server` が自分で行うサーバーライセンスの更新（`tests/ElasticLM.Server.Tests/ServerLicenseTests.cs` の `OwnRefresh_ReplayedAnswerOfAnotherRequest_KeepsTheLicenseAndLogsTheMismatch`）。どちらも、ループバックのポートで待つ中継（`ReplayingRelay`。V1d5 のリースの中継を、パスを選べるようにした）が、前の更新の答えを返す。
  - 発行と検証: `LicenseVerifierTests.Verify_AnswerClaims_HaveTheirForm`、`IssuingTests.Issue_AnswerClaims_WrittenOrRefused`。サービス: `ElmService.ActivationsTests`（オンラインの有効化、再試行、更新、再有効化、`flag` の複製の `areq` と `act`）、`ElmService.TrialsTests`、`ElmService.OfflineTests`。
- 変異の確かめ（17 個。どれもビルドの終了コードを見てから、600 秒の上限つきで走らせ、`dotnet test` の終了コードで決めた。どれも落ちた。最初の回で、SA1119 でビルドに通らなかった 4 個と、テストを 1 つも選ばない絞り込みの 2 個は、書き直して走らせ直した）:
  - クライアントの比べ: `act` と `activationId` の比べを外す（R5 のテスト）、`act` のないトークンを通す、更新の `act` と保存した ID の比べを外す、`seq` の `<=` を `<` にする（どれも `Refresh_SignedFileWithOtherClaims_IsRefusedWithItsMessage`）、`seq` の比べを外す（R1 のテスト）、`areq` の比べを外す、`areq` のないトークンを通す（署名したファイルのテスト）、照らす集まりをこの要求の `jti` だけにする（R1 のテスト。再試行の答えを断る）、待っているオフラインの要求の `nonce` を集まりから外す（`ActivationClientTests` のオフラインのテスト）、合わない答えのハッシュを `unkept` に書く（R1 のテスト）、取り込みの `act` の比べを外す（`Import_UnsignedActivationIdChanged_IsRefused`）。
  - サービス: 更新の `areq` を前の要求の ID にする（`Sequence_EachBranch_IsJudged`）、`act` を書かない（`Activate_NewInstallation_IssuesABoundFile`）、試用の `areq` を証明の `jti` でなくする（`Trial_OncePerFingerprint_UntilRemoved`）。
  - 形の検査: `areq` の形を見ない（検証器と発行の両方のテスト）、`act` の形を見ない。
- 独立レビューのあとの変異の確かめ（14 個。やり方は上と同じで、どれも落ちた。最初の回で残った 3 個（置き換えたオフラインの要求、解除の証明、オフラインの更新の取り込みで向け先を落とすもの）は、`Activate_AimKeptThroughOtherSteps_TakesTheFileOfTheEarlierRequest` を足して落ちた）: 向け先で絞らない、待っているオフラインの要求を向け先で絞らない、更新・解除の証明・オフラインの更新の取り込み・置き換えたオフラインの要求で向け先を落とす、`aims` を書かない、読まない、件数 16 を 17 に、長さの `<=` を `<` に、`jti` の長さを見ない、`flag` の次の `seq` に要求の値を使わない、複製を記録する前の検査を外す、`deny` でもその検査をする。
- 測定: 有効化は §4.2 の測る道である。足した仕事は、サービスでは `Guid` を 1 つ文字列にしてトークンに 2 つのクレーム（約 80 バイト）を足すこと、クライアントでは文字列の比べが 4 つと一覧の中の探しである。手元では `tools/ElasticLM.Measure/run.ps1` を走らせていない（4 つの実行ファイルと `samples/VerifyLicense` の NativeAOT の発行が要る）。`Measure` は、行を持つ場で走らせる。
- 同じ種類を探した範囲: ライセンスファイルを受け取るのは `ActivationClient` の `Keep`（オンラインの有効化・更新・試用）と `Import`（オフライン）だけで、`elm-server` の自分の更新、`elm server activate` などのコマンド、C の関数はどれもこのクラスを通る。サービスがライセンスファイルを出すのは `ElmService.IssueFile`（有効化の 3 つの道とオフライン）と、管理 API の `Unbound` のファイル（`ElmService.Licenses.cs`。有効化がないので、`areq` と `act` を持たない）だけである。`elm offline process` は、応答の有効化 ID とライセンス ID を表示のために確かめずに読むだけで、保存しない（§12.8.4）。
- 行の外で見つけたこと: なし（独立レビューが見つけた 2 つは、問 5a-6 と問 5a-7 としてこの行で直した）。

## 退けた案

### 有効化と試用が受け取る、前の要求の範囲（問 5a-7）

- **決定:** `pending` のうち、同じ向け先の要求の `jti` だけを受け取る。向け先は `activation.json` の `aims` に書く。
- **理由:** 答えを 1 つ止めた中継が、別のキーの有効化や購入の有効化に、止めた答えを返せる（独立レビューが走らせた）。
- **却下した案:** (B) 一覧のどの `jti` も受け取る。答えを 1 つ止める中継には、R2 と R3 がそのまま残る。

### `flag` のあとの有効化の `seq`（問 5a-6）

- **決定:** 最後に出した値と要求の `seq` の大きいほう + 1。
- **理由:** サービスが端末より遅れても、端末は持っているものより後のファイルを受け取り、問 5a-1 の比べに通る。
- **却下した案:** (B) Issue と別の行にする。この行が持ち込む後退で、差が 16 以上なら管理者しか戻せない。

### 答えを要求に結ぶもの（問 5a-1）

- **決定:** 有効化のトークンに `areq` を入れ、クライアントはこの要求と、送った `retry` と待っているオフラインの要求のどれかに当たるときだけ受け取る。更新では `seq` が増えていることも見る。
- **理由:** R1〜R3 は、別の要求への本物の答えを、同じ有効化か同じインストール鍵のほかの有効化の答えとして受け取ったことで起きた。リースの答え（V1d5）と同じく、答えを要求に結ぶものが要る。
- **却下した案:** (B) 有効化 ID と `seq` だけを比べる。R2 と R3 の新しい有効化には比べる ID も `seq` もなく、防げない。(C) §5.4 に受け入れる。持ち主のいない有効化が台数を使い、有効化し直すと複製でブロックされる害は、通信を止めることより大きい。

### 有効化 ID をトークンのクレームにする（問 5a-2）

- **決定:** トークンに `act` を入れ、クライアントは署名した値を保存する。M4b の Grill の「有効化 ID をトークンのクレームにする」（`../design-decisions.md`）の却下を戻す。
- **理由:** M4b は「ID は更新と解除のパスに使うだけで、検証には要らない」として、署名の外の本文で渡した。R5 で、TLS の内側の中継が署名の外の ID を書き換えると、クライアントはそれを保存し、有効化を捨てることが分かった。パスに使う値も、守る値である。
- **却下した案:** (B) 更新のときだけ有効化 ID を比べる。有効化と試用の答えの ID は、比べるものがなく守れない。(C) §5.4 に受け入れる。

### 合わない答えの誤りのコード（問 5a-3）

- **決定:** `ELM_REQUEST_MISMATCH`（38）。
- **理由:** オフラインの応答とリースの答え（V1d5）が、同じ意味ですでに使っている。
- **却下した案:** `ELM_MALFORMED`（形は正しいので意味が違う）。新しいコード。

### 合わなかった要求の `jti`（問 5a-4）

- **決定:** 答えのない一覧に残す。
- **理由:** 中継が要求をサービスに渡していれば、サービスはその要求で `seq` を進めているか、有効化を作っている。次の要求の `retry` がその `jti` を名指さなければ、「1 小さく `retry` にない」で複製と判定される。
- **却下した案:** 一覧から消す。

### `areq` か `act` のない答え（問 5a-5）

- **決定:** 断る。保存済みのファイルは、いまのまま `Check` を通る。
- **理由:** `areq` のない答えを受け取ると、V1d5a より前の形の本物のファイルを、中継がどの要求にも返せる。公開した版はまだないので、移行の仕組みは要らない。
- **却下した案:** `areq` のない答えも受け取る。
