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
| 1 | サービスを起動できない。たとえば、署名鍵や証明書のファイルを読めない、署名鍵と証明書が対応していない、データベースでエラーが起きた |
| 2 | 引数が有効でない。または設定のエラー（設定ファイルや値が有効でない、必須の値がない） |

`setup` の終了コード:

| 終了コード | 意味 |
| --- | --- |
| 0 | API キーを作った |
| 1 | データベースでエラーが起きた。使えない、スキーマがこのバージョンの `elm-service` より新しい、適用済みのマイグレーションが変わっている、`bootstrapOwner` の管理者が存在するが無効になっているか別のロールを持っている、キーを記録できない、のいずれか |
| 2 | 引数が有効でない、サービスと同じ設定のエラーがある、`bootstrapOwner` が設定されていない、または `database` の設定を使えない |

終了コード 1 か 2 で終わる前に、`elm-service` は理由を標準エラー出力に表示する。

## 設定の渡し方

- 設定は、JSON の設定ファイル、環境変数、またはその両方で渡す。ファイルは、サービスにも `setup` にも `--config` で渡す。

  ```text
  elm-service --config /etc/elm-service/settings.json
  elm-service setup --config /etc/elm-service/settings.json
  ```

- `--config` がなければ、`elm-service` は環境変数だけを読む。
- 環境変数は、ファイルにある同じ設定より優先する。空の値を設定した環境変数は、設定されていないものとして扱う。
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
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE` | 署名鍵のパスフレーズそのもの。ファイルのパスではない |
| `nonce.secretFile` | `ELM_NONCE_SECRET` | ノンスのシークレットそのもの（base64）。ファイルのパスではない |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET` | Web クライアントのシークレットそのもの。ファイルのパスではない |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK` | 鍵暗号化鍵そのもの（base64）。ファイルのパスではない |
| `trust.rootKeys` | `ELM_TRUST_ROOTKEYS` または `ELM_TRUST_ROOT_KEYS` | `ELM_TRUST_ROOTKEYS` は規則に従い、ファイルのパスを入れる。`ELM_TRUST_ROOT_KEYS` はルート公開鍵を PEM テキストで入れ、ファイルから鍵が 1 つも得られないときだけ読む |
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES` | スコープを `;` ではなく空白で区切る |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP` | `value=role` の組を `;` で区切る。たとえば `elm-owners=owner;elm-support=support`。`;` を含むクレームの値は、代わりにファイルに書く |

## 最上位の設定

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `listen` | `ELM_LISTEN` | URL | `http://127.0.0.1:8080` | サービスが待ち受ける場所。パス、クエリ、フラグメントのない `http` または `https` の URL。ホストは IP アドレス、`localhost`、`0.0.0.0`、`[::]` のいずれか。`https` には `tls.certificate` と `tls.key` が必要 |
| `publicUrls` | `ELM_PUBLICURLS` | URL のリスト | `listen` の URL（`listen` がループバックアドレスか `localhost` のときだけ） | デバイスがサービスに接続するときの URL。デバイスのプルーフは、このうちの 1 つを指定していなければならない。どれもクエリとフラグメントのない `https` の URL。`listen` がループバックアドレスでなければ必須。TLS を終端するプロキシの後ろでは、`listen` が `http` でも、これらは `https` にする。管理画面のログインを有効にしたときは、管理画面は最初の URL で提供するので、その URL は `https` で、パスが `/` でなければならない |

## `tls`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `tls.certificate` | `ELM_TLS_CERTIFICATE` | ファイルのパス | なし | `https` の `listen` に使う PEM の証明書 |
| `tls.key` | `ELM_TLS_KEY` | ファイルのパス | なし | `tls.certificate` の PEM の秘密鍵 |

## `database`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `database.provider` | `ELM_DATABASE_PROVIDER` | 文字列 | `sqlite` | `sqlite` または `postgres`。SQLite は 1 ノードでしか使えない。複数のノードには PostgreSQL（15 から 18）が必要 |
| `database.connectionFile` | `ELM_DATABASE_CONNECTION`（接続文字列） | ファイルのパス | なし（必須） | 接続文字列を入れたファイル。シークレットだ。[シークレット](#シークレット)を参照 |

## `signing`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `signing.key` | `ELM_SIGNING_KEY` | ファイルのパス | なし（必須。`setup` には不要） | 署名鍵。P-256 の鍵を暗号化した PKCS#8 の PEM ファイル |
| `signing.certificates` | `ELM_SIGNING_CERTIFICATES` | ファイルのパスのリスト | なし（必須。`setup` には不要） | 署名鍵の鍵証明書のファイル。1 から 4 個で、空のものがあってはならない。各証明書の前後の空白と改行は取り除く。証明書は現在有効で、ライセンスとリースへの署名を許可していなければならない。`usageLog.directory` を設定したときは、ログへの署名も許可していなければならない |
| `signing.passphraseFile` | `ELM_SIGNING_PASSPHRASE`（パスフレーズ） | ファイルのパス | なし（`ELM_SIGNING_PASSPHRASE` が設定されていなければ必須。`setup` には不要） | 署名鍵のパスフレーズを入れたファイル。シークレットだ。[シークレット](#シークレット)を参照 |

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

## `nonce`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `nonce.secretFile` | `ELM_NONCE_SECRET`（シークレット） | ファイルのパス | なし（必須。`setup` には不要） | ノンスのシークレットを入れたファイル。32 バイト以上の乱数を base64 で書く。すべてのノードで同じ値にする。既定値はなく、サービスが作ることもない。シークレットだ。[シークレット](#シークレット)を参照 |

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
| `usageLog.directory` | `ELM_USAGELOG_DIRECTORY` | フォルダーのパス | なし。利用ログを書かない | このノードの利用ログのフォルダー。サービスがこのフォルダーに書き込めなければならず、署名鍵の証明書がログへの署名を許可していなければならない |

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
| `oidc.cliScopes` | `ELM_OIDC_CLISCOPES`（空白区切り） | 文字列のリスト | `openid`、`offline_access` | `elm admin login` が要求するスコープ。1 から 20 個で、それぞれ空白、`"`、`\` を含まない 1 から 100 文字の印字可能な ASCII 文字 |
| `oidc.roleClaim` | `ELM_OIDC_ROLECLAIM` | 文字列 | `groups` | 値がロールを表すトークンのクレーム。1 から 255 文字 |
| `oidc.roleMap` | `ELM_OIDC_ROLEMAP`（`;` で区切った `value=role` の組） | 文字列のオブジェクト | なし | クレームの値と、その値に与えるロール。1 から 100 個。値は 1 から 255 文字で、ロールは `owner`、`admin`、`support`、`viewer` のいずれか。環境変数では、同じ値を 2 回書かない |
| `oidc.webClientId` | `ELM_OIDC_WEBCLIENTID` | 文字列 | なし。管理画面のログインは無効 | 管理画面のログインに使うコンフィデンシャルクライアント。1 から 255 文字。`oidc.issuer` と Web クライアントのシークレットが必要。`oidc.cliClientId` とも `oidc.audience` とも別の値にする |
| `oidc.webClientSecretFile` | `ELM_OIDC_WEBCLIENTSECRET`（シークレット） | ファイルのパス | なし | Web クライアントのシークレットを入れたファイル。空白を含まない 1 から 1024 文字の印字可能な ASCII 文字。`oidc.webClientId` を設定したときは必須。シークレットだ。[シークレット](#シークレット)を参照 |

## `apiKeys`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `apiKeys.inactiveAdminDays` | `ELM_APIKEYS_INACTIVEADMINDAYS` | 整数 | `90` | 管理者がこの日数ログインしないと、その管理者の API キーが使えなくなる。0 から 3650。`0` にするとこのルールは無効になる。`oidc.issuer` を設定したときだけ適用する |

## `dataProtection`

| 設定 | 環境変数 | 型 | 既定 | 範囲と注意 |
| --- | --- | --- | --- | --- |
| `dataProtection.kekFile` | `ELM_DATAPROTECTION_KEK`（鍵） | ファイルのパス | なし | 管理画面のログインに使う鍵暗号化鍵を入れたファイル。32 バイトを base64 で書く。`oidc.webClientId` を設定したときは必須で、設定しないときは指定できない。シークレットだ。[シークレット](#シークレット)を参照 |

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

- シークレットのファイルは 16 KiB までで、空であってはならない。末尾の改行は取り除く。
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
