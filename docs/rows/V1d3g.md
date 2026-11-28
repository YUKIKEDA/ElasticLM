# V1d3g（Issue #529）: `TlsChainSelfCheckTests.Run_CallerCancels_ThrowsTheCancellation` が、ときどき取り消しを投げずに返る原因を調べて直す

## Grill の記録

持ち主の決定（2026-11-28、Issue #529 のコメント）で行を足し（PR #530）、すぐ始めた。原因は製品の側にあり、直し方は持ち主に聞いた（問 3g-1）。設計の独立レビューの指摘は D1〜D9、PR #531 の独立レビューの指摘は R1〜R7 で、D1、D4、R1、R2 は問 3g-2 として持ち主に聞いた（下の「持ち主の決定」と「同じ種類を探した範囲」）。

### 落ち方

`TlsChainSelfCheckTests.Run_CallerCancels_ThrowsTheCancellation`（`tests/ElasticLM.Server.Tests`）が、PR #523 の run 46109315592、`build.ps1 (windows-latest)` の Release（job 140869448350、頭は 7c8c7e9）で、37 ミリ秒で 1 回落ちた。文は「Assert.ThrowsAny() Failure: No exception was thrown / Expected: typeof(System.OperationCanceledException)」（TlsChainSelfCheckTests.cs:253）。PR #523 は `ElmRemoteTextTests.cs` と記録だけを変え、サーバーにもこのテストにも触れていない。落ちた job は走らせ直した。

テストは、何も送らない TCP の待ち受けに `TlsChainSelfCheck.RunAsync` を向け、待ち受けが接続を受けたら呼び出し側のトークンを取り消し、`OperationCanceledException` を待つ。「No exception was thrown」は、`RunAsync` が取り消しを投げずに、文か null を返したことを表す。

### 原因

dotnet/runtime release/10.0 の次のソースを読んだ（ファイルと関数で示す）。

- `src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs`: `ConnectAsync(EndPoint, CancellationToken)`、`AwaitableSocketAsyncEventArgs.ConnectAsync`、`ThrowException`、`CreateException`。
- `src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs`: `CancelConnectAsync`。
- `src/libraries/System.Net.Sockets/src/System/Net/Sockets/NetworkStream.cs`: コンストラクターと `WrapException`。
- `src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs`: `Dispose(bool)`、`SetToDisconnected`、`Connected`。

読んで分かったこと:

- `Socket.ConnectAsync(EndPoint, CancellationToken)` の取り消しは `SocketAsyncEventArgs.CancelConnectAsync` を呼ぶ。この関数は、操作がまだ `InProgress` のあいだ、ソケットを閉じる（`_currentSocket?.Dispose()`。コメントは「cancel it by closing the socket」）。
- 待ち受けが接続を受けた時点で、システムの接続は済んでいる。それでも、このプロセスが接続の完了をまだ処理していなければ（混んだランナーで起きうる）、テストの取り消しはソケットを閉じる。
- 接続の待ちが取り消しになるのは、誤りが `OperationAborted` か `ConnectionAborted` で、トークンが取り消されているときだけである（`AwaitableSocketAsyncEventArgs.ThrowException`）。接続が済んでいれば、待ちは成功で返り、次の手順が閉じたソケットで誤りになる。`NetworkStream` を作るときの `!socket.Connected` の `IOException`（`Dispose` が `SetToDisconnected` を呼ぶ）、書き込みの `IOException`（`NetworkStream.WrapException` が包む）、`OperationAborted` 以外の `SocketException` のどれかである。
- `RunAsync` の `catch (SocketException)` と `catch (Exception e) when (e is IOException or AuthenticationException)` は、呼び出し側のトークンを見なかった。そのため、これらの誤りから「… the TLS handshake with {address} failed.」などの文を返していた。

これは、§13.5 の「呼び出し側の取り消し（`StartAsync` の `CancellationToken`）は、この文にせず、取り消しの例外のまま上げる」に反する。PR #530 の §20.7 の行は「§13.5 は取り消しの振る舞いを書いていない」と書いたが、誤りだった（§13.5 の「答えが得られないとき」の項に書いてある）。この PR で §20.7 の行を直した。

原因は、ソースの読みと、下の決まって起こした再現で特定した。そのままの競合は、手元では 9,000 回で 1 回も起きなかった（下の測った事実）。CI で落ちた 1 回がどの誤りの道を通ったか（`NetworkStream` を作るときか、書き込みか、ソケットの誤りか）は、記録が残っていないので分からない。直しは誤りの種類によらないので、どの道でも同じに働く。

今の製品では起きない。`elm-server` と `elm-service` の `Program.cs` は、`StartAsync` に取り消せるトークンを渡していない（PR #519 の Windows サービスのマージのあとも同じ）。

### 測った事実

Windows 11（build 26300）、SDK 10.0.404、Release。

1 つ目の測りは、`docs/529-roadmap-row`（131b07d）から切ったこの行のブランチで、直す前の製品で取った。

- 手を加えない製品とテストで、このテストの形（待ち受けを作る、`RunAsync` を始める、接続を受ける、取り消す、結果を読む）を流した。1 つのプロセスで 1 回ずつ続けて 3,000 回を 1 組とし、3 組流した。組の違いは取り消しの前の空回しで、入れない組、0〜2,000 回の組、0〜20,000 回の組である。9,000 回のすべてで `TaskCanceledException` か `OperationCanceledException` が投げられ、文は 1 回も返らなかった。測りのためのテストは、測りのあとで消した。32 本を並べて流した試しは、ループバックのポートを使い切って接続が受けられずに止まったので、数えていない。
- 決まって起こす再現: 製品の `RunAsync` の `ConnectAsync` のあとに、呼び出し側のトークンの取り消しを待ってからソケットを閉じる変更を入れた。これは、システムが接続を済ませたあとに `CancelConnectAsync` が走ったときと同じ状態である。変更は測りの間だけ入れ、測りのあとで元に戻した（コミットしていない）。直す前の製品では 3 回とも、CI と同じ「Assert.ThrowsAny() Failure: No exception was thrown / Expected: typeof(System.OperationCanceledException)」で落ちた。`RunAsync` が返した値は「The check that Windows sends the intermediate CA certificates of the TLS certificate 'chain.crt' did not get an answer: the TLS handshake with 127.0.0.1:{port} failed.」だった。変更を入れずに流すと通った（`TaskCanceledException`、1 回）。

2 つ目の測りは、問 3g-2 の時間の上限の直しまでをコミットした 0147879（`origin/main` の 4758d74 を取り込んだあと）で取った。結果は下の変異の確かめにある。

3 つ目の測りは、`ElmServer.RemoveLeftSocketAsync` の接続の窓を、テストで決まって起こせるかの確かめである（0147879 の木で、測りの間だけ置いたテストで取り、消した）。Windows で `AF_UNIX` のソケットに `ConnectAsync` を 20 回呼び、返った直後の `ValueTask.IsCompleted` を読んだ。待ち受け（`Listen(1)`、受けない）への 20 回は、20 回とも返った時点で終わっていた（初めの 5 回は成功、あとの 15 回は `NoBufferSpaceAvailable`）。待ち受けのないソケットのファイルへの 1 回も、返った時点で `ConnectionRefused` で終わっていた。接続が待ちになる間がないので、取り消しを接続の途中に入れるテストは組めない。

### 持ち主の決定

問 3g-1（2026-11-28、Issue #529 のコメント）: 案 A（推奨のとおり）。製品を直す。

問 3g-2（2026-11-28、Issue #529 のコメント）: 推奨のとおり、同じ種類の 2 か所もこの PR で直す。10 秒の上限が切れたあとの IO の誤りとソケットの誤りは `did not finish within 10 seconds` の文にする。`RemoveLeftSocketAsync` は、呼び出し側の取り消しを取り消しのまま上げる。ただし、`RemoveLeftSocketAsync` で決まって起こすテストが組めないときは、それだけを名前のある行に移す。

### 直したこと

- `TlsChainSelfCheck.RunAsync`（`src/ElasticLM.Issuing`）: 確かめが `OperationCanceledException` でない例外で終わり、`catch` の条件を見るときに呼び出し側のトークンが取り消されていれば、例外の種類によらず、その例外を内側に持つ `OperationCanceledException` を投げる。この `catch` は、時間の上限の `catch` のあと、ソケットの誤りと IO の誤りの `catch` の前にある。
  - 振る舞いが変わるのは、呼び出し側が取り消したときだけである。
  - 時間の上限（10 秒）の文と、答えが得られないときの文（`failed`、`failed (socket error {SocketError})`、`did not finish within 10 seconds`）は変わらない。時間の上限の `catch` の条件（上限が切れ、呼び出し側は取り消していない）も変えていない。
  - XML ドキュメントに `<exception cref="OperationCanceledException">` を足した。
  - サーバーが送った証明書の写しは、確かめが例外で終わる道でも破棄する（PR #531 のレビューの R4。今までは、取り消しの例外がそのまま上がる道でも破棄していなかった）。
- design §13.5 の「答えが得られないとき」の項に、この扱いの文と、その理由の文を足した。
- テスト `Run_HandshakeFailsAfterTheCallerCancels_ThrowsTheCancellation` を足した。接続を RST で切る場合と、TLS でないバイト（`HTTP/1.1 400 Bad Request` と空の行）で答える場合の 2 つの場合を流す（R3）。
  - テストは、`RunAsync` を始めてから、呼び出し側のトークンに自分のコールバックを登録する。dotnet/runtime release/10.0 の `src/libraries/System.Private.CoreLib/src/System/Threading/CancellationTokenSource.cs` では、`Register` が新しいコールバックをリストの先頭に足し、`ExecuteCallbackHandlers` が先頭から取って呼ぶ（その要約に「The handlers are invoked synchronously in LIFO order.」とある）。公開の `Cancel` のドキュメントは順を約束していない。この順のために、テストのコールバックが走る間、トークンは取り消された状態で、確かめの待ち（リンクしたトークン）はまだ取り消されていない。
  - コールバックの中で、受けた接続を RST で切るか（`LingerOption(true, 0)`）、TLS でないバイトを送り、確かめが終わるまで待つ（タスクの待ちのハンドルで、実時間の 30 秒を上限にする）。確かめはハンドシェイクの段で、RST なら `IOException`、TLS でないバイトなら `AuthenticationException` で終わる。テストは、`OperationCanceledException` が投げられ、その内側の例外がその型であることを確かめる。
  - このテストが決まって起こすのは、ハンドシェイクの段の道である。CI で落ちた接続の段の道（接続が成功で返ってから、閉じたソケットで誤りになる）は、同じ `catch` を通る。この道は、上の決まって起こす再現でだけ通した。
  - 内側の例外の確かめがあるので、コールバックの順が変わってリンクしたトークンが先に取り消されれば、テストは素通りせずに落ちる。
- 時間の上限（問 3g-2）: 上限が切れ、呼び出し側は取り消していないときに、確かめが `OperationCanceledException`、`IOException`、`SocketException` のどれかで終わったら、`did not finish within 10 seconds` の文にする（今までは `OperationCanceledException` だけだった）。§13.5 に 1 文足した。
- テスト `Run_ConnectionFailsAfterTheLimit_SaysItDidNotFinish` を足した。接続を RST で切る場合（`IOException`）と、待ち受けのないポートへの接続（`SocketException` の `ConnectionRefused`）の 2 つの場合を流す。
  - テストの時計は偽の時計に読みも数えも任せ、確かめが上限のために作るタイマーの状態を取っておく。dotnet/runtime release/10.0 の `CancellationTokenSource` は、`TimeProvider` で作るタイマーの状態に自分を渡すので、それが上限の `CancellationTokenSource` である（テストは、取っておけたことを確かめる）。
  - テストは、上限のトークンにあとからコールバックを登録し、時計を 10 秒進める。上と同じ順のために、このコールバックが走る間、上限は切れた状態で、確かめの待ちはまだ取り消されていない。RST の場合はその間に接続を切る。待ち受けのないポートの場合は、Windows がループバックの断られた接続を約 2 秒試し直してから失敗するので（M4 と M5 の落ち方で 2.07〜2.09 秒）、その間に接続の待ちが `ConnectionRefused` で終わる。
  - コールバックの順が変われば、確かめの待ちが先に取り消され、テストは上限の文で通る。順が保たれていることは、M4 で分かる。
- `Run_CallerCancels_ThrowsTheCancellation` は変えていない。直したあとは、取り消しがハンドシェイクの待ちに届いても、接続の段で閉じたソケットの誤りになっても、取り消しを投げる。

### 変異の確かめ

0147879 で、変異ごとにビルドの終了コードが 0 であることを確かめてから、それぞれ 3 回流した。変異は、ファイルの文字を書き換えて入れ、最後に元の文字を書き戻して、ビルドが通ることを確かめた。`TlsChainSelfCheckTests` は 17 件（Windows では 1 件をスキップ）。

- 変異なし: 3 回とも、失敗 0。
- M1: 呼び出し側の取り消しの `catch` の条件に `&& e is InvalidCastException` を加え、当たらないようにした。3 回とも、`Run_HandshakeFailsAfterTheCallerCancels_ThrowsTheCancellation` の 2 つの場合だけが「Assert.ThrowsAny() Failure: No exception was thrown」で落ちた。コールバックの順が上のとおりであることも、これで分かる。
- M2: その条件の `cancellationToken.IsCancellationRequested` を `cancellationToken.CanBeCanceled` にし、取り消していなくても誤りを取り消しにした。3 回とも、`Run_NoTlsAnswer_SaysTheHandshakeFailed` だけが落ちた。
- M3: その条件を `IOException` と `SocketException` だけに狭めた。3 回とも、`Run_HandshakeFailsAfterTheCallerCancels_ThrowsTheCancellation` の TLS でないバイトの場合だけが落ちた。
- M4: 時間の上限の `catch` を、`OperationCanceledException` だけに戻した。3 回とも、`Run_ConnectionFailsAfterTheLimit_SaysItDidNotFinish` の 2 つの場合だけが落ちた。
- M5: 時間の上限の `catch` から `SocketException` を外した。3 回とも、`Run_ConnectionFailsAfterTheLimit_SaysItDidNotFinish` の待ち受けのないポートの場合だけが落ちた。
- 決まって起こす再現を入れた直した製品: `Run_CallerCancels_ThrowsTheCancellation` は 3 回とも通った。
- 決まって起こす再現と M1 を一緒に入れた製品: `Run_CallerCancels_ThrowsTheCancellation` は 3 回とも、CI と同じ「No exception was thrown」で落ちた。

da72eee と 6dd62f4（時間の上限を直す前）でも、変異なし、M1、M2、再現を（6dd62f4 では M3 も）それぞれ 3 回流し、同じ結果だった。

### 同じ種類を探した範囲

製品の `src/` で、`IOException`、`SocketException`、`HttpRequestException`、`AuthenticationException` を受ける `catch` を探し、取り消せるトークンで通信するものを読んだ。`src/` で呼び出し側のトークンで `ConnectAsync` を呼ぶのは、`TlsChainSelfCheck` と `ElmServer.RemoveLeftSocketAsync` の 2 か所である。

- `ElmServer.RemoveLeftSocketAsync`（`src/ElasticLM.Server/ElmServer.cs`）は同じ種類である（設計の独立レビューの D1）。管理 API のソケットのファイルが残っているとき、`StartAsync` のトークンで `socket.ConnectAsync` を呼ぶ。取り消しが接続の窓に入れば、次の `ServerStartException` になりうる。
  - 接続が成功で返れば「Another elm-server is running …」。
  - `OperationAborted` 以外のソケットの誤りなら「The administration socket … cannot be used」。
  - 今の製品は取り消せるトークンを渡さないので起きない。
  - 上の 3 つ目の測りのとおり、Windows では `AF_UNIX` の接続が `ConnectAsync` から返る前に終わり、取り消しを接続の途中に入れるテストは組めない。Linux と macOS では測っていない。問 3g-2 の決定のとおり、これはこの PR では直さず、名前のある行に移す（行を足すかは、持ち主に聞いている）。
- `TlsChainSelfCheck` の時間の上限も同じ種類だった（D4、PR #531 のレビューの R2）。10 秒の上限もリンクしたトークンで接続を取り消すので、上限が接続の窓に入ると、文は `did not finish within 10 seconds` ではなく `failed` になっていた。テスト `Run_ListenerNeverAnswers_EndsAtTheLimit` は、接続を受けた直後に偽の時計で上限を切れさせるので、同じ窓を持ち、`Assert.Equal` の文の比べで同じようにゆれえた（このテストの名前を挙げた Issue はない。`gh issue list --state all --search` で探した）。問 3g-2 の決定のとおり、この PR で直した（上の「直したこと」）。
- `ServerAdminApi.ConnectAsync`（`src/ElasticLM.Cli/ServerAdminApi.cs`）は、ハンドラーの 30 秒の上限で接続を取り消す。上限が接続の窓に入ると、`TimeoutException` ではなく `HttpRequestException` になる。呼ぶ側（`ServerCommands` など）は両方を同じ誤りとして扱うので、出力は変わらない。
- `ActivationClient` と `LicenseClient` の、`OperationCanceledException` を `HttpRequestException` と一緒に受ける `catch` は、取り消しも通信の誤り（`NetworkError`）として返すことを、もともと決めている。誤りの種類によって取り消しが文に変わる形ではない。
- `ElmService.Web.cs` の `EndSessionAsync` は、要求が切られたときを含めて null を返すことを決めている。
- `AdminCommands` と `ServerLicenseCommands` の `catch` は、`elm` の 1 回の要求で、呼び出し側のトークンを受けない。取り消すのは自分の時間の上限だけで、それを通信の誤りとして返すことを決めている（`AdminCommands` の 100 秒。`ServerAdminApi` は `CancellationToken.None` で送る）。
- `ElmServer.StartAsync` と `ElmService.StartAsync` で `app.StartAsync` を囲む `catch (Exception e) when (e is IOException or SocketException)` が受けるのは、待ち受けのバインドの誤りである。取り消しでソケットを閉じる道はない。

## 退けた案

- **決定:** 製品を直す。確かめが `OperationCanceledException` でない例外で終わり、そのとき呼び出し側のトークンが取り消されていれば、例外の種類によらず `OperationCanceledException` を投げる（問 3g-1、案 A）。
- **理由:** §13.5 と `cancellationToken` の約束に製品を合わせる、小さな直しである。振る舞いが変わるのは呼び出し側が取り消したときだけで、時間の上限の文と答えが得られないときの文は変わらない。
- **却下した案:** テストだけを直す（案 B）。待ち受けがクライアントの最初のバイトを読んでから取り消す形にすれば、テストのゆれは消える。しかし、接続の段の取り消しで文を返す製品の振る舞いが、§13.5 に反したまま残る。
- **却下した案:** 案 A に加えて、案 B の形のテストを足す（案 C）。今のテスト `Run_CallerCancels_ThrowsTheCancellation` は、直したあとは、ハンドシェイクの途中の取り消しをもう確かめている。取り消しのあとに確かめがほかの誤りで終わる場合は、足したテストがハンドシェイクの段で決まって確かめる。
- **決定:** 同じ種類の 2 か所も、この PR で直す。`RemoveLeftSocketAsync` は、決まって起こすテストが組めなければ名前のある行に移す（問 3g-2）。
- **理由:** `similar-findings` の「一緒に直す」に合い、時間の上限の直しは数行で、変わるのは上限が接続の窓に入ったときの文だけである。
- **却下した案:** 2 か所とも新しい行に移し、この PR は呼び出し側の取り消しだけを直す。同じファイルの同じ形が、別の行まで残る。
