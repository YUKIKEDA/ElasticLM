# V1d3d（Issue #490）: `elm-server` と `elm-service` が、起動の確かめのあとに TLS の資格情報を作り直し、確かめと違うものを送ることがあるかを測る

## Grill の記録

V1d3c の作業（Issue #435、PR #489）で見つけ、持ち主の決定（2026-11-27、Issue #435 のコメント、案 A）で立てた行。持ち主の決定は「先に測り、仕組みを §13.5 に事実として足し、製品の扱いは測ってから決める」で、この行は測りと設計の文だけで、製品は変えない。製品の扱いの問（3d-1、3d-2）は、この行の PR に載せた（下の「持ち主に聞くこと」）。

### 測った環境と測り方

- 木: `origin/main` の 9661ee7 から切ったこの行のブランチ。Windows 11（build 26300）、SDK 10.0.404、.NET 10.0.15、Debug。管理者でない利用者で、.NET は中間 CA を `CurrentUser\CA` に書いた（`LocalMachine\CA` に書く道は測っていない）。
- 道具: 測りの間だけ、`ElasticLM.Server.Tests` に試しのテスト（`CredentialCacheSpike.cs`）を足し、測ったあと消した（コミットしていない）。製品の `elm-server` を、テストと同じ `Deployment.StartServerAsync` で、ルート → 中間 CA → サーバーの連なりの PEM（`TestCertificateChain`。ストアを書き換えるテストの排他を持つ）で起動した。起動の確かめ（`TlsChainSelfCheck`）が通ったあと、次を読んだ。
  - .NET の資格情報の表（`System.Net.Security.SslSessionsCache.s_cachedCreds`）を、リフレクションで読んだ。キーの各フィールドと、表が持つ資格情報のオブジェクトの同一性（作り直したかの判定）。製品はリフレクションを使わない。これは測りの道具だけである。
  - `EventListener` で、`Private.InternalDiagnostics.System.Net.Security` の `Caching New Handle` と `Scavenged cache` の行を数えた（V1d3c と同じ）。
  - 送られたかは、クライアントの検証のコールバックの `chain.ChainPolicy.ExtraStore` に中間 CA の拇印があるかで決めた（§13.5 の V1c12 の Q4 と同じ）。
  - 片づけは、ほかの自己署名の証明書のサーバーの資格情報を、ループバックのハンドシェイクで 1 つずつ足して起こした。`Scavenged cache` の行が出たところで止めた。
  - 「ストアの書き換え」は、`elm-server` が起動したあとに、.NET が書いた中間 CA を拇印で `CurrentUser\CA` と `LocalMachine\CA` から消すことで作った。
- 回数: 1 つの形を 1 つのテストのプロセスで 1 回とし（表はプロセスに 1 つなので）、下の 6 つの形を 3 回ずつ、期限の 2 つの形を 2 回ずつ流した。どの形も、回ごとに結果は同じだった。この前に、6 つの形を 1 つのプロセスで続けて流した 1 回目があり、結果は同じだったが、表がほかの形のキーを持ち越すので、数には使っていない。

### 問 1: 製品のプロセスのキーの数

- 測った（`elm-server`、3 回）: 起動のあとの表は 2 つだった。待ち受けのサーバーの資格情報（拇印はサーバーの証明書の SHA-512、`resume=True`）と、起動の確かめのクライアント（拇印は空、`resume=False`）。そのあと、測りのクライアントの最初のハンドシェイクで、クライアントの 1 つ（拇印は空、`resume=True`）が増えて 3 つになり、https の要求 400 件（新しい `HttpClient` で 200 件、同じ `HttpClient` で 200 件）と、並べたハンドシェイク 50 本のあとも 3 つだった。どれも、プロトコルは `None`、失効の確かめなし、信頼リストなし。
- 読んだ（測っていない）:
  - `elm-server` の `service.url` のサーバーライセンスの更新（`ActivationClient`、`BoundedAnswer.CreateHandler` の `SocketsHttpHandler` の既定）は、クライアントの 1 つ（拇印は空、`resume=True`）。上の測りのクライアントと同じキーになる。
  - `elm-service` の IdP への外向き（`ElmService.Oidc.cs` の 92 行の `HttpClient`。Web のサインインの `Backchannel` も同じものを使う）は、同じ形のクライアントの 1 つ。
  - `elm-service` の PostgreSQL への TLS（Npgsql 10.0.3 の `src/Npgsql/Internal/NpgsqlConnector.cs`）: `EnabledSslProtocols = SslProtocols.None`、失効の確かめは `CheckCertificateRevocation` の設定（1231〜1237 行）、クライアントの証明書は接続文字列の `SslCertificate`（なければ環境変数）のファイルを、物理の接続を開くたびに読む（`NegotiateEncryption`、1114〜1180 行）。キーは、クライアントの証明書の拇印と失効の確かめで分かれる。
  - `elm-service` の待ち受けと起動の確かめは、`elm-server` と同じ `UseHttps` の形と `TlsChainSelfCheck` なので、同じ 2 つ。`elm-service` のプロセスでは測っていない。
  - どちらの製品も、TLS の証明書をプロセスの間に読み直さない（起動の `StartAsync` で 1 度読む。`ElmServer.cs` の 294〜296 行と `ElmService.cs` の 190 行。`elm server reload` が読み直すのはサーバーライセンスだけ）。
- 帰結: 表は、`elm-server` で多くて 3 つ、`elm-service` で多くて 4 つ。同じキーの入れ直しは数を増やさない（`SslSessionsCache.cs` の 170〜203 行）。片づけは、新しいキーを入れて数が 32 の倍数になったときにだけ走る（205〜218 行）ので、ふつうの運用では走らない。増える道として見つけたのは、`elm-service` の Npgsql のクライアントの証明書の入れ替えだけで、入れ替えを読むたびに 1 つ増え、28 回で 32 に達する（4 + 28 = 32）。

### 問 2 と問 3: 片づけのあとの作り直しと、送るもの

| 形 | 回数 | 片づけ | ストアの中間 CA | 片づけの間の接続 | 結果（片づけのあとのハンドシェイク 3 本） |
| --- | --- | --- | --- | --- | --- |
| `plain` | 3 | なし | ある | なし | 資格情報は起動のときのまま。3 本とも中間 CA を送った |
| `scavenge` | 3 | あり（29 個足して 32） | ある | なし | 資格情報を作り直した。3 本とも中間 CA を送った |
| `remove` | 3 | なし | 消した | なし | 資格情報は起動のときのまま。3 本とも中間 CA を送った |
| `remove-scavenge` | 3 | あり（29 個足して 32） | 消した | なし | 資格情報を作り直した。3 本ともサーバーの証明書だけを送り、ルートだけを信頼するクライアントは連なりを組めなかった |
| `remove-scavenge-open` | 3 | あり（29 個足して 32） | 消した | 1 本開いた | 資格情報は起動のときのまま。3 本とも中間 CA を送った |

- 片づけは、どの回も 29 個目で走った（起動のあとの 3 つ + 29 = 32）。`Scavenged cache, New Cache Count` は、接続を開かない形で 2（測りのクライアントと、最後に足した 1 つ）、接続を開いた形で 3（それに `elm-server` のサーバーの資格情報）だった。起動の確かめのクライアントの資格情報（`resume=False`）も、どの接続も持たないので捨てられた。
- `remove` と `remove-scavenge` の違いは片づけだけで、作り直さない限り、ストアが変わっても送るものは変わらない。

### 問 4: ほかの経路

- 期限（`expiry` と `remove-expiry`。中間 CA とサーバーの証明書の `NotAfter` を起動の 75 秒後にした連なり。名前は `CN=V1d3d spike ` で始め、テストの掃除の接頭辞と分けた）:
  - `expiry`（2 回）: 期限のあとのハンドシェイク 3 本は、どれも起動のときと違う資格情報で、3 本とも中間 CA を送った。
  - `remove-expiry`（2 回）: 起動のあとにストアから中間 CA を消すと、期限の前は起動のときの資格情報のまま中間 CA を送った。期限のあとの最初の 1 本はサーバーの証明書だけを送り、そのあと中間 CA が `CurrentUser\CA` にまた入っていて、2 本目と 3 本目は中間 CA を送った。
  - ソース（dotnet/runtime v10.0.15）: 資格情報の期限は、サーバーの証明書と `SslStreamCertificateContext.IntermediateCertificates` の `NotAfter` のいちばん早いもの（`SslStream.Protocol.cs` の 770〜815 行）。期限を過ぎた資格情報は使わない（`SslSessionsCache.cs` の 127 行）。新しい資格情報の期限も過ぎていれば、.NET は `certificateContext.Duplicate()`（`SslStreamCertificateContext.cs` の 170〜181 行）で `Create` をやり直す。Windows の `Create` は、ストアだけで連なりが組めなければ中間 CA をストアに書く（`SslStreamCertificateContext.Windows.cs`）。その `Duplicate` は、SChannel の資格情報を得る `SslStreamPal.AcquireCredentialsHandle`（`SslStream.Protocol.cs` の 772 行）のあとの 794 行にある。測った順（最初の 1 本はサーバーの証明書だけ、そのあとストアに戻って送る）は、この順と合う。期限を過ぎたあとは、ハンドシェイクのたびに資格情報を作り直す（期限が過ぎたままなので）。
  - 期限の道でクライアントが受け入れるものは変わらない: サーバーの証明書か中間 CA の期限が過ぎれば、OS の検証は通らない。`elm-server` のクライアントは、そのとき公開鍵の拇印を `cnf.jkt` と比べるが（§13.5）、運用者の証明書の鍵がインストール鍵でない限り一致しない。`elm-service` のクライアントは OS の検証だけを使う。どちらも、中間 CA が送られたかによらず断る。
- `IsClosed` と `IsInvalid`（`SslSessionsCache.cs` の 127 行）: 表は自分の参照（`SafeCredentialReference.cs` の `DangerousAddRef`）を持つので、片づけで参照を離されるまで閉じない。片づけのほかに閉じる道は見つけなかった。
- `SslStreamCertificateContext`: Kestrel は待ち受けごとに 1 度作り（dotnet/aspnetcore v10.0.15 の `HttpsConnectionMiddleware.cs` の 112 行）、どの接続にも同じものを渡す（352 行）。片づけのあとの作り直しは、これを作り直さないので、中間 CA をストアに書き込み直さない（`remove-scavenge` で、片づけのあとも中間 CA はストアに戻らなかった）。

### Linux と macOS

- Linux（読んだ）: `SslStreamPal.Unix.cs` の `AcquireCredentialsHandle`（59〜62 行）は `null` を返し、`SslStream.Protocol.cs` の 947 行は `_credentialsHandle != null` のときだけ表に入れるので、表は使われない。送る連なりは、OpenSSL のコンテキストに `SslStreamCertificateContext` の中間 CA を足したもの（`Interop.OpenSsl.cs` の 284〜290 行。コンテキストの表は 181〜203 行で、キーは `SslStreamCertificateContext` そのもの）。
- Linux（測った）: WSL のコンテナー（`wslc`、`mcr.microsoft.com/dotnet/sdk:10.0`、SDK 10.0.403、.NET 10.0.14、Ubuntu 24.04.5）で、`SslStream` だけの試しのプログラム（Kestrel と製品ではない）を 1 回流した。Kestrel と同じに 1 度作ったコンテキストのサーバーは、最初のハンドシェイクで中間 CA を送り、ほかの証明書のサーバーのハンドシェイク 40 本のあとも 20 本のうち 20 本で送った。表は、どの時点でも 0 だった。
- macOS: `SslStreamPal.OSX.cs` の `AcquireCredentialsHandle`（129〜132 行）も `null` を返すことを読んだだけで、測っていない。

### 測っていないこと

- `elm-service` のプロセスでの表の数（コードを読んで数えた。待ち受けと起動の確かめは `elm-server` と同じコード）。
- 外向きの TLS の 3 つのキー（`ActivationClient`、IdP、Npgsql）を、製品のプロセスで（読んで数えた）。
- 管理者のアカウントで .NET が `LocalMachine\CA` に書くときの形。
- 片づけの間に、ストアがほかのプログラムに書き換えられ続けている形（V1d3a の測りの形）。この行は、中間 CA を消したあとの静かなストアで測った。V1d3a で測ったとおり、書き換えの最中の最初のハンドシェイクはサーバーの証明書だけを送ることがあるので、作り直しの最初のハンドシェイクも同じことがありうるが、測っていない。

### 持ち主に聞くこと

問は 2 つ。

**問 3d-1（長く動くサーバーの、片づけの道の扱い）**

起動の確かめ（V1d3a）は、起動のときの資格情報が中間 CA を送ることを確かめる。その資格情報が片づけで捨てられ、そのときストアから中間 CA が消えていると、サーバーはそれ以後サーバーの証明書だけを送る（上の `remove-scavenge`）。片づけが走るのは、表の数が 32 の倍数になったときで、`elm-server` では達せず、`elm-service` では Npgsql のクライアントの証明書の入れ替えを 28 回読んだときで、しかも、その瞬間にサーバーへの接続が 1 本もないときだけである。

- **A（推奨）: 製品は変えない。** §13.5 にこの PR の事実を残し、起動の確かめはプロセスの全期間の保証ではないことを、§13.5 の限界として書いたままにする。
  - 利点: 起動の確かめの目的（中間 CA を落とした状態で黙って動き続けない）に対して、この道に入るには、ほかのプログラムか運用者が、動いているサーバーの中間 CA をストアから消すことが要る。さらに `elm-service` の 28 回の入れ替えと、接続のない瞬間が重なるときだけで、`elm-server` には道がない。
  - 欠点: 起きたときは、運用者が気付くまで、中間 CA を自分で見つけられないクライアントが接続を断る（`ELM_SERVER_PIN_MISMATCH`、V1c4c の How-to）。製品はそれを記録しない。
- **B: 起動の確かめの接続を、プロセスの間開いたままにする。** 開いた接続が資格情報を持つ間、片づけはそれを捨てない（上の `remove-scavenge-open`）。
  - 利点: 片づけの道を閉じる。
  - 欠点: 新しい行が要る。Kestrel が要求のない接続をいつ切るか（`KeepAliveTimeout` などの既定）は確かめていないので、その行でまず測る。期限の道は閉じない（ただし期限の道は、下の問 3d-2 のとおり、クライアントの受け入れを変えない）。
- **C: 起動の確かめを、動いている間も定期的にやり直し、送られていなければ運用の記録に書く（または止まる）。**
  - 利点: 片づけでも、ほかの原因でも、作り直したあとの状態を見つける。
  - 欠点: 新しい行が要り、間隔、記録の形、止まるかを決める。確かめと次の作り直しの間は見えない。

推奨は A。理由は、要件（クライアントが中間 CA を受け取れること、V1c12 と V1d3a）を崩すには、動いているサーバーのストアから中間 CA を消す外の操作が要り、片づけの条件も、製品の固定の設定では `elm-server` で成り立たず、`elm-service` でも 28 回の入れ替えを要するためである。

**問 3d-2（起動の確かめの文の `… what that handshake sent stays for as long as this process runs.`）**

この文は、起動の確かめに落ちて、プロセスがすぐ終わるときにだけ出る。起動の確かめのあとに資格情報を作り直す道（片づけと期限）は、そのプロセスでは来ない。期限の道（上の問 4）は、作り直しで送るものが変わっても、クライアントの受け入れを変えない（期限の過ぎた証明書は、中間 CA があってもなくても断られる）。

- **A（推奨）: 文は変えない。** §13.5 の理由の段落だけを、この PR で「その資格情報が使われる間は続く」に直した（製品のプロセスではふつうは終わるまで使われる、と添えた）。
  - 利点: 文が言うこと（このプロセスのままでは直らないので、起動し直す）は、文が出るプロセスでは正しい。運用者が読む文は短いまま。
  - 欠点: 文の言い方は、.NET の表の仕組みの全体（片づけと期限）を言わない。
- **B: 文を「what that handshake sent stays while this process keeps that credential」のように直す。**
  - 利点: .NET の仕組みに正確になる。
  - 欠点: 製品の文の変更なので、新しい行が要る（文を持つのは `TlsChainSelfCheck.cs`、それを確かめる `TlsChainSelfCheckTests.cs`、`TlsChainTests.cs`、`ElmServiceTests.cs`、利用者の文書の `server-configuration.md` の英語と日本語、設計 §13.5）。運用者には「credential」が何かが分からない。

推奨は A。理由は、文は起動に落ちたプロセスで出て、そのプロセスでは文のとおりであり、運用者がすること（起動し直す、毎回ならマシンのストアに入れる）が変わらないためである。

## 退けた案

持ち主が問 3d-1 と 3d-2 に答えたあとに書く。
