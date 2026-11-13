---
sidebar_position: 2
---

# elm-server の設定

このページは、`elm-server` のすべての設定をまとめたものだ。`elm-server` は、顧客のネットワークでフローティングライセンスのシートを貸し出すオンプレミスのライセンスサーバーだ。サーバーをインストールして運用する人に向けている。サーバーの役割と、シート、リース、借用の仕組みについては、先に[フローティングライセンス](../explanation/floating-licenses.md)を読むこと。

Windows では、`elm-server` と `elm server` のコマンドに、Windows 10 バージョン 1803 か Windows Server 2019、またはそれ以降が要る。サーバーのローカルの管理 API は、Unix ドメインソケットで待ち受ける。Unix ドメインソケットは、同じマシンのプログラムどうしが、ネットワークのポートを使わずに接続するためのファイルだ（[ポート](#ポート)を参照）。Windows にも Unix ドメインソケットがあり、使えるのはこれらのバージョンからだ。このページでは、このファイルをソケットのファイルと呼ぶ。

## コマンドラインと終了コード

```text
elm-server [--config <settings.json>] [--set-aside-broken-records]
```

| 引数 | 意味 |
| --- | --- |
| `--config <path>` | JSON の設定ファイル。指定しなければ、設定は環境変数だけから読む。[設定の渡し方](#設定の渡し方)を参照 |
| `--set-aside-broken-records` | 最新の借用の記録の検証に失敗しても起動する。壊れたファイルの名前を `borrows-broken-<UTC time>.jsonl` に変え、そのファイルにある借用を除いて起動する。除いた借用は数えないので、その借用が終わる前に、同じシートをもう一度貸し出すことがある。この引数がなければ、借用の記録が壊れているときは起動せず、終了コード 1 で終わる。借用の記録は[ログ](logs.md)で説明している |
| `--help`、`-h` | 使い方を表示し、0 で終わる |

これ以外の引数や、パスのない `--config` はエラーになる。

| 終了コード | 意味 |
| --- | --- |
| 0 | サーバーが正常に止まった（Ctrl+C、`SIGTERM`、またはサービスの停止）。または、`--help` で使い方を表示した |
| 1 | サーバーを起動できない。理由は表の下に挙げる |
| 2 | 不明な引数、パスのない `--config`、または設定のエラー。設定ファイルがない、読めない、64 KiB より大きい、または有効でない。値が有効でない。`product` がない。設定ではない [`admin.listen`](#admin) に値がある |

終了コード 1 か 2 で終わる前に、`elm-server` は `error:` と理由を標準エラー出力に表示する。

終了コード 1 になるのは、たとえば次のときだ。

- サーバーライセンスがまだない。初回の起動ではこうなる。サーバーはインストール鍵を作り、メッセージにその鍵の[サムプリント](../explanation/devices-and-proofs.md#インストール鍵)とマシンフィンガープリントを示す。これをベンダーに送ると、ベンダーがサーバーライセンスを発行する。または、ベンダーのライセンスキーでサーバーライセンスをアクティベーションする。オンラインなら [`elm server activate`](elm-cli.md#elm-server-activate)、オフラインなら [`elm server activate-request`](elm-cli.md#elm-server-activate-request) と [`elm server import`](elm-cli.md#elm-server-import) を使う。サーバーがベンダーから何を受け取る必要があるかは、[フローティングライセンス](../explanation/floating-licenses.md#elm-server-の運用)で説明している。
- ソケットのファイル（データフォルダーの `admin.sock`）のパスが、システムの上限より長い。メッセージは `error: The path of the administration socket '<path>' is <length> bytes in UTF-8, and this system allows <limit>. Use a data folder with a shorter path.` になる。サーバーは、ルート鍵を読む前、データフォルダーを作る前にこれを確かめるので、この起動は何も残さない。[データフォルダー](#データフォルダー)を参照。
- システムに Unix ドメインソケットがない。このページの冒頭に挙げたバージョンより古い Windows などだ: `error: This system has no Unix domain sockets, which the server's administration API needs. On Windows, Windows 10 version 1803 or Windows Server 2019, or later, is required.` サーバーは、パスの長さを確かめたあと、同じ時点でこれを確かめる。
- ルート公開鍵が指定されていない。
- 同じデータフォルダーで、別の `elm-server` が動いている: `error: Another elm-server is running on the data folder '<folder>': it holds the lock file '<path>'.` そのサーバーを止めるか、このサーバーに専用の `dataDirectory` を指定する。Windows では、バックアップやウイルス対策のスキャンなど、ほかのプログラムが `server.lock` を開いているあいだも、同じ行になる。そのときは、サーバーをもう一度起動し、そのプログラムの対象から `server.lock` を外す（対象から外すファイルは、[データフォルダー](#データフォルダー)に挙げている）。ファイルのロックが効かない場所（[データフォルダー](#データフォルダー)を参照）では、2 つ目のサーバーは代わりに `error: Another elm-server is running on the data folder '<folder>': its administration socket '<path>' accepts connections.` で止まる。
- ロックファイル `server.lock` を開けない。ほかのプログラムがファイルを開いているとき（上の項目）は除く。権限がない、ディスクがいっぱい、ファイルシステムが読み取り専用、といったときだ: `error: The lock file '<path>' cannot be opened: <reason>` 原因は、行の最後の理由に出る。サーバーのユーザーがデータフォルダーに書き込めることと、`server.lock` がファイルであることを確かめる。
- `admin.sock` のパスにあるものを、使うことも消すこともできない。その名前のフォルダーがある場合などだ: `error: The administration socket '<path>' cannot be used: <reason>` そのパスにあるものを消して、サーバーをもう一度起動する。
- 設定で指定したファイル（ルート鍵、サーバーライセンス、TLS 証明書）を読めないか、有効でない。
- TLS 証明書に拡張キー使用法（証明書を使ってよい用途の一覧）があり、その中にサーバー認証（`1.3.6.1.5.5.7.3.1`。OpenSSL では `TLS Web Server Authentication`、Windows では `Server Authentication` と表示される）がない。
- TLS 証明書のファイルで、サーバーの証明書のあとにある証明書が CA の証明書ではない。メッセージは、ファイルの中で何枚目かと、そのサブジェクトを示す: `The TLS certificate '<file>' cannot be used by a server: its certificate 2 (CN=…) is not a CA certificate.`
- Windows で、サーバーが TLS 証明書のファイルの中間 CA の証明書を送らなかった。または、送っているかを確かめられなかった。2 つのメッセージと対処は[中間 CA の証明書](#tls)にある。
- サーバーライセンスの検証に失敗した。`error:` のあとのメッセージは、失敗した確認のエラーコードで始まる。たとえば、別のインストール鍵に紐付いたライセンスでは `error: ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to another installation key. This server's installation key is <thumbprint>.` となる。すべてのコードは[エラーコード](error-codes.md)に挙げている。
- `service.url` を設定しているのに、サーバーライセンスを書き込めない。
- 借用の記録が壊れているか、読み書きできない。
- 利用ログを書き込めない。
- 管理者トークンを書き込めない: `error: The administrator token in '<folder>' cannot be created or read.` Windows では、サーバーが起動するときに、ほかのプログラムが `admin.token` を 2 秒より長く開いたままにしていても、こうなる。そのときは、サーバーをもう一度起動する。繰り返すなら、ウイルス対策のスキャンなど、ファイルを開いているプログラムの対象から `admin.token` を外す。
- `listen` のアドレスか、ソケットのファイルで待ち受けられない。ほかのプログラムがそのポートを使っている、そこで待ち受ける権限がない、このマシンにそのアドレスがない、データフォルダーのファイルシステムにソケットのファイルを置けない、といった場合だ。最後の行は `error: elm-server cannot listen on <listen> and <socket path>: <reason>` になる。`listen` の URL を、サーバーが読んだとおりに `/` で終わる形で示し、`admin.sock` の絶対パスを示す。

## 設定の渡し方

- 設定は、JSON の設定ファイル、環境変数、またはその両方で渡す。ファイルは `--config` で渡す。

  ```text
  elm-server --config /etc/elm-server/settings.json
  ```

- `--config` がなければ、`elm-server` は環境変数だけを読む。
- 環境変数は、ファイルにある同じ設定より優先する。空の値を設定した環境変数は、設定されていないものとして扱う。ただ 1 つの例外は `ELM_TLS_PASSWORD` で、空の値は空のパスワードとして使う。
- ファイルは 64 KiB までだ。ファイルが知らないキー（たとえばつづりを間違えたキー）はエラーになる。キーの名前は大文字と小文字を区別する。
- `--config` や設定（`license`、`dataDirectory`、`trust.rootKeys`、`tls.certificate`、`tls.key`）に書いた相対パスは、`elm-server` のプロセスの作業フォルダー、つまりサーバーを起動したフォルダーからの相対パスだ。設定ファイルのフォルダーや実行ファイルのフォルダーからの相対パスではない。ただ 1 つの例外は `log.usage` で、その相対パスは `dataDirectory` の下を指す。
- 設定にエラーがあると、`elm-server` は起動する前に止まり、`error:` と理由を表示する。終了コードは[コマンドラインと終了コード](#コマンドラインと終了コード)にある。
- サーバーが設定を読むのは、起動するときだけだ。[`elm server reload`](elm-cli.md#elm-server-reload) は、動いているサーバーに、同じ `license` のパスからサーバーライセンスのファイルを読み直させる。それ以外のものは読み直さない。どの設定の変更も、次の起動から反映される。
- `borrow.maxDuration` などの期間は、`d.hh:mm:ss` の形で書く。日数、ドット、時、分、秒の順だ。`7.00:00:00` は 7 日、`0.12:00:00` は 12 時間を表す。日数は省略できるので、`12:00:00` も 12 時間になる。

### 環境変数の名前

環境変数の名前は、`ELM_` のあとに設定のパスを大文字にして続け、`.` を `_` に置き換えたものだ。たとえば、`rateLimit.perIp` は `ELM_RATELIMIT_PERIP`、`publicUrls` は `ELM_PUBLICURLS` になる。リストは 1 つの値として書き、要素を `;` で区切る。たとえば `ELM_ALLOW=10.0.0.0/8;192.168.1.0/24` だ。

次の 2 つの環境変数は、この規則に従わない。

| 環境変数 | 中身 |
| --- | --- |
| `ELM_TRUST_ROOT_KEYS` | ベンダーのルート公開鍵の PEM テキスト。ファイルのパスではない。`trust.rootKeys`（ファイルの中、または `ELM_TRUST_ROOTKEYS`）がファイルを 1 つも指定していないときだけ読む |
| `ELM_TLS_PASSWORD` | `tls.certificate` で指定した PKCS#12 ファイルのパスワード。設定ファイルには対応する設定がない。空の値は空のパスワードになる |

**ルート鍵の 2 つの名前:** `ELM_TRUST_ROOTKEYS` は、`trust.rootKeys` と同じくファイルのパスを入れる。`ROOT` と `KEYS` のあいだに `_` がある `ELM_TRUST_ROOT_KEYS` は、PEM テキストそのものを入れる。

## ポート

| 設定 | 既定 | 用途 |
| --- | --- | --- |
| `listen` | `https://0.0.0.0:7070`。すべての IPv4 アドレスのポート 7070 | クライアント。TLS 上のリースのプロトコルに使う |

`elm server` のコマンドが使うローカルの管理 API には、ポートも設定もない。サーバーは、[データフォルダー](#データフォルダー)のソケットのファイル `admin.sock` で待ち受ける。Windows、Linux、macOS のどれでも同じで、そのソケットの上で平文の `http` を使う。接続はマシンの外に出ないので、暗号化しない。接続を守るのは、データフォルダーのアクセス権と管理者トークンだ（[セキュリティモデル](../explanation/security-model.md#顧客の管理者向け)を参照）。接続できるのは、サーバーのマシン上の、データフォルダーを開けるプロセスだけだ。[`elm-server` のローカルの管理 API](administration-api.md#elm-server-のローカルの管理-api) を参照。

## 最上位の設定

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `product` | `ELM_PRODUCT` | 文字列 | なし（必須） | サーバーライセンスの製品 ID。英小文字、数字、`-` からなる 1 から 64 文字。既定の `dataDirectory` の名前にも使う |
| `listen` | `ELM_LISTEN` | URL | `https://0.0.0.0:7070` | サーバーがクライアントを待ち受ける場所。クエリとフラグメントのない `https` の URL。ホストは IP アドレス、`localhost`、`0.0.0.0`、`[::]` のいずれか |
| `publicUrls` | `ELM_PUBLICURLS` | URL のリスト | `listen` から作る | クライアントがサーバーに接続するときの URL。どれもクエリとフラグメントのない `https` の URL。表の下の注意を参照 |
| `license` | `ELM_LICENSE` | ファイルのパス | `elm-server` の実行ファイルと同じフォルダーの `server.elmlic` | サーバーライセンスのファイル。`service.url` を設定したときは、サーバーがこのファイルに書き込めなければならない。表の下の注意を参照 |
| `allow` | `ELM_ALLOW` | 文字列のリスト | なし | リースのリクエストを許可するエントリー。[アクセスルール](#アクセスルール)を参照 |
| `deny` | `ELM_DENY` | 文字列のリスト | なし | リースのリクエストを拒否するエントリー。[アクセスルール](#アクセスルール)を参照 |
| `dataDirectory` | `ELM_DATADIRECTORY` | フォルダーのパス | Windows では `%ProgramData%\ElasticLM\<product>`、macOS では `/Library/Application Support/ElasticLM/<product>`、Linux では `/var/lib/elasticlm/<product>` | サーバーのデータフォルダー。パスの長さに上限がある。フォルダーはローカルのファイルシステムに置く。[データフォルダー](#データフォルダー)を参照 |

注意:

- `publicUrls`: クライアントのプルーフには、クライアントがプルーフを送った先の URL が入る。サーバーは、これらの URL のどれかが入ったプルーフだけを受け入れる。DNS の別名など、マシンのホスト名とは違う名前でクライアントがサーバーに接続するときは、その URL をここに入れる。省略したときは、`listen` のホストが `0.0.0.0` か `[::]` なら `https://<host name>:<port>/` と `https://localhost:<port>/`、それ以外なら `listen` の URL になる。
- `license`: 実行ファイルのフォルダー（`Program Files` や `/usr/local/bin` の下など）には、書き込めないことが多い。`license` には、データフォルダーの `server.elmlic` のように、実行ファイルとは別の場所のファイルを指定する。サーバーライセンスのファイルを書き込むコマンド（[`elm server activate`](elm-cli.md#elm-server-activate)、[`elm server refresh`](elm-cli.md#elm-server-refresh)、[`elm server import`](elm-cli.md#elm-server-import)）は、`--license` でそのパスを指定する必要があり、サーバーの設定は読まない。これらのコマンドには、`license` と同じパスを渡す。
- `dataDirectory`: インストール鍵をマシン全体用に保存する同じ製品のアプリは、そのマシンで同じ既定のフォルダーを使う。サーバーには専用のフォルダーを指定する。

### データフォルダー

`elm-server` は、データフォルダーに次のファイルを置く。フォルダーは、`admin.sock`、`server.lock`、`admin.token` を除いてバックアップする。初めの 2 つは中身を持たず、トークンはサーバーが起動のたびに作り直すからだ。フォルダーをウイルス対策でスキャンするときも、同じ 3 つのファイルを対象から外す。Windows では、サーバーが動いているあいだに `admin.sock` や `server.lock` を読もうとしたプログラムは、エラーになる。

| ファイル | 中身 |
| --- | --- |
| `installation.key` | サーバーのインストール鍵。初回の起動時に作る。サーバーライセンスはこの鍵に紐付くので、鍵が新しくなるとサーバーライセンスも新しく要る。Windows では DPAPI で保護する |
| `clock` | 時刻の状態。インストール鍵で署名してあり、サーバーはこれで時計が戻されたことに気づく |
| `activation.json` | サーバーライセンスを更新する `elm-service` から、`elm server activate` か `elm server import` が受け取ったアクティベーション。どちらかのコマンドを実行したあとにだけある。隣の `activation.lock` はロックファイルだ |
| `admin.token` | `elm server` のコマンドが管理 API に送る管理者トークン。サーバーは、起動のたびに新しいトークンを作ってファイルを置き換えるので、前の起動のトークンは拒否される。ファイルは、サーバーが止まったあとも残る。これを読める人は、サーバーを管理できる |
| `admin.sock` | ローカルの管理 API のソケットのファイル。サーバーが動いているあいだ、ここにある。サーバーは止まるときにこれを消す。強制終了されたサーバーはこれを残し、次の起動が消す。秘密は入っていない |
| `server.lock` | ロックファイル。中身は空で、サーバーが動いているあいだ開いたままにする。同じフォルダーで 2 つ目の `elm-server` が起動しないようにするためだ。サーバーが止まったあとも残る。サーバーが動いているあいだは消さないこと |
| `borrows-<UTC time>.jsonl` | 借用の記録。[ログ](logs.md#elm-server-の借用の記録)を参照 |
| `usage-<UTC time>.jsonl` | 利用ログ。`log.usage` で別のフォルダーを指定したときは、ここには置かない。[ログ](logs.md#利用ログ)を参照 |

データフォルダーと `admin.token` を読めるのは、Linux と macOS ではサーバーのユーザーと root だけ、Windows ではサーバーのユーザー、Administrators グループ、SYSTEM だけだ。ここでいうサーバーのユーザーは、サーバーを起動してフォルダーを作ったユーザーだ。サーバーがフォルダーをそのように作る。Linux と macOS では、フォルダーのモードを `0700`、`admin.token` のモードを `0600` にする。Windows では、フォルダーに、サーバーのユーザー、Administrators グループ、SYSTEM にアクセスを許すアクセス制御リストを付け、`admin.token` は DPAPI で、1 人のユーザーではなくマシンに対して保護する。`admin.sock` に接続できるのも、同じユーザーだけだ。サーバーは、ほかのユーザーが読み書きできる既存のフォルダーは使わず、何を直せばよいかを示すメッセージを出して止まる。

データフォルダーは、ローカルのファイルシステムに置く。`server.lock` のファイルのロックがあるので、2 つ目のサーバーはそのフォルダーでは起動しない。Linux と macOS では、このロックが効くのは、ファイルのロックがあるファイルシステムだけで、ネットワークファイルシステムの一部にはこれがない。Linux と macOS では、サーバーの環境で、環境変数 `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` が `1` か `true` のときも効かない。この変数は設定しない。ロックが効かない場所では、1 つのフォルダーでほぼ同時に起動した 2 つのサーバーが、どちらも動くことがある。そうなると、`elm server deactivate` が、動いているサーバーを止まっていると判断することがある。

`admin.sock` の絶対パスは、UTF-8 で、Windows と Linux では 107 バイトまで、macOS では 103 バイトまでだ。そのうち 11 バイトを、ファイル名とその前の区切り文字が使う。そのため、データフォルダー自体の絶対パスは、Windows と Linux では 96 バイトまで、macOS では 92 バイトまでになる。ASCII 以外の文字は、1 文字で 2〜4 バイトを使う。相対パスの `dataDirectory` は、先に作業フォルダーからの絶対パスに直す。パスがこれより長いと、サーバーは起動せず、終了コード 1 で終わる。`elm server` のコマンドは、同じメッセージを出して、終了コード 2 で止まる。既定のフォルダーは、Windows と Linux では、どの製品 ID でも上限に収まる。macOS では、製品 ID が 53 文字までのときだけ収まる。それより長い製品 ID のサーバーには、パスの短い `dataDirectory` が要る。`--product` は既定のフォルダーを指すので、そのサーバーに対する `elm server` のコマンドには `--data` が要る。

インストール鍵はデータフォルダーの中にある。そのため、新しい空のフォルダーで起動したサーバーは新しい鍵を作り、サーバーライセンスはその鍵と合わない。移したデータフォルダーは、新しいパスでも、サーバーの確認を通らなければならない。通らなければ、サーバーは `error: The folder '<folder>' is not safe for secrets` で始まる行を出して止まる。Linux と macOS では、フォルダーがグループとほかのユーザーにアクセスを許さず（モード `0700`）、親フォルダーにグループもほかのユーザーも書き込めないことが要る。`installation.key` など、中のファイルのモードは `0600` なので、フォルダーの所有者も変えてはならない。Windows では、フォルダーの所有者と親フォルダーの所有者が、それぞれサーバーのユーザー、Administrators グループ、SYSTEM のどれかであることが要る。アクセス制御リストは、ほかの誰にもアクセスを許さず、親フォルダーから継承していないものでなければならない。フォルダーをコピーすると、コピーは新しい親フォルダーのアクセス制御リストを受け継ぐので、サーバーはそれを使わない。`admin.sock`、`admin.token`、`server.lock` は移さなくてよい。

### アクセスルール

`allow` と `deny` のエントリーは、次のいずれかだ。

| エントリー | 一致するもの |
| --- | --- |
| IP アドレス（`192.168.1.20` など） | そのアドレス |
| CIDR の範囲（`10.0.0.0/8` など） | 範囲内のすべてのアドレス |
| `host:<name>` | クライアントが送るホスト名。大文字と小文字を区別しない |
| `user:<name>` | クライアントが送る OS のユーザー名。大文字と小文字を区別する |

- `allow` と `deny` のエントリーは、合わせて 1,000 個までだ。`host:` と `user:` の名前は 1 から 255 文字。
- IP のエントリーは、すべてのリースのリクエストに適用する。`host:` と `user:` のエントリーは新しいリースにだけ適用する。クライアントがホスト名と OS のユーザー名を送るのは、アプリがこれを有効にしたとき（`LicenseClientOptions` の `SendClientInfo`。C では `send_client_info`）だけで、既定では無効だ。これらの名前はクライアントが自分で名乗るもので、サーバーは確かめられない（[フローティングライセンス](../explanation/floating-licenses.md#顧客の管理者にできることできないこと)を参照）。
- 種類（IP、ホスト、ユーザー）ごとに、`deny` のエントリーに一致するか、その種類の `allow` のエントリーがあってどれにも一致しないとき、リクエストを拒否する。ホスト名やユーザー名のない新しいリースは、`host:` と `user:` のどのエントリーにも一致しない。
- 拒否したリクエストには、`403` と `ELM_FORBIDDEN` を返す。

## `rateLimit`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `rateLimit.perIp` | `ELM_RATELIMIT_PERIP` | 整数 | `20` | 1 つの送信元（IPv4 アドレス、または IPv6 の /64）からの 1 秒あたりのリクエスト数。1 から 10000。上限を超えたリクエストには `429` と `ELM_RATE_LIMITED` を返す。`elm-server` は `X-Forwarded-For` を読まないので、リバースプロキシや NAT の内側にあるクライアントは、すべて 1 つの送信元として数える |

## `trust`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` | ファイルのパスのリスト | なし（必須） | ベンダーのルート公開鍵の PEM ファイル。サーバーライセンスの鍵証明書を、この鍵で確かめる。ファイルの代わりに、`ELM_TRUST_ROOT_KEYS`（2 つ目の `_` に注意）に PEM テキストを入れてもよい。どちらもなければ、サーバーは起動しない |
| `trust.revokedKeyIds` | `ELM_TRUST_REVOKEDKEYIDS` | 文字列のリスト | なし | サーバーがもう信頼しない鍵 ID（`kid`） |

## `tls`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | ファイルのパス | なし。サーバーが起動のたびに、インストール鍵で自己署名証明書を作る | サーバーの TLS 証明書。`tls.key` を設定したときは PEM の証明書、設定しないときは PKCS#12 ファイルで、そのパスワードは `ELM_TLS_PASSWORD` に入れる（設定しなければパスワードなし）。証明書には秘密鍵が含まれていなければならない。拡張キー使用法があるときは、その中にサーバー認証（`1.3.6.1.5.5.7.3.1`）が含まれていなければならない |
| `tls.key` | `ELM_TLS_KEY` | ファイルのパス | なし | PEM の `tls.certificate` に対応する PEM の秘密鍵 |

中間 CA の証明書:

- CA が中間 CA でサーバーの証明書に署名しているなら、中間 CA の証明書を同じファイルに入れる。サーバーは自分の証明書と一緒にそれを送る。PEM のファイルでは、サーバーの証明書を最初に、中間 CA をそのあとに並べる。サーバーは `tls.key` を最初の証明書と組み合わせる。PKCS#12 のファイルでは、秘密鍵を持つ最初の証明書がサーバーの証明書で、秘密鍵を持たない証明書が中間 CA になる。ルート CA は要らない。ルート CA は、アプリを動かすマシンが自分で信頼していなければならない。
- サーバーの証明書のあとにある証明書は、どれも CA の証明書（基本制約で CA とされた証明書）でなければならない。そうでなければサーバーは起動しない（終了コード 1）。サーバーの証明書からルートへつながらない中間 CA は、サーバーがメッセージを出さずに外す。
- サーバーは起動のときに、これらの証明書から証明書チェーンを組む。そのとき、システムの証明書ストアと、証明書に書かれたアドレス（Authority Information Access）も使う。サーバーのマシンが知っているルートまでチェーンが届かないと、足りない証明書をダウンロードしようとする。そのアドレスが応答しないネットワークでは、ファイルに中間 CA がなくても起動が遅くなる。Linux では 15 秒ほど遅くなった。Windows と macOS では測っていない。Linux では、サーバーは自分の証明書に書かれた OCSP レスポンダーにも証明書の状態を問い合わせ、証明書と一緒に送る（OCSP ステープリング）。
- Windows では、証明書ストアだけでチェーンを組めないとき、.NET が中間 CA を、中間証明機関の証明書ストア（マシンのストア、書き込めなければサーバーのユーザーのストア）に入れ、そのまま残す。Windows はそのストアにある中間 CA しか送らないためだ。それでもマシンが信頼するルートまでチェーンが届かないと、.NET はチェーンの最後の証明書も同じストアに入れる。ファイルやダウンロードから得た、マシンが信頼していないルートである。そのストアに入った証明書があっても、ルートを含めて、信頼されるものは増えない。サーバーのユーザーがどちらのストアにも書き込めなければ、Windows は中間 CA を送れず、サーバーは起動しない（次の項目を参照）。
- Windows では、ファイルに中間 CA があると、サーバーは起動のたびに、待ち受けを始めた直後に、自分の `listen` のアドレスへ TLS で 1 回接続する（`listen` が `0.0.0.0` か `[::]` のときは、ループバックアドレスへ接続する）。送られてきた証明書を読み、リクエストを送らずに接続を閉じる。Windows は、サーバーの証明書と一緒にどの証明書を送るかを、プロセスの最初の TLS ハンドシェイクで決め、プロセスが終わるまで変えない。そのときにほかのプログラムが証明書ストアを書き換えていると、Windows が中間 CA を送らないことがある。サーバーのチェーンに入る中間 CA が送られていなければ、サーバーはその証明書を示して、終了コード 1 で止まる: `error: Windows left the intermediate CA certificate <subject> (thumbprint <thumbprint>) of the TLS certificate '<file>' out of the first TLS handshake, and what that handshake sent stays for as long as this process runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate Certification Authorities store.` 足りない証明書が複数あれば、メッセージはそれぞれを `and` でつないで示し、最後は `add these certificates` になる。サーバーを起動し直す。起動のたびにこうなるなら、最も考えられるのは、サーバーのユーザーがどちらのストアにも書き込めず、Windows が中間 CA を持てないためだ。管理者が、中間 CA の証明書を、コンピューターの中間証明機関の証明書ストア（ユーザーのストアではなく、ローカルコンピューターのストア）に入れる。Windows が同じ CA の別の証明書（サブジェクトと鍵が同じで、サーバーの現在の時刻に有効なもの。ストアにある、更新された証明書など）を送っていれば、その中間 CA は送られたものとして扱う。その証明書でアプリがチェーンを受け入れるかは、アプリの側の確認で決まる。
- その接続が失敗するか、10 秒以内に証明書が届かなければ、サーバーはやはり終了コード 1 で止まる: `error: The check that Windows sends the intermediate CA certificates of the TLS certificate '<file>' did not get an answer: the TLS handshake with <address> <what happened>.` `<what happened>` は、`failed`、`failed (socket error <name>)`、`did not finish within 10 seconds` のどれかだ。サーバーを起動し直す。起動のたびにこうなるなら、そのマシンで、サーバー自身のアドレスへの TLS 接続を妨げるものを探す。考えられる原因は、Windows の設定でクライアント側の TLS のバージョンが無効になっていて、サーバー自身の接続がサーバーの受け付けるバージョンを出せないことと、ループバックアドレスの TLS 接続に割り込むプログラム（一部のセキュリティソフトなど）だ。
- サーバーが待ち受けを始めてから、この確かめが終わるまでの間（ふつうは数十ミリ秒、長くても 10 秒）に接続したアプリは、中間 CA のないサーバーの証明書を受け取ることがある。サーバーはこの間のリクエストに答えず、まだ待ち受けていないときと同じように接続を切る。そのため、そのあと止まったサーバーは、そのリクエストについて何もしていない。サーバーがこの確かめをするのは、Windows で起動するときだけだ。

## `borrow`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `borrow.maxDuration` | `ELM_BORROW_MAXDURATION` | 期間 | ライセンスにある上限 | このサーバーが許す借用の最長期間。`d.hh:mm:ss`（日数、続けて時、分、秒）の形で書き、`7.00:00:00` は 7 日だ。1 秒から 100 年まで |
| `borrow.reserve` | `ELM_BORROW_RESERVE` | 整数 | `0` | 機能ごとに、借用に回さずに残しておくシートの数。0 から 1000000 |

## `log`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `log.usage` | `ELM_LOG_USAGE` | ファイルのパス | `usage.jsonl` | 利用ログ。ファイルの名前は、このパスのフォルダーと拡張子を除いたファイル名から決まる。サーバーの起動のたびと、UTC で日付が変わるたびに、新しいファイルを始める（既定では `usage-<UTC date and time>.jsonl`）。相対パスは `dataDirectory` の下とみなす。ファイルを指していなければならない |

## `admin`

`elm-server` に `admin` の設定はない。ローカルの管理 API は、ソケットのファイルで待ち受ける（[ポート](#ポート)を参照）。設定ファイルの `admin.listen` に `null` 以外の値があるか、`ELM_ADMIN_LISTEN` が空でないと、サーバーは起動せず、`error: admin.listen is no longer a setting: the administration API listens on the socket file admin.sock in the data folder. Remove admin.listen (ELM_ADMIN_LISTEN).` と終了コード 2 で止まる。`admin` の下のほかのキーは、不明なキーとして扱う。

## `service`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `service.url` | `ELM_SERVICE_URL` | URL | なし。サーバーライセンスのトークンの更新を自動では行わない | サーバーライセンスのトークンの更新をする `elm-service`。`https` の URL、またはループバックアドレスの `http` の URL。設定したときは、サーバーは起動時に `license` のファイルに書き込めるかを確かめる |

## 例

```json
{
  "product": "acme-cad",
  "listen": "https://0.0.0.0:7070",
  "publicUrls": ["https://licenses.example.com:7070/"],
  "license": "/etc/elm-server/server.elmlic",
  "trust": {
    "rootKeys": ["/etc/elm-server/acme-root.pem"]
  },
  "allow": ["10.0.0.0/8"],
  "rateLimit": {
    "perIp": 50
  },
  "borrow": {
    "maxDuration": "7.00:00:00",
    "reserve": 2
  }
}
```
