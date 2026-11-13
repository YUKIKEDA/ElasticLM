# V1d9a（Issue #378）: 途中で終わった管理 API の呼び出しの監査の行に残っている抜けを確かめて直す

## Grill の記録

- 測った事実（Issue #378 の「V1d9a の Grill」。`origin/main` 528918e で走らせた）:
  - 認証の途中で断った呼び出しで、呼び出した側がいなくなると、監査の行が 10 回中 10 回なくなった（CSRF と、役割のない OIDC のトークン）。
  - 形の悪い本文や遅い本文に、Kestrel は 400 か 408 を返したのに、行は `ELM_INTERNAL_ERROR` で、運用の記録には `Error` の段のスタックつきの `Kestrel[13]` が出た。
  - 強制返却で、確定のあとの読み取りが失敗すると、呼び出した側は `500` を受け取り、利用ログの `lease.released` の行がなかった。
  - PostgreSQL 18 で、確定の途中に呼び出した側が切ると、3 回とも取り消されて、行は 1 つ（失敗の行）だった。確定してから取り消しが見える場合は、再現できなかった。
- 持ち主が決めたこと（2026-11-13。どれも推奨案 A）:
  - 問 9a-1: 認証の途中の拒否も、認証のあとと同じ扱いにする（答えを書き、閉じていたら `ELM_NETWORK_ERROR` の行）。画面のログインの拒否も同じ。
  - 問 9a-2: `BadHttpRequestException` は、Kestrel の状態で決める（400 は `ELM_MALFORMED`、408 は `ELM_NETWORK_ERROR`）。V1d9 の「例外の型は見ない」を変える。
  - 問 9a-3: 強制返却は、方針と製品を確定の前に読む（端末の返却と同じ）。
  - 問 9a-4: PostgreSQL の確定には、取り消しを渡さない。
  - 問 9a-5: 呼び出した側が起こせる例外（`BadHttpRequestException`）は、自前の 1 行にして接続を切る。サービスの本物の故障（途中で短くなったファイル）は、いまのまま `Kestrel[13]` に残す。
- こちらが決めたこと:
  - 結果の決め方（`ElmService.FailureCode`）は、先に要求の取り消しを見て、取り消されていなければ `BadHttpRequestException` の状態を見る。V1d9 の「取り消されていれば `ELM_NETWORK_ERROR`」をそのまま残し、取り消されていない例外のうち、呼び出した側が起こしたものだけを `ELM_INTERNAL_ERROR` から外すためである。400 と 408 のほかの状態は `ELM_INTERNAL_ERROR` のままにする（管理 API の本文の読み取りは 64 KiB と 8 KiB の 1 回分で止まり、Kestrel の 413（上限 1 MiB）は来ない）。
  - 自前の 1 行は `ServiceLog.RequestUnreadable`（イベント 21、`Information`）。`The request of an administration call (<action>) could not be read (<状態>); the connection was closed.` で、要求の中身もスタックも書かない。段は、画面のログインの拒否（イベント 16）と同じ `Information` にした。呼び出した側の誤りで、サービスの故障ではないためである。
  - 認証の途中の拒否は、1 つの手順（`ElmService.AuditedRefusalAsync`）にまとめ、OIDC、CSRF、画面のログインの 3 か所がそれを使う。答えの書き込みが例外になったときは、認証のあとと同じく、行を書いてから例外を投げ直す。
  - 確定にトークンを渡さない形（`ServiceStore.InWriteTransactionAsync`）は、管理 API に限らず、`ServiceStore` のすべての書き込みのトランザクションに当てる。決定は「PostgreSQL の確定」だが、トランザクションは両方のエンジンで同じ手順を通り、SQLite でも、確定の前に取り消されていれば確定しない（下のテストで測った）。
  - 強制返却の確定のあとの「行は書いた」という印（`Progress(context).Recorded = true`）は、確定のあと、利用ログの 1 行（`ReleasedLine`）の前に置く。確定のあとに読み取りはなくなったが、利用ログの 1 行が残るので、そこで例外が出ても失敗の行を足さないためである（持ち主のレビュー S1、PR #417。いったん外した形は、`ServiceUsageLog.Write` が例外を外に出さないことに頼っていた）。
  - 画面のログインの拒否で、行が書けない（`DbException`）ときは、もう書いたページのあとで `500` のページを書かない（`Response.HasStarted` を見る）。前は、書いたページのあとで状態を変えようとして例外になり、ページが途中で切れた（下の M10 で確かめた）。同じ所を直したので、この行で直した。
- テスト（`tests/ElasticLM.Service.Tests`）:
  - 問 9a-1: `ElmServiceEndedEarlyTests.AuthenticationRefusal_CallerGoneBeforeItsAnswer_IsAuditedAsNetworkError`（CSRF と役割のないトークン、それぞれ 50 回）、`SignInRefusal_CallerGoneBeforeItsPage_IsAuditedAsNetworkError`（20 回）、`SignInRefusal_RowCannotBeWritten_KeepsTheRefusalPage`。時計の読み取りを、認証の手順が自分でする読み取り（`HeldClock.HoldTimeReadInAsync`）で止め、その間に呼び出した側が切る。Issue の再現と同じ形である。
  - 問 9a-2、9a-5: `Body_BadChunk_IsAuditedAsMalformedAndLoggedInOneLine`、`Body_TooSlow_IsAuditedAsNetworkErrorAndLoggedInOneLine`（Kestrel の最小の本文の速さを待つので、実時間で約 6 秒かかる）、`FailureCode_ByCancellationAndStatus_GivesTheResult`。本物の故障が `Kestrel[13]` を残すことは、`ServiceUsageLogTests.UsageGet_FileCutWhileItIsSent_FailsTheTransferAndIsAuditedAsInternalError` に足した。
  - 問 9a-3: `ServiceUsageLogTests.ForcedRelease_ProductUnreadable_ReleasesNothing`（両方のエンジン）。V1d9 の `StateChange_FailsAfterItsCommit_LeavesOneRow` を置き換えた。確定のあとで答えが書けないときに行が 1 つであることは、`ElmServiceAdminTests.StateChange_CallerGoneAfterItsCommit_LeavesOneRow` が引き続き確かめる。
  - 問 9a-4: `WriteTransaction_TokenCanceledAfterItsBody_Commits`（両方のエンジン）、`StateChange_CallerGoneWhileItsCommitRuns_IsStoredWithOneRow`（PostgreSQL。確定のときに 3 秒眠る遅延の制約トリガーで確定を長くし、その間に呼び出した側が切る。Issue の再現と同じ形）。
  - PostgreSQL は、CI と同じ `postgres:18`（同じダイジェスト）を WSL のコンテナー（`wslc`）で動かして走らせた。
- 変異の確かめ（10 個。どれも、上のテストが落ちた）:
  - M1 拒否の答えのあとで閉じた接続を見ない: 認証の途中の拒否の 2 つと、画面のログインの拒否のテストが落ちた。
  - M2 拒否の答えの書き込みが例外になったら行を書かない: 同じ 3 つが落ちた。
  - M3 400 を `ELM_INTERNAL_ERROR` にする: 不正なチャンクのテストと `FailureCode` のテストが落ちた。
  - M4 408 を `ELM_INTERNAL_ERROR` にする: 遅い本文のテストと `FailureCode` のテストが落ちた。
  - M5 取り消しより先に状態を見る: `FailureCode` のテストが落ちた。
  - M6 `BadHttpRequestException` を Kestrel に渡す: 本文の 2 つのテストが落ちた。
  - M7 接続を切らない: 本文の 2 つのテストが落ちた。
  - M8 強制返却の製品の読み取りを確定のあとに戻す: `ForcedRelease_ProductUnreadable_ReleasesNothing` が両方のエンジンで落ちた。
  - M9 確定に要求のトークンを渡す: `WriteTransaction_TokenCanceledAfterItsBody_Commits` が両方のエンジンで、`StateChange_CallerGoneWhileItsCommitRuns_IsStoredWithOneRow` が PostgreSQL で落ちた。
  - M10 画面のログインで、書いたページのあとにも `500` のページを書く: `SignInRefusal_RowCannotBeWritten_KeepsTheRefusalPage` が落ちた。
- 同じ種類を探した範囲: `elm-service` の `AdminAsync` の認証の部分と、画面のログイン（`ElmService.Web.cs`）の、答えのあとに行を書く所のすべて（3 か所。どれも直した）。管理 API の本文を読む所（`ReadBodyAsync` を使う、API キー、モデル、オフラインの処理）。`ServiceStore` の `InTransactionAsync` のすべて（23 か所。どれも新しい手順にした）。`elm-server` のローカルの管理 API は、本文を読む操作がなく、`BadHttpRequestException` の道がない。認証の途中の拒否もない（トークンが合わなければ行を書かない、§13.7）。
- PR #417 のレビューのあとで直したこと:
  - 本文の途中で接続を切った呼び出しの結果が揺れた（レビューが SQLite で測った。RST では 40 回のうち 16 回が `ELM_INTERNAL_ERROR`、FIN では 9 回が `ELM_MALFORMED`）。本文の読み取りが `IOException` で終わり、要求がまだ取り消されていなければ、サービスの時計で最大 1 秒、取り消しを待つ（`ReadBodyAsync`、`AbortWait`）。`Body_CallerLeavesInTheMiddle_IsAuditedAsNetworkErrorEachTime` が、RST と FIN をそれぞれ 40 回、4 度走らせて、どれも `ELM_NETWORK_ERROR` だった。`IOException` だけを `ELM_NETWORK_ERROR` にする案は、FIN の 400 を分けられないので選ばなかった。変異の確かめ: 待ちを 0 にすると、RST で `ELM_INTERNAL_ERROR`、FIN で `ELM_MALFORMED` になって落ちた。
  - 接続を切る道は、状態が 400 か 408 の `BadHttpRequestException` に限った（`IsUnreadableBody`）。変異の確かめ: 413 を足すと `FailureCode_ByCancellationAndStatus_GivesTheResult` が落ちた。
  - 確定にトークンを渡さないことの結果（止まった `COMMIT` は取り消せず、停止はそれを待つ。上限は PostgreSQL の `Command Timeout` と SQLite の 5 秒）を、設計 §18.2 に書いた。時間の上限を別に付ける案は、確定したかどうかが決まらない形に戻るので選ばなかった。
  - 持ち主のレビュー（8daa564）の S1: 上の「こちらが決めたこと」の印を戻した。変異の確かめはしていない（`ReleasedLine` に例外を出させる道が、テストからない）。
  - `InWriteTransactionAsync` と AuraORM 0.1.0 の `InTransactionAsync`（パッケージのコミット 44283d0 の `src/AuraORM/Execution/Database.cs` と `Transaction.cs` を読んだ）の違いは 2 つ。確定にトークンを渡さないこと。AuraORM は、本体がトランザクションを自分で確定か巻き戻しをしていれば巻き戻しを飛ばす（内部の `_completed` を見る `RollbackAfterAsync`）が、こちらは飛ばさないこと。`ServiceStore` の本体は、どれも自分で確定も巻き戻しもしないので、動きは同じである。巻き戻しが失敗したときに、本体の例外と合わせて `AggregateException` にするのは、AuraORM と同じである。
- 行の外で見つけたこと: 端末の API（`elm-service` の `ElmService.Client.cs` と `ElmService.Leases.cs` の本文の読み取り、`elm-server` の `ElmServer.cs` の `ReadBodyAsync`）も、本文を読めない `BadHttpRequestException` を Kestrel に渡すので、呼び出した側が `Error` の段の `Kestrel[13]` を書かせられると読める（コードを読んだだけで、走らせていない）。端末の要求は監査の行を書かないので、この行の監査の抜けではない。

## 退けた案

- **決定:** 認証の途中の拒否と画面のログインの拒否も、答えのあとに、閉じた接続を見てから行を書き、答えの書き込みが例外になっても行を書く（問 9a-1）。
- **理由:** 答えが書けなかった拒否が監査に残らないと、拒否された試みを、呼び出した側が接続を切るだけで記録から消せる。認証のあとの拒否（V1d9）と同じ形にすれば、どちらの拒否も同じ規則で残る。
- **却下した案:** (B) 答えの前に行を書く。行に書く結果（届いたかどうか）が、答えを書く前には決まらない。(C) 受け入れて設計に書く。拒否の記録が呼び出した側の振る舞いで消える。

- **決定:** `BadHttpRequestException` の行の結果は、Kestrel の状態で決める（400 は `ELM_MALFORMED`、408 は `ELM_NETWORK_ERROR`）（問 9a-2）。
- **理由:** どちらも呼び出した側が起こしたことで、`ELM_INTERNAL_ERROR` と記録すると、サービスの故障と見分けられない。
- **却下した案:** (B) どれも `ELM_MALFORMED`。遅い本文は形の誤りではない。(C) いまのまま設計に書く。監査の行が、サービスの故障を実際より多く示す。

- **決定:** 強制返却は、利用ログの行のための読み取りを、確定の前にする（問 9a-3）。
- **理由:** 確定のあとに失敗しうる読み取りがあると、返却したのに利用ログの行がなく、呼び出した側は `500` を受け取る。端末の返却は、すでに確定の前に読んでいる。
- **却下した案:** (B) 確定のあとの失敗を受け止めて `204` を返す。利用ログの行は、やはり書けない。(C) 受け入れる。利用ログ（席の記録）に穴が残る。

- **決定:** 書き込みのトランザクションの確定には、要求の取り消しを渡さない（問 9a-4）。
- **理由:** PostgreSQL では、確定の途中の取り消しで、確定したかどうかが呼び出した側の離れ方で決まり、確定のあとに取り消しが届けば、変更と 2 つの行が残りうる。確定を最後まで進めれば、結果が 1 つに決まる。
- **却下した案:** (B) 取り消されたら、変更があるかを確かめてから行を書く。確かめる読み取りも失敗しうるうえ、操作ごとに確かめ方が違う。(C) 結果が決まらないと設計に書く。監査の行と状態が食い違いうる。

- **決定:** 呼び出した側が起こせる例外（`BadHttpRequestException`）は、自前の 1 行にして接続を切る。サービスの本物の故障は `Kestrel[13]` に残す（問 9a-5）。
- **理由:** 呼び出した側が送る本文だけで、スタックつきの `Error` の項目をいくつでも書かせられると、運用者が本物の故障を見落とす。本物の故障は、スタックがあるほうが原因を追える。
- **却下した案:** 全部いまのまま、文書に書く。運用の記録の `Error` の段が、呼び出した側の誤りで埋まる。
