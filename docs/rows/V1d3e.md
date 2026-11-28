# V1d3e（Issue #454）: `BackupTests.Run_RefusedInputs_FailWithTheReason` が、ときどき `elm.db` の長さの比べで落ちる原因を調べて直す

## Grill の記録

持ち主の決定（2026-11-28、Issue #454 のコメント）で行を足し、すぐ始めた。原因はテストの側だけにあり、製品の振る舞いも設計の文も変えないので、問は立てていない。

### 落ち方

`BackupTests.Run_RefusedInputs_FailWithTheReason` が、BackupTests.cs:145 の `Assert.Equal(databaseLength, new FileInfo(test.FilePath("elm.db")).Length)` で、ときどき落ちる。Issue #454 の本文とコメントにある記録: ubuntu-latest で 122880 から 237568（PR #447、run 43581708386）、macOS で 4096 から 249856（PR #455、run 44760943926）、ubuntu-latest で 4096 から 249856（PR #469、run 44921721147、PR #519、run 46052147649）、手元の Windows で 2 回（PR #476 と PR #497。PR #497 では 8192 から 249856）。どの PR も、このテストもバックアップのコードも変えていない。走らせ直すと通った。

### 原因

テストは、`setup` のあとに `elm.db` の長さを読み、既にあるファイルとデータベースそのものへのバックアップを断らせてから、もう一度長さを読んで比べていた。

- `setup` は WAL のデータベースに書き、その接続は閉じずに Microsoft.Data.Sqlite のプールに戻る。書いた中身は、自動のチェックポイント（既定で 1,000 ページ）に届かないので `elm.db-wal` にあり、`elm.db` は 1 ページか 2 ページ（4,096 か 8,192 バイト）のままである。
- SQLite は、データベースの最後の接続が閉じるときに WAL をデータベースのファイルに書き戻し、`elm.db-wal` を消す。
- xUnit v3 は、ほかのクラスのテストを同時に流す（`ElasticLM.Service.Tests` は並べ方を変えていない）。`TestService.DisposeAsync` は、どのテストでも `SqliteConnection.ClearAllPools()` を呼び、これはプロセスのすべてのプールの、使われていない接続を閉じる。
- その呼び出しが、このテストの 2 回の読みの間に入ると、このテストのプールに残った接続が閉じ、WAL が `elm.db` に書き戻されて、ファイルが 249,856 バイトになる。

`Backup.RunAsync` は、データベースのファイルがあることと、バックアップのファイルがないことを確かめてから接続を開くので、断るこの 2 つの入力ではデータベースを開かない（`src/ElasticLM.Service/Backup.cs`）。長さが変わったのは、バックアップのせいではなかった。

### 測った事実

測ったのは、`origin/main` の 0f3e24e から切ったこの行のブランチ、Windows 11（build 26300）、SDK 10.0.404、Debug、このテスト 1 件だけを流した。測りの間だけ、テストの 2 回目の読みの前に、環境変数で入る `SqliteConnection.ClearAllPools()`（ほかのテストの片づけと同じ呼び出し）と、長さを書き出す行を足し、測ったあと消した。

- 今までの形で、呼び出しを入れないと通った。入れると「Expected: 4096 / Actual: 249856」で落ち、そのとき `elm.db-wal` はなかった（書き戻されて消えた）。CI の落ち方と同じ数である。
- 直した形で、同じ呼び出しを入れても、入れなくても通った（入れて 2 回、入れずに 1 回）。
- 直した形の比べが変化を見つけるかの確かめ: 2 回目の読みの前にテストの接続で `CREATE TABLE` を流してプールを空けると、バイトの比べで落ちた（データベースのヘッダーの変更の回数が 2 から 3 に変わった）。

ふだんの落ち方（同時に流すほかのテストの片づけが間に入る回数）は、この行では数えていない。上の記録のとおり、CI でも手元でも、Service のテストを丸ごと流したときに、何度か起きていた。

### 直したこと

- `BackupTests.Run_RefusedInputs_FailWithTheReason`: `setup` のあと、`elm.db` を読む前に自分で `SqliteConnection.ClearAllPools()` を呼び、WAL を `elm.db` に書き戻す。そのあとは、どのテストが `ClearAllPools()` を呼んでも、閉じる接続がないので `elm.db` は変わらない。比べは、長さの代わりにファイルのバイト全体にした（テストの Expected の「the file unchanged (the database too)」を、長さだけでなく中身まで確かめる）。Preconditions に、接続を閉じて WAL をファイルに入れてあることを書いた。
- 同じ種類を探した範囲: `ElasticLM.Service.Tests` で SQLite のファイルの長さや中身を、プールの接続が閉じる前と後に読むテストは、このテストだけである（`elm.db`、`-wal`、`-shm`、`.Length`、`ClearAllPools` を検索した）。`Run_WhileAWriteIsOpen_CopiesTheCommittedRows` の `elm.db-wal` の長さの確かめは、書き手の接続を開いたまま読むので、ほかのテストの `ClearAllPools()` は WAL を書き戻さない。Microsoft.Data.Sqlite を使うテストのプロジェクトは、ほかにない（E2E のテストは別のプロセスの `elm-service` を使う）。

### テスト、ゲート

測りの道具を消した木で（同じ環境）:

- `dotnet csharpier check .`、`dotnet build ElasticLM.slnx -warnaserror`（Release と Debug）: 通った。
- Service のテストを丸ごと、1 つのプロセスで、Release と Debug を交互に 5 回ずつ（10 回）: 10 回とも 577 件のうち失敗 0（成功 468、スキップ 109。スキップは PostgreSQL の件と、Windows で流さない `Run_OnUnix_CreatesTheFileForItsOwnerOnly`）。
- そのあと、テストのコメントだけを直した木で: `dotnet csharpier check .`、`dotnet build ElasticLM.slnx -c Debug -warnaserror`、`BackupTests`（9 件、スキップ 1）、roadmap の行の ID を突き合わせるテストを含む `ElasticLM.Licensing.Tests`（402 件、失敗 0）が通った。

## 退けた案

- **決定:** テストが、`elm.db` を読む前に自分でプールを空け、ファイルのバイトを比べる。
- **理由:** テストが比べるのは、断られたバックアップがデータベースのファイルを変えないことである。プールに残った接続を先に閉じれば、WAL の書き戻しはテストの比べの外で済み、ほかのテストがいつ片づけても結果は変わらない。
- **却下した案:** `TestService.DisposeAsync` の `ClearAllPools()` を、自分のデータベースのプールだけを空ける `SqliteConnection.ClearPool` に変える。ほかのテストからの割り込みは減るが、このテストの比べは、プールの接続がいつ閉じるか（ほかのテストの中の `ClearAllPools()` もある）にまだ頼る。
- **却下した案:** このテストを並べないコレクション（`DisableParallelization`）に入れる。ほかのテストの片づけは入らなくなるが、Microsoft.Data.Sqlite がプールの接続をいつ閉じるかに、比べがまだ頼る。
