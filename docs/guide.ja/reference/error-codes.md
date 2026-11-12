---
sidebar_position: 4
---

# エラーコード

このページには、ElasticLM のすべてのエラーコードを載せる。コードごとに、意味、どの部分が返すか、サーバーがそのコードと一緒に送る HTTP の状態、すべきことを示す。読者は、検証やリクエストの失敗を処理するアプリ開発者と、`elm`、`elm-server`、`elm-service` が出したエラーを読む管理者だ。ライセンスファイルを確かめる順序、つまりどのコードが先に返るかは、[ライセンスファイルと検証](../explanation/license-files-and-verification.md)で説明している。

## コードが届く経路

同じコードが、4 つの経路で届く。.NET では、失敗した検証やリクエストは `LicenseError` を持つ。その `Code` は `ErrorCode` の値で、`WireName` は表にある名前だ。libelasticlm では、数値を返す関数がコードの番号を返す。`elasticlm.h` は、コードごとにその名前のマクロを定義している（`ELM_NO_SEATS` は `40`）。このスレッドのメッセージは `elm_last_error_message` で得られる。`elm-server` と `elm-service` の HTTP API は、エラーを problem 形式のレスポンス（RFC 9457、`application/problem+json`）で返し、その `code` と `title` の項目に名前が入る。`elm` は出力に名前を書く。たとえば `elm license verify` は `invalid: ELM_EXPIRED: The license has expired.` と出力する。コードの名前と番号は、一度公開したら変わらない。新しいコードには、空いている番号を割り当てる。

「返すもの」の列には、そのコードを自分で返すプログラムとライブラリを書く。

- `ElasticLM.Licensing`: ライセンスファイル、リーストークン、サーバーライセンスを検証する .NET のライブラリ。
- `ElasticLM.Client`: アクティベーションとリースのための .NET のクライアント。受け取ったライセンスやリース、保存しているライセンスやリースのすべてに `ElasticLM.Licensing` の検証を行うので、そのコードも返す。
- `libelasticlm`: C ライブラリ。両方の .NET ライブラリの機能を提供してそのコードを返し、さらに呼び出し方に関わるコードを加える。
- `elm-server`: オンプレミスのライセンスサーバー。クライアントへのレスポンスと、ローカルの管理 API で返す。
- `elm-service`: SaaS 型のアクティベーションサービス。デバイスへのレスポンスと、管理 API で返す。
- `elm`: コマンドラインツール。`elm license verify` のように、自分で行う検証で返す。`elm-server` や `elm-service` がレスポンスで返したコードも表示する。

HTTP の状態は、サーバーがリクエストにそのコードで答える場合に示す。「—」は、そのコードがローカルでしか返らないか、`elm-server` がリースの再登録（`POST /v1/leases/reclaim`）のレスポンスの中でリースごとにしか返さないことを表す。リースの再登録のレスポンス自体は `200` だ。`elm-server` のライセンスの再読み込み（`POST /admin/v1/license/reload`）は、新しいサーバーライセンスが通らなかった検証のコードを、ほかの場面での HTTP の状態に関係なく `409` で返す。そうしたコードの行には、そのことを書いている。

## クライアントが自動で再試行するもの

`ElasticLM.Client` と `libelasticlm` は、いくつかのコードをアプリを介さずに処理する。

- `ELM_NONCE_REQUIRED`: クライアントは、レスポンスにある新しいノンスで、リクエストをもう一度だけ送る。アプリにこのコードが届くのは、2 回目のリクエストもこのコードを受け取ったときだけだ。
- リースの更新はバックグラウンドで行う。`ELM_CLONE_DETECTED` と `ELM_UNKNOWN_LEASE` 以外のコードで失敗すると、クライアントは 15 秒後、次に 30 秒後、そのあとはリースの有効期限まで 60 秒ごとに再試行する。`elm-server` から `ELM_UNKNOWN_LEASE` が返ると、クライアントはリースを再登録する。`elm-service` から返ったときと、`ELM_CLONE_DETECTED` のあとは、リースは失われる。それぞれの状態は[フローティングライセンス](../explanation/floating-licenses.md#リースの更新と再試行)で説明している。
- 保存したアクティベーションのトークンの更新も、バックグラウンドで行う。失敗すると、クライアントは 1 時間後に再試行する。`Retry-After` の時間のほうが長ければ、その時間のあとに再試行する（最大 1 日）。`ELM_PROOF_INVALID` のあとと、クローンを検出したあとは再試行しない。トークンの更新は[アクティベーション](../explanation/activation.md#トークンの更新)で説明している。

それ以外の失敗は、リースの取得、借用、アクティベーション、トライアルの失敗も含めて、すべてアプリに返る。サーバーが `Retry-After` を送ったとき（`ELM_SERVER_RECOVERING` か `ELM_RATE_LIMITED` のとき）は、結果にその時間が入る。.NET では `LeaseResult.Failed` または `ActivationResult.Failed` の `RetryAfter`、C では出力の `retry_after_seconds` だ。

## 成功、検証、署名付きのリクエスト（0 から 24）

| コード | 番号 | HTTP の状態 | 返すもの | 意味 | すべきこと |
| --- | --- | --- | --- | --- | --- |
| `ELM_OK` | 0 | 2xx | すべて | 成功。 | 何もしない。 |
| `ELM_INVALID_ARGUMENT` | 1 | 400 | `elm-server`、`elm-service`、`libelasticlm` | 引数が正しくない。管理 API のクエリの値や本文の項目が正しくない、機能のバージョンが `major.minor[.patch]` の形でない、シート数が 1 から機能のシート数までの範囲にない、借用の期間が 1 秒より短い、借用の更新を求めた（借用は期間のあいだずっと続き、更新しない。[借用](../explanation/floating-licenses.md#借用)を参照）、C で出力先が `NULL` か `struct_size` が違う、のどれかだ。 | 呼び出しを直す。メッセージは、正しくない引数か値を C での名前で挙げるか、どこが正しくないかを書く。 |
| `ELM_MALFORMED` | 2 | 400（`elm-server` のライセンスの再読み込みでは 409） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | 入力の形式かサイズが正しくない。有効でないライセンスファイルやトークン、サイズの上限を超えたファイル、解析できないリクエストの本文、壊れた保存済みのアクティベーションファイル、有効でないサーバーのレスポンス、のどれかだ。 | ファイルや本文が想定したもので、欠けていないかを確かめる。保存したアクティベーションが壊れている場合は、管理者にアクティベーションを解除してもらい、もう一度アクティベーションする。 |
| `ELM_UNTRUSTED_KEY` | 3 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | ライセンスやリースにあるどの鍵証明書も、トラストストアのルート鍵でその署名鍵を証明していない。 | アプリが発行元のルート鍵を信頼していることと、ファイルがその発行元のものであることを確かめる。 |
| `ELM_BAD_SIGNATURE` | 4 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | 署名が検証に通らない、またはトークンが ES256 で署名されていない。`elm-server` は、リースの再登録のレスポンスで、リースごとにこのコードを返す。 | ファイルは書き換えられたか壊れたものとして扱い、発行元から新しいファイルを受け取る。 |
| `ELM_CERTIFICATE_EXPIRED` | 5 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | トークンが、その鍵証明書の有効期間の外で発行された。 | 有効な鍵証明書のもとで署名したファイルを、発行元に求める。 |
| `ELM_CLOCK_TAMPERED` | 6 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | 時計が戻された、またはトークンの発行時刻が未来になっている。`elm-server` は、リースの再登録のレスポンスで、リースごとにもこのコードを返す。 | システムの時計を正しく合わせる。 |
| `ELM_WRONG_PRODUCT` | 7 | 400（`elm-server` のライセンスの再読み込みでは 409） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | ライセンスやリースが別の製品のものである。`elm-service` では、ライセンスキーがリクエストの製品のものでない。`elm-server` は、リースの再登録のレスポンスで、リースごとにもこのコードを返す。 | この製品のライセンスかキーを使う。 |
| `ELM_WRONG_INSTALLATION` | 8 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | ライセンスやリースが、別のインストール鍵に紐付いている。`elm-server` は、リースの再登録のレスポンスで、リースごとにもこのコードを返す。 | このインストールでアクティベーションするか、このインストール用のライセンスを使う。`elm-server` が起動時かライセンスの再読み込みでサーバーライセンスを拒否したときは、ベンダーからライセンスキーを受け取っていれば、このサーバーでサーバーライセンスをアクティベーションする。または、メッセージが示すインストール鍵に紐付けたサーバーライセンスを、ベンダーに発行してもらう（[elm-service でサーバーライセンスをアクティベーションする](../how-to/activate-a-server-license-with-elm-service.md)、[サーバーライセンスを更新する](../how-to/renew-a-server-license.md)）。 |
| `ELM_FINGERPRINT_MISMATCH` | 9 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | マシンフィンガープリントがライセンスと一致しない。 | ライセンスを受けたマシンで動かすか、このマシン用にライセンスを発行し直してもらう。 |
| `ELM_EXPIRED` | 10 | 403（`elm-server` のライセンスの再読み込みでは 409） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | ライセンスやリースが、有効期限と猶予期間を過ぎている。`elm-server` は、自分のサーバーライセンスの期限が切れると、すべてのリースのリクエストにこのコードを返す。`elm-service` は、サービスでのライセンスの期間が終わったときに返す。 | ライセンスを更新するか、期限を延長する。`elm-server` の場合は、新しいサーバーライセンスを受け取る。 |
| `ELM_FEATURE_NOT_LICENSED` | 11 | 403 | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | 機能がライセンスに含まれていない。 | その機能を含むライセンスを求める。 |
| `ELM_FEATURE_VERSION_NOT_LICENSED` | 12 | 403 | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | 機能のバージョンが、ライセンスで許された範囲の外にある。 | このバージョンを含むライセンスを求める。 |
| `ELM_FEATURE_EXPIRED` | 13 | 403 | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | 機能が、その機能自体の有効期限を過ぎている。 | 機能の期限を更新する。 |
| `ELM_FINGERPRINT_UNAVAILABLE` | 14 | 400（`elm-server` のライセンスの再読み込みでは 409） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service`、`elm` | マシンフィンガープリントを計算できない。または、フィンガープリントが必要なリクエスト（トライアル、借用、フィンガープリントを照合するポリシーでのアクティベーション）にフィンガープリントがない。 | フィンガープリントを得られる環境で動かすか、ノードロックでないライセンスを使う。 |
| `ELM_INSTALLATION_KEY_UNREADABLE` | 15 | — | `ElasticLM.Client`、`libelasticlm` | インストール鍵を読めない、または作れない。 | 選んだスコープの鍵のフォルダーの権限を確かめる。 |
| `ELM_WRONG_KIND` | 16 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | ライセンスの種類が、求められた種類と違う。たとえば、トライアルをサーバーライセンスとして使った。 | 求められた種類のライセンスを使う。 |
| `ELM_KEY_REVOKED` | 17 | 409（`elm-server` のライセンスの再読み込み） | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm` | ライセンスやリースが、トラストストアで失効した鍵で署名されている。 | 現在の鍵で署名したファイルを受け取る。 |
| `ELM_LEASE_OUT_OF_BOUNDS` | 18 | — | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm`、`elm-server` | リースがサーバーライセンスの範囲外にある。別のサーバーライセンスのリースである、サーバーライセンスにない機能やシート数を持つ、期間が長すぎる、サーバーライセンスの終了を過ぎている、のいずれかだ。`elm-server` は、リースの再登録のレスポンスで、リースごとにこのコードを返す。 | サーバーライセンスを取得し直す。繰り返すなら、サーバーの管理者に伝える。 |
| `ELM_INVALID_STATE` | 19 | — | `ElasticLM.Client`、`libelasticlm`、`elm` | 今はその呼び出しができない。サービスの URL がない、アクティベーションがすでに保存されている、アクティベーションが保存されていない、アクティベーション解除のプルーフがまだ保存されている、トークンの更新の時期ではない、別のプロセスがアクティベーションのロックを持っている、レスポンスのないリクエストが 16 件たまった、同じハンドルのコールバックの中から呼んだ、のどれかだ。`elm` は、サーバーがライセンスをオンラインで更新しているときにこのコードを返す。 | メッセージに従い、先にアクティベーション解除をする、待つ、コールバックの外から呼ぶ、のいずれかをする。レスポンスのないリクエストが 16 件たまったあとは、もう一度アクティベーションする前に、管理者がそのインストールのアクティベーションを解除する。 |
| `ELM_PROOF_INVALID` | 20 | 401（オフラインのリクエストファイルでは 400） | `elm-server`、`elm-service` | リクエストのプルーフがない、検証に通らない、または別のメソッド、URI、本文、鍵のものである。オフラインのリクエストファイルの署名が検証に通らない。 | 同じインストールからリクエストを送り直す。オフラインのファイルなら、新しく作る。 |
| `ELM_NONCE_REQUIRED` | 21 | 401 | `elm-server`、`elm-service` | プルーフのノンスが空か、サーバーの知らない値か、期限が切れている。`nonce` クレームのないプルーフには、代わりに `ELM_PROOF_INVALID` が返る。レスポンスの `ELM-Nonce` に新しいノンスが入っている。 | 何もしない。クライアントが新しいノンスでリクエストを送り直す。 |
| `ELM_REPLAYED` | 22 | 401（オフラインのリクエストファイルでは 409） | `elm-server`、`elm-service` | プルーフがすでに使われた。または、このリクエストのノンスを別のオフラインのリクエストが使った。 | 新しいリクエストかリクエストファイルを作る。 |
| `ELM_SERVER_PIN_MISMATCH` | 23 | — | `ElasticLM.Licensing`、`ElasticLM.Client`、`libelasticlm` | オンプレミスのサーバーが、クライアントがピンとして記録したサーバーではない。サーバーの鍵や TLS の鍵がサーバーライセンスの鍵と違う、または保存したピンが壊れている。自分の TLS 証明書（`tls.certificate`）を持つサーバーでも、クライアントの OS がその証明書を受け入れないときにこのコードになる。その CA が信頼されていない、サーバーの名前やアドレスが証明書にない、証明書の期限が切れている、のどれかだ。 | クライアントが正しいサーバーにつながっているかを確かめる。意図してサーバーを置き換えたなら、ピンをリセットする。自分の TLS 証明書を持つサーバーなら、その証明書を発行した CA をクライアントの OS に信頼させ、証明書の名前と期限を確かめる。 |
| `ELM_PAYLOAD_TOO_LARGE` | 24 | 413 | `elm-server`、`elm-service` | リクエストの本文が 64 KiB より大きい。 | 本文を小さくして送る。 |

## ライセンス、アクティベーション、リース（30 から 44）

| コード | 番号 | HTTP の状態 | 返すもの | 意味 | すべきこと |
| --- | --- | --- | --- | --- | --- |
| `ELM_LICENSE_NOT_FOUND` | 30 | 404（`elm-server` のライセンスの再読み込みでは 409） | `ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service` | ライセンスキーやライセンスが存在しない。クライアントは、アクティベーションが保存されていないときにこのコードを返す。`elm-server` は、サーバーライセンスのファイルがないと、ライセンスの再読み込みをこのコードで拒否する。起動時にファイルがないときは、`elm-server` はコードのないメッセージを出して止まる。メッセージは `error: The server license '<file>' does not exist.` で始まる（[コマンドラインと終了コード](server-configuration.md#コマンドラインと終了コード)）。 | ライセンスキーや ID を確かめる。先にアクティベーションする。`elm-server` にライセンスファイルを渡す。 |
| `ELM_LICENSE_SUSPENDED` | 31 | 403 | `elm-service` | ライセンスが一時停止されている。 | ベンダーに問い合わせる。管理者がライセンスを再開できる。 |
| `ELM_LICENSE_REVOKED` | 32 | 403 | `elm-service` | ライセンスが失効している。 | ベンダーに新しいライセンスを求める。 |
| `ELM_ACTIVATION_LIMIT` | 33 | 409 | `elm-service` | ライセンスのアクティベーション数が上限に達した。 | ほかのデバイスのアクティベーションを解除するか、アクティベーション数を増やしてもらう。 |
| `ELM_DEACTIVATION_LIMIT` | 34 | 409 | `elm-service` | ライセンスのアクティベーション解除の回数が上限に達した。 | ベンダーに問い合わせる。管理者が代わりにアクティベーションを解除できる。 |
| `ELM_TRIAL_USED` | 35 | 409 | `elm-service` | このインストールのフィンガープリントで、この製品のトライアルがすでに使われている。 | ライセンスを購入する。管理者はトライアルの記録を削除できる。 |
| `ELM_CLONE_DETECTED` | 36 | 409 | `ElasticLM.Client`、`libelasticlm`、`elm-server`、`elm-service` | インストールのコピーが見つかった。クローンへの対処方法が `deny` の場合は、アクティベーションやリースが止まる。 | 管理者にアクティベーションを解除してもらい、もう一度アクティベーションする。止まったリースは、有効期限で終わる。 |
| `ELM_REQUEST_EXPIRED` | 37 | 400 | `elm-service` | オフラインのリクエストファイルの期限が切れている。 | 新しいリクエストファイルを作る。 |
| `ELM_REQUEST_MISMATCH` | 38 | — | `ElasticLM.Client`、`libelasticlm` | オフラインのレスポンスが、クライアントが待っているリクエストと一致しない。 | 最新のリクエストに対するレスポンスを取り込むか、新しいリクエストを作る。 |
| `ELM_TRIAL_NOT_OFFERED` | 39 | 404 | `elm-service` | 製品がトライアルを提供していない。アプリが渡した製品のコードに当たる製品がないか、製品にトライアルのポリシーが指定されていない。 | アプリが渡す製品 ID を確かめる。正しければベンダーに問い合わせる。製品にトライアルのポリシーが必要だ。 |
| `ELM_NO_SEATS` | 40 | 409 | `elm-server`、`elm-service` | 機能に空いているシートがない。または、ライセンスで許されるシート数が、保持されているシート数より少なくなった。 | シートが空くのを待つか、シートを増やしてもらう。 |
| `ELM_UNKNOWN_LEASE` | 41 | 404 | `elm-server`、`elm-service` | サーバーがそのリースを知らない。 | `elm-server` から返ったなら、何もしない。サーバーの再起動のあと、クライアントがリースを再登録する。`elm-service` から返ったなら、リースは失われているので、新しいリースを取得する。管理者は、リース ID を確かめる。 |
| `ELM_BORROW_NOT_ALLOWED` | 42 | 403 | `elm-server`、`elm-service` | 借用が許されていない、または期間が上限を超えている。`elm-server` は、リースの再登録のレスポンスで、借用のトークンにもこのコードを返す。再起動のあと、借用はリースの再登録ではなく、サーバーの借用の記録から戻るからだ。 | もっと短い期間で借用するか、借用を許可してもらう。 |
| `ELM_SERVER_RECOVERING` | 43 | 503 | `elm-server` | サーバーが再起動のあと、リースを再登録している。レスポンスには `Retry-After` が付く。 | リースの取得や借用では、`Retry-After` の時間だけ待ってから、もう一度試す。その時間をユーザーに表示する。リースの更新は、クライアントが再試行する。 |
| `ELM_RETURN_NOT_ALLOWED` | 44 | 409 | `elm-server`、`elm-service` | 今は借用の早期返却ができない。30 日間に許された早期返却の回数を使い切った、または借用のあとでライセンスが不利な方向に変わった（別のライセンスになった、機能のシート数が減った、借用の上限が短くなった）。`elm-server` の管理者は、借用を解放できない。 | 借用が有効期限で終わるのを待つ。 |

## 認証、制限、管理（50 から 56）

`ELM_CONFLICT` でいうマシンに紐付けないライセンスとは、ポリシーの `binding` が `unbound` のライセンスだ。デバイスに紐付かず、アクティベーションすることはなく、ライセンスファイルは管理 API から得る。ノードロックのライセンス（`binding` が `nodeLocked`）は、代わりにデバイスごとにアクティベーションする。クラウドのフローティングのライセンスとは、ポリシーの `floating` が `cloud` のライセンスで、`elm-service` がそのシートをリースとして貸し出す。ポリシーの設定は[アクティベーション](../explanation/activation.md#ポリシーの設定)に一覧がある。

| コード | 番号 | HTTP の状態 | 返すもの | 意味 | すべきこと |
| --- | --- | --- | --- | --- | --- |
| `ELM_UNAUTHORIZED` | 50 | 401 | `elm-server`、`elm-service` | 管理者の認証に失敗した。管理者トークン、API キー、アクセストークン、セッションがない、または正しくない。 | トークン、API キー、サインインを確かめる。`elm server` のコマンドでは、`elm` が読んだ管理者トークンが、実行中のサーバーのものではない。コマンドをもう一度実行し、同じコードがまた出るなら、サーバーを再起動する（[elm server](elm-cli.md#elm-server)）。 |
| `ELM_FORBIDDEN` | 51 | 403 | `elm-server`、`elm-service` | 管理者に権限がない、アクセストークンがどのロールにも対応しない、CSRF 対策のヘッダーがない、または `elm-server` の `allow` / `deny` の設定がアドレス、ホスト、ユーザーを拒否した。 | メッセージにある権限を持つキーかロールを使う。またはサーバーのアクセスの設定を変える。 |
| `ELM_RATE_LIMITED` | 52 | 429 | `elm-server`、`elm-service` | このアドレス、インストール鍵、API キー、管理者からのリクエストが多すぎる。レスポンスには `Retry-After` が付く。 | `Retry-After` の時間だけ待ってから、もう一度試す。 |
| `ELM_SERVER_BUSY` | 53 | 503 | `elm-server` | サーバーが、使用済みのプルーフの ID（`jti`）を上限の 100,000 件まで保持していて、そのいくつかが期限切れになるまで、プルーフ付きのリクエストを拒否する。 | しばらくしてから、もう一度試す。 |
| `ELM_NOT_FOUND` | 54 | 404 | `elm-service` | 管理 API の呼び出しが、存在しない対象を指している。たとえば、API キー、トライアル、利用ログのファイルだ。 | ID や名前を確かめる。 |
| `ELM_CONFLICT` | 55 | 409 | `elm-service` | リクエストが、対象の状態や一意の値と衝突する。使用中のコード、対象の状態では許されない変更（呼び出した管理者自身、ブートストラップのオーナー、有効な最後のオーナーを無効にするなど）、ノードロックやクラウドのフローティングのライセンスのライセンスファイルのリクエスト（ライセンスファイルがあるのは、マシンに紐付けないライセンスだけだ）、一時停止・失効・期限切れのライセンスのライセンスファイルのリクエスト、マシンに紐付けないライセンスやクラウドのフローティングのライセンスのアクティベーション、クラウドのフローティングでないライセンスのリース、利用ログを書かないノードへの利用ログのリクエスト、のどれかだ。 | メッセージに従う。マシンに紐付けないライセンスでは、アクティベーションせずにライセンスファイルを使う。 |
| `ELM_ACTIVATION_NOT_FOUND` | 56 | 404 | `elm-service` | その ID のアクティベーションがない。管理者が削除したか、アクティベーションが解除された。 | もう一度アクティベーションする。 |

## ネットワークのエラーと内部エラー（90 と 99）

| コード | 番号 | HTTP の状態 | 返すもの | 意味 | すべきこと |
| --- | --- | --- | --- | --- | --- |
| `ELM_NETWORK_ERROR` | 90 | — | `ElasticLM.Client`、`libelasticlm`、`elm`。`elm-service` と `elm-server` は、ログの `result` に書く | サーバーやサービスにつながらない、またはレスポンスがなかった。`elm-service` の監査ログと、`elm-server` の `admin.action` の行では、呼び出し側が応答の前か途中で接続を切ったことを表す。このコードを返すレスポンスはない。 | URL とネットワークを確かめ、サーバーが動いているかを確かめる。 |
| `ELM_INTERNAL_ERROR` | 99 | 500（`elm-server` のライセンスの再読み込みでは 409） | すべて | 内部エラー。データベースの処理が失敗した、ファイルに書き込めない、予期しない失敗が起きた、のどれかだ。`elm-server` は、借用の記録に新しいライセンスを書き込めないとき、このコードでライセンスの再読み込みを拒否する。`ElasticLM.Client` と `libelasticlm` は、失敗したリクエストへのサーバーのレスポンスが、リダイレクトのとき、本文が JSON でないとき、`code` を持たないとき、`code` がクライアントの知らないコードのとき、`ELM_OK` や `ELM_NETWORK_ERROR` のようにサーバーがクライアントには返さないコードのときにも、このコードを返す。そのときのメッセージは、レスポンスの HTTP の状態を入れた「The server answered 400 without a known error code.」のような文になる。クライアントは、リースの再登録のレスポンスで、リースの `code` が、サーバーがそこでリースに付けるコードでないときも、そのリースについてこのコードを返す。 | メッセージを読む。`elm-server` や `elm-service` が返したときは、そのプログラムの運用ログを読んで原因を調べる。運用ログは[ログ](logs.md)で説明している。繰り返すなら報告する。メッセージが「without a known error code」で終わるときは、このコードを選んだのはサーバーではなくクライアントだ。[エラーを処理する](../how-to/handle-errors.md)の `ELM_INTERNAL_ERROR` の行を見る。再登録で失われたリースのメッセージは、コードによらず「The server refused to reclaim the lease.」だ。 |
