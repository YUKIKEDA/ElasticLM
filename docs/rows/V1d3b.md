# V1d3b（Issue #392。Issue #399 と #414 の点を含む）: テストの助け `ProblemServer` が、ときどき、片づけのときに例外を出して、関係のないテストを落とす原因を調べて直す

## Grill の記録

- 測った事実（Grill、`origin/main` の 5ce2174、Windows 11）:
  - `ProblemServer`（`tests/ElasticLM.Client.Tests`。`ElasticLM.Native.Tests` も結び付けて使う）の `DisposeAsync` は「取り消し → 待ち受けを止める → 受け入れのループの終わりを待つ」の順だった。答えを返したループが次の `AcceptTcpClientAsync` を呼ぶ前に待ち受けが止まると、`InvalidOperationException`「Not listening」が出る。ループはこれを止まった印として受けないので、`DisposeAsync` から投げられる（CI の実行 43372189421 の 1 回目、ジョブ 131710490773、macOS の Debug、`ServerAnswerTests.cs` の 353 行）。ループを写した試しで、今の順は 3000 回のうち 40 回、直した順は 0 回だった。
  - 同じ形（取り消し → 止める → 待つ）が `RedirectTests` の `RedirectingListener`（`tests/ElasticLM.Service.Tests`）にもある。こちらは `AggregateException` を受けていたので落ちない。
  - `TestCertificateChain.Listen`（`tests/ElasticLM.Server.Tests`）は、取り消しのあとに `SocketException`（`OperationAborted`）で終わることがあり、`OperationCanceledException` しか受けていなかった（手元で 30 回のうち 1 回落ちた）。
  - 空いているポートを探して閉じてから使う助け（E2E の `ServiceProcess.FreePort` と、その使い手の `ServerProcess` と `AdminSocketTests`、`TlsChainTests` の `FreePort`、`ElmServiceTests` の 2 つのテスト、`tests/native/run.ps1` の `Get-FreePort`）は、閉じてから起動までの間に、別のプログラムがそのポートを取りうる。E2E の `ListenTests.Server_ListenPortHeld_ExitsWithOneLine` が、先にポートを取れずに `Address already in use` で落ちた（Issue #399。CI の実行 43388679229 の 1 回目、ジョブ 131764078550、macOS）。
  - 同じポートで起動し直すテスト（`LeaseClientTests`、`BorrowAndAdminTests`、`LeaseAnswerTests`、`ElasticLM.Native.Tests` の `LeaseTests`、`ElasticLM.Server.Tests` の `ListenTests`）にも、止めてから起動し直すまでの間がある。Windows では、ポート 0 への bind が渡すポートはマシン全体で進み、2,000 回の bind で、すぐに同じポートが戻ることはなかった。これらのテストは、手元の 30 回と CI の 600 回で、一度も落ちていない。
  - 「address already in use」の行は、通る `ListenTests` の出力にも出る（テスト自身がポートを持って、聞けない起動を確かめるため）。この行だけでは、落ちた原因とは言えない。
  - `LeaseClientTests.Answer_NonceHeaderOutOfRange_IsTakenAsNoNonce` が 1 回落ちた（Issue #414）とき、助けの `Acquired` は「`Acquired` を期待したが `Failed`」としか言わず、誤りのコードと文が残らなかった。
- 持ち主が決めたこと（Issue #392 のコメント「V1d3b の Grill」、2026-11-14。すべて推奨案 A）:
  - 3b-1: `ProblemServer` は「取り消し → 受け入れのループの終わりを待つ → 待ち受けを止める」の順にする。`RedirectingListener` もそろえる。
  - 3b-2: `TestCertificateChain.Listen` は、取り消しのあとの `SocketException`（`OperationAborted`）を止まった印として受ける。
  - 3b-3: 起動の前にポートを決める必要のない所は、すべてポート 0 で起動し、聞いているポートを読み返す。E2E の `Server_ListenPortHeld` は、テストが先に取ったポートを持ったまま渡す（Issue #399）。
  - 3b-4: 同じポートで起動し直すテストは、間（窓）を残し、remarks に窓と測った回数を書く。
  - 3b-5: 閉じたポートをつながらない先として使うテスト（`ElmOfflineTests`、`TlsChainSelfCheckTests`、`ServerLicenseTests`）は残し、remarks に前提を書く。
  - 3b-6: 再現は試しの 3000 回と、本物のテストの手元のくり返し。macOS は CI に任せ、回数を PR に書く。
  - 3b-7（Issue #414）: `LeaseClientTests` の `Acquired` と `Failed` の助けを、`Failed` のときに誤りのコードと文を出す形にし、同じ形の助けもそろえる。Issue #414 は開いたままにする。
- こちらが決めたこと:
  - ポートの読み返し方。プロセスの中のテスト（`TlsChainTests` と `ElmServiceTests` の、起動が確かめで止まる 2 つずつ）は、起動が例外で終わるので `ElmServer.PublicUrls` を読めない。ホストが聞き始めたときに書く `Microsoft.Hosting.Lifetime` の「Now listening on: https://127.0.0.1:{port}」を、テストの記録の提供者から読む（`RecordingLoggerProvider.ListeningPort`）。発行した製品（E2E と `tests/native/run.ps1`）は、運用のログの `server.started: listening on port {Port}` と `service.started: listening on {Url}` を読む。E2E の `AdminSocketTests` の、構成で足した http の待ち受けは、同じく「Now listening on: http://127.0.0.1:{port}」を読む。
  - E2E の `ServerProcess` は、テストがポートを渡さなければ、最初の起動をポート 0 で行い、`server.started` の行のポートを、それからの起動の `listen` にする（配置したサーバーの `listen` と同じく、クライアントが同じ所に見つける）。起動し直しの窓は、クライアントがサーバーを同じ所に見つける必要のある `OnPremisesTests` だけに残り、remarks に書いた（3b-4）。`AdminSocketTests` の、殺して起動し直すテストと、1 つのデータのフォルダーに 2 つ起動するテスト（5 回）は、`Twin`（自分でポートを選ぶ新しいサーバー）を使い、窓をなくした。
  - `tests/native/run.ps1` は、`elm-service` と `elm-server` の標準出力を作業のフォルダーのファイルに向け、共有で読みながら開始の行を待つ（上限 30 秒）。これまでの `/health` と `/v1/server/license` の待ちは、開始の行が答え始めたあとに書かれる（設計 §18.1）ので、それに替えた。
  - 3b-7 の「同じ形の助け」: `ElasticLM.Server.Tests` の `BorrowAndAdminTests.Acquired` と `LeaseAnswerTests.Acquired`、E2E の `ServiceFlowTests.Activated`。`LeaseClientTests.Failed` は、期待するコードを引数に取り、違うときに実際のコードと文を出す形にした。`ElasticLM.Client.Tests` の `ActivationClientTests.Activated` と `Failed`、`ElasticLM.Service.Tests` の `HostedLeaseClientTests.Acquired` は、すでに結果の全体（コードと文を含む）を例外の文にしているので変えていない。
- 再現と確かめ（実装の head、Windows 11、SDK 10.0.403、Release。macOS は CI に任せた）:
  - 試し（`ProblemServer` の受け入れのループと止め方を写した、作業のフォルダーの外のプログラム。Debug。クライアントが答えを読み終えた直後に止める。コミットしていない）: 今の順（取り消し → 止める → 待つ）は 3000 回のうち 392 回と 684 回（2 回流した）、直した順（取り消し → 待つ → 止める）は 3000 回のうち 0 回と 0 回、`Not listening` を出した。ほかの例外は 0 回。
  - 本物のテストのくり返し（直したあと。テストの実行ファイルを `-method` で 1 つのテストに絞って流した）: `ServerAnswerTests.ServerLicenseRequest_ProblemAnswer_GivesTheListedCodeAndNeverTheDetail` を 1000 回、`ElasticLM.Native.Tests` の `ActivationTests.Activate_ProblemNamesElmOk_Returns99AndNoLicense` と `LeaseTests.AcquireAndBorrow_ServerLicenseProblemNamesElmOk_Return99AndNoLease` を 300 回ずつ。すべて通った。`TlsChainTests`（13 のテスト。ポート 0 にした 2 つと、`TestCertificateChain.Listen` を使うもの）を 30 回、すべて通った。
  - E2E（`tests/ElasticLM.E2E/run.ps1 -Bin`、NativeAOT で発行した win-x64、SQLite）: 24 のうち 20 が通り、4 は飛ばした（管理者の権限か Windows でない OS が要るもの）。失敗は 0。C のテスト（`tests/native/run.ps1`、同じ発行物）: 93 の `ok`、`not ok` は 0。どちらも、ポートを開始の行から読む形で通った。
- 同じ種類を探した範囲（`similar-findings`）:
  - 受け入れのループ（`AcceptTcpClientAsync`、`AcceptAsync`、`AcceptSocketAsync` を tests の下で探した）: `ProblemServer`、`RedirectingListener`、`TestCertificateChain.Listen` は直した。`RawAnswerServer`（`ElasticLM.Client.Tests`）は「取り消し → 破棄 → 待つ」の順だが、ループが `InvalidOperationException` も止まった印として受けるので落ちない。`FakeAdminSocket`（取り消しを使わず、破棄のあとの `SocketException` と `ObjectDisposedException` を受ける）、`ServerLicenseTests` の 7071 番の待ち受け（`InvalidOperationException` も受ける）、`ElmOfflineTests.RunAgainstAsync`（1 回だけ受ける）、`TlsChainSelfCheckTests` と `TlsChainTests` と `ElmServiceTests` の 1 回だけ受ける待ち受けは、同じ形を持たない。
  - 空いているポートを探して閉じる助け（`TcpListener(IPAddress.Loopback, 0)` を tests の下で探した）: 上の 3b-3 の所は直し、3b-4 と 3b-5 の所は remarks を書いた。ほかは、テストが持ったまま使う（`ListenTests` の holder、`ServiceUsageLogTests`、`ElmServiceTests` の holder と hole）。
- 行の外で見つけたこと: なし。

## 退けた案

- **決定:** E2E の `ServerProcess` で、ポートを渡さないサーバーは、2 回目からの起動も、最初の起動が読んだポートを使う（3b-4 の窓を残す）。
- **理由:** `OnPremisesTests` は、サーバーを殺して起動し直したあと、同じ `LicenseClient` が同じ URL でサーバーを見つけて、リースを取り戻すことを確かめる。配置したサーバーの `listen` も変わらない。
- **却下した案:** 起動のたびにポート 0 で起動する。窓はなくなるが、クライアントの URL が変わり、上の確かめが別のものになる。
- **決定:** `AdminSocketTests` の、殺して起動し直すテストと、1 つのデータのフォルダーに 2 つ起動するテストは、新しい `ServerProcess`（`Twin`）で起動し直す。
- **理由:** どちらも、クライアントが同じ所にサーバーを見つける必要がない（確かめは `admin.sock` とロックのファイルを通る）。窓を残す理由がない。
- **却下した案:** 同じ `ServerProcess` で起動し直す（窓が残る）。
