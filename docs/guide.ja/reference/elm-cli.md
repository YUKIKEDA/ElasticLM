---
sidebar_position: 1
---

# elm コマンドリファレンス

このページは、ベンダーと管理者が使うコマンドラインツール `elm` の、すべてのコマンドとオプションをまとめたものだ。`elm` は、鍵を作って証明し、ライセンスファイルを発行・検証し、同じマシン上のオンプレミスのライセンスサーバーを管理し、`elm-service` の管理 API を呼び出し、オフラインのリクエストファイルをサービスに渡し、`elm-service` のシークレットのファイル（ノンスのシークレットと、管理画面のサインインの鍵暗号化鍵）を作り、署名付きのログを検証する。使うコマンドがすでにわかっているときに調べるためのページだ。鍵、証明書、ライセンスファイル、サーバーとは何かについては、先に[概要](../explanation/overview.md)と[トラストチェーン](../explanation/trust-chain.md)を読むこと。

## 共通の動作

### 書式

```text
elm <group> <command> [arguments] [options]
```

グループは `key`、`license`、`server`、`admin`、`offline`、`secret`、`log` だ。オプションは、コマンドのあとにどの順で書いてもよい。複数の値をとるオプションは繰り返して書く（`--feature A --feature B`）。`elm key certify --use` は、カンマ区切りのリストも受け付ける。

どのコマンドも `--help`（`-h` または `-?` でもよい）を受け付け、使い方を表示する。`elm --version` は `elm` のバージョンを表示する。

日付や時刻（`--expires`、`--not-before`、`--feature` の `exp=`）は ISO 8601 で書く。時刻のない日付は UTC の 0 時、オフセットのない時刻は UTC とみなす。

製品 ID（`--product`）は、英小文字、数字、`-` からなる 1 から 64 文字で、たとえば `example-editor` のように書く。`elm-service` での製品の `code` であり、`elm-server` の設定 `product` でもある。`ElasticLM.Client`、`elm-server`、`elm-service` はこれ以外の形を受け付けず、`elm server` のコマンドはこの ID でサーバーのフォルダーを探す。`elm license issue` は形を確かめないが、製品 ID がこれ以外の形のライセンスは、この 3 つでは使えない。

エラーは、`error:` で始まる 1 行として標準エラー出力に書く。警告は `warning:` で始まる。不明なコマンドやオプションのとき、必須のオプションがないとき、ファイルかフォルダーのパスを受け取るオプションか引数が空か空白だけのときは、続けて `Run 'elm --help' for usage.` を書き、終了コード 2 で終わる。

### `--json`

結果を、テキストではなく JSON で標準出力に書く。このオプションを受け付けるのは `elm key root create`、`elm key signing create`、`elm key certify`、`elm license issue`、`elm license inspect`、`elm license verify`、`elm server status`、`elm server leases`、`elm server reload`、`elm server seal`、`elm server release`、`elm offline process`、`elm secret create`、`elm log verify` だ。`elm server` のコマンドは、管理 API のレスポンスを受け取ったまま書く。

### `--force`

出力先のファイルがすでにあれば上書きする。このオプションがないと、ファイルを上書きすることになるコマンドは終了コード 2 で止まり、何も書かない。このオプションを受け付けるのは `elm key root create`、`elm key signing create`、`elm key certify`、`elm license issue`、`elm server activate-request`、`elm server refresh-request`、`elm server deactivate-proof`、`elm offline process`、`elm secret create` だ。

### パスフレーズ

`elm` が書く秘密鍵は、どれもパスフレーズで暗号化した PKCS#8 の PEM ファイルだ。Linux と macOS では、秘密鍵のファイルをモード `0600` で書く。Windows では、ファイルは置いたフォルダーのアクセス権を引き継ぐ。`--force` のときは、前のファイルに書き込まずに、新しいファイルで置き換える。そのため、前のファイルのモードやアクセス権は残らず、前のファイルを開いていたプログラムにも新しい中身は見えない。

秘密鍵を読み書きするコマンド（`elm key root create`、`elm key signing create`、`elm key certify`、`elm license issue`）は、パスフレーズを環境変数から読む。

- `--passphrase-env <name>` で変数の名前を指定する。既定は `ELM_KEY_PASSPHRASE` だ。
- 変数が設定されていないか空で、入力が対話的なときは、`elm` がターミナルでパスフレーズを尋ねる。入力した文字は表示しない。新しい鍵では 2 回尋ね、2 つが一致しなければならない。
- 変数が設定されていないか空で、入力がリダイレクトされているときは、コマンドは終了コード 2 で止まる。
- 新しい鍵のパスフレーズは 12 文字以上でなければならない。

### 信頼のオプション

署名を検証するコマンド（`elm license issue`、`elm license verify`、`elm log verify`、`elm server` のサーバーライセンスのコマンド）には、ベンダーのルート公開鍵、つまり[トラストストア](../explanation/trust-chain.md)が必要だ。

- `--trust <file>` で、ルート公開鍵の PEM ファイルを指定する。1 つのファイルに複数の鍵を入れてもよく、オプションを繰り返してもよい。
- `--trust` がないときは、環境変数 `ELM_TRUST_ROOT_KEYS` から鍵を読む。この変数には 1 つ以上の PEM 形式の公開鍵を入れる。`--trust` を指定したときは、この変数は読まない。
- どちらもないときは、コマンドは終了コード 2 で止まる。
- `--revoked <key ID>` で、失効した鍵 ID（`kid`）を指定する。繰り返して書いてもよい。

### 環境変数

| 変数 | 使うコマンド | 意味 |
| --- | --- | --- |
| `ELM_KEY_PASSPHRASE` | `elm key`、`elm license issue` | 秘密鍵のパスフレーズ。`--passphrase-env` で別の変数を指定したときは使わない |
| `ELM_TRUST_ROOT_KEYS` | `--trust` をとるコマンド | PEM 形式のルート公開鍵 1 つ以上。`--trust` を指定しないときに使う |
| `ELM_API_KEY` | `elm admin request`、`elm offline process` | `elm-service` の API キー。設定されていれば、保存したログインの代わりに使う |
| `ELM_SERVICE_URL` | `elm admin request` | `--url` を指定しないときの `elm-service` の URL |
| `ELM_ADMIN_LOGIN` | `elm admin`、`elm offline process` | `elm admin login` のログインを保存するファイル。既定の場所の代わりに使う |

`elm-server` も、設定 [`service.url`](server-configuration.md#service) として `ELM_SERVICE_URL` を読み、この変数は設定ファイルの値より優先される。`elm` と `elm-server` が同じサービスアカウントや同じコンテナのような同じ環境で動くときは、`elm admin request` のためにこの変数を設定すると、`elm-server` がサーバーライセンスのトークンの更新に使うサービスも変わる。

ログインを保存する既定のファイルは、Windows では `%LOCALAPPDATA%\ElasticLM\elm\login`、Linux では `$XDG_DATA_HOME/elasticlm/elm/login`（`XDG_DATA_HOME` が設定されていなければ `~/.local/share/elasticlm/elm/login`）、macOS では `~/Library/Application Support/ElasticLM/elm/login` だ。

### 終了コード

| コード | 名前 | 意味 |
| --- | --- | --- |
| `0` | 成功 | コマンドが求められたことをした。`elm log verify` は、ログが壊れていなければ、警告があっても 0 を返す |
| `1` | 検証の失敗 | ライセンスファイルやログの検証に失敗した。または、サーバー、`elm-service`、ID プロバイダーがリクエストを拒否したか、エラーを返した |
| `2` | 不正な入力 | 引数、オプション、値が間違っているか足りない。ファイルがない、読めない、大きすぎる、または `--force` なしで出力先がすでにある。パスフレーズ、信頼する鍵、資格情報のどれかが見つからない |
| `3` | ネットワークのエラー | サーバー、`elm-service`、ID プロバイダーに接続できない、リクエストがタイムアウトした、または（`elm server` と `elm offline process` では）レスポンスを読めない |

`elm` が[エラーコード](error-codes.md)を表示したときは、終了コードはそのエラーコードで決まる。`ELM_NETWORK_ERROR` なら `3` で、それ以外のコードは、`elm` が自分で行った検証によるものでも、サーバーや `elm-service` のレスポンスによるものでも `1` になる。終了コード `2` は、何かを検証したり送ったりする前に `elm` が自分の入力を確かめた結果で、メッセージにはコードではなく、問題のある入力の名前が出る。唯一の例外は `elm license issue` だ。このコマンドはライセンスを書き出す前に検証し、その検証で出たコードは `2` になる。そのライセンスは入力から作ったものだからだ。

## elm key

### elm key root create

```text
elm key root create --out <file> [options]
```

ルート鍵を作る。暗号化した秘密鍵のファイルと、その公開鍵のファイルを書き、鍵 ID を表示する。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--out` | ファイルのパス | — | はい | 書き出す、暗号化した秘密鍵のファイル |
| `--public-out` | ファイルのパス | `<out>.pub.pem` | いいえ | 書き出す公開鍵のファイル |
| `--passphrase-env` | 変数名 | `ELM_KEY_PASSPHRASE` | いいえ | パスフレーズを入れた環境変数 |
| `--force` | — | オフ | いいえ | 既存のファイルを上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

### elm key signing create

```text
elm key signing create --out <file> [options]
```

署名鍵を作る。暗号化した秘密鍵のファイルと、その公開鍵のファイルを書き、鍵 ID を表示する。`elm key certify` で証明するまで、この鍵で署名したものを検証側は受け入れない。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--out` | ファイルのパス | — | はい | 書き出す、暗号化した秘密鍵のファイル |
| `--public-out` | ファイルのパス | `<out>.pub.pem` | いいえ | 書き出す公開鍵のファイル |
| `--passphrase-env` | 変数名 | `ELM_KEY_PASSPHRASE` | いいえ | パスフレーズを入れた環境変数 |
| `--force` | — | オフ | いいえ | 既存のファイルを上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

### elm key certify

```text
elm key certify --root <file> --public <file> --out <file> [options]
```

ルート鍵で署名鍵を証明する鍵証明書を書く。証明は、指定した用途と限られた期間に対してだけ有効だ。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--root` | ファイルのパス | — | はい | 暗号化したルート鍵のファイル |
| `--public` | ファイルのパス | — | はい | 証明する署名鍵の公開鍵のファイル |
| `--use` | `license`、`lease`、`log` | `license` | いいえ | 鍵で署名してよいもの。複数を指定するときは、繰り返すかカンマで区切る |
| `--not-before` | ISO 8601 の日付または時刻 | 現在時刻 | いいえ | 有効期間の始まり |
| `--days` | 整数（1 から 731） | `365` | いいえ | 有効期間の日数 |
| `--out` | ファイルのパス | — | はい | 書き出す証明書のファイル |
| `--passphrase-env` | 変数名 | `ELM_KEY_PASSPHRASE` | いいえ | ルート鍵のパスフレーズを入れた環境変数 |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

### elm key thumbprint

```text
elm key thumbprint <public-key-file>
```

ルート鍵や署名鍵などの公開鍵の、鍵 ID（`kid`、JWK のサムプリント）を表示する。`--revoked` に渡す鍵 ID を調べるときや、鍵のファイルを、`elm license inspect` が証明書やライセンスに表示する `kid` と照らし合わせるときに使う。インストール鍵には公開鍵のファイルがないので、`--bind-key` に渡すサムプリントはこのコマンドでは得られない。[紐付けに使う値の入手先](#紐付けに使う値の入手先)を参照。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<public-key-file>` | ファイルのパス | — | はい | 引数。公開鍵の PEM ファイル |

## elm license

### elm license issue

```text
elm license issue --signing-key <file> --certificate <file> --product <id> --license-id <id> --expires <time> --out <file> [options]
```

証明済みの署名鍵で署名したライセンスファイルを発行する。ファイルは書き出す前にトラストストアで検証し、検証に失敗すれば書き出さない。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--signing-key` | ファイルのパス | — | はい | 暗号化した署名鍵のファイル |
| `--certificate` | ファイルのパス | — | はい | 署名鍵の証明書のファイル。複数を指定するときは繰り返す（4 つまで） |
| `--product` | 製品 ID | — | はい | 製品 ID（[書式](#書式)を参照） |
| `--license-id` | ライセンス ID | — | はい | ライセンス ID |
| `--kind` | `standard`、`trial`、`server` | `standard` | いいえ | ライセンスの種類 |
| `--feature` | `ID[;ver=RANGE][;seats=N][;exp=DATE]` | — | いいえ | 機能。バージョンの範囲、シート数、有効期限を任意で付けられる（[機能の値](#機能の値)を参照）。複数を指定するときは繰り返す |
| `--expires` | ISO 8601 の日付または時刻、または `never` | — | はい | トークンの有効期限。`never` を指定すると、警告 `The license has no expiry. It cannot be revoked once delivered.` を書く。`--kind server` のときは、2 つ目の警告 `A server license issued by hand is never refreshed, so a cloned server cannot be detected. Without an expiry, a clone works for as long as the original.` も書く |
| `--grace-days` | 数値（小数も可） | `7`（`trial` では `0`） | いいえ | 猶予期間の日数 |
| `--refresh-days` | 数値（小数も可） | — | いいえ | 有効期限の何日前から、クライアントがオンラインでトークンの更新を始めるか |
| `--bind-key` | サムプリント | — | いいえ | ライセンスを紐付けるインストール鍵の[サムプリント](../explanation/devices-and-proofs.md#インストール鍵)（[紐付けに使う値の入手先](#紐付けに使う値の入手先)を参照） |
| `--fingerprint` | フィンガープリント | — | いいえ | ライセンスを紐付けるマシンフィンガープリント（[紐付けに使う値の入手先](#紐付けに使う値の入手先)を参照）。複数を指定するときは繰り返す |
| `--fingerprint-matching` | `any`、`all`、`ignore` | `any` | いいえ | マシンフィンガープリントをどう照合するか |
| `--lease-minutes` | 数値（小数も可） | `10` | いいえ | サーバーライセンスのみ。リースの期間（分） |
| `--borrow-days` | 数値（小数も可） | `0` | いいえ | サーバーライセンスのみ。借用の上限（日）。`0` なら借用を許さない |
| `--overage` | 整数（0 から 1000） | `0` | いいえ | サーバーライセンスのみ。超過を許すパーセント |
| `--clone` | `deny`、`flag` | `deny` | いいえ | サーバーライセンスのみ。コピーされたアプリのインストールが、コピー元と同じリースを更新したときに `elm-server` がすること。`deny` なら両方のコピーのリースを止め、`flag` ならクローンを利用ログに記録して通常どおりリースを更新する（[再試行とクローン](../explanation/floating-licenses.md#再試行とクローン)） |
| `--borrow-returns` | 整数（0 から 1000） | `2` | いいえ | サーバーライセンスのみ。1 つのインストールが 30 日間に借用を早期返却できる回数 |
| `--meta` | `key=value` | — | いいえ | ベンダーのメタデータ。複数を指定するときは繰り返す |
| `--out` | ファイルのパス | — | はい | 書き出すライセンスファイル |
| `--trust` | ファイルのパス | `ELM_TRUST_ROOT_KEYS` | いいえ | 書き出す前にライセンスを検証するルート公開鍵のファイル。複数を指定するときは繰り返す |
| `--revoked` | 鍵 ID | — | いいえ | 失効した鍵 ID。複数を指定するときは繰り返す |
| `--passphrase-env` | 変数名 | `ELM_KEY_PASSPHRASE` | いいえ | 署名鍵のパスフレーズを入れた環境変数 |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

`--grace-days`、`--refresh-days`、`--lease-minutes`、`--borrow-days` には、`0.5` のような小数も指定できる。ライセンスにはそれぞれを秒単位で、端数を切り捨てて記録する。`--lease-minutes` は 1 秒以上にならなければならない。`--overage` と `--borrow-returns` は整数だけを受け付ける。

`--lease-minutes`、`--borrow-days`、`--overage`、`--clone`、`--borrow-returns` を、`server` 以外の `--kind` と一緒に指定すると、コマンドは終了コード 2 で止まる。`server` のライセンスには、`--bind-key` と、すべての機能の `seats=` も必要だ。`standard` と `trial` のライセンスには `seats=` を付けられず、`trial` のライセンスの猶予期間には `--grace-days 0` しか指定できない。これらの規則のどれかに反するライセンスは書き出さず、コマンドは終了コード 2 で止まる。

#### 機能の値

値は機能 ID で始め、そのあとに下の部分を、それぞれ `;` に続けて書く。バージョンの範囲にはスペースと `<` や `>` が入るので、シェルでは値全体を引用符で囲む。

| 部分 | 形 | 意味 |
| --- | --- | --- |
| `ID` | テキスト | アプリが求める機能 ID |
| `ver=RANGE` | スペースで区切った 1 から 4 個の比較で、すべてを満たさなければならない。比較は、`>=`、`<=`、`>`、`<`、`=` のどれかに、数字だけからなる `major.minor` または `major.minor.patch` のバージョンを続けたもの。patch がなければ 0 とみなす | ライセンスが対象とする機能のバージョン。たとえば `>=1.0 <2.0`。指定しなければ、すべてのバージョン |
| `seats=N` | 1 以上の整数 | その機能の同時使用シート数。`server` のライセンスでは必須で、それ以外では指定できない |
| `exp=DATE` | ISO 8601 の日付または時刻（`--expires` と同じ） | その機能自体の有効期限。指定しなければ、機能はライセンスと同じだけ有効だ |

たとえば、`server` のライセンスで `--feature "pro;ver=>=1.0 <2.0;seats=5;exp=2027-06-30"` と指定すると、`pro` を、1.0 以上 2.0 未満のバージョンに、5 シートで、2027-06-30 00:00 UTC まで許可する。

#### 紐付けに使う値の入手先

`--bind-key` には、インストール鍵（1 台のデバイスやサーバーを識別する鍵ペア）のサムプリントを指定する。`--fingerprint` には、16 進数の小文字 32 文字のマシンフィンガープリントを指定する。どちらも、ライセンスを使うマシンから得る。

- **`elm-server` の場合。** サーバーライセンスがないまま起動すると、`elm-server` は止まり、インストール鍵のサムプリントとフィンガープリントを含むメッセージを表示する。顧客の管理者が、それをベンダーに送る。
- **アプリのデバイスの場合。** `elm` には、これらを表示するコマンドがない。アプリがユーザーに表示しなければならない。これらを得る呼び出しは、[概要](../explanation/overview.md#オフラインのみ)に書いてある。

`elm key thumbprint` はここでは使えない。このコマンドは公開鍵のファイルを読むが、インストール鍵には公開鍵のファイルがないからだ。

### elm license inspect

```text
elm license inspect <license-file> [--json]
```

ライセンスファイルとその証明書のヘッダーとペイロードを表示する。署名は検証しない。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<license-file>` | ファイルのパス | — | はい | 引数。ライセンスファイル |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

### elm license verify

```text
elm license verify <license-file> --product <id> [options]
```

ライセンスファイルを、トラストストアと指定した条件で検証する。ライセンスが有効でなければ終了コード 1 を返す。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<license-file>` | ファイルのパス | — | はい | 引数。ライセンスファイル |
| `--trust` | ファイルのパス | `ELM_TRUST_ROOT_KEYS` | いいえ | 信頼するルート公開鍵のファイル。複数を指定するときは繰り返す |
| `--revoked` | 鍵 ID | — | いいえ | 失効した鍵 ID。複数を指定するときは繰り返す |
| `--product` | 製品 ID | — | はい | ライセンスの対象でなければならない製品 ID |
| `--kind` | `standard`、`trial`、`server` | `standard` | いいえ | ライセンスがとるべき種類 |
| `--feature` | 機能 ID | — | いいえ | ライセンスが許可していなければならない機能 |
| `--version` | `major.minor[.patch]` | — | いいえ | 確かめる機能のバージョン |
| `--key-thumbprint` | サムプリント | — | いいえ | 紐付けたライセンスのための、インストール鍵のサムプリント |
| `--fingerprint` | フィンガープリント | — | いいえ | 現在のマシンフィンガープリント。複数を指定するときは繰り返す。`--key-thumbprint` が必要 |
| `--this-machine` | — | オフ | いいえ | このマシンの、その製品用のフィンガープリントを使う。`--key-thumbprint` が必要 |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

## elm server

`elm server` のコマンドは、同じマシン上のオンプレミスのライセンスサーバーを管理する。サーバーのデータフォルダーから管理者トークンを読むので、サーバーと同じユーザーで実行する。`--data` がなければ、データフォルダーは `--product` のマシン全体用のフォルダーになる。Windows では `%ProgramData%\ElasticLM\<product>`、Linux では `/var/lib/elasticlm/<product>`、macOS では `/Library/Application Support/ElasticLM/<product>` だ。各リクエストは 30 秒でタイムアウトする。

### サーバー管理のオプション

`elm server status`、`elm server leases`、`elm server reload`、`elm server seal`、`elm server release` は、実行中のサーバーの管理 API を呼び出す。これらのコマンドは次のオプションをとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--url` | `http` の URL | `http://127.0.0.1:7071` | いいえ | サーバーの管理 API（サーバーの設定 `admin.listen`） |
| `--product` | 製品 ID | — | `--data` か `--product` | サーバーの製品 ID。既定の場所からデータフォルダーを探すのに使う |
| `--data` | フォルダーのパス | — | `--data` か `--product` | サーバーのデータフォルダー（サーバーの設定 `dataDirectory`） |
| `--json` | — | オフ | いいえ | レスポンスを JSON で書く |

### elm server status

```text
elm server status (--data <folder> | --product <id>) [options]
```

サーバーライセンス、再起動後にリースの再登録をしているかどうか、機能ごとの使用中のシート数を表示する。とるのは[サーバー管理のオプション](#サーバー管理のオプション)だけだ。

### elm server leases

```text
elm server leases (--data <folder> | --product <id>) [options]
```

サーバーが保持しているリースと借用の一覧を表示する。とるのは[サーバー管理のオプション](#サーバー管理のオプション)だけだ。

### elm server reload

```text
elm server reload (--data <folder> | --product <id>) [options]
```

実行中のサーバーに、ライセンスファイルを読み直させる。とるのは[サーバー管理のオプション](#サーバー管理のオプション)だけだ。

### elm server seal

```text
elm server seal (--data <folder> | --product <id>) [options]
```

サーバーに、利用ログのまだ署名していない行へすぐ署名させる。とるのは[サーバー管理のオプション](#サーバー管理のオプション)だけだ。

### elm server release

```text
elm server release <lease-id> (--data <folder> | --product <id>) [options]
```

リースまたは借用を、サーバー側で解放する。引数のほかに、[サーバー管理のオプション](#サーバー管理のオプション)をとる。

| 引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<lease-id>` | リース ID | — | はい | `elm server leases` が表示するリース ID |

### サーバーライセンスのコマンド

以下のコマンドは、サーバーのデータフォルダーに保存したアクティベーションを使ってサーバーライセンスを管理する。サーバーを止めていても動く。サーバーのインストール鍵が必要で、この鍵は `elm-server` が初めて起動したときに作る。どのコマンドも下のオプションをとる。各コマンドの節には、そのコマンドだけのオプションと引数を載せる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--product` | 製品 ID | — | はい | サーバーの製品 ID（サーバーの設定 `product`） |
| `--data` | フォルダーのパス | `--product` のマシン全体用のデータフォルダー | いいえ | サーバーのデータフォルダー（サーバーの設定 `dataDirectory`） |
| `--trust` | ファイルのパス | `ELM_TRUST_ROOT_KEYS` | いいえ | 信頼するルート公開鍵のファイル。複数を指定するときは繰り返す |
| `--revoked` | 鍵 ID | — | いいえ | 失効した鍵 ID。複数を指定するときは繰り返す |
| `--url` | ループバックアドレスの `http` の URL | `http://127.0.0.1:7071` | いいえ | サーバーの管理 API |

新しいライセンスファイルを書いたとき、サーバーが実行中なら、コマンドはサーバーの[ローカルの管理 API](administration-api.md#elm-server-のローカルの管理-api) にファイルの読み直しを求める。サーバーが止まっていれば、サーバーは起動するときにファイルを読む。下のどの場合も、ファイルは書かれていて、エラーは標準エラー出力の 1 行で、終了コードは 1 だ。

| 読み直しのリクエストへのレスポンス | コマンドが出す行 |
| --- | --- |
| サーバーが新しいファイルを読んで、それを拒否した（`409`。コードは [`POST /admin/v1/license/reload`](administration-api.md#post-adminv1licensereload) のもの） | `error: the server kept its license: <code>: <reason>` |
| コードのあるほかのエラー。リクエストそのものが拒否され、サーバーはファイルを読んでおらず、それまでのライセンスを使い続けている。`ELM_UNAUTHORIZED` は、`--data` か `--product` が、`--url` のサーバーとは別のサーバーのフォルダーを指しているということだ | `error: <code>: <reason> The license file is written; the server did not confirm a reload.` |
| 読み直しのレスポンスではなく、エラーコードもないレスポンス。そのポートで待ち受ける別のプログラムが返すようなレスポンスだ | `error: The server's administration API answered <status>. The license file is written; the server did not confirm a reload.` |

`--license` が、フォルダーを指定したほうのサーバーの設定 `license` であれば、そのサーバーは、次に起動するとき、またはそのサーバー自身の `--url` を付けた `elm server reload` のときに、書かれたファイルを読む。

`deactivate`、`deactivate-proof`、`activate-request`、`refresh-request` は、最初に同じ API にサーバーの状態を問い合わせる。フォルダーに読める管理者トークンがないとき、または `--url` から 30 秒以内に HTTP のレスポンスが返らないとき（何も待ち受けていない、または接続が失敗する）は、サーバーは実行中でないとみなす。読み直しのリクエストでも同じだ。状態ではないレスポンスが返ると、これらのコマンドは `error: <code>: <reason>` で止まる。コードのないレスポンスなら `error: The server's administration API answered <status>.` で止まる。どちらの場合も何も書かず、終了コードは 1 だ。止めたサーバーのフォルダーには、管理者トークンが残る。別のサーバーが既定の `--url` で待ち受けているあいだは、これらのコマンドには、何も応答しない `--url` を指定する必要がある。止めたサーバー自身の `admin.listen` がその例だ。

成功のレスポンスの本文が JSON でないときは、読み直しのリクエストも状態の問い合わせも、`error: The server's administration API answered with a body that is not JSON.` と終了コード 3 で止まる。

### elm server activate

```text
elm server activate <license-key> --product <id> --service <url> --license <file> [options]
```

`elm-service` を使ってサーバーのライセンスをオンラインでアクティベーションし、ライセンスファイルを書く。ライセンスキーは、このコマンドでは引数で渡し、`elm server activate-request` では `--key` で渡す。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<license-key>` | ライセンスキー | — | はい | 引数。サーバーライセンスのライセンスキー |
| `--service` | `https` の URL、またはループバックアドレスの `http` の URL | — | はい | `elm-service` の URL |
| `--license` | ファイルのパス | — | はい | サーバーのライセンスファイル（サーバーの設定 `license`） |

### elm server refresh

```text
elm server refresh --product <id> --service <url> --license <file> [options]
```

`elm-service` を使って、サーバーのライセンスのトークンの更新をオンラインで行い、ライセンスファイルを書く。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--service` | `https` の URL、またはループバックアドレスの `http` の URL | — | はい | `elm-service` の URL |
| `--license` | ファイルのパス | — | はい | サーバーのライセンスファイル（サーバーの設定 `license`） |

### elm server deactivate

```text
elm server deactivate --product <id> --service <url> [options]
```

サーバーのライセンスのアクティベーション解除をオンラインで行い、ライセンスファイルを削除する。サーバーが実行中なら、終了コード 1 で止まる。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--service` | `https` の URL、またはループバックアドレスの `http` の URL | — | はい | `elm-service` の URL |
| `--license` | ファイルのパス | — | いいえ | サーバーのライセンスファイル。このアクティベーションのファイルであるときだけ削除する |

### elm server activate-request

```text
elm server activate-request --key <license-key> --product <id> --out <file> [options]
```

`elm offline process` に渡す、オフラインのアクティベーションのリクエストファイル（`.elmreq`）を書く。実行中のサーバーがオンラインでライセンスのトークンの更新をしているあいだは、終了コード 1 で止まる。ライセンスキーは、このコマンドでは `--key` で渡し、`elm server activate` では引数で渡す。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--key` | ライセンスキー | — | はい | サーバーライセンスのライセンスキー |
| `--out` | ファイルのパス | — | はい | リクエストファイルの書き出し先 |
| `--renew` | — | オフ | いいえ | 待機中のリクエストの期限が切れる前に、新しいリクエストを作る。それまでのリクエストファイルは置き換わるので、捨てなければならない |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |

### elm server refresh-request

```text
elm server refresh-request --product <id> --out <file> [options]
```

`elm offline process` に渡す、オフラインのトークンの更新のリクエストファイル（`.elmreq`）を書く。実行中のサーバーがオンラインでライセンスのトークンの更新をしているあいだは、終了コード 1 で止まる。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--out` | ファイルのパス | — | はい | リクエストファイルの書き出し先 |
| `--renew` | — | オフ | いいえ | 待機中のリクエストの期限が切れる前に、新しいリクエストを作る。それまでのリクエストファイルは置き換わるので、捨てなければならない |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |

### elm server import

```text
elm server import <answer> --product <id> --license <file> [options]
```

待機中のリクエストに対するサービスのレスポンスファイル（`.elmresp`）を取り込み、ライセンスファイルを書く。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<answer>` | ファイルのパス | — | はい | 引数。サービスのレスポンスファイル（`.elmresp`） |
| `--license` | ファイルのパス | — | はい | サーバーのライセンスファイル（サーバーの設定 `license`） |

### elm server deactivate-proof

```text
elm server deactivate-proof --out <file> --product <id> [options]
```

オフラインのアクティベーション解除のプルーフ（`.elmdeact`）を書き、アクティベーションとそのライセンスファイルを削除する。サーバーが実行中なら、終了コード 1 で止まる。[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)もとる。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--out` | ファイルのパス | — | はい | アクティベーション解除のプルーフ（`.elmdeact`）の書き出し先 |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |
| `--license` | ファイルのパス | — | いいえ | サーバーのライセンスファイル。このアクティベーションのファイルであるときだけ削除する |

### elm server forget-proof

```text
elm server forget-proof --product <id> [options]
```

保存しているアクティベーション解除のプルーフを、サービスが受け取ったあとで破棄し、サーバーがもう一度アクティベーションできるようにする。とるのは[サーバーライセンスのコマンドのオプション](#サーバーライセンスのコマンド)だけだ。

## elm admin

`elm admin` のコマンドは、`elm-service` にサインインし、その管理 API を呼び出す。サービスの URL は `https`、またはループバックアドレスの `http` だ。保存したログインは、そのログインを作った URL と同じオリジン（スキーム、ホスト、ポート）へのリクエストにだけ使う。アクセストークンは、期限切れまで 60 秒を切ると更新する。トークンと鍵を標準出力や標準エラー出力に書くことはない。

### elm admin login

```text
elm admin login --url <url>
```

`elm-service` の ID プロバイダーで（OIDC のデバイス認可フローを使って）サインインし、ログインを保存する。それまでのログインは置き換える。サインイン用のアドレスとコードは標準エラー出力に書く。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--url` | `https` の URL、またはループバックアドレスの `http` の URL | — | はい | `elm-service` の URL |

### elm admin logout

```text
elm admin logout
```

保存したログインを削除する。ID プロバイダーが対応していれば、その前にリフレッシュトークンを失効させる。アクセストークンは、期限が切れるまで有効なままだ。

このコマンドにはオプションがない。

### elm admin request

```text
elm admin request <METHOD> <path> [--body <file>] [--url <url>]
```

管理 API を 1 回呼び出し、成功したレスポンスの本文を標準出力に書く。本文が改行で終わらなければ、最後に改行を書く（空の本文では何も書かない）。それ以外のレスポンスの本文は標準エラー出力に書き、終了コード 1 で終わる。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<METHOD>` | `GET`、`POST`、`PUT`、`PATCH`、`DELETE` | — | はい | 引数。HTTP メソッド |
| `<path>` | `/v1/admin/` で始まるパス | — | はい | 引数。パス。クエリを含めてもよい。2,048 文字以内で、フラグメント、バックスラッシュ、`.` や `..` のセグメント、`%2e`、`%2f`、`%5c` を含まないこと |
| `--body` | ファイルのパス | — | いいえ | `application/json` として送る JSON ファイル。64 KiB まで |
| `--url` | `https` の URL、またはループバックアドレスの `http` の URL | `ELM_SERVICE_URL`、次に保存したログインの URL | いいえ | `elm-service` の URL |

`ELM_API_KEY` が設定されていればそれを使い、なければ保存したログインを使う。どちらもなければ、終了コード 2 で止まる。リダイレクトはたどらず、リクエストは 100 秒でタイムアウトする。

## elm offline

### elm offline process

```text
elm offline process --url <url> --in <file> [--out <file>] [options]
```

オフラインのアクティベーションのリクエスト、トークンの更新のリクエスト、またはアクティベーション解除のプルーフを `elm-service` に送る。リクエストの場合は、デバイスが取り込むレスポンスファイルを書く。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--url` | `https` の URL、またはループバックアドレスの `http` の URL | — | はい | `elm-service` の URL |
| `--in` | ファイルのパス | — | はい | リクエストファイル（`.elmreq`）またはアクティベーション解除のプルーフ（`.elmdeact`） |
| `--out` | ファイルのパス | — | リクエストでは必須。アクティベーション解除のプルーフでは指定できない | アクティベーションまたはトークンの更新のリクエストに対するレスポンス（`.elmresp`）の書き出し先 |
| `--force` | — | オフ | いいえ | レスポンスファイルがすでにあれば上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

`ELM_API_KEY` があるか、`--url` と同じオリジンへの `elm admin login` を保存してあれば、ファイルはサービスの管理用の窓口に送る。そうでなければ公開の窓口に送る。リダイレクトはたどらず、リクエストは 30 秒でタイムアウトする。

## elm secret

### elm secret create

```text
elm secret create --out <file> [options]
```

`elm-service` のシークレットのファイルを書く。中身は 32 バイトの乱数を base64 で書いた 1 行だ。1 つのファイルは、ノンスのシークレットか、管理画面のサインインの鍵暗号化鍵のどちらかとして使う。ノンスのシークレットはサービスの設定 [`nonce.secretFile`](service-configuration.md#nonce) で、鍵暗号化鍵は [`dataProtection.kekFile`](service-configuration.md#dataprotection) で指定する。長さは、どちらの用途でも 32 バイトに固定している。ノンスのシークレットにサービスが求める最小の長さで、鍵暗号化鍵の長さでもあるからだ。用途ごとに別のファイルを作ること。`secret file: <file>` を表示する。`--json` を付けると、`file` にファイルのパスを入れたオブジェクトを書く。シークレットそのものは、標準出力にも標準エラー出力にも書かない。Linux と macOS では、ファイルをモード `0600` で書く。Windows では、ファイルは置いたフォルダーのアクセス権を引き継ぐ。`--force` のときは、秘密鍵と同じく、新しいファイルで前のファイルを置き換える。

| オプション | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `--out` | ファイルのパス | — | はい | 書き出すシークレットのファイル |
| `--force` | — | オフ | いいえ | ファイルがすでにあれば上書きする |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

## elm log

### elm log verify

```text
elm log verify <files>... [options]
```

ライセンスサーバーの利用ログや借用の記録、または `elm-service` の利用ログについて、署名とチェーンを検証する。エラーが見つかれば終了コード 1 を、警告だけなら 0 を返す。

| オプションまたは引数 | 値 | 既定 | 必須 | 意味 |
| --- | --- | --- | --- | --- |
| `<files>` | ファイルまたはフォルダーのパス | — | はい（1 つ以上） | 引数。ログのファイル、またはログのファイルを入れたフォルダー。ファイル名の順に、古いものから検証する |
| `--product` | 製品 ID | — | サーバーのログでは必須 | サーバーライセンスの製品 ID。`elm-service` のログでは使わない |
| `--node` | ノード名。`A`–`Z`、`a`–`z`、`0`–`9`、`-` からなる 1 から 64 文字 | — | `elm-service` の利用ログのフォルダーでは必須 | ファイル（`usage-<node>-*.jsonl`）を検証するノード。`--stream usage` で、フォルダーを指定したときだけ使える |
| `--stream` | `usage`、`borrows` | `usage` | いいえ | 検証するログ。利用ログか借用の記録か |
| `--trust` | ファイルのパス | `ELM_TRUST_ROOT_KEYS` | いいえ | 信頼するルート公開鍵のファイル。複数を指定するときは繰り返す |
| `--revoked` | 鍵 ID | — | いいえ | 失効した鍵 ID。複数を指定するときは繰り返す |
| `--json` | — | オフ | いいえ | 結果を JSON で書く |

## 例

ファイル名、ID、鍵は架空のものだ。

```text
elm key root create --out root.key.pem
elm key signing create --out signing.key.pem
elm key certify --root root.key.pem --public signing.key.pem.pub.pem --use license --days 365 --out signing.cert
elm license issue --signing-key signing.key.pem --certificate signing.cert --trust root.key.pem.pub.pem \
  --product example-editor --license-id LIC-0001 --feature "pro;ver=>=1.0 <2.0" --expires 2027-12-31 --out LIC-0001.elmlic
elm license verify LIC-0001.elmlic --trust root.key.pem.pub.pem --product example-editor --feature pro --version 1.4
elm server status --product example-editor
elm admin request GET /v1/admin/licenses --url https://licensing.example.com
elm offline process --url https://licensing.example.com --in server.elmreq --out server.elmresp
elm secret create --out nonce.secret
elm log verify ./usage --product example-editor --trust root.key.pem.pub.pem
```
