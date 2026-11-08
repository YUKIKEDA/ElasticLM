---
sidebar_position: 3
---

# elm-service の設定

このページは、`elm-service` のすべての設定をまとめたものだ。`elm-service` は SaaS 型のアクティベーションサービスで、デバイスのアクティベーションをし、ライセンストークンを発行し、管理 API と管理画面を提供する。サービスをデプロイするベンダーの運用担当者に向けている。アクティベーションの仕組みは[アクティベーション](../explanation/activation.md)を、サービスの鍵とシークレットが何を守るかは[セキュリティモデル](../explanation/security-model.md)を読むこと。

## コマンドラインと終了コード

```text
elm-service [setup] [--config <settings.json>]
```

| 引数 | 意味 |
| --- | --- |
| `setup` | サービスを起動する代わりに、データベースを準備して最初の API キーを作る。最初の引数でなければならない。データベースのテーブルを作るか更新し、`bootstrapOwner` の管理者に、`owner` の権限を持ち 90 日間有効な新しい API キーを作って、監査ログに記録する。標準出力にはキーだけを書くので、`elm-service setup > key.txt` で保存できる。キー ID、キーの先頭の数文字、所有者、有効期限は標準エラー出力に書く。実行するたびに別のキーを作る |
| `--config <path>` | JSON の設定ファイル。サービスにも `setup` にも使う。指定しなければ、設定は環境変数だけから読む。[設定の渡し方](#設定の渡し方)を参照 |

これ以外の引数、2 番目以降の位置にある `setup`、またはパスのない `--config` を指定すると、使い方（`Usage: elm-service [setup] [--config <file>]`）を表示し、2 で終わる。`--help` はない。

サービスの終了コード:

| 終了コード | 意味 |
| --- | --- |
| 0 | サービスが正常に止まった |
| 1 | サービスを起動できない。たとえば、ルート公開鍵が指定されていない、署名鍵のパスフレーズが指定されていない、署名鍵やその証明書を読めない、署名鍵と証明書が対応していない、TLS 証明書を読めない、TLS 証明書の拡張キー使用法（証明書を使ってよい用途の一覧）にサーバー認証（`1.3.6.1.5.5.7.3.1`。OpenSSL では `TLS Web Server Authentication`、Windows では `Server Authentication`、日本語の Windows では「サーバー認証」と表示される）が含まれていない、TLS 証明書のファイルで最初の証明書のあとにある証明書が CA の証明書ではない、データベースでエラーが起きた、利用ログを書き込めない、`listen` で待ち受けられない |
| 2 | 引数が有効でない。または設定のエラー（設定ファイルや値が有効でない、必須の値がない） |

`listen` で待ち受けられないとき（ほかのプログラムがそのポートを使っている、そこで待ち受ける権限がない、このマシンにそのアドレスがない）、最後の行は `elm-service cannot listen on <listen>: <reason>` になる。URL は、サービスが読んだとおりに、`/` で終わる形で示す。

`setup` の終了コード:

| 終了コード | 意味 |
| --- | --- |
| 0 | API キーを作った |
| 1 | データベースでエラーが起きた。使えない、スキーマがこのバージョンの `elm-service` より新しいか、別のバージョンによって違う形に変えられている、`bootstrapOwner` の管理者が存在するが無効になっているか別のロールを持っている、キーを記録できない、のいずれか |
| 2 | 引数が有効でない、サービスと同じ設定のエラーがある、`bootstrapOwner` が設定されていない、または `database` の設定を使えない |

終了コード 1 か 2 で終わる前に、`elm-service` は理由を標準エラー出力に表示する。

## 設定の渡し方

- 設定は、JSON の設定ファイル、環境変数、またはその両方で渡す。ファイルは、サービスにも `setup` にも `--config` で渡す。

  ```text
  elm-service --config /etc/elm-service/settings.json
  elm-service setup --config /etc/elm-service/settings.json
  ```

- `--config` がなければ、`elm-service` は環境変数だけを読む。
- 環境変数は、ファイルにある同じ設定より優先する。空の値を設定した環境変数は、設定されていないものとして扱う。ただ 1 つの例外は `ELM_SIGNING_PASSPHRASE` で、空の値は空のパスフレーズとして使う。
- ファイルは 64 KiB までだ。ファイルが知らないキー（たとえばつづりを間違えたキー）はエラーになる。キーの名前は大文字と小文字を区別する。
- ファイルの値は省略してよいが、空にしてはならない。空の文字列や、リストの中の空または `null` の要素は、既定値にはならずエラーになる。
- シークレットは設定ファイルに書かない。[シークレット](#シークレット)を参照。
- `elm-service setup` には、`signing.*`、`trust.*`、ノンスのシークレットは要らない。使うのは `database` の設定と `bootstrapOwner` だ。
- 設定にエラーがあると、`elm-service` は起動する前に止まり、理由を表示する。終了コードは[コマンドラインと終了コード](#コマンドラインと終了コード)にある。

### 環境変数の名前

環境変数の名前は、`ELM_` のあとに設定のパスを大文字にして続け、`.` を `_` に置き換えたものだ。たとえば、`rateLimit.clientPerIp` は `ELM_RATELIMIT_CLIENTPERIP`、`publicUrls` は `ELM_PUBLICURLS` になる。リストは 1 つの値として書き、要素を `;` で区切る。たとえば `ELM_SIGNING_CERTIFICATES=/etc/elm/signing-1.cert;/etc/elm/signing-2.cert` だ。

次の設定は、この規則に従わない。

| 設定 | 環境変数 | 変数の中身 |
| --- | --- | --- |
| `database.connectionFile` | `ELM_DATABASE_CONNECTION` | 接続文字列そのもの。ファイルのパスではない |
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE` | 署名鍵のパスフレーズそのもの。ファイルのパスではない。空の値は空のパスフレーズになる |
| `nonce.secretFile` | `ELM_NONCE_SECRET` | ノンスのシークレットそのもの（base64）。ファイルのパスではない |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET` | Web クライアントのシークレットそのもの。ファイルのパスではない |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK` | 鍵暗号化鍵そのもの（base64）。ファイルのパスではない |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` または `ELM_TRUST_ROOT_KEYS` | `ELM_TRUST_ROOTKEYS` は規則に従い、ファイルのパスを入れる。`ELM_TRUST_ROOT_KEYS` はルート公開鍵を PEM テキストで入れ、ファイルから鍵が 1 つも得られないときだけ読む。**2 つ目の `_` に注意:** 2 つの名前の違いはこれだけだ |
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES` | スコープを `;` ではなく空白で区切る |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP` | `value=role` の組を `;` で区切る。たとえば `elm-owners=owner;elm-support=support`。`;` を含むクレームの値は、代わりにファイルに書く |

## 最上位の設定

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `listen` | `ELM_LISTEN` | URL | `http://127.0.0.1:8080` | サービスが待ち受ける場所。パス、クエリ、フラグメントのない `http` または `https` の URL。ホストは IP アドレス、`localhost`、`0.0.0.0`、`[::]` のいずれか。`https` には `tls.certificate` と `tls.key` が必要 |
| `publicUrls` | `ELM_PUBLICURLS` | URL のリスト | `listen` の URL（`listen` がループバックアドレスか `localhost` のときだけ） | デバイスがサービスに接続するときの URL。デバイスのプルーフは、このうちの 1 つを指定していなければならない。どれもクエリとフラグメントのない `https` の URL。`listen` がループバックアドレスでなければ必須。TLS を終端するプロキシの後ろでは、`listen` が `http` でも、これらは `https` にする。管理画面のサインインを有効にしたときは、管理画面は最初の URL で提供するので、その URL は `https` で、パスが `/` でなければならない（`publicUrls` がなければ、ループバックの `listen` の URL で提供する） |

## `tls`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | ファイルのパス | なし | `https` の `listen` に使う PEM の証明書。拡張キー使用法があるときは、その中にサーバー認証（`1.3.6.1.5.5.7.3.1`）が含まれていなければならない |
| `tls.key` | `ELM_TLS_KEY` | ファイルのパス | なし | `tls.certificate` の PEM の秘密鍵 |

CA が中間 CA でサービスの証明書に署名しているなら、中間 CA の証明書を、同じ PEM ファイルのサービスの証明書のあとに入れる。サービスは自分の証明書と一緒にそれを送る。`tls.key` は最初の証明書と組み合わせる。ルート CA は要らない。最初の証明書のあとにある証明書は、どれも CA の証明書でなければならない。そうでなければサービスは起動しない（終了コード 1）。サービスは `elm-server` と同じようにチェーンを組んで送る。足りない証明書のダウンロード、Linux の OCSP ステープリング、Windows の中間証明機関の証明書ストアも同じだ。`elm-server` の設定の[中間 CA の証明書](server-configuration.md#tls)を参照。

## `database`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `database.provider` | `ELM_DATABASE_PROVIDER` | 文字列 | `sqlite` | `sqlite` または `postgres`。SQLite は 1 ノードでしか使えない。複数のノードには PostgreSQL（15 から 18）が必要 |
| `database.connectionFile` | `ELM_DATABASE_CONNECTION` | ファイルのパス | なし（必須） | 接続文字列を入れたファイル。シークレットだ。[シークレット](#シークレット)を参照 |

接続文字列は、データベースのドライバーのものを使う。`sqlite` なら Microsoft.Data.Sqlite、`postgres` なら Npgsql だ。たとえば次のように書く（値は架空のもの）。

```text
Data Source=/var/lib/elm-service/elm.db
Host=db.example.com;Port=5432;Database=elm;Username=elm;Password=example-password
```

## `signing`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `signing.key` | `ELM_SIGNING_KEY` | ファイルのパス | なし（必須。`setup` には不要） | 署名鍵。P-256 の鍵を暗号化した PKCS#8 の PEM ファイル |
| `signing.certificates` | `ELM_SIGNING_CERTIFICATES` | ファイルのパスのリスト | なし（必須。`setup` には不要） | 署名鍵の鍵証明書のファイル。1 から 4 個で、空のものがあってはならない。証明書は現在有効で、ライセンスとリースへの署名を許可していなければならない。`usageLog.directory` を設定したときは、ログへの署名も許可していなければならない |
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE` | ファイルのパス | なし（`ELM_SIGNING_PASSPHRASE` が設定されていなければ必須。`setup` には不要） | 署名鍵のパスフレーズを入れたファイル。シークレットだ。[シークレット](#シークレット)を参照 |

## `trust`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` | ファイルのパスのリスト | なし（必須。`setup` には不要） | ベンダーのルート公開鍵の PEM ファイル。署名鍵の証明書を、この鍵で確かめる。ファイルの代わりに、`ELM_TRUST_ROOT_KEYS` に PEM テキストを入れてもよい。どちらもなければ、サービスは起動しない |
| `trust.revokedKeyIds` | `ELM_TRUST_REVOKEDKEYIDS` | 文字列のリスト | なし | サービスがもう信頼しない鍵 ID（`kid`） |

## `bootstrapOwner`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `bootstrapOwner.iss` | `ELM_BOOTSTRAPOWNER_ISS` | 文字列 | なし | 最初の `owner` の管理者の OIDC 発行者（issuer）。1 から 255 文字。`iss` と `sub` は両方設定するか、どちらも設定しない。`oidc.issuer` を設定したときは、`oidc.issuer` と同じでなければならない |
| `bootstrapOwner.sub` | `ELM_BOOTSTRAPOWNER_SUB` | 文字列 | なし | その発行者における、最初の `owner` の管理者のサブジェクト（`sub`）。1 から 255 文字 |

`elm-service setup` には `bootstrapOwner` が必要で、この管理者の API キーを作る。

- OIDC を使うとき（`oidc.issuer` を設定したとき）: `iss` は ID プロバイダーの発行者、`sub` は、最初の `owner` になる人に ID プロバイダーが与えるサブジェクトだ。その人がサインインすると、`oidc.roleMap` の内容にかかわらず `owner` のロールを得る。
- OIDC を使わないとき: 誰もサインインせず、最初の `owner` は `setup` が作る API キーで操作する。`iss` と `sub` はその管理者に名前を付けるだけなので、`local` と `owner` のように、1 から 255 文字の任意の文字列でよい。あとで OIDC を有効にするかもしれないなら、最初から、その ID プロバイダーの発行者とその人のサブジェクトを使う。OIDC を有効にすると、`bootstrapOwner.iss` は `oidc.issuer` と同じでなければならず、その `sub` を持つ人がサインインのときに `owner` のロールを得る。

## `nonce`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `nonce.secretFile` | `ELM_NONCE_SECRET` | ファイルのパス | なし（必須。`setup` には不要） | ノンスのシークレットを入れたファイル。32 バイト以上の乱数を base64 で書く。すべてのノードで同じ値にする。既定値はなく、サービスが作ることもない。[`elm secret create`](elm-cli.md#elm-secret-create) でこの形のファイルを作れる。シークレットだ。[シークレット](#シークレット)を参照 |

## `rateLimit`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `rateLimit.perIp` | `ELM_RATELIMIT_PERIP` | 整数 | `20` | 1 つの送信元（IPv4 アドレス、または IPv6 の /64）から、デバイス用のエンドポイント以外のすべてのパスへの、1 秒あたりのリクエスト数。認証の前に数える。1 から 10000 |
| `rateLimit.perKey` | `ELM_RATELIMIT_PERKEY` | 整数 | `10` | 1 つの API キー、または（OIDC のアクセストークンと管理画面のセッションでは）1 人の管理者による、1 秒あたりの管理 API のリクエスト数。1 から 10000 |
| `rateLimit.clientPerIp` | `ELM_RATELIMIT_CLIENTPERIP` | 整数 | `20` | 1 つの送信元から、デバイス用のエンドポイント（アクティベーション、トライアル、ノンス、オフラインのファイル、リース）への、1 秒あたりのリクエスト数。認証の前に数える。1 から 10000 |
| `rateLimit.clientPerKey` | `ELM_RATELIMIT_CLIENTPERKEY` | 整数 | `2` | 1 つのインストール鍵による、1 秒あたりのデバイスのリクエスト数。1 から 10000 |

## `audit`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `audit.retentionDays` | `ELM_AUDIT_RETENTIONDAYS` | 整数 | なし。すべての行を残す | 監査ログの行を残す日数。1 から 36600。それより古い行は 1 時間ごとに削除する |

## `usageLog`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `usageLog.directory` | `ELM_USAGELOG_DIRECTORY` | フォルダーのパス | なし。利用ログを書かない | このノードの利用ログのフォルダー。サービスがこのフォルダーに書き込めなければならず、署名鍵の証明書がログへの署名を許可していなければならない。そのような証明書は、[`elm key certify`](elm-cli.md#elm-key-certify) に `--use log` を付けて作る（`--use license,lease,log` のように、ほかの用途と一緒に指定する） |

## `node`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `node.name` | `ELM_NODE_NAME` | 文字列 | ホスト名。`A-Z`、`a-z`、`0-9`、`-` 以外の文字を `-` に置き換え、64 文字で切る（何も残らなければ `node`） | このノードの名前。`A-Z`、`a-z`、`0-9`、`-` からなる 1 から 64 文字。利用ログのファイル名の一部になる。利用ログのフォルダーを共有するノードには、それぞれ別の名前を付ける |

## `oidc`

管理 API がアクセストークンを受け入れる ID プロバイダー。`oidc.issuer` がなければ、管理 API は API キーだけを受け入れ、ほかの `oidc` の設定は 1 つも設定できない。

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `oidc.issuer` | `ELM_OIDC_ISSUER` | URL | なし。OIDC は無効 | 発行者（issuer）。クエリ、フラグメント、ユーザー情報のない `https` の URL（またはループバックのホストの `http`）で、2048 文字まで。トークンの `iss` とは文字列として比べる。設定したときは、`oidc.audience`、`oidc.cliClientId`、`oidc.roleMap` が必須になる |
| `oidc.audience` | `ELM_OIDC_AUDIENCE` | 文字列 | なし | アクセストークンの `aud` が含んでいなければならない値。1 から 255 文字。`oidc.cliClientId` とは別の値にする |
| `oidc.cliClientId` | `ELM_OIDC_CLICLIENTID` | 文字列 | なし | `elm admin login` が使うクライアント ID。シークレットを持たないパブリッククライアント。1 から 255 文字 |
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES` | 文字列のリスト | `openid`、`offline_access` | `elm admin login` が要求するスコープ。1 から 20 個で、それぞれ空白、`"`、`\` を含まない 1 から 100 文字の印字可能な ASCII 文字 |
| `oidc.roleClaim` | `ELM_OIDC_ROLECLAIM` | 文字列 | `groups` | 値がロールを表すトークンのクレーム。1 から 255 文字 |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP` | 文字列のオブジェクト | なし | クレームの値と、その値に与えるロール。1 から 100 個。値は 1 から 255 文字で、ロールは `owner`、`admin`、`support`、`viewer` のいずれか。環境変数では、同じ値を 2 回書かない |
| `oidc.webClientId` | `ELM_OIDC_WEBCLIENTID` | 文字列 | なし。管理画面のサインインは無効 | 管理画面のサインインに使うコンフィデンシャルクライアント。1 から 255 文字。`oidc.issuer` と Web クライアントのシークレットが必要。`oidc.cliClientId` とも `oidc.audience` とも別の値にする |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET` | ファイルのパス | なし | Web クライアントのシークレットを入れたファイル。空白を含まない 1 から 1024 文字の印字可能な ASCII 文字。`oidc.webClientId` を設定したときは必須。シークレットだ。[シークレット](#シークレット)を参照 |

管理画面のサインインを使うには、ID プロバイダーに Web クライアントを次の URL で登録する。`<origin>` は、`publicUrls` の最初の URL（`https://activation.example.com` など）のスキーム、ホスト、ポートだ。`publicUrls` がなければ、ループバックの `listen` の URL のものになる。

| ID プロバイダーでの項目 | URL |
| --- | --- |
| リダイレクト URI（コールバック） | `<origin>/auth/callback` |
| ログアウト後のリダイレクト URI | `<origin>/` |

サインインには PKCE 付きの認可コードフローを使い、スコープは `openid` だけを要求する。サインアウトのときは、ID プロバイダーのディスカバリードキュメントに `end_session_endpoint` があれば、管理画面はブラウザーを `client_id` と `post_logout_redirect_uri` 付きでそのエンドポイントに送る。

## `apiKeys`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `apiKeys.inactiveAdminDays` | `ELM_APIKEYS_INACTIVEADMINDAYS` | 整数 | `90` | 管理者がこの日数サインインしないと、その管理者の API キーが使えなくなる。0 から 3650。`0` にするとこのルールは無効になる。`oidc.issuer` を設定したときだけ適用する |

## `dataProtection`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK` | ファイルのパス | なし | 管理画面のサインインに使う鍵暗号化鍵を入れたファイル。32 バイトを base64 で書く。`oidc.webClientId` を設定したときは必須で、設定しないときは指定できない。シークレットだ。[シークレット](#シークレット)を参照 |

## `session`

管理画面のセッション。これらは `oidc.webClientId` を設定したときだけ設定する。

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `session.idleMinutes` | `ELM_SESSION_IDLEMINUTES` | 整数 | `30` | リクエストがないままこの分数が過ぎると、セッションが終わる。5 から 120 |
| `session.maxHours` | `ELM_SESSION_MAXHOURS` | 整数 | `12` | セッションが続く最長の時間（時間単位）。1 から 24 |

## シークレット

シークレットは、値そのものを入れた環境変数か、設定で指定したファイルで渡す。設定ファイルにはファイルの名前を書くだけで、シークレットそのものは書かない。シークレットをコンテナイメージに入れてもいけない。両方を渡したときは、環境変数を優先する。

| シークレット | 環境変数（値） | 設定（ファイル） | 形式 | 必須 |
| --- | --- | --- | --- | --- |
| データベースの接続文字列 | `ELM_DATABASE_CONNECTION` | `database.connectionFile` | `database.provider` の接続文字列 | 常に必須 |
| 署名鍵のパスフレーズ | `ELM_SIGNING_PASSPHRASE` | `signing.passphraseFile` | 暗号化した `signing.key` のパスフレーズ | サービスでは必須。`setup` には不要 |
| ノンスのシークレット | `ELM_NONCE_SECRET` | `nonce.secretFile` | 32 バイト以上の乱数を base64 で書く。すべてのノードで同じ値 | サービスでは必須。`setup` には不要 |
| Web クライアントのシークレット | `ELM_OIDC_WEBCLIENTSECRET` | `oidc.webClientSecretFile` | 空白を含まない 1 から 1024 文字の印字可能な ASCII 文字 | `oidc.webClientId` を設定したとき |
| 鍵暗号化鍵 | `ELM_DATAPROTECTION_KEK` | `dataProtection.kekFile` | 32 バイトを base64 で書く。すべてのノードで同じ値 | `oidc.webClientId` を設定したとき |

- シークレットのファイルは 16 KiB までで、空であってはならない。末尾にある改行はすべて取り除く。
- 署名鍵そのものはファイル（`signing.key`）で、上のパスフレーズで暗号化する。その鍵証明書（`signing.certificates`）とルート公開鍵（`trust.rootKeys`）は公開してよいもので、シークレットではない。
- CLI のクライアント（`oidc.cliClientId`）にはシークレットがない。

## 例

```json
{
  "listen": "http://0.0.0.0:8080",
  "publicUrls": ["https://activation.example.com/"],
  "database": {
    "provider": "postgres",
    "connectionFile": "/run/secrets/elm-database-connection"
  },
  "signing": {
    "key": "/etc/elm-service/signing-key.pem",
    "certificates": ["/etc/elm-service/signing-key.cert"],
    "passphraseFile": "/run/secrets/elm-signing-passphrase"
  },
  "trust": {
    "rootKeys": ["/etc/elm-service/acme-root.pem"]
  },
  "nonce": {
    "secretFile": "/run/secrets/elm-nonce-secret"
  },
  "usageLog": {
    "directory": "/var/lib/elm-service/usage"
  },
  "node": {
    "name": "node-1"
  }
}
```
