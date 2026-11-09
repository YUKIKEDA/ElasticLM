---
sidebar_position: 21
---

# OIDC でサインインする

このガイドは、API キーを配る代わりに、管理者が自社の ID プロバイダー（IdP）で OpenID Connect（OIDC）を使って `elm-service` にサインインできるようにする、ベンダーの運用担当者に向けている。OIDC を使うと、`elm admin login` はコマンドラインから、管理画面はブラウザーから管理者をサインインさせ、管理者のロールは IdP のグループで決まる。このガイドでは、試すために IdP として Keycloak をコンテナで起動してその設定を書き、[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)のサービスで OIDC を有効にする。そのあと、Microsoft Entra ID で何を設定するかと、どの IdP でも気をつけることを示す。Docker のある Linux ホストで進める。Windows や macOS では、Linux の仮想マシンか、Docker を入れた WSL の Linux ディストリビューションで進める。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、Docker のある Linux ホストで、チュートリアルのフォルダーで打つ。チュートリアルの `tools` フォルダーは `PATH` に足しておく。チュートリアルを Windows や macOS で進めたときは、`tools` にチュートリアルの Linux 用のアーカイブが要る。

- サービスが止まっているときは、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていないときは、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。これは OIDC を有効にする前にする。有効にしたあとは、`setup` は下で指定する新しいオーナーのキーを作るからだ。

ここでは、Keycloak はホストのポート 8180 で待ち受ける。サービスは引き続き `http://127.0.0.1:8080` で待ち受ける。チュートリアルと同じように、`localhost` ではなく `127.0.0.1` と打つ。IdP は、サービス用に登録したアドレスしか受け付けないからだ。

## IdP の設定を書く

Keycloak は、起動するときにレルムを JSON ファイルから読み込む。レルムとは、1 つの組織のための IdP の設定だ。チュートリアルのフォルダーに `import` という名前のフォルダーを作り、その中に次の内容で `elm-realm.json` という名前のファイルを作る。中にあるパスワードとクライアントシークレットは、試すためだけのものだ。

<!-- steps:file path=import/elm-realm.json -->
```json
{
  "realm": "elm",
  "enabled": true,
  "accessTokenLifespan": 300,
  "groups": [
    {
      "name": "elm-owners"
    }
  ],
  "users": [
    {
      "id": "6f1c2a4e-5b7d-4e8f-9a0b-1c2d3e4f5a6b",
      "username": "alice",
      "enabled": true,
      "email": "alice@example.com",
      "emailVerified": true,
      "firstName": "Alice",
      "lastName": "Example",
      "requiredActions": [],
      "credentials": [
        {
          "type": "password",
          "value": "alice-try-only",
          "temporary": false
        }
      ],
      "groups": ["/elm-owners"]
    }
  ],
  "clients": [
    {
      "clientId": "elm-cli",
      "name": "elm command-line tool",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "consentRequired": false,
      "attributes": {
        "oauth2.device.authorization.grant.enabled": "true"
      },
      "protocolMappers": [
        {
          "name": "elm-service audience",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.custom.audience": "elm-service",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        },
        {
          "name": "groups",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-group-membership-mapper",
          "config": {
            "claim.name": "groups",
            "full.path": "false",
            "access.token.claim": "true",
            "id.token.claim": "true",
            "userinfo.token.claim": "false"
          }
        }
      ]
    },
    {
      "clientId": "elm-web",
      "name": "elm-service admin UI",
      "enabled": true,
      "publicClient": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "elm-web-try-only",
      "standardFlowEnabled": true,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "consentRequired": false,
      "redirectUris": ["http://127.0.0.1:8080/auth/callback"],
      "webOrigins": [],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "http://127.0.0.1:8080/"
      },
      "protocolMappers": [
        {
          "name": "groups",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-group-membership-mapper",
          "config": {
            "claim.name": "groups",
            "full.path": "false",
            "access.token.claim": "true",
            "id.token.claim": "true",
            "userinfo.token.claim": "false"
          }
        }
      ]
    }
  ]
}
```

それぞれの部分の役割は次のとおりだ。

- グループ `elm-owners` には、`owner` のロールを得る管理者を入れる。ユーザー `alice` はこのグループに入っている。下のサービスの設定でこのユーザーを指定できるように、ここではその `id` を固定している。Keycloak は、これを `sub` としてこのユーザーのトークンに入れる。
- `elm-cli` は `elm admin login` のクライアントだ。シークレットを持たないパブリッククライアントで、デバイス認可フローだけを使う。このクライアントのオーディエンスのマッパーは、サービスの `oidc.audience` の値である `elm-service` を、アクセストークンの `aud` に入れる。サービスは、これがないアクセストークンを拒否する。`name` は Keycloak が承認画面に表示する名前で、管理者はこれを見て、どのプログラムが求めているのかを知る。
- `elm-web` は管理画面のクライアントだ。シークレットを持つコンフィデンシャルクライアントで、PKCE 付きの認可コードフローだけを使う。リダイレクト URI とサインアウト後のアドレスは、サービスのアドレスにそれぞれ `/auth/callback` と `/` を付けたもので、ワイルドカードを使わずにそのとおりに書く。
- どちらのクライアントも、ユーザーのグループを最上位の `groups` クレームに、パスを付けないグループ名（`full.path` をオフ）で入れる。入れる先は、アクセストークンと ID トークンの両方だ。サービスはこのクレームからロールを読むからだ。

## Keycloak を起動する

Keycloak のコンテナを作り、`import` フォルダーをその中にコピーしてから起動する。Keycloak は、起動するときにレルムを読み込む。

<!-- steps:run -->
```shell
docker create --name elm-keycloak -p 127.0.0.1:8180:8080 quay.io/keycloak/keycloak@sha256:27b3230fbabb8c9ff3c66c864cf09dcd8a41aca82e5100679d109ef0eeb05741 start-dev --import-realm
docker cp import elm-keycloak:/opt/keycloak/data/
docker start elm-keycloak
docker exec elm-keycloak bash -c 'for i in $(seq 120); do exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /realms/elm/.well-known/openid-configuration HTTP/1.0\r\n\r\n" >&3 && head -n 1 <&3 | grep -q " 200 " && exit 0; sleep 1; done; exit 1'
```

`start-dev` は、Keycloak を開発用に動かす。通信は暗号化しない http で、データはコンテナと一緒に消える。最後の行は、Keycloak がレルムについて応答するようになるまで待つ。30 秒ほどかかる。サービスは、これが終わってから起動する。サービスは最初のサインインのときに IdP の設定を読み、まだ一度も読めていないあいだは、間隔を少しずつ延ばしながら読み直すからだ。

## サービスで OIDC を有効にする

管理画面のサインインには、シークレットが 2 つ要る。1 つ目は鍵暗号化鍵で、データベースにあるサインイン用の鍵を保護する。ノンスのシークレットと同じコマンドで作る。

<!-- steps:run -->
```shell
elm secret create --out kek.secret
```

Web クライアントのシークレット、つまりレルムの `secret` の値を、ファイルに入れる。

<!-- steps:file path=web-client.secret -->
```text
elm-web-try-only
```

本番では、クライアントのシークレットは IdP に作らせる。設定の[シークレット](../reference/service-configuration.md#シークレット)にあるとおり、どちらのファイルも、サービスのアカウントだけが読める場所に置く。

`service.json` の中身を、チュートリアルの設定に次の設定を足したものに置き換える。

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing.key.pem",
    "certificates": ["signing.cert"]
  },
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "nonce": {
    "secretFile": "nonce.secret"
  },
  "bootstrapOwner": {
    "iss": "http://127.0.0.1:8180/realms/elm",
    "sub": "6f1c2a4e-5b7d-4e8f-9a0b-1c2d3e4f5a6b"
  },
  "oidc": {
    "issuer": "http://127.0.0.1:8180/realms/elm",
    "audience": "elm-service",
    "cliClientId": "elm-cli",
    "roleMap": {
      "elm-owners": "owner"
    },
    "webClientId": "elm-web",
    "webClientSecretFile": "web-client.secret"
  },
  "dataProtection": {
    "kekFile": "kek.secret"
  }
}
```

- `oidc.issuer` はレルムのアドレスで、サービスはこれをトークンの `iss` と文字列として比べる。暗号化しない `http` を使えるのは、ここのようにループバックアドレスのときだけだ。
- `oidc.audience` は、アクセストークンの `aud` が含んでいなければならない値で、どちらのクライアント ID とも違う。
- `oidc.roleMap` は、`elm-owners` のメンバーに `owner` のロールを与える。ロールは `owner`、`admin`、`support`、`viewer` だ。
- `bootstrapOwner` は、今度は `alice` を指定する。OIDC を有効にしたときは、その `iss` が `oidc.issuer` と同じでなければ、サービスは起動しない。ブートストラップのオーナーは、`roleMap` の内容にかかわらず常に `owner` としてサインインし、`elm-service setup` はそのオーナーのキーを作る。こうして、IdP から締め出されたオーナーでもサービスに入れるようにしている。
- `publicUrls` は書かない。`https` のアドレスしか指定できないからだ。書かないときは、管理画面のアドレスは `http://127.0.0.1:8080/` になる。

2 つ目のターミナルで Ctrl+C を押し、そこでサービスをもう一度起動する。そのターミナルには `ELM_SIGNING_PASSPHRASE` がまだ設定されている。

<!-- steps:background ready=http://127.0.0.1:8080/auth/login -->
```shell
elm-service --config service.json
```

`/auth/login` は、ブラウザーを Keycloak のサインイン画面に送る。この画面が応答すれば、サービスがレルムを読めていて、Keycloak が `elm-web` とそのリダイレクト URI を知っていることがわかる。リダイレクト URI が違うと、Keycloak は代わりにエラーの画面を返す。

サービスは、どこでサインインするかを `elm` に伝える。

<!-- steps:run capture=oidc-client pattern="(elm-cli)" -->
```shell
elm admin request GET /v1/admin/oidc --url http://127.0.0.1:8080
```

`elm` は、発行者、クライアント ID `elm-cli`、スコープを表示する。

```text
{"issuer":"http://127.0.0.1:8180/realms/elm","clientId":"elm-cli","scopes":["openid","offline_access"]}
```

チュートリアルの API キーはまだ使える。このキーで、管理者の一覧を読む。

<!-- steps:run capture=tutorial-owner pattern="(tutorial)" -->
```shell
elm admin request GET /v1/admin/admins --url http://127.0.0.1:8080
```

一覧には、チュートリアルのオーナー `https://idp.example` `tutorial` がある。`alice` は、サインインすると一覧に現れる。

## elm でサインインする

1 つ目のターミナルから `ELM_API_KEY` を削除する。`elm` は、サインインよりもこの変数を優先して使うからだ。PowerShell では `Remove-Item Env:ELM_API_KEY`、Bash か zsh では `unset ELM_API_KEY` を実行する。それからサインインする。

```shell
elm admin login --url http://127.0.0.1:8080
```

`elm` は、Keycloak のアドレスとコードを標準エラー出力に表示して待つ。そのアドレスをブラウザーで開き、画面に `elm command-line tool` と出ていることを確かめてからコードを入力し、パスワード `alice-try-only` で `alice` としてサインインする。すると `elm` は、このユーザーとこのサービスのサインインを保存する。`elm admin request` は、サインインが終わるまでそれを使う。`elm admin request GET /v1/admin/me --url http://127.0.0.1:8080` を試すと、ロール `owner` の `alice` が表示される。`elm admin logout --url http://127.0.0.1:8080` でサインインを終える。サインインを保存する場所などの詳細は、[elm コマンドリファレンス](../reference/elm-cli.md#elm-admin-login)にある。

自分で求めたのではないコードは、決して入力しない。コードとアドレスを送ってくる人は、その人のサインインをあなたにさせようとしている。

## 管理画面にサインインする

ブラウザーで `http://127.0.0.1:8080/` を開き、`alice` としてサインインする。サービスはサインインをセッション Cookie に保つ。セッションは、リクエストがないまま `session.idleMinutes`（30）が過ぎるか、`session.maxHours`（12）が過ぎると終わる。管理画面からサインアウトすると、サービスのセッションは終わる。IdP のセッションが終わるのは、IdP とブラウザーがサインアウトの画面に従ったときだけなので、共用のブラウザーでは Keycloak からもサインアウトする。サインインで守れないものは、ほかにも[セキュリティモデル](../explanation/security-model.md#saas-型のサービスとその管理者)に挙げている。

BROWSERS_TRIED

本番では、管理画面を https で提供する。サービスの前で TLS を終端するか、サービスに [`tls`](../reference/service-configuration.md#tls) を設定する。そして `publicUrls` に https のアドレスを設定する。リダイレクト URI は、そのアドレスを使うようになる。

## チュートリアルのオーナーを無効にする

`alice` がサインインしたら、チュートリアルのオーナーはもう要らない。そのままでも、そのオーナーの API キーは、サインインがないまま 90 日たつと使えなくなる。OIDC を有効にすると `apiKeys.inactiveAdminDays` が適用されるからだ。キー自体も 90 日で期限が切れる。`alice` としてサインインした状態で、`GET /v1/admin/admins` でそのオーナーの `id` を探し、無効にする。

```shell
elm admin request POST /v1/admin/admins/<id>/disable --url http://127.0.0.1:8080
```

そのオーナーの API キーは、チュートリアルのキーも含めて、すぐに使えなくなる。オーナーは別のオーナーを無効にできるが、ブートストラップのオーナー、自分自身、有効な最後のオーナーは無効にできない。

## Microsoft Entra ID

Microsoft Entra ID では、テナントにアプリケーションを 2 つ登録する。1 つは `elm admin login` 用で、デバイス認可フローを使うパブリッククライアントにする。もう 1 つは管理画面用で、シークレットとリダイレクト URI `<your address>/auth/callback` を持つコンフィデンシャルクライアントにする。サービスのアプリケーションで API のスコープを公開し、そのスコープを `openid` と `offline_access` と並べて `oidc.cliScopes` に入れ、`oidc.audience` にはその API のアプリケーション ID を設定する。Entra ID がサービス用のアクセストークンを発行するのは、要求がそのスコープを求めたときだけだからだ。そのうえで、次の点に気をつける。

- `oidc.issuer` は、テナントの発行者 `https://login.microsoftonline.com/<tenant ID>/v2.0` だ。v2.0 のトークンが持つ `iss` は `/v2.0` で終わる（Microsoft の[アクセストークンのクレームのリファレンス](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference)を参照）。
- `groups` クレームにはグループのオブジェクト ID が入るので、`oidc.roleMap` のキーはその ID になる。ユーザーが 200 を超えるグループに入っていると、Entra ID はトークンにこのクレームを入れない。するとサービスには、そのユーザーのロールが見えない。管理者を入れるグループは少なくしておくか、管理者に `roles` クレームのアプリロールを与えて、`oidc.roleClaim` を `roles` にする。
- Entra ID の `sub` は、同じユーザーでもアプリケーションごとに違う（同じリファレンスにそう書かれている）。管理画面と `elm admin login` は別々のアプリケーションを使うので、1 人の人が 2 人の管理者になり、`bootstrapOwner.sub` はそのうち 1 人にしか一致しない。サービスがユーザーの識別に別のクレームを使えるようになるまで（今後の変更）、各管理者のロールは `oidc.roleMap` で与え、`GET /v1/admin/admins` には 1 人につき 2 つのエントリーがあるものと考えておく。

このガイドは Entra ID では試していない。各設定は、自分のテナントについての Microsoft のドキュメントと照らし合わせて確かめる。

## どの IdP でも気をつける設定

- デバイスフローの承認画面に、クライアントの名前を表示する。管理者が、どのプログラムが求めているのかわかるようにするためだ。
- `oidc.audience` は、どちらのクライアント ID とも違う値にする。同じだと、サービスは起動しない。
- ロールのクレームは、ID トークンとアクセストークンの両方に入れる。
- 管理画面のクライアントには認可コードフローだけを許可し、それ以外はすべてオフにする。Keycloak なら、サービスアカウントやダイレクトグラントなどだ（`fullScopeAllowed: false` にすると、関係のないロールがトークンに入らない）。
- IdP のトークンの有効期間は短くする。サービスはアクセストークンを自分で確かめ、IdP に問い合わせないので、サインアウトしたトークンも期限が切れるまで有効なままだ。
- `apiKeys.inactiveAdminDays`（90）は、その日数サインインしていない管理者の API キーを使えなくする。

## Keycloak を止める

このガイドのコンテナは試すためだけのもので、`start-dev` はデータをコンテナの中に置く。まずサービスを止め、次にコンテナを止めて削除する。`docker stop elm-keycloak` を実行し、続けて `docker rm -v elm-keycloak` を実行する。`kek.secret` と `web-client.secret` も削除し、`service.json` から `oidc`、`dataProtection`、新しく書いた `bootstrapOwner` の設定を取り除けば、チュートリアルのサービスに戻る。チュートリアルのオーナーがまた使えるのは、それを無効にしなかったときだけだ。

## 関連するページ

- `elm-service` の設定の [`oidc`](../reference/service-configuration.md#oidc)、[`dataProtection`](../reference/service-configuration.md#dataprotection)、[`session`](../reference/service-configuration.md#session)。
- [`elm admin login`](../reference/elm-cli.md#elm-admin-login) と [`elm admin logout`](../reference/elm-cli.md#elm-admin-logout)。
- [セキュリティモデル](../explanation/security-model.md): サインインが守るものと、守らないもの。
