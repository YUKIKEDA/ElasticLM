# V1d3f（Issue #521）: `ElmRemoteTextTests.Request_BodyThatStops_EndsAfter100SecondsWithoutAByte` が、遅いランナーで偽の時計を 101 秒より先に進めて落ちる原因を調べて直す

## Grill の記録

持ち主の決定（2026-11-28、Issue #521 のコメント）で行を足し、すぐ始めた。持ち主は、同じファイルの、偽の時計を実時間の間隔で進める形のテストも探して直すと決めた。`ElasticLM.Cli.Tests` で、同じ形のテストも探した。原因はテストの側だけにあり、製品の振る舞いも設計の文も変えないので、問は立てていない。

### 落ち方

`ElmRemoteTextTests.Request_BodyThatStops_EndsAfter100SecondsWithoutAByte` が、ElmRemoteTextTests.cs:202 の `Assert.InRange(moved, 100, 101)` で落ちた（「Range: (100 - 101) / Actual: 107」、1 回）。PR #505 の run 46027666405、ジョブ `build.ps1 (windows-latest)`（140615910190）の Release。PR #505 は CLI もこのテストも変えていない。走らせ直すと通った。

### 原因

テストは、"{" が標準出力に出たあと、偽の時計を 1 秒ずつ 99 回進め（1 回ごとに実時間で 20 ミリ秒待つ）、`elm` が終わっていないことを確かめてから、`elm` が終わるまで、実時間の 50 ミリ秒ごとに 1 秒ずつ進めた。そして、進めた秒数が 100 か 101 であることを確かめていた。

- `elm admin request` は、本文を読むたびに、ホストの時計で 100 秒の `CancellationTokenSource` を作り、その読みに渡す（`AdminCommands.ReadSome`）。
- 偽の時計が 100 秒に届くと、`Advance` の中でタイマーが切れ、読みが取り消される。取り消しがソケットの読みから返り、例外が `CliException` になり、`elm` が誤りの行を書いて終わるまでの実時間は、ランナーの込み具合で決まる。
- その間もテストは 50 ミリ秒ごとに時計を進めるので、遅いランナーでは、`elm` が終わったときに 101 秒より先まで進んでいた。確かめていたのは、製品が切れた時刻ではなく、テストのループが何回回ったかだった。
- 101 秒を許していたのは、"{" が出てから、その次の読みがタイマーを作るまでの間に、テストの最初の 1 秒が入りうるからである。この間も実時間で決まる。

### 測った事実

`origin/main` の d89e9e3 から切ったこの行のブランチ、Windows 11（build 26300）、SDK 10.0.404 の環境で、Debug で、このテスト 1 件だけを流して測った。`web/admin/dist` は、手元の Node.js が 24 でなく Corepack もないので、ほかの作業の木で作ったものを写して使った（このテストは管理画面を使わない）。測りの間だけ、テストの偽の時計を、タイマーの知らせを実時間で 400 ミリ秒遅らせて製品に渡すもの（`FakeTimeProvider` を継ぎ、`CreateTimer` に渡すコールバックを `ThreadPool` に回して `Thread.Sleep(400)` のあとに呼ぶ）に替え、測ったあと消した。遅いランナーで、製品がタイマーの切れたことに気付くのが遅れる形を、決まって起こすためである。

- 今までの形で、知らせを遅らせると、3 回とも「Range: (100 - 101) / Actual: 106」で落ちた。CI の落ち方と同じ形である。遅らせないと通った。
- 直した形（a912bf7）で、同じく知らせを遅らせても、3 回とも通った。遅らせないときも通った。レビューの指摘を直した形（下の「直したこと」）でも、遅らせて 3 回、遅らせずに 1 回、どれも通った。
- 変異の確かめ（製品の `AdminCommands.IdleTimeout` を一時だけ変えた）: a912bf7 では、99 秒にすると「The command ended before 100 seconds without a byte.」（実時間の 0.5 秒の待ちのあとの確かめ）で落ち、101 秒にすると、`elm` が終わらず、実時間の 20 秒の待ちが `TimeoutException` で落ちた。レビューの指摘を直した形では、99 秒と 99.5 秒で「The wait fired before 100 seconds without a byte.」、100.001 秒と 101 秒で「The wait did not fire at 100 seconds without a byte.」で落ちた。どれも時計を進めた直後の確かめで、実時間の待ちに頼らない（1 件 6〜8 秒で、そのほとんどはビルドとテストの起動）。どれも戻した。

### 直したこと

- `ElmRemoteTextTests.Request_BodyThatStops_EndsAfter100SecondsWithoutAByte`: 偽の時計 `TimerAfterCondition` は、"{" が標準出力に出たあとに `elm` が作る最初のタイマー（本文の次の読みの待ち）を知らせ、そのタイマーが切れたかを記録する。テストはその知らせを待ってから時計を 100 秒より 1 ミリ秒少なく進め、タイマーが切れていないことを確かめ、実時間で 0.5 秒待って `elm` が終わっていないことも確かめる。もう 1 ミリ秒進めて、タイマーが切れたことを確かめ、`elm` が終了コード 3 と決まった 1 行で終わることを、実時間で 20 秒の上限で待つ。`FakeTimeProvider.Advance` は、時刻に届いたタイマーのコールバックを返る前に呼ぶので、進めた直後の確かめは実時間によらず決まる。時計を進めるのはタイマーができたあとなので、待ちはちょうど 100 秒で切れる。進めた秒数を数えて範囲で確かめることをやめた。タイマーの長さでは選ばないので、製品の待ちが 100 秒より短くても長くても、1 ミリ秒の単位で、進めた直後の確かめで落ちる（上の変異の確かめ）。0.5 秒の待ちは、タイマーのほかに `elm` を終わらせるものがないことの、2 つ目の確かめとして残した。
- 同じ種類を探した範囲: `ElasticLM.Cli.Tests` で偽の時計を進めるすべての箇所（`.Advance(`、`Task.Delay(`、`Task.WhenAny`、`Assert.InRange` を検索した）。
  - `ElmRemoteTextTests.Request_BodyThatKeepsComing_IsReadBeyond100Seconds`: 時計を 60 秒ずつ 2 回進めるだけで、進めた量を確かめない。タイマーができる前に進めても、待ちが 100 秒に届かないだけなので、結果は変わらない。本文の初めから数える誤った製品は、"a" の前にタイマーを作るので、決まって落ちる。
  - `ElmRemoteTextTests.OidcAnswer_ThatStops_EndsAfterThirtySecondsOfTheClock` と `ElmOfflineTests.Process_AnswerThatStops_ExitsThreeAfterThirtySeconds`: 30 秒の期限（`BoundedAnswer` の `CancellationTokenSource`）は要求を送る前に作るので、待ち受けが要求を受け取った（`Held`）ときには、もうある。そのあと決まった量だけ進める。
  - `ElmAdminTests.LoginFile_ReaderHoldsTheOldFile_IsWrittenOnceTheReaderCloses` と `LoginFile_ReplacementFails_LeavesNoNewFile`: 実時間の 10 ミリ秒ごとに時計を 50 ミリ秒進めるが、確かめるのは書き込みの結果だけで、進めた量ではない。前のテストでは、読み手が開いている間に進めるのは 1 秒で、`FileReplacement.Move` の 2 秒の上限に届かないので、製品がいつ試しても、読み手を閉じる前に諦めることはない。後のテストは、2 秒の上限のあとの失敗を待つだけである。
  - `ElmAdminTests` のほかの `Advance` は、`elm` を同じスレッドで動かし、その間には進めない。

### テスト、ゲート

測りの道具を消した木で（同じ環境）:

- `dotnet csharpier check .`、`dotnet build ElasticLM.slnx -c Release -warnaserror`: 通った。
- Cli のテストを丸ごと、Debug で 5 回、Release で 1 回（6 回）: 6 回とも 100 件のうち失敗 0（成功 92、スキップ 8）。a912bf7 でも、レビューの指摘を直した形でも、同じ 6 回を流し、同じ結果だった。
- roadmap の行の ID を突き合わせるテストを含む `ElasticLM.Licensing.Tests`（Debug）: 429 件のうち失敗 0（成功 424、スキップ 5）。
- `./build.ps1` は流していない（手元に Node.js 24 と Corepack がない）。CI が流す。

## 退けた案

- **決定:** `elm` が次の読みのタイマーを作ったことを偽の時計で知ってから、100 秒より 1 ミリ秒少なく、そして 1 ミリ秒を決まった量で進め、そのたびにタイマーが切れたかを確かめ、終わり方を確かめる（`testing.mdc` の、上限ちょうどとその 1 つ先）。
- **理由:** §16 の文（バイトが 100 秒来なければ終わる）は、100 秒の手前で切れず、100 秒で切れることで確かめられる。タイマーができたあとに進め、進めた直後に切れたかを読めば、どちらも実時間の速さによらない。
- **却下した案:** 進めた秒数の範囲を広げる（例えば 100 から 120）。遅いランナーでの落ち方は減るが、範囲の上は実時間で決まるままで、101 秒より長い製品の待ちを見つけられなくなる。
- **却下した案:** ループの実時間の間隔を長くする。落ちにくくなるだけで、何回回ったかを確かめることは変わらない。
