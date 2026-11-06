---
sidebar_position: 2
---

# SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする

このチュートリアルでは、ベンダーが運用する SaaS 型のアクティベーションサービス `elm-service` を動かし、その管理 API で、トライアルとライセンスを持つ製品を作る。そして、自分のマシンでトライアルを始め、そのあとライセンスキーでアクティベーションする小さな .NET のコンソールアプリを書く。アクティベーションは、ライセンスを 1 台のデバイスに紐付ける。紐付いたデバイスは、サービスから受け取ったライセンスファイルを保存する。詳しくは[アクティベーション](../explanation/activation.md)で説明している。ElasticLM を初めて使う人のうち、ターミナルと `dotnet` コマンドを使える人に向けている。このチュートリアルは単独で進められるので、ほかのチュートリアルを先に終えておく必要はない。

## 始める前に

次のものが要る。

- .NET 10 SDK。`dotnet --list-sdks` の一覧に、`10.` で始まるバージョンがなければならない。
- マシンのポート 8080 が空いていること。サービスはこのポートで待ち受ける。ポートが空いていれば、PowerShell の `Get-NetTCPConnection -LocalPort 8080 -ErrorAction SilentlyContinue` も、Linux と macOS の `lsof -i :8080` も、何も表示しない。
- ダウンロードしたファイルを確かめるための [GitHub CLI](https://cli.github.com/)（`gh`）。このチュートリアルで `gh` を使うのは、この確認のときだけだ。
- Windows では PowerShell 7.1 以降。このチュートリアルでは、秘密の値を表示せずに読み込むために使う。バージョンは `$PSVersionTable.PSVersion` で表示できる。

このチュートリアル用に、空のフォルダーを作る。そのフォルダーでターミナルを開き、このページのコマンドはすべてそこで打つ。

`elm` ツール、`elm-service`、`ElasticLM.Licensing` と `ElasticLM.Client` のパッケージは、GitHub のプレリリースからダウンロードする。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)を開き、「Pre-release」のラベルが付いたリリースのうち、いちばん新しいものを使う。GitHub が「Latest」のラベルを付けるのはプレリリースでないリリースだけなので、そのラベルを目安にしない。

リリースの Assets から、次のファイルをチュートリアルのフォルダーに保存する。

- 自分のシステムに合うアーカイブ `elm-<version>-<RID>` と `elm-service-<version>-<RID>`。`<RID>` は `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`、`osx-x64` のどれかだ。Windows 用は `.zip` ファイル、それ以外は `.tar.gz` ファイルになっている。名前が `-symbols` で終わるアーカイブにはデバッグシンボルしか入っていないので、そうでないほうを取る。
- `ElasticLM.Client.<version>.nupkg` と `ElasticLM.Licensing.<version>.nupkg`。アプリは `ElasticLM.Client` を使い、`ElasticLM.Client` は `ElasticLM.Licensing` に依存するので、両方が要る。同じ名前の `.snupkg` ファイルは、パッケージのデバッグシンボルなので要らない。
- ファイルのハッシュの一覧 `SHA256SUMS`。

展開したり使ったりする前に、チュートリアルのフォルダーにあるアーカイブとパッケージを、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめる。この節には、ダウンロードしたプログラムを macOS で動かす方法と、プログラムに必要な Linux と macOS のバージョンも書いてある。

2 つのアーカイブを `tools` という名前のフォルダーに展開する。Windows の PowerShell では `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` と打ってから `Expand-Archive elm-service-<version>-<RID>.zip -DestinationPath tools -Force` と打つ。どちらのアーカイブにも同じ `LICENSE` ファイルが入っていて、`-Force` を付けると 2 つ目で上書きできる。Linux と macOS では `mkdir -p tools` と打ってから、`tar -xzf elm-<version>-<RID>.tar.gz -C tools` と `tar -xzf elm-service-<version>-<RID>.tar.gz -C tools` を打つ。これで `tools` には `elm` と `elm-service`（Windows では `elm.exe` と `elm-service.exe`）が入る。`elm` は鍵を作り、サービスの管理 API を呼び出すコマンドラインツールで、`elm-service` はサービスそのものだ。

<!-- steps:prebuilt name=elm -->
<!-- steps:prebuilt name=elm-service -->

`elm` と `elm-service` というコマンドで `tools` の中のプログラムが動くように、このターミナルで `tools` フォルダーを `PATH` に足す。Windows の PowerShell では `$env:PATH = "$PWD/tools;$env:PATH"` と打ち、Bash か zsh では `export PATH="$PWD/tools:$PATH"` と打つ。

2 つのパッケージを `packages` という名前のフォルダーに移す。PowerShell では `New-Item -ItemType Directory packages | Out-Null` と打ってから `Move-Item ElasticLM.*.nupkg packages` と打つ。Bash か zsh では `mkdir -p packages` と打ってから `mv ElasticLM.*.nupkg packages/` と打つ。

<!-- steps:prebuilt name=packages -->

チュートリアルのフォルダーに、次の内容で `nuget.config` という名前のファイルを作る。このファイルがあると、アプリは名前が `ElasticLM.` で始まるパッケージを `packages` からだけ取り、ほかのパッケージはすべて nuget.org から取る。パス `packages` は、`nuget.config` があるフォルダーからの相対パスだ。このチュートリアルで使う ElasticLM のパッケージは GitHub のプレリリースのもので、nuget.org に同じ名前のパッケージがあっても、それは ElasticLM のものではない。このパッケージソースの対応付けによって、そのようなパッケージは決して使われない。

<!-- steps:file path=nuget.config -->
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packages" value="packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packages">
      <package pattern="ElasticLM.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## 鍵を作る

鍵は 2 つ要る。アプリが信頼するルート鍵と、サービスが発行するライセンスファイルに署名する署名鍵だ。署名鍵が署名したものをアプリが受け入れるように、ルート鍵が署名鍵を証明する。

`elm` は、それぞれの秘密鍵をパスフレーズで暗号化したファイルに保存する。鍵を作るときは、12 文字以上の新しいパスフレーズを尋ね、もう一度入力させる。あとのコマンドがその鍵を使うときは、そのパスフレーズをまた尋ねる。入力中は何も表示されない。このチュートリアルでは、2 つの鍵に同じパスフレーズを使う。環境変数 `ELM_KEY_PASSPHRASE` が設定されていれば、`elm` はパスフレーズをそこから取り、尋ねない。そのため、下の出力にあるパスフレーズを尋ねる行は表示されない。このターミナルで設定するには、PowerShell では `$env:ELM_KEY_PASSPHRASE = '<passphrase>'`、Bash か zsh では `export ELM_KEY_PASSPHRASE='<passphrase>'` と打つ。これはスクリプトや、誰も入力できないターミナルのためのものだ。実際の運用では、スクリプトにパスフレーズを書いておかない。詳しくは、`elm` のリファレンスの[パスフレーズ](../reference/elm-cli.md#パスフレーズ)を参照。

下の出力にある鍵 ID、API キー、作ったものの ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。

ルート鍵を作る。

<!-- steps:run -->
```shell
elm key root create --out root.key.pem --public-out root.pub.pem
```

`elm` は次のように表示する。

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
private key (encrypted): root.key.pem
public key: root.pub.pem
```

署名鍵を作る。

<!-- steps:run -->
```shell
elm key signing create --out signing.key.pem --public-out signing.pub.pem
```

`elm` は次のように表示する。

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
private key (encrypted): signing.key.pem
public key: signing.pub.pem
```

ライセンスとリースの署名に使うために、ルート鍵で署名鍵を証明する。サービスには、この両方の用途が要る。

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license,lease --out signing.cert
```

`elm` は次のように表示する。

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license, lease
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` が鍵証明書だ。有効期間は 1 年で、そのあいだ署名鍵は新しいライセンスに署名してよい。この有効期間は、署名鍵が署名したライセンスがいつまで有効かを制限しない。この点は、[鍵証明書](../explanation/trust-chain.md#鍵証明書)の「有効期間と発行時刻」で説明している。

## サービスを準備する

アプリがサービスに送る各リクエストには、ノンスが入っている。ノンスは、サービスが少し前に渡した 1 回限りの値で、コピーしたリクエストをもう一度送れないようにするためのものだ。詳しくは[デバイスとプルーフ](../explanation/devices-and-proofs.md)で説明している。サービスは、シークレットを使ってノンスを作り、確かめる。新しいランダムなシークレットを入れたファイルを作る。

<!-- steps:run -->
```shell
elm secret create --out nonce.secret
```

`elm` は次のように表示する。

```text
secret file: nonce.secret
```

サービスは、データをデータベースに置く。このチュートリアルでは、1 つのファイルからなるデータベースである SQLite を使う。ファイルは `service.db` で、サービスが作る。チュートリアルのフォルダーに、データベースの接続文字列を書いた `database.connection` という名前のファイルを作る。

<!-- steps:file path=database.connection -->
```text
Data Source=service.db
```

チュートリアルのフォルダーに、次の内容で `service.json` という名前のファイルを作る。これがサービスの設定だ。

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
    "iss": "https://idp.example",
    "sub": "tutorial"
  }
}
```

- `database` は、使うデータベースを指定する。SQLite で、接続文字列は `database.connection` にある。
- `signing` は、署名鍵とその鍵証明書を指定する。サービスはこの鍵でライセンスファイルに署名する。
- `trust.rootKeys` は、署名鍵の鍵証明書を確かめるルート公開鍵の一覧を指定する。
- `nonce.secretFile` は、上で作ったシークレットのファイルを指定する。
- `bootstrapOwner` は、サービスの最初の管理者を指定する。この例のように ID プロバイダーによるサインイン（OIDC）を使わないときは、下で作る API キーの持ち主を示すだけで、この名前でサインインする人はいない。あとで OIDC を足すときは、`iss` を設定 `oidc.issuer` と同じ値にしなければ、サービスは起動しない。

パスは、サービスを起動したフォルダーからの相対パスだ。接続文字列、ノンスのシークレット、署名鍵のパスフレーズはシークレットなので、設定にはそれを入れたファイルか変数を書き、値そのものは書かない。サービスは既定で `http://127.0.0.1:8080` で待ち受けるので、待ち受けるアドレスを設定する必要はない。本番では、代わりに https で動かす。`tls` を含むすべての設定は [elm-service の設定](../reference/service-configuration.md)に挙げている。

データベースを準備し、最初の API キーを作る。API キーは、`elm` が管理 API を呼び出すときに使うキーだ。

<!-- steps:run capture=api-key env=ELM_API_KEY -->
```shell
elm-service setup --config service.json
```

`elm-service setup` は、データベースのテーブルを作り、1 行目に新しい API キーを表示し、2 行目にそのキーの説明を表示する。

```text
elm_FAKEapiKEY000000000000000000000000000000000
API key 01960000-0000-7000-8000-000000000001 (elm_FAKE…) for https://idp.example tutorial, owner permissions, expires 2026-04-15T09:30:00Z.
```

`elm admin request` は、API キーを環境変数 `ELM_API_KEY` から読む。1 行目のキーをコピーし、このターミナルで変数に読み込む。こうすると、キーがもう一度表示されることも、シェルの履歴に残ることもない。PowerShell では `$env:ELM_API_KEY = Read-Host -MaskInput` と打ち、キーを貼り付けて Enter を押す。Bash か zsh では `read -rs ELM_API_KEY; export ELM_API_KEY` と打ち、キーを貼り付けて Enter を押す。貼り付けているあいだ、何も表示されない。

## サービスを起動する

チュートリアルのフォルダーで 2 つ目のターミナルを開き、1 つ目のターミナルと同じ方法で、`tools` フォルダーをその `PATH` にも足す。`PATH` はターミナルごとに別だからだ。

サービスは、署名鍵のパスフレーズを環境変数 `ELM_SIGNING_PASSPHRASE` から読む。2 つ目のターミナルで、同じ方法で変数に読み込む。PowerShell では `$env:ELM_SIGNING_PASSPHRASE = Read-Host -MaskInput` と打ち、Bash か zsh では `read -rs ELM_SIGNING_PASSPHRASE; export ELM_SIGNING_PASSPHRASE` と打つ。続けて `signing.key.pem` のパスフレーズを打ち、Enter を押す。

2 つ目のターミナルでサービスを起動する。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

サービスは動き続け、ログを表示する。各エントリーは、`info:` で始まってエントリーの出どころを示す行と、そのあとにインデントして続くメッセージからなる。最初のいくつかのエントリーは、サービスが動く Web ホストが出す。待ち受けるアドレス、`Application started. Press Ctrl+C to shut down.`、ホスティング環境、`Content root path` だ。`Content root path` は `elm-service` の実行ファイルがあるフォルダーで、`service.json` の中のパスとは関係ない。続いてサービスは `usage log: not written (usageLog.directory is not set)` と表示する。このチュートリアルでは利用ログを残さないからだ。探す行は `service.started` で、サービスが起動したことを示す。

```text
info: elm-service[1]
      service.started: listening on http://127.0.0.1:8080/, database Sqlite, signing key FAKE-SIGNING-KEY-ID-000000000000000000000S1 (certificates until 01/15/2027 09:30:00 +00:00), in 223 ms
```

ポート 8080 がふさがっていると、サービスは代わりに長いエラーを表示し、終了コード 1 で終わる。その最後の行は `elm-service cannot listen on http://127.0.0.1:8080/: Failed to bind to address http://127.0.0.1:8080: address already in use.` だ。そのポートを使っているプログラムを止めてから、サービスをもう一度起動する。

サービスは動かしたままにして、残りの手順は 1 つ目のターミナルで進める。下の URL には `localhost` ではなく `127.0.0.1` と打つ。アプリのリクエストには、送り先の URL が入る。サービスは自分のアドレス `http://127.0.0.1:8080` へのリクエストだけを受け付け、`http://localhost:8080` へ送ったリクエストは `ELM_PROOF_INVALID` で拒否する。

## 製品とライセンスを作る

製品、その機能、ポリシー、ライセンスは、`elm admin request` で作る。このコマンドは、管理 API にリクエストを 1 つ送る。リクエストの本文は、先に書いておく JSON のファイルだ。レスポンスは、リクエストが作ったか変えたオブジェクトで、`id` から始まる 1 行の JSON になっている。下のファイルの `<product-id>` のような `<name>` は、それぞれ前の手順のレスポンスにある値に置き換える。すべてのルートとメンバーは[管理 API](../reference/administration-api.md)に挙げている。

製品 `acme-cad` を作る。その `code` が、アプリの使う製品 ID になる。

<!-- steps:file path=product.json -->
```json
{"code":"acme-cad","name":"ACME CAD"}
```

<!-- steps:run capture=product-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/products --body product.json --url http://127.0.0.1:8080
```

`elm` は製品を表示する。

```text
{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}
```

`id` が製品の ID で、下の `<product-id>` だ。製品の ID をパスに入れて、製品に機能 `pro` を足す。

<!-- steps:file path=feature.json -->
```json
{"code":"pro","name":"Pro"}
```

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/products/<product-id>/features --body feature.json --url http://127.0.0.1:8080
```

ポリシーは、その下で作るライセンスの規則を持つ。トライアルのポリシーを作る。`licenseKind` を `trial` にすると、トライアル用のポリシーになる。トライアルには終わったあとの猶予期間がないので、`gracePeriod` は 0 にする。トライアルの期間は既定の 14 日だ。

<!-- steps:file path=trial-policy.json -->
```json
{"productId":"<product-id>","name":"Trial","licenseKind":"trial","gracePeriod":0}
```

<!-- steps:run capture=trial-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body trial-policy.json --url http://127.0.0.1:8080
```

`elm` は、既定の値を含むすべての設定とともにポリシーを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000201","productId":"01960000-0000-7000-8000-000000000101","name":"Trial","licenseKind":"trial","binding":"nodeLocked","maxActivations":1,"deactivationLimit":null,"expirationBasis":"fromIssue","duration":null,"tokenLifetime":2592000,"refreshWindow":1728000,"gracePeriod":0,"fingerprintMatching":"any","cloneStrategy":"deny","floating":"none","leaseDuration":600,"overage":null,"borrowLimit":null,"borrowReturnLimit":2,"clientInfoStrategy":"none","trialDuration":1209600,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}
```

`id` が、下の `<trial-policy-id>` だ。製品は、トライアルのポリシーを指定しているときだけトライアルを提供するので、製品の `trialPolicyId` を設定する。

<!-- steps:file path=product-trial.json -->
```json
{"trialPolicyId":"<trial-policy-id>"}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body product-trial.json --url http://127.0.0.1:8080
```

販売するライセンスのポリシーを作る。名前だけを渡すと、ほかは既定の値になる。このポリシーのライセンスは 1 台のデバイスに紐付き、期限がない。

<!-- steps:file path=policy.json -->
```json
{"productId":"<product-id>","name":"Standard"}
```

<!-- steps:run capture=policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body policy.json --url http://127.0.0.1:8080
```

`id` が、下の `<policy-id>` だ。このポリシーの下で、機能 `pro` を持つライセンスを作る。

<!-- steps:file path=license.json -->
```json
{"policyId":"<policy-id>","name":"First customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body license.json --url http://127.0.0.1:8080
```

`elm` はライセンスを表示する。

```text
{"id":"01960000-0000-7000-8000-000000000301","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000202","name":"First customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00000"}
```

最後の `key` がライセンスキーで、下の `<license-key>` だ。ベンダーはこのキーを顧客に渡し、顧客はそれをアプリに入力する。サービスがキーを見せるのはこのレスポンスだけで、保存するのはそのハッシュだけなので、ここでコピーしておく。

## アプリを作る

フォルダー `app` にコンソールアプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。`ElasticLM.Licensing` も一緒に入る。`packages` にあるのはプレリリースのバージョンだけなので、`--prerelease` を付けてプレリリースのバージョンも選べるようにする。`--source packages` を付けると、`dotnet add` はバージョンを `packages` の中だけで探す。

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Client --prerelease --source packages
```

`app/Program.cs` の中身を、次のコードに置き換える。

<!-- sample: samples/ActivateOnline/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

// Starts a trial at elm-service, or activates with a license key, and checks the feature pro.
//
//   app <service-url> <root-public-key.pem> <data-folder> [license-key]
//
// A real app embeds the service URL and the root public key instead of reading them from the command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

if (args.Length is not (3 or 4))
{
    Console.Error.WriteLine("usage: app <service-url> <root-public-key.pem> <data-folder> [license-key]");
    return 2;
}

try
{
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = new Uri(args[0]),
        Trust = TrustStore.Create([File.ReadAllText(args[1])], revokedKeyIds: []),
        DataDirectory = args[2],
    };
    if (!ActivationClient.TryCreate(options, out ActivationClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    // Disposing the client stops its refresh in the background; the activation stays in the data folder.
    await using ActivationClient client = created!;

    // Without a license key, start a trial, which the service gives each machine once; with one, activate.
    bool isTrial = args.Length == 3;
    ActivationResult result = isTrial ? await client.StartTrialAsync() : await client.ActivateAsync(args[3]);
    if (result is ActivationResult.Failed failed)
    {
        return Refused(failed.Error);
    }

    // Check the kept license file for the feature, as the app does at each start, also with no network.
    switch (client.Check(FeatureId, version: null, isTrial ? LicenseKind.Trial : LicenseKind.Standard))
    {
        case LicenseVerificationResult.Valid valid when isTrial:
            Console.WriteLine($"trial: {FeatureId}");
            Console.WriteLine($"expires: {valid.License.ExpiresAt:u}");
            return 0;
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"activated: {FeatureId}");
            Console.WriteLine($"license: {valid.License.LicenseId}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            return Refused(invalid.Error);
        default:
            return 1;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read, a second argument that is not a public key in PEM, or a first argument that is neither an
    // https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// The client, the service, or the check refused: print the error code and the reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
```

アプリは、ルート公開鍵をトラストストア（アプリが信頼するルート鍵の集まり）に読み込み、製品 `acme-cad` と、1 つ目の引数の URL にあるサービスに対する `ActivationClient` を作る。クライアントは、3 つ目の引数のデータフォルダーに自分のファイルを置く。置くのは、サービスがこのアプリのコピーを見分けるためのクライアント自身のインストール鍵と、サービスがクライアントに発行するライセンスファイルだ。

ライセンスキーがなければ、アプリは `StartTrialAsync` を呼び、サービスにトライアルを求める。キーがあれば `ActivateAsync` を呼ぶ。サービスは、インストール鍵とマシンフィンガープリントに紐付けたライセンスファイルを返す。クライアントはそれをルート公開鍵で確かめて保存する。`ActivationResult` は、`Activated` か、エラーを持つ `Failed` のどちらかだ。そのあとアプリは `Check` を呼ぶ。`Check` は、保存したファイルに機能 `pro` があるかを、実際のアプリが起動のたびにするのと同じくオフラインで確かめる。アプリは機能と、トライアルの期限かライセンスの ID を表示する。アプリが動いているあいだ、クライアントは期限が来る前にライセンスファイルを更新する。`await using` はアプリの終わりにこれを止め、アクティベーションはデータフォルダーに残る。このアプリは、チュートリアルを短くするために、サービスの URL とルート公開鍵をコマンドラインから読む。実際のアプリは、両方をコードに埋め込む。

## トライアルを始めてアクティベーションする

クライアントが作るデータフォルダー `client-data` を渡して、トライアルを始める。

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data
```

最初の `dotnet run` はアプリをビルドするので、何も表示しないまま数秒かかる。そのあとアプリは 2 行を表示する。1 行目は次のとおりだ。

<!-- steps:expect -->
```text
trial: pro
```

2 行目はトライアルの終わりで、今から 14 日後の UTC の日時だ。たとえば `expires: 2026-01-29 09:30:00Z` のようになる。サービスは、1 つの製品のトライアルを各マシンに 1 回だけ与える。そのために、トライアルと一緒にマシンフィンガープリントを記録する。

次に、同じデータフォルダーで、ライセンスキーを使ってアクティベーションする。`<license-key>` は、ライセンスのレスポンスにあったキーに置き換える。

<!-- steps:run -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data <license-key>
```

アプリは 2 行を表示する。1 行目は次のとおりだ。

<!-- steps:expect -->
```text
activated: pro
```

2 行目はライセンスの ID で、ライセンスのレスポンスの `id` と同じだ。たとえば `license: 01960000-0000-7000-8000-000000000301` のようになる。`client-data` では、トライアルがアクティベーションに置き換わった。この順序が大事だ。クライアントは、アクティベーションを保存していないときにだけトライアルを始め、アクティベーションは保存したトライアルを置き換えるからだ。

## サービスが拒否したとき

クライアント、サービス、保存したファイルの確認のどれかが拒否すると、アプリはエラーコードと理由を表示し、終了コード 1 で終わる。この節でアプリを動かすと、どれもわざとそのように終わる。そのほとんどは 2 つ目のデータフォルダー `other-data` を使う。クライアントは、`client-data` と同じくこのフォルダーも作る。サービスから見ると、同じマシンにあるアプリの別のインストールだ。

`other-data` でトライアルを始める。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_USED: A fingerprint of this installation already had a trial of the product.
```

`client-data` でもう一度アクティベーションする。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem client-data <license-key>
```

<!-- steps:expect -->
```text
ELM_INVALID_STATE: An activation is kept; deactivate it before activating another license.
```

クライアントは `client-data` のアクティベーションを保存しているので、サービスに何も送らずに拒否する。

次のような、サービスが発行したことのないキーで `other-data` をアクティベーションする。1 文字だけ間違えたキーでも、同じレスポンスになる。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data ELM-ABCDE-FGHJK-MNPQR-STVWX
```

<!-- steps:expect -->
```text
ELM_LICENSE_NOT_FOUND: No license has this key.
```

ライセンスキーの大文字と小文字や `-` の違いは関係ない。`elm-abcde-fghjk-mnpqr-stvwx` は、上と同じキーだ。

サービスが待ち受けていないポートにリクエストを送る。

<!-- steps:run capture=network-error pattern="(ELM_NETWORK_ERROR):" exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8081 root.pub.pem other-data
```

アプリは `ELM_NETWORK_ERROR: The service cannot be reached:` で始まる行を表示する。そのあとには、OS が返した理由が OS の言葉で続く。

製品からトライアルのポリシーを外し、もう一度 `other-data` でトライアルを始める。

<!-- steps:file path=product-no-trial.json -->
```json
{"trialPolicyId":null}
```

<!-- steps:run -->
```shell
elm admin request PATCH /v1/admin/products/<product-id> --body product-no-trial.json --url http://127.0.0.1:8080
```

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- http://127.0.0.1:8080 root.pub.pem other-data
```

<!-- steps:expect -->
```text
ELM_TRIAL_NOT_OFFERED: The product does not offer a trial.
```

サービスはマシンフィンガープリントを見る前にこれを拒否するので、このマシンがトライアルを使ったあとでも、このコードになる。

| コード | 起きたこと | 対処 |
| --- | --- | --- |
| `ELM_TRIAL_USED` | このマシンは、別のデータフォルダーで、すでにこの製品のトライアルを使った。 | トライアルは 1 台のマシンに 1 回だけなので、ライセンスキーでアクティベーションする。 |
| `ELM_INVALID_STATE` | データフォルダーにアクティベーションが保存されているので、クライアントはもう一度アクティベーションしない。 | 何もしなくてよい。アプリはアクティベーション済みで、起動のたびに `Check` で確かめる。 |
| `ELM_LICENSE_NOT_FOUND` | サービスに、このキーのライセンスがない。 | ライセンスのレスポンスから、キーをコピーし直す。 |
| `ELM_NETWORK_ERROR` | サービスに接続できない。サービスが動いていないか、URL が間違っている。メッセージは `The service cannot be reached:` で始まる。 | [サービスを起動する](#サービスを起動する)のとおりにサービスを起動し、`http://127.0.0.1:8080` を渡す。 |
| `ELM_TRIAL_NOT_OFFERED` | 製品にトライアルのポリシーが指定されていない。 | [製品とライセンスを作る](#製品とライセンスを作る)のとおりに、製品の `trialPolicyId` を設定する。 |

トライアルには、マシンフィンガープリントも 1 つ以上要る。`/etc/machine-id` のない Linux のコンテナーのように、マシンフィンガープリントを読めない場所では、サービスはトライアルを `ELM_FINGERPRINT_UNAVAILABLE` で拒否する。アプリが `usage: app <service-url> <root-public-key.pem> <data-folder> [license-key]` と表示したときは、引数が 3 つでも 4 つでもなかった。`error: A root key is not a P-256 public key in PEM.` で始まる行を表示したときは、2 つ目の引数が公開鍵のファイルではない。`error: Invalid URI` は、1 つ目の引数が URL ではないことを示し、`error: The service URL must be an absolute https URL, or http on a loopback address. (Parameter 'options')` は、ループバックでないホストへの http の URL であることを示す。どの場合も、引数の順序と URL を確かめる。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

## サービスを止める

2 つ目のターミナルで Ctrl+C を押して、サービスを止める。そのあと、両方のターミナルを閉じる。`ELM_API_KEY` と `ELM_SIGNING_PASSPHRASE` はそのターミナルの中にしかないので、これで消える。

最初からやり直すには、サービスを止めた状態で、チュートリアルのフォルダーを丸ごと消す。そうすれば、`service.db` にあるサービスのトライアルとアクティベーションの記録と、`client-data` と `other-data` にあるクライアントのアクティベーションと鍵の両方が消える。そのあと、`tools` と `packages` へのダウンロードも含めて、[始める前に](#始める前に)からやり直す。

## 作ったもの

ルート鍵と署名鍵を作り、製品、トライアルのポリシー、ライセンスを持つ `elm-service` を準備し、自分のマシンでトライアルを始めてから、ライセンスキーでアクティベーションする .NET のアプリを書いた。実際の運用では、ベンダーがサービスを https で動かし、販売や課金のシステムが管理 API でライセンスを作る。

さらに知るには、次のページを読む。

- [アクティベーション](../explanation/activation.md): 製品、ポリシー、ライセンス、トークンの更新、アクティベーション解除、トライアル。
- [elm-service の設定](../reference/service-configuration.md): サービスのすべての設定と、そのシークレット。
- [管理 API](../reference/administration-api.md): すべてのルートと、そのリクエストとレスポンス。
- [elm コマンドリファレンス](../reference/elm-cli.md): `elm secret create`、`elm admin request`、ほかのコマンド。
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient`、そのオプション、各メソッドが返すもの。
