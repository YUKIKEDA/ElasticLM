# V1d3d（Issue #490）: `elm-server` と `elm-service` が、起動の確かめのあとに TLS の資格情報を作り直し、確かめと違うものを送ることがあるかを測る

## Grill の記録

V1d3c の作業（Issue #435、PR #489）で見つけ、持ち主の決定（2026-11-27、Issue #435 のコメント、案 A）で立てた行。持ち主の決定は「先に測り、仕組みを §13.5 に事実として足し、製品の扱いは測ってから決める」で、この行は測りと設計の文と利用者の文書の 1 文だけで、製品は変えない。

- 持ち主の決定（2026-11-27、Issue #490 のコメント、どちらも推奨案 A）:
  - 問 3d-1（長く動くサーバーの、片づけと期限の道の扱い）: 製品は変えない。§13.5 に、事実と、起動の確かめは起動のときの資格情報だけを確かめるという限界を書く。PR #501 の設計の独立レビューの H1 で、`elm-service` の PostgreSQL のキー（32 に達する道としていたもの）がないと分かり、持ち主に伝えたあと、改めて A と確かめた（同じ日）。
  - 問 3d-2（起動の確かめの文 `… what that handshake sent stays for as long as this process runs.`）: 文は変えない。
  - 利用者の文書: `docs/guide/reference/server-configuration.md` と日本語の写しの「Windows は…プロセスが終わるまで変えない」を、この PR で「ふつうは」の形に直す（H1 のあとの持ち主の指示）。
  - 行の外: `elm-service` の PostgreSQL への接続が TLS を使わない疑い（how-to の `SSL Mode` の案内）は、動かして確かめてから bug の Issue と行にする（持ち主の指示。コーディネーターが受け持つ）。
- 設計の独立レビュー（PR #501、f1f4830）: dotnet/runtime の引用はすべて合っていた。指摘（H1、M1〜M5、L1〜L4）は、この記録と §13.5 と §20.7 の表の V1d3d の行に入れた。

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
  - `elm-service` の PostgreSQL への接続は TLS を使わない（設計の独立レビューの H1 で分かった。コードを読んだだけで、動かしてはいない）。`Database.cs` の 60 行は `NpgsqlSlimDataSourceBuilder` を `EnableTransportSecurity()` なしで作る。Npgsql 10.0.3 の `src/Npgsql/NpgsqlSlimDataSourceBuilder.cs` の 36 行は既定の `TransportSecurityHandler` を持ち、それは `src/Npgsql/Internal/TransportSecurityHandler.cs` の 20〜21 行で `NegotiateEncryption` を `NotSupportedException` にする（`EnableTransportSecurity()` の 653〜655 行が `RealTransportSecurityHandler` に替える）。そのため、`NpgsqlConnector.cs` の `NegotiateEncryption`（1114 行から）は呼ばれず、キーにならない。この記録の先の版は、Npgsql がクライアントの証明書のファイルを接続のたびに読むので、入れ替えを 28 回読むと 32 に達すると書いたが、この道はない。
  - `elm-service` の待ち受けと起動の確かめは、`elm-server` と同じ `UseHttps` の形と `TlsChainSelfCheck` なので、同じ 2 つ。`elm-service` のプロセスでは測っていない。
  - どちらの製品も、TLS の証明書をプロセスの間に読み直さない（起動の `StartAsync` で 1 度読む。`ElmServer.cs` の 294〜296 行と `ElmService.cs` の 190 行。`elm server reload` が読み直すのはサーバーライセンスだけ）。
- 帰結: 表は、`elm-server` も `elm-service` も多くても 3 つ。同じキーの入れ替えは数を増やさない（`SslSessionsCache.cs` の 170〜203 行）。片づけは、表に資格情報を入れるとき（新しいキーのときと、同じキーの閉じたものか期限を過ぎたものを入れ替えるとき。173〜191 行）に、数が 32 の倍数ならだけ走る（205〜218 行）。どちらの製品にも、32 に達する道は見つかっていない。

### 問 2 と問 3: 片づけのあとの作り直しと、送るもの

| 形 | 回数 | 片づけ | ストアの中間 CA | 片づけの間の接続 | 結果（片づけのあとのハンドシェイク 3 本） |
| --- | --- | --- | --- | --- | --- |
| `plain` | 3 | なし | ある | なし | 資格情報は起動のときのまま。3 本とも中間 CA を送った |
| `scavenge` | 3 | あり（29 個足して 32） | ある | なし | 資格情報を作り直した。3 本とも中間 CA を送った |
| `remove` | 3 | なし | 消した | なし | 資格情報は起動のときのまま。3 本とも中間 CA を送った |
| `remove-scavenge` | 3 | あり（29 個足して 32） | 消した | なし | 資格情報を作り直した。3 本ともサーバーの証明書だけを送り、ルートだけを信頼するクライアントは連なりを組めなかった |
| `remove-scavenge-open` | 3 | あり（29 個足して 32） | 消した | 1 本開いた | 資格情報は起動のときのまま。3 本とも中間 CA を送った |

- 片づけは、どの回も 29 個目で走った（起動のあとの 3 つ + 29 = 32）。`Scavenged cache, New Cache Count` は、接続を開かない形で 2、接続を開いた形で 3（それに `elm-server` のサーバーの資格情報）だった。残った 2 つは、1 回目（1 つのプロセスで続けて流した回）の表の中身で見ると、クライアントの 1 つ（拇印は空、`resume=True`）と、最後に足したサーバーの資格情報だった。どちらも、片づけを起こした最後のハンドシェイクの、まだ閉じていない両端が持っていた（`CacheCredential` はハンドシェイクの中から呼ばれる。`SslStream.Protocol.cs` の 746〜815 行と、そのあとの 935〜961 行）。3 回ずつの回では、表の中身は確かめていない。起動の確かめのクライアントの資格情報（`resume=False`）は、どの接続も持たないので捨てられた。
- `remove` と `remove-scavenge` の違いは片づけだけで、作り直さない限り、ストアが変わっても送るものは変わらない。

### 問 4: ほかの経路

- 期限（`expiry` と `remove-expiry`。中間 CA とサーバーの証明書の `NotAfter` を、同じ時刻（起動の 75 秒後）にした連なり。名前は `CN=V1d3d spike ` で始め、テストの掃除の接頭辞と分けた）:
  - `expiry`（2 回）: 期限のあとのハンドシェイク 3 本は、どれも起動のときと違う資格情報で、3 本とも中間 CA を送った。
  - `remove-expiry`（2 回）: 起動のあとにストアから中間 CA を消すと、期限の前は起動のときの資格情報のまま中間 CA を送った。期限のあとの最初の 1 本はサーバーの証明書だけを送り、そのあと中間 CA が `CurrentUser\CA` にまた入っていて、2 本目と 3 本目は中間 CA を送った。
  - 中間 CA だけが先に期限を迎える形（サーバーの証明書はまだ有効）は測っていない。
  - ソース（dotnet/runtime v10.0.15、`SslStream.Protocol.cs` の 746〜815 行）: 資格情報の期限は、サーバーの証明書と `SslStreamCertificateContext.IntermediateCertificates` の `NotAfter` のいちばん早いもの。期限を過ぎた資格情報は使わない（`SslSessionsCache.cs` の 127 行）。新しい資格情報の期限も過ぎていれば、.NET は `certificateContext.Duplicate()`（`SslStreamCertificateContext.cs` の 170〜181 行）で `Create` をやり直す。Windows の `Create` は、ストアだけで連なりが組めなければ中間 CA をストアに書く（`SslStreamCertificateContext.Windows.cs`）。その `Duplicate` は、SChannel の資格情報を得る `SslStreamPal.AcquireCredentialsHandle` のあとに来る。測った順（最初の 1 本はサーバーの証明書だけ、そのあとストアに戻って送る）は、この順と合う。期限を過ぎたあとは、ハンドシェイクのたびに資格情報を作り直す（期限が過ぎたままなので）。
  - 測った形（サーバーの証明書も期限を過ぎる）では、クライアントが受け入れるものは変わらない: サーバーの証明書の期限が過ぎれば、OS の検証は通らない。`elm-server` のクライアントは、そのとき公開鍵の拇印を `cnf.jkt` と比べるが（§13.5）、運用者の証明書の鍵がインストール鍵でない限り一致しない。`elm-service` のクライアントは OS の検証だけを使う。どちらも、中間 CA が送られたかによらず断る。中間 CA だけが先に期限を迎える形でも、その中間 CA を通る連なりは期限で通らないが、ストアに更新した同じ CA の証明書があれば、作り直した資格情報がそれを送ることはありうる（測っていない）。
- `IsClosed` と `IsInvalid`（`SslSessionsCache.cs` の 127 行）: 表は自分の参照（`SafeCredentialReference.cs` の `DangerousAddRef`）を持つので、片づけで参照を離されるまで閉じない。片づけのほかに閉じる道は見つけなかった。
- `SslStreamCertificateContext`: Kestrel は待ち受けごとに 1 度作り（dotnet/aspnetcore v10.0.15 の `HttpsConnectionMiddleware.cs` の 112 行）、どの接続にも同じものを渡す（352 行）。片づけのあとの作り直しは、これを作り直さないので、中間 CA をストアに書き込み直さない（`remove-scavenge` で、片づけのあとも中間 CA はストアに戻らなかった）。

### Linux と macOS

- Linux（読んだ）: `SslStreamPal.Unix.cs` の `AcquireCredentialsHandle`（59〜62 行）は `null` を返し、`SslStream.Protocol.cs` の 947 行は `_credentialsHandle != null` のときだけ表に入れるので、`SslSessionsCache` の表は使われない。かわりに、OpenSSL のコンテキスト（`SSL_CTX`）の表がある（`src/libraries/Common/src/Interop/Unix/System.Security.Cryptography.Native/Interop.OpenSsl.cs` の 35〜37 行と 181〜203 行）。キーは、クライアントかサーバーか、プロトコル（ALPN の有無を足したもの）、`SslStreamCertificateContext` そのものである。サーバーのコンテキストは、`AllowTlsResume` などの条件を満たすと表に入る（351〜378 行）。この表も、数が 32 の倍数になるたびに、使われていないコンテキストを捨てる（`src/libraries/Common/src/System/Net/SafeHandleCache.cs` の `CheckExpiredModulo = 32` と `CheckForCleanup`）。捨てたあとに作り直すコンテキストも、同じ `SslStreamCertificateContext` の中間 CA を足して作る（284〜290 行）。Kestrel のコンテキストは待ち受けごとに 1 つなので、送る連なりはプロセスの間変わらない。
- Linux（測った）: WSL のコンテナー（`wslc`、`mcr.microsoft.com/dotnet/sdk:10.0`、SDK 10.0.403、.NET 10.0.14、Ubuntu 24.04.5）で、Kestrel も製品も使わず、`SslStream` だけの試しのプログラムを 1 回流した。Kestrel と同じに 1 度作ったコンテキストのサーバーは、最初のハンドシェイクで中間 CA を送り、ほかの証明書のサーバーのハンドシェイク 40 本のあとも 20 本のうち 20 本で送った。`SslSessionsCache` の表は、どの時点でも 0 だった。この試しはサーバーのコンテキストを 41 個作ったので、ソースの上では `SSL_CTX` の表の片づけが走るが、走ったことも、何を捨てたかも記録していない。
- macOS: `SslStreamPal.OSX.cs` の `AcquireCredentialsHandle`（129〜132 行）も `null` を返すことを読んだだけで、測っていない。

### 測っていないこと

- `elm-service` のプロセスでの表の数（コードを読んで数えた。待ち受けと起動の確かめは `elm-server` と同じコード）。
- 外向きの TLS の 2 つのキー（`ActivationClient`、IdP）を、製品のプロセスで（読んで数えた）。
- 管理者のアカウントで .NET が `LocalMachine\CA` に書くときの形。
- 中間 CA だけが先に期限を迎える形。
- Linux の `SSL_CTX` の表の片づけが走ったか。
- 片づけの間に、ストアがほかのプログラムに書き換えられ続けている形（V1d3a の測りの形）。この行は、中間 CA を消したあとの静かなストアで測った。V1d3a で測ったとおり、書き換えの最中の最初のハンドシェイクはサーバーの証明書だけを送ることがあるので、作り直しの最初のハンドシェイクも同じことがありうるが、測っていない。

### 持ち主に聞いたこと

問は 2 つで、持ち主はどちらも推奨案 A を選んだ（上の「持ち主の決定」）。問 3d-1 は、H1 で前提（`elm-service` が 28 回の入れ替えで 32 に達する）が崩れたので、持ち主に伝え、A のままと確かめた。

**問 3d-1（長く動くサーバーの、片づけの道の扱い）**

起動の確かめ（V1d3a）は、起動のときの資格情報が中間 CA を送ることを確かめる。その資格情報が片づけで捨てられ、そのときストアから中間 CA が消えていると、サーバーはそれ以後サーバーの証明書だけを送る（上の `remove-scavenge`）。片づけが走るのは表の数が 32 の倍数になったときで、どちらの製品にも、そこに達する道は見つかっていない（H1 のあとの数え方）。

- **A（推奨、選ばれた）: 製品は変えない。** §13.5 にこの PR の事実を残し、起動の確かめは起動のときの資格情報だけの確かめであることを、§13.5 の限界として書く。
  - 利点: この道に入るには、表が 32 に達することと、接続のない瞬間と、ほかのプログラムか運用者が動いているサーバーの中間 CA をストアから消すことが重なる必要があり、製品の固定の設定では 32 に達する道がない。
  - 欠点: 起きたときは、運用者が気付くまで、中間 CA を自分で見つけられないクライアントが接続を断る（`ELM_SERVER_PIN_MISMATCH`、V1c4c の How-to）。製品はそれを記録しない。
- **B: 起動の確かめの接続を、プロセスの間開いたままにする。** 開いた接続が資格情報を持つ間、片づけはそれを捨てない（上の `remove-scavenge-open`）。
  - 利点: 片づけの道を閉じる。
  - 欠点: 新しい行が要る。Kestrel が要求のない接続をいつ切るか（`KeepAliveTimeout` などの既定）は確かめていないので、その行でまず測る。期限の道は閉じない。
- **C: 起動の確かめを、動いている間も定期的にやり直し、送られていなければ運用の記録に書く（または止まる）。**
  - 利点: 片づけでも、ほかの原因でも、作り直したあとの状態を見つける。
  - 欠点: 新しい行が要り、間隔、記録の形、止まるかを決める。確かめと次の作り直しの間は見えない。

**問 3d-2（起動の確かめの文の `… what that handshake sent stays for as long as this process runs.`）**

この文は、起動の確かめに落ちて、プロセスがすぐ終わるときにだけ出る。起動の確かめのあとに資格情報を作り直す道（片づけと期限）は、そのプロセスでは来ない。

- **A（推奨、選ばれた）: 文は変えない。** §13.5 の理由の段落だけを、この PR で「その資格情報が使われる間は続く」に直した（製品のプロセスではふつうは終わるまで使われる、と添えた）。
  - 利点: 文が言うこと（このプロセスのままでは直らないので、起動し直す）は、文が出るプロセスでは正しい。運用者が読む文は短いまま。
  - 欠点: 文の言い方は、.NET の表の仕組みの全体（片づけと期限）を言わない。
- **B: 文を「what that handshake sent stays while this process keeps that credential」のように直す。**
  - 利点: .NET の仕組みに正確になる。
  - 欠点: 製品の文の変更なので、新しい行が要る（文を持つのは `TlsChainSelfCheck.cs`、それを確かめる `TlsChainSelfCheckTests.cs`、`TlsChainTests.cs`、`ElmServiceTests.cs`、利用者の文書の `server-configuration.md` の英語と日本語、設計 §13.5）。運用者には「credential」が何かが分からない。

## 退けた案

- **決定:** 製品は変えない。起動の確かめは起動のときの資格情報だけを確かめ、片づけか期限で作り直した資格情報は確かめも記録もしないことを、§13.5 に事実と限界として書く（持ち主の決定 3d-1、推奨案 A、2026-11-27、Issue #490。設計の独立レビューの H1 のあとに改めて確かめた）。起動の確かめの文は変えない（持ち主の決定 3d-2、推奨案 A、同じ日）。利用者の文書の `server-configuration.md` の「プロセスが終わるまで変えない」は「ふつうは」の形に直す。
- **理由:** 作り直した資格情報がサーバーの証明書だけを送るには、表が 32 に達することと、接続のない瞬間と、動いているサーバーの中間 CA をストアから消す外の操作が重なる必要があり、どちらの製品にも 32 に達する道は見つかっていない。期限の道は、測った形（サーバーの証明書も期限を過ぎる）では、送るものによらずクライアントが断る。起動の確かめの文は、起動に落ちてすぐ終わるプロセスでだけ出て、そのプロセスでは文のとおりである。
- **却下した案:** 3d-1 の (B) 起動の確かめの接続をプロセスの間開いたままにする（片づけの道は閉じるが、Kestrel が要求のない接続を切る時期を確かめる新しい行が要り、期限の道は閉じない）。(C) 起動の確かめを定期的にやり直して記録する（どの原因の作り直しも見つけるが、新しい行で間隔、記録の形、止まるかを決める必要があり、確かめの間は見えない）。3d-2 の (B) 文を資格情報の言い方に直す（.NET の仕組みには正確になるが、製品の文とテストと利用者の文書を変える新しい行が要り、運用者に「credential」が伝わらない）。
