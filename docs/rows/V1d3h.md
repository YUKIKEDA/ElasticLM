# V1d3h（Issue #533）: `elm-server` の起動のとき、残った管理 API のソケットのファイルを確かめる接続が、呼び出し側の取り消しを起動の失敗の文に変えうることを直す

## Grill の記録

行は、V1d3g の作業（Issue #529、PR #531）が見つけ、持ち主の決定 3g-2（2026-11-28、Issue #529）で移した。Grill は 2026-11-28 に Issue #533 のコメントで行い、持ち主は問 3h-1〜3h-5 をすべて推奨のとおりに決めた。macOS の数（下の「macOS の待ち受けの列」）は、この行の作業でソースを読み直して 192 から 128 に直し、持ち主に確かめた（2026-11-28、Issue #533 のコメント）。

### 読んだソース

dotnet/runtime は、タグ v10.0.15（[a3788b7](https://github.com/dotnet/runtime/tree/a3788b71495457354b64ca8040b6ded5ff6ca4f9)）の `src/libraries/System.Net.Sockets/src/System/Net/Sockets/` を読んだ。

トークンの登録から実際に呼ばれる関数:

- 入口: `Socket.ConnectAsync(EndPoint, CancellationToken)`（[Socket.Tasks.cs L86-101](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs#L86-L101)）。取り消されていれば、接続の前に `ValueTask.FromCanceled` を返す（L88-91）。そのあと `AwaitableSocketAsyncEventArgs.ConnectAsync`（[L1187-1210](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs#L1187-L1210)）が、`Socket.ConnectAsync(e, userSocket: true, saeaMultiConnectCancelable: false, cancellationToken)` を呼ぶ。呼び出しの中で終われば、結果をそのまま返す（成功か、`CreateException` の例外）。待ちになったときだけ `_cancellationToken` を覚え、待ちの誤りが `OperationAborted` か `ConnectionAborted` で、トークンが取り消されていれば取り消しの例外にする（`ThrowException`、[L1351-1362](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs#L1351-L1362)）。
- `Socket.ConnectAsync(SocketAsyncEventArgs, …)`（[Socket.cs L2882-2984](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L2882-L2984)）: `saeaMultiConnectCancelable` が false なので、`StartOperationConnect` は `_multipleConnectCancellation` を作らない（[SocketAsyncEventArgs.cs L602-607](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L602-L607)）。ConnectEx を使うのは、ストリームで `AF_UNIX` でないときだけである（[Socket.cs L2964-2967](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L2964-L2967)）。
- Windows の `AF_UNIX`: `DoOperationConnect`（[SocketAsyncEventArgs.Windows.cs L289-296](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Windows.cs#L289-L296)）は同期の `SocketPal.Connect` で、トークンを使わない（使わない引数の警告 IDE0060 を抑えている）。
- Windows の TCP: `DoOperationConnectEx`（[L298-326](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Windows.cs#L298-L326)）は `ProcessIOCPResult`（[L183-253](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Windows.cs#L183-L253)）を通る。操作が待ちになったときだけ、トークンに `CancelIoEx` を登録する（[L191-218](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Windows.cs#L191-L218)）。ソケットは閉じない。`TlsChainSelfCheck` の接続はこの道である。
- Unix: `DoOperationConnect` と `DoOperationConnectEx`（[SocketAsyncEventArgs.Unix.cs L74-92](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Unix.cs#L74-L92)）は `SocketAsyncContext.ConnectAsync`（[SocketAsyncContext.Unix.cs L1539-1587](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncContext.Unix.cs#L1539-L1587)）を呼ぶ。待ちになるのは `connect` が `EINPROGRESS` を返したときだけで（[SocketPal.Unix.cs L697-710](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketPal.Unix.cs#L697-L710)）、ほかの誤りはその場で返す。待ちになった操作にだけ、トークンに `TryCancel` を登録する（[SocketAsyncContext.Unix.cs L885-891](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncContext.Unix.cs#L885-L891)）。`TryCancel`（[L204-240](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncContext.Unix.cs#L204-L240)）は操作の状態を変えて `OperationAborted` にするだけで、ソケットは閉じない。
- `SocketAsyncEventArgs.CancelConnectAsync`（[SocketAsyncEventArgs.cs L609-625](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L609-L625)）は、操作が進んでいるあいだソケットを閉じる（`_currentSocket?.Dispose()`）。これを呼ぶのは、公開の静的な `Socket.CancelConnectAsync`（[Socket.cs L3030-3035](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L3030-L3035)）だけである。`System.Net.Sockets/src` の 49 のファイルをすべて取って探した。トークンで取り消すときには呼ばれない。
- 多重接続（`DnsEndPoint`）: トークンは `DnsConnectAsync` にそのまま渡る（[Socket.cs L2923](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L2923)。`_multipleConnectCancellation` は null なので、[SocketAsyncEventArgs.cs L687-692](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L687-L692) はトークンを替えない）。アドレスごとの接続にも同じトークンを渡し（[L783](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L783)）、取り消されていれば `OperationAborted` にして終える（[L794-798](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L794-L798)）。`finally` は、`OperationAborted` か `OperationCanceledException` のとき、呼び出し側のソケットを `Dispose` する（[L834-844](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L834-L844)）。つまり `DnsEndPoint` の道だけは、取り消しのあとソケットが閉じる。ただし `CancelConnectAsync` ではなく多重接続の `finally` による。`TlsChainSelfCheck`（`IPEndPoint`）も `RemoveLeftSocketAsync`（`UnixDomainSocketEndPoint`）も、この道を通らない。
- 計測のイベント: 接続が呼び出しの中で終わると、`FinishOperationSync` が `AfterConnectAcceptTelemetry` を呼び（[SocketAsyncEventArgs.cs L1049-1067](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L1049-L1067)、[L222-233](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs#L222-L233)）、`SocketsTelemetry.AfterConnect` が、誤りなら `ConnectFailed`、どちらでも `ConnectStop` を書く（[SocketsTelemetry.cs L145-172](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketsTelemetry.cs#L145-L172)。イベントは [L35-57](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketsTelemetry.cs#L35-L57)）。どちらも、接続する流れのスレッドで、`ConnectAsync` が返る前に書かれる。
- イベントの届け方（`System.Private.CoreLib`）: `WriteEvent` は `WriteEventCore` から `WriteEventWithRelatedActivityIdCore` を通り（[EventSource.cs L1334-1340](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L1334-L1340)、[L1369](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L1369)）、`WriteToAllListeners`（[L1436](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L1436)、[L2140-2185](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L2140-L2185)）から `DispatchToAllListeners` を呼ぶ。`DispatchToAllListeners` は、書く側の呼び出しの中で、各 listener の `OnEventWritten` を順に呼ぶ（[L2187-2215](https://github.com/dotnet/runtime/blob/a3788b71495457354b64ca8040b6ded5ff6ca4f9/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/EventSource.cs#L2187-L2215)）。そのため、listener は書いた流れの `AsyncLocal` を読める。listener が投げた例外は、`ThrowOnEventWriteErrors` が立っていなければ書く側に返らない（L2211-2214）。

XNU は、[apple-oss-distributions/xnu](https://github.com/apple-oss-distributions/xnu) の main（f6217f8、xnu-12377.1.9）を読んだ。

- `listen` の列の長さは `somaxconn` を超えない（[bsd/kern/uipc_socket.c L911-915](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_socket.c#L911-L915)）。`somaxconn` の既定は `SOMAXCONN`（[L250](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_socket.c#L250)）で、128 である（[bsd/sys/socket.h L597](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/sys/socket.h#L597)）。
- 新しい接続は、列が `so_qlen >= (soqlimitcompat ? so_qlimit : 3 * so_qlimit / 2)` のとき作られない（`sonewconn`、[bsd/kern/uipc_socket2.c L360-363](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_socket2.c#L360-L363)）。`soqlimitcompat` の既定は 1 である（[L135](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_socket2.c#L135)、sysctl の `kern.ipc.soqlimitcompat`、[L2986-2988](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_socket2.c#L2986-L2988)）。
- `AF_UNIX` の接続は、`sonewconn` が作らなければ `ECONNREFUSED` を返す（`unp_connect`、[bsd/kern/uipc_usrreq.c L1400-1406](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_usrreq.c#L1400-L1406)）。ソケットでない普通のファイルは、その前に `ENOTSOCK` になる（[L1331-1335](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_usrreq.c#L1331-L1335)。.NET では `SocketError.NotSocket` で、確かめは「cannot be used」の行で止まり、ファイルを消さない。測っていない）。待ち受けのないソケットのファイルは `ECONNREFUSED` である（[L1352-1357](https://github.com/apple-oss-distributions/xnu/blob/f6217f891ac0bb64f3d375211650a4c1ff8ca1ea/bsd/kern/uipc_usrreq.c#L1352-L1357)）。
- Kestrel の列の長さの既定は 512（dotnet/aspnetcore v10.0.15、[SocketTransportOptions.cs L57](https://github.com/dotnet/aspnetcore/blob/547fc16b976e18911f9df7630f1f75758ab248f2/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs#L57)）で、`SocketConnectionListener` が `Listen(_options.Backlog)` を呼ぶ（[SocketConnectionListener.cs L54](https://github.com/dotnet/aspnetcore/blob/547fc16b976e18911f9df7630f1f75758ab248f2/src/Servers/Kestrel/Transport.Sockets/src/SocketConnectionListener.cs#L54)）。`ElmServer` は `Backlog` を変えない。

Linux は、測ったカーネル（6.18.58.1）の系列のタグ v6.18（torvalds/linux 7d0a66e）の `net/unix/af_unix.c` を読んだ。`unix_stream_connect` は、ソケットが非ブロッキングなら待ちの時間を 0 にし（[L1672](https://github.com/torvalds/linux/blob/7d0a66e4bb9081d75c82ec4957c50034cb0ea449/net/unix/af_unix.c#L1672)）、相手の列がいっぱいなら、待たずに `-EAGAIN` を返す（[L1720-1722](https://github.com/torvalds/linux/blob/7d0a66e4bb9081d75c82ec4957c50034cb0ea449/net/unix/af_unix.c#L1720-L1722)）。.NET は接続の前にソケットを非ブロッキングにする（`SocketAsyncContext.ConnectAsync` の `SetHandleNonBlocking`、上の L1544）。

### 測った事実

Grill（Issue #533 のコメント。内訳は事実調べの報告から）で、`origin/main` の 29cf4a3 の木で測った。測ったのは scratch の console プロジェクトで、測りのあとで消した。

- 環境: Windows 11 26300、.NET 10.0.15。Ubuntu 24.04.5（WSL のカーネル 6.18.58.1）、.NET 10.0.14。Linux では、プロジェクトを publish し、`wslc run -d mcr.microsoft.com/dotnet/sdk:10.0` のコンテナーに `wslc container cp` で入れて流した。
- 接続が呼び出しの中で終わること: `AF_UNIX` のソケットに `ConnectAsync` を呼び、返った直後の `ValueTask.IsCompleted` を読み、呼び出しの時間と結果を記録した。56 回とも `IsCompleted` は `True` だった。呼び出しの時間は、多くが 0.5 ms 未満で、最も長いもので約 22 ms だった。

  | 相手 | Windows（28 回） | Linux（28 回） |
  | --- | --- | --- |
  | `Listen(1)` で受けない待ち受け、20 回 | 成功 5、`NoBufferSpaceAvailable` 15 | 成功 2、`WouldBlock` 18 |
  | `Listen(0)`、5 回 | 成功 5 | 成功 1、`WouldBlock` 4 |
  | 残ったファイル、bind だけで listen しないもの、普通のファイル、各 1 回 | どれも `ConnectionRefused` | どれも `ConnectionRefused` |

- 普通のファイルへの接続: 上の表のとおり、Linux でも `ConnectionRefused` だった（1 回）。CI の Linux で、今までのテスト `Start_OrdinaryFileAtTheSocketPath_IsDeletedWhereItsConnectionIsRefused` が通ることも確かめになる（このテストは OS ごとの結果をテストの出力に書く）。
- 決まって起こす形: `RemoveLeftSocketAsync` と同じ形の関数を、`EventListener` が取り消す形で流した。2 つの時点は、`ConnectStart` の時点と、`ConnectStop` か `ConnectFailed` の時点である。3 つの相手（受ける待ち受け、断られる残りのファイル、列がいっぱいの待ち受け）× 2 つの時点 × 50 回で、Windows と Linux で 300 回ずつ流し、どれも、取り消されたまま同じ結果だった。ほかのスレッドの接続 5,462 回（Linux）は、印がないので引き金にならなかった。

この行の作業で測ったこと（Windows 11 26300、SDK 10.0.404、Release、この PR の木）:

- 足したテスト `Start_CancelledRightAfterTheProbesConnect_ThrowsTheCancellationAndKeepsTheFiles` の 3 つの場合を、1 回のプロセスで 3 つずつ、60 回続けて流した。180 件すべて通った（`dotnet test --filter-method` の終了コードと合計の数を数えた）。28cae9e の木と、独立レビューの直し（内側の例外の確かめを足した）の木で、それぞれ 60 回、どちらも 180 件すべて通った。
- macOS と Linux の結果（3h-3）: PR #537 の 9627bd5 の CI（run 46132357510）で、`build.ps1 (macos-latest)`（job 140941545221、arm64、Release と Debug）と `build.ps1 (ubuntu-latest)`（job 140941545219）が通った。足した 2 つのテストの 5 つの場合は、どちらの OS でも落ちもスキップもしなかった（ログは落ちたテストとスキップしたテストだけを名指しする）。つまり macOS でも、3 つの場合とも、起動は取り消しで終わり、内側の例外の型は計測のイベントの誤りと合い、`admin.sock` と `admin.token` は残った。接続が返した値そのもの（macOS の `full queue` が `ConnectionRefused` か）は、ログに残らないので読めていない。

### 持ち主の決定（2026-11-28、Issue #533 のコメント。すべて推奨のとおり）

- 3h-1: 直すのは、接続の前のトークンの確かめと、接続の結果を製品が読むところのあいだに来た取り消し。結果を読むときにトークンが取り消されていれば、接続が成功しても、断られても、ほかの誤りでも `OperationCanceledException` を投げ、誤りがあれば内側に持たせる（#531 と同じ形）。直すのは 3 か所（断られたときの `catch` の条件、文を書く `catch` の前の取り消しの `catch`、成功のあとの行の前の確かめ）。Issue の本文を事実に合わせて書き直し、ラベルを `type:bug` にする。
- 3h-2: 断られた場合も、取り消されていればファイルを消さずに取り消しを投げる。
- 3h-3: テストは製品に継ぎ目を足さず、上の `EventListener` の形にする。場合は 3 つで、OS で限らない。期待は 3 つとも同じで、`OperationCanceledException` が投げられ、`admin.sock` が残り、`admin.token` が変わらない。変異は 4 つ。macOS の結果は PR の CI で読む。
- 3h-4: 事実と違う記録を、この PR で直す（§13.7、§13.5、`docs/rows/V1d3g.md`、§20.7 の V1d3g の行、この Issue の Facts）。V1d3g の製品の直しには触れない。行を進めるセッションの指示（2026-11-28。3h-4 の範囲に足した。`TlsChainSelfCheck.cs` のコメントは Issue #533 のコメントにも記録された）で、`.cursor/rules/csharp.mdc` の「A cancellation can surface as another exception」の説明と、`TlsChainSelfCheck.cs` のコメントも直した（規則とコードの振る舞いは変えない）。
- 3h-5: macOS の件は、この行では直さない。新しい行も作らない。§13.7 と §5.4 の表の行に、ソースから読んだこととして書く。

### macOS の待ち受けの列（ソースから。測っていない）

Grill の 3h-5 は「受けていない接続が 192 を超えると」と書いた。読み直すと、既定（`kern.ipc.soqlimitcompat=1`）では、XNU が Kestrel の 512 を `somaxconn` の 128 に縮め、列に 128 あると次の接続を断る。192（128 の 1.5 倍）は `kern.ipc.soqlimitcompat=0` のときの値である。持ち主は、数を事実に合わせるよう指示した（2026-11-28）。ロックが効かない所で、動いているサーバーが受けていない接続をそれだけ抱えている間に起動したサーバーは、接続が断られるので、その `admin.sock` を消しうる。

### 直したこと

- `ElmServer.RemoveLeftSocketAsync`（`src/ElasticLM.Server/ElmServer.cs`）:
  - 断られたときの `catch` の条件に `!cancellationToken.IsCancellationRequested` を足した。取り消されていれば、ファイルを消さずに外の `catch` へ進む。
  - 文を書く `catch` の前に、`cancellationToken.IsCancellationRequested && e is not OperationCanceledException` の `catch` を足し、その例外を内側に持つ `OperationCanceledException` を投げる。
  - 接続が成功したあと、「accepts connections」の文の前で `cancellationToken.ThrowIfCancellationRequested()` を呼ぶ。
  - `StartAsync` の XML ドキュメントに `<exception cref="OperationCanceledException">` を足した。
- テスト（`tests/ElasticLM.Server.Tests/AdminSocketTests.cs`）:
  - `Start_CancelledRightAfterTheProbesConnect_ThrowsTheCancellationAndKeepsTheFiles`: 3 つの場合（`accepting` は `FakeAdminSocket`、`refused` は `FakeAdminSocket.LeaveFile`、`full queue` は列が 1 の受けない待ち受けを、接続が失敗するまで（1,000 回まで）埋めたもの）。期待は、起動のトークンの `OperationCanceledException`、印のある接続がちょうど 1 回、内側の例外（成功なら null、ほかは計測のイベントの誤りと同じ `SocketErrorCode` の `SocketException`、`refused` では `ConnectionRefused`）、`admin.sock` が残る、`admin.token` が同じバイト。テストの出力に、その OS で起動の接続が何を返したかを書く。
  - `Start_TokenThatCanBeCancelledAndIsNot_KeepsTheProbesResults`: 取り消せるが取り消さないトークンで、受ける待ち受けは「accepts connections」の文、断られるファイルは消えて起動する。`IsCancellationRequested` を `CanBeCanceled` にする変異（M4）を落とすためのテストで、今までのテストは取り消せないトークンで起動するので、この変異では落ちない。
  - `EventListener`（`CancelAfterConnect`）は `System.Net.Sockets` のイベントを `Informational` で受け、テストの終わりに破棄する。`BannedSymbols.txt` に当たる名前はない。

### 変異の確かめ

`ElmServer.cs` の文字を書き換えて変異を入れ、Release のビルドの終了コードが 0 であることを確かめてから、`AdminSocketTests` の 26 件（`--filter-class "*AdminSocketTests"`）をそれぞれ 3 回流した。1 回の上限は 900 秒で、どの回も上限の前に終わった。最後に元の文字を書き戻した（コミットしていない）。

1 回目は、28cae9e の木（独立レビューの前）で、変異なしと M1〜M4 を流した。結果は下の表と同じだった。独立レビューの K1 が、内側の例外を外す変異 M5 が生き残ることを見つけた（26 件すべて通った）。内側の例外の確かめを足したあと（独立レビューの直しの木）、変異なしと M1〜M5 をもう一度流した。下の表は、この 2 回目の結果である。

| 変異 | 変えた所 | 3 回の結果 |
| --- | --- | --- |
| なし | — | 3 回とも失敗 0 |
| M1 | 断られたときの `catch` の条件から `&& !cancellationToken.IsCancellationRequested` を外す | 3 回とも `refused` の場合だけが落ちた（`Assert.Equal` の違い。ファイルを消して起動が先へ進み、あとの手順が投げた取り消しで終わった） |
| M2 | 取り消しの `catch` の条件に `&& path.Length < 0` を足し、当たらないようにする | 3 回とも `refused` と `full queue` の場合が落ちた（`ServerStartException`。Windows では `full queue` の接続は `NoBufferSpaceAvailable` で終わる） |
| M3 | 成功のあとの `cancellationToken.ThrowIfCancellationRequested()` を外す | 3 回とも `accepting` の場合だけが落ちた（`ServerStartException`） |
| M4 | 3 か所の `IsCancellationRequested` を `CanBeCanceled` にする（成功のあとは `if (cancellationToken.CanBeCanceled)` で投げる） | 3 回とも `Start_TokenThatCanBeCancelledAndIsNot_KeepsTheProbesResults` の 2 つの場合だけが落ちた（`accepting` は `ServerStartException` でなく取り消し、`refused` は「The start was cancelled.」） |
| M5 | 取り消しの `catch` が投げる `OperationCanceledException` の内側の例外を `null` にする | 3 回とも `refused` と `full queue` の場合が落ちた（`Assert.IsType() Failure: Value is null`） |

Windows で、起動の接続が返したもの（テストの出力。`--report-xunit-trx` の TRX から読んだ）: `accepting` は `Success`、`refused` は `ConnectionRefused`、`full queue` は `NoBufferSpaceAvailable`。CI の `./build.ps1` は TRX を残さないので、Linux と macOS で何を返したかは CI のログから読めない。CI で分かるのは、3 つの場合の期待（取り消し、内側の例外の型と計測のイベントの誤りの一致、ファイル）が通るかである。

### 独立レビュー

設計の独立レビュー（D1〜D11）と、コードとテストの独立レビュー（K1〜K5）を、PR #537 の 9ca99c7 で受けた。表は PR の本文にある。

- 直したもの: D1（`TlsChainSelfCheckTests` の remarks と検索の範囲）、D2（V1d3g の記録の段落の位置）、D3 と K1（内側の例外の確かめと変異 M5）、D5（Issue の本文の macOS の数）、D6（§13.7 の「確かめ」の言い分け）、D7（§13.5 と csharp.mdc の範囲）、D8（Grill の測りの内訳。事実調べの報告から）、D9（macOS の普通のファイルは `ENOTSOCK`）、D11（csharp.mdc を直した指示の出どころ）、K2（`StartAsync` の XML ドキュメント）、K3（`FullQueue` のコメントを OS ごとに）、K4（`EventListener` の呼ばれ方の出典）、K5（`FillAsync` の失敗のときの破棄）。
- D4（OS ごとの結果）: Windows の結果は上の「変異の確かめ」に書いた。Linux と macOS は、CI が TRX を残さないので返した値は読めず、CI で通るかだけを PR に書く。
- D10（V1d3g の CI の 1 回の原因）: 決定 3h-4 が、道は分からないと書くと決めているので、持ち主に新しく聞かず、§20.7 の V1d3g の行と `docs/rows/V1d3g.md` の言い切りを「直す前の catch がトークンを読まないことは、再現で確かめた。CI の 1 回をどの誤りが起こしたかは分からない（決定 3h-4）」に弱めた。原因を追う新しい行は作らない。

### 同じ種類を探した範囲

- `src/` で `Socket.ConnectAsync` をトークンつきで呼ぶのは 3 か所である（`rg "ConnectAsync\(" src`）。`TlsChainSelfCheck.RunAsync` は V1d3g で直してあり、この行ではコメントだけを直した。`ElmServer.RemoveLeftSocketAsync` はこの行で直した。`ServerAdminApi.ConnectAsync`（`src/ElasticLM.Cli/ServerAdminApi.cs`）は `SocketsHttpHandler` の `ConnectCallback` で、トークンは `elm` の要求のものである。接続の直後に取り消されても、接続を返すだけで、結果からファイルを消したり文を書いたりはしない。要求はそのあと同じトークンで取り消しになり、呼ぶ側はそれを通信の誤りとして扱う（V1d3g の記録のとおり）ので、同じ種類ではない。
- 「取り消しがソケットを閉じる」と書いた所: design §13.5、§13.7、§20.7 の V1d3g と V1d3h の行、`docs/rows/V1d3g.md`、`.cursor/rules/csharp.mdc`、`TlsChainSelfCheck.cs` のコメント、`TlsChainSelfCheckTests.cs` の `Run_HandshakeFailsAfterTheCallerCancels_ThrowsTheCancellation` の remarks。どれも直した（`rg "CancelConnectAsync|closing the socket|closes the socket|ソケットを閉じる" docs .cursor src tests website` で探した。tests は設計の独立レビューの D1 で足した）。

## 退けた案

- **決定:** 製品に継ぎ目を足さず、計測のイベントを受ける `EventListener` で、起動の接続の直後に取り消すテストにする（3h-3）。
- **理由:** 接続はどの OS でも呼び出しの中で終わり、取り消しを接続の途中に入れる道がない。イベントは接続する流れで `ConnectAsync` が返る前に書かれるので、その流れの印を見て取り消せば、毎回同じ時点に取り消しが入る（Windows と Linux で 300 回ずつ同じ結果）。
- **却下した案:** 製品にテスト用の継ぎ目（接続のあとに呼ぶ関数）を足す。テストのためだけの公開しない道が製品に増える。
- **却下した案:** 一時の変更による変異の確かめだけで直しを入れる、または窓に届かないと記録して終える（Issue #533 の Grill の点）。決まって起こせる形が見つかったので、取らない。
- **決定:** macOS の列がいっぱいのときの断りは、この行では直さず、記録に書く（3h-5）。
- **理由:** 起きるのは、ロックが効かない所で、動いているサーバーが受けていない接続を 128 抱えているときだけで、§5.4 の問 D の行と同じく、運用者の置き方で決まる。
- **却下した案:** 新しい行を作って直す。持ち主が取らなかった。
