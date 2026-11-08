---
sidebar_position: 6
---

# 署名鍵を入れ替える

このガイドは、`elm-service` を動かしていて、サービスがライセンスファイルに署名する鍵を入れ替えるベンダーに向けている。定期的に入れ替えるときや、鍵の鍵証明書が切れる前に入れ替えるときに使う。鍵が漏れたおそれがあるときは、代わりに[署名鍵を失効させる](revoke-a-signing-key.md)に従う。そのガイドでは、1 回の再起動で鍵を入れ替え、古い鍵を失効させる。アプリが信頼しているのは署名鍵ではなくルート鍵なので、アプリの更新は要らない。古い鍵が署名したライセンスファイルも使えるままだ。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品があり、サービスの署名鍵を証明したルート鍵が手元にある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。古いファイルが使えるままになる理由は[署名鍵を入れ替える](../explanation/trust-chain.md#署名鍵を入れ替える)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、ルート鍵 `root.key.pem`、ルート公開鍵 `root.pub.pem`、サービスの設定ファイル `service.json` を使う。下のアプリは、2 つの ElasticLM のパッケージが入ったフォルダー `packages` と、チュートリアルのフォルダーにあるファイル `nuget.config` も使う。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。`elm-service setup` は、サービスが動いているときも含め、実行するたびに新しいキーを作る。前に作ったキーも有効なままだ。

例では、チュートリアルの値を使う。製品 `acme-cad`、その機能 `pro`、`http://127.0.0.1:8080` のサービスだ。自分の値に置き換える。下のファイルの `<product-id>` は製品の `id` で、次のコマンドで製品の `code` と一緒に一覧できる。

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

`elm` は製品の一覧の 1 ページを表示する。

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}]}
```

下の出力にある鍵 ID、ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。ここでは、デバイスとネットワークにつながったコンピューターが同じマシンだ。

## 今の鍵が署名したデバイスを準備する

入れ替えのあともデバイスが動き続けることを確かめるため、サービスがまだ今の鍵で署名しているうちに、デバイスを 1 つアクティベーションしておく。この節では、ライセンスを作り、[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md)のオフラインのやり取りでデータフォルダーをアクティベーションする。このガイドでも、そのガイドのアプリを使う。デバイスをすでにアクティベーションしているなら、[新しい署名鍵を作る](#新しい署名鍵を作る)まで飛ばす。

ポリシーと、その下で機能 `pro` を持つライセンスを作る。ポリシーには名前しかないので、ほかは既定の値になる。

<!-- steps:file path=rotation-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, rotation check"}
```

<!-- steps:run capture=rotation-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body rotation-policy.json --url http://127.0.0.1:8080
```

レスポンスの最初の `id` がポリシーの ID で、次のファイルの `<rotation-policy-id>` だ。

<!-- steps:file path=rotation-license.json -->
```json
{"policyId":"<rotation-policy-id>","name":"Rotation customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=rotation-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body rotation-license.json --url http://127.0.0.1:8080
```

レスポンスの最後の `key` がライセンスキーで、下の `<rotation-license-key>` だ。

[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md#アプリを作る)で `offline-app` を作ったなら、それを使い、下の `rotation-data` のアクティベーションまで飛ばす。`dotnet new` は、同じアプリをもう一度作るのを拒否するからだ。作っていなければ、アプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` は、パッケージのコピーを NuGet のグローバルパッケージフォルダーにも置く。その場所は `dotnet nuget locals global-packages --list` で表示でき、環境変数 `NUGET_PACKAGES` で場所を変えたときも同じように表示できる。このフォルダーには、このマシンのすべてのプロジェクトのパッケージが入っている。ElasticLM を試し終えたら、そこにある ElasticLM のフォルダー `elasticlm.licensing` と `elasticlm.client` だけを削除する。

`offline-app/Program.cs` の中身を、次のコードに置き換える。[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md#アプリを作る)と同じコードだ。

<!-- sample: samples/ActivateOffline/Program.cs -->
<!-- steps:file path=offline-app/Program.cs -->
```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

// Activates and refreshes an installation of acme-cad with files that a person carries to elm-service and back, checks the
// feature pro, and deactivates it online or with a deactivation proof.
//
//   app <root-public-key.pem> <data-folder> request <key> <request-file>
//   app <root-public-key.pem> <data-folder> refresh <request-file>
//   app <root-public-key.pem> <data-folder> import <response-file>
//   app <root-public-key.pem> <data-folder> deactivate <service-url>
//   app <root-public-key.pem> <data-folder> deactivate-proof <proof-file>
//   app <root-public-key.pem> <data-folder> forget
//
// A real app embeds the root public key, and the service URL if it deactivates online, instead of reading them from the
// command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

string? command = args.Length > 2 ? args[2] : null;
int length = command switch
{
    "request" => 5,
    "refresh" or "import" or "deactivate" or "deactivate-proof" => 4,
    "forget" => 3,
    _ => -1,
};
if (args.Length != length)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder> <command> [arguments]");
    Console.Error.WriteLine(
        "commands: request <key> <file>, refresh <file>, import <file>, deactivate <service-url>, deactivate-proof <file>, forget"
    );
    return 2;
}

try
{
    // Only the online deactivation reaches the service; the other commands work with files alone.
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = command == "deactivate" ? new Uri(args[3]) : null,
        Trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []),
        DataDirectory = args[1],
    };
    if (!ActivationClient.TryCreate(options, out ActivationClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    await using ActivationClient client = created!;

    return command switch
    {
        "request" => await RequestAsync(client, args[3], args[4]),
        "refresh" => await RefreshAsync(client, args[3]),
        "import" => await ImportAsync(client, args[3]),
        "deactivate" => await DeactivateAsync(client),
        "deactivate-proof" => await DeactivationProofAsync(client, args[3]),
        _ => await ForgetAsync(client),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read or written, a first argument that is not a public key in PEM, or a service URL that is
    // neither an https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Makes the activation request. It holds the license key, so the file must not be left where others can read it.
static async Task<int> RequestAsync(ActivationClient client, string licenseKey, string file) =>
    Save(await client.CreateActivationRequestAsync(licenseKey), file);

// Makes the refresh request of the kept activation.
static async Task<int> RefreshAsync(ActivationClient client, string file) => Save(await client.CreateRefreshRequestAsync(), file);

// Writes the request or the deactivation proof to the file that the user carries to the service.
static int Save(OfflineRequestResult result, string file)
{
    switch (result)
    {
        case OfflineRequestResult.Created created:
            File.WriteAllText(file, created.File);
            Console.WriteLine($"file: {file}");
            if (created.Replaced)
            {
                // The service would take an answer to the earlier request for a clone after this one.
                Console.WriteLine("replaced: discard the earlier request file");
            }

            return 0;
        case OfflineRequestResult.Failed failed:
            return Refused(failed.Error);
        default:
            return 1;
    }
}

// Imports the service's answer, and checks the kept license file for the feature, as the app does at each start.
static async Task<int> ImportAsync(ActivationClient client, string file)
{
    ActivationResult imported = await client.ImportResponseAsync(File.ReadAllText(file));
    if (imported is ActivationResult.Failed failed)
    {
        return Refused(failed.Error);
    }

    switch (client.Check(FeatureId, version: null))
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"activated: {FeatureId}");
            Console.WriteLine($"license: {valid.License.LicenseId}");
            Console.WriteLine($"sequence: {valid.License.Sequence}");
            Console.WriteLine($"expires: {valid.License.ExpiresAt:u}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            return Refused(invalid.Error);
        default:
            return 1;
    }
}

// Deactivates online: the service frees the activation's place, and the client removes the kept license file.
static async Task<int> DeactivateAsync(ActivationClient client)
{
    if (await client.DeactivateAsync() is LicenseError error)
    {
        return Refused(error);
    }

    Console.WriteLine("deactivated");
    return 0;
}

// Makes the deactivation proof. The client removes the kept license file now, and refuses a new activation until the proof
// is forgotten.
static async Task<int> DeactivationProofAsync(ActivationClient client, string file) => Save(await client.CreateDeactivationProofAsync(), file);

// Forgets the deactivation proof, once the user has handed it in to the service.
static async Task<int> ForgetAsync(ActivationClient client)
{
    if (await client.ForgetDeactivationProofAsync() is LicenseError error)
    {
        return Refused(error);
    }

    Console.WriteLine("forgotten");
    return 0;
}

// The client or the check refused: print the error code and the reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
```

ライセンスキーを使って、データフォルダー `rotation-data` をアクティベーションする。リクエストを作り（`request` は `CreateActivationRequestAsync` を呼ぶ）、`elm offline process` で提出して、レスポンスを取り込む（`import` は `ImportResponseAsync` を呼び、続けて `Check` を呼ぶ）。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem rotation-data request <rotation-license-key> rotation.elmreq
elm offline process --url http://127.0.0.1:8080 --in rotation.elmreq --out rotation.elmresp
dotnet run --project offline-app -- root.pub.pem rotation-data import rotation.elmresp
```

アプリはリクエストファイルを表示する。`elm offline process` は、アクティベーション、ライセンス、レスポンスのシーケンス番号を表示する。そのあとアプリは、機能、ライセンスの ID、ライセンスファイルのシーケンス番号、ライセンスファイルの期限を表示する。

```text
file: rotation.elmreq
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000303, seq 1: rotation.elmresp
activated: pro
license: 01960000-0000-7000-8000-000000000303
sequence: 1
expires: 2026-02-14 09:30:00Z
```

<!-- steps:expect -->
```text
activated: pro
sequence: 1
```

このライセンスファイルには、今の署名鍵が署名した。レスポンスを取り込んだので、`rotation.elmreq` を削除する。リクエストファイルにはライセンスキーが入っている。

## 新しい署名鍵を作る

新しい署名鍵は、サービスを動かすマシンで、新しいファイル名で作る。こうすると、サービスを再起動するまで、今の鍵のファイルはそのまま残る。新しい鍵には、ルート鍵とも、漏れたおそれのあるどの鍵とも違う新しいパスフレーズを付ける。チュートリアルですべての鍵に同じパスフレーズを使ったのは、手順を短くするためだけだ。まず、ターミナルから `ELM_KEY_PASSPHRASE` を消す。こうすると `elm` は、変数にあるパスフレーズを使わずに、新しいパスフレーズを尋ねる。PowerShell では `Remove-Item Env:ELM_KEY_PASSPHRASE` と打ち、Bash か zsh では `unset ELM_KEY_PASSPHRASE` と打つ。そのあと鍵を作る。

<!-- steps:run -->
```shell
elm key signing create --out signing-2.key.pem --public-out signing-2.pub.pem
```

`elm` は新しいパスフレーズを 2 回尋ね、次のように表示する。

```text
Passphrase for the new key:
Repeat the passphrase:
key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S2
private key (encrypted): signing-2.key.pem
public key: signing-2.pub.pem
```

サービスは、署名鍵のパスフレーズを、サービスを動かすターミナルの `ELM_SIGNING_PASSPHRASE` から読む。下の再起動の前に、これを新しいパスフレーズにする。

新しい鍵を、ライセンスとリースへの署名用に、ルート鍵で証明する。この 2 つが、サービスに要る用途だ。設定で `usageLog.directory` を指定しているときは、サービスは利用ログにも署名するので、鍵には `--use license,lease,log` が要る。証明は、[ルート鍵を安全に保管する](keep-the-root-key-safe.md)のとおり、ルート鍵を置いたオフラインのマシンでする。新しい公開鍵 `signing-2.pub.pem` をそこへ運び、そこでこのコマンドを実行し、持ち帰るのは鍵証明書 `signing-2.cert` だけにする。ルート鍵を、サービスを動かすマシンに持ち込んではならない。公開鍵と鍵証明書は秘密ではない。

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2.cert
```

`elm` はルート鍵のパスフレーズを尋ね、次のように表示する。

```text
Passphrase for 'root.key.pem':
certificate: signing-2.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S2
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license, lease
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

<!-- steps:expect -->
```text
certificate: signing-2.cert
uses: license, lease
```

`signing key ID:` が新しい鍵の ID だ。鍵証明書は、`--days` の既定の値である 1 年のあいだ有効だ。

## 新しい鍵でサービスを再起動する

`service.json` の `signing` を、新しい鍵とその鍵証明書に変える。ほかの設定はそのままにする。チュートリアルの設定なら、ファイルは次のようになる。

<!-- steps:file path=service.json -->
```json
{
  "database": {
    "provider": "sqlite",
    "connectionFile": "database.connection"
  },
  "signing": {
    "key": "signing-2.key.pem",
    "certificates": ["signing-2.cert"]
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

サービスは署名鍵を起動時にしか読まないので、再起動する。2 つ目のターミナルで Ctrl+C を押して、サービスを止める。[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)と同じ方法で、新しい鍵のパスフレーズを `ELM_SIGNING_PASSPHRASE` に読み込む。PowerShell では `$env:ELM_SIGNING_PASSPHRASE = Read-Host -MaskInput` と打ち、Bash か zsh では `read -rs ELM_SIGNING_PASSPHRASE; export ELM_SIGNING_PASSPHRASE` と打つ。続けてパスフレーズを打ち、Enter を押す。そのあとサービスを起動する。

<!-- steps:background ready=http://127.0.0.1:8080/health -->
```shell
elm-service --config service.json
```

サービスが止まっているあいだ、オンラインでアクティベーションやトークンの更新をするアプリは `ELM_NETWORK_ERROR` を受け取る。自分からトークンを更新するクライアントは、1 時間後にもう一度試す。デバイスが保存しているライセンスファイルは使えるままだ。サービスのログの `service.started` の行に、新しい鍵が出る。

```text
info: elm-service[1]
      service.started: listening on http://127.0.0.1:8080/, database Sqlite, signing key FAKE-SIGNING-KEY-ID-000000000000000000000S2 (certificates until 01/15/2027 09:30:00 +00:00), in 179 ms
```

サービスが起動しないときは、理由を表示する。たとえば、次の場合は終了コード 1 で終わる。

| 最後の行 | 起きたこと | 対処 |
| --- | --- | --- |
| `The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.` | `ELM_SIGNING_PASSPHRASE` に新しい鍵のものとは違うパスフレーズが入っているか、`signing.key` が署名鍵でないファイルを指している。 | 新しい鍵のパスフレーズを `ELM_SIGNING_PASSPHRASE` に読み込み、`signing.key` を確かめる。 |
| `Set ELM_SIGNING_PASSPHRASE or signing.passphraseFile to the signing key's passphrase.` | サービスのターミナルで `ELM_SIGNING_PASSPHRASE` が設定されていない。 | 上のとおりに、新しい鍵のパスフレーズを `ELM_SIGNING_PASSPHRASE` に読み込む。 |
| `The signing key or a certificate file cannot be read: Could not find file '…'.` | `signing` にあるファイルが、そのパスにない。メッセージには完全なパスが出る。 | パスを直す。パスは、サービスを起動するフォルダーからの相対パスだ。 |
| `No certificate that the trust store vouches for lets the signing key FAKE-SIGNING-KEY-ID-000000000000000000000S2 sign for License, Lease at 2026-01-15T09:30:00Z.` | `signing.certificates` のどの鍵証明書も、この鍵をこれらの用途で今は証明していない。別の鍵の鍵証明書である、サービスに要る用途を付けずに証明した（この行は `License` と `Lease` の両方が足りない場合で、`--use lease` だけが抜けているときは `Lease` だけが出る）、まだ有効になっていないか期限が切れている、ルート鍵が `trust.rootKeys` にない、のどれかだ。鍵の ID が `trust.revokedKeyIds` にあるときも、同じ行が出る。 | 上のとおりに鍵をもう一度証明し、`signing.key` と `signing.certificates` が新しい鍵のファイルを指していることを確かめる。 |

カンマが抜けているなど、設定ファイルそのものに誤りがあると、終了コード 2 で終わり、`The settings file 'service.json' is not valid:` で始まる行が出る。終了コードは[コマンドラインと終了コード](../reference/service-configuration.md#コマンドラインと終了コード)に挙げている。下で古い鍵を削除するまでは、古い鍵に戻せる。古い `signing` の設定を戻し、`ELM_SIGNING_PASSPHRASE` をそのパスフレーズにして、サービスをもう一度起動し直す。

## 使われている鍵を確かめる

サービスに、署名鍵の状態を尋ねる。

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/signing-key --url http://127.0.0.1:8080
```

`elm` は次のように表示する。

```text
{"keyId":"FAKE-SIGNING-KEY-ID-000000000000000000000S2","uses":["license","lease"],"expiresAt":"2027-01-15T09:30:00Z","expiresSoon":false}
```

`keyId` を、`elm key certify` が新しい鍵について表示した `signing key ID:` と比べる。両者は同じだ。`expiresAt` は新しい鍵証明書の期限だ。これ以降、サービスは新しいライセンスファイルにすべて新しい鍵で署名する。

## デバイスが動き続けることを確かめる

古い鍵が署名したライセンスファイルには古い鍵の鍵証明書が入っているので、アプリはそのファイルを、その鍵証明書が切れたあとも受け入れ続ける。デバイスが新しい鍵に移るのは、次にトークンを更新したときだ。オンラインのデバイスは、自分でトークンを更新する。ネットワークにつながらないデバイスは、ここでのように更新のファイルを運んだときに移る。`rotation-data` のデバイスのトークンを更新する。更新のリクエストを作り（`refresh` は `CreateRefreshRequestAsync` を呼ぶ）、提出して、レスポンスを取り込む。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem rotation-data refresh rotation-refresh.elmreq
elm offline process --url http://127.0.0.1:8080 --in rotation-refresh.elmreq --out rotation-refresh.elmresp
dotnet run --project offline-app -- root.pub.pem rotation-data import rotation-refresh.elmresp
```

アプリは、次のシーケンス番号と新しい期限を表示する。

```text
file: rotation-refresh.elmreq
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000303, seq 2: rotation-refresh.elmresp
activated: pro
license: 01960000-0000-7000-8000-000000000303
sequence: 2
expires: 2026-02-14 09:31:00Z
```

<!-- steps:expect -->
```text
activated: pro
sequence: 2
```

このライセンスファイルには新しい鍵が署名し、アプリは同じトラストストアでそれを受け入れた。ルート鍵が両方の鍵を証明しているからだ。レスポンスを取り込んだので、`rotation-refresh.elmreq` を削除する。

## 古い鍵を使わないようにする

サービスは、もう `signing.key.pem` を読まない。新しい鍵が動いたら、次のようにする。

- 古い公開鍵 `signing.pub.pem` を残しておくか、`elm key thumbprint signing.pub.pem` が表示する鍵 ID を記録しておく。いつかその鍵を失効させるときに、ID が要る。
- 古い鍵証明書は、もう `signing.certificates` にない。今そこにあるのは新しい鍵の鍵証明書だけだ。古い鍵証明書を戻さない。古い鍵が署名したライセンスファイルには、古い鍵証明書のコピーがそれぞれ入っている。
- 古い秘密鍵 `signing.key.pem` を、すべてのコピーとともに削除する。こうすると、鍵が漏れることも、間違ってもう一度サービスに渡すこともなくなる。

漏れたおそれがない限り、古い鍵を失効させない。トラストストアで鍵 ID を失効として載せたアプリは、その鍵が署名したライセンスファイルをすべて拒否する。まだトークンを更新していないデバイスのファイルも拒否される。漏れたおそれがあるときは、[署名鍵を失効させる](revoke-a-signing-key.md)に従う。

## 同じ鍵の鍵証明書を更新する

署名鍵の鍵証明書は、有効期間（既定では 1 年）が過ぎると切れる。そのあとは、サービスはライセンスファイルを発行できず、リクエストは `500` と `ELM_INTERNAL_ERROR` で失敗する。同じ鍵を使い続けるには、それより前に、その公開鍵をもう一度証明する（`elm key certify --root root.key.pem --public signing-2.pub.pem --use license,lease --out signing-2-renewed.cert` など）。新しいファイルを `signing.certificates` に足し（`["signing-2.cert", "signing-2-renewed.cert"]`。ファイルは 1〜4 個）、古い鍵証明書が切れる前にサービスを再起動する。`GET /v1/admin/signing-key` は、残りが 30 日を切ると `expiresSoon` に `true` を返す。

`--not-before` で開始日時を未来にした鍵証明書は、前もって `signing.certificates` に入れておける。ただし、サービスがその鍵証明書を数えるのは、開始日時のあとに起動したときだけだ。そのため、新しい鍵証明書の開始日時のあと、古い鍵証明書が切れる前に、サービスをもう一度起動し直す。

## 関連するページ

- [鍵の入れ替え](../explanation/trust-chain.md#鍵の入れ替え): 鍵証明書の更新、署名鍵の入れ替え、ルート鍵の入れ替え。
- `elm-service` の設定の [`signing` と `trust`](../reference/service-configuration.md#signing) と、その[終了コード](../reference/service-configuration.md#コマンドラインと終了コード)。
- 管理 API の [`GET /v1/admin/signing-key`](../reference/administration-api.md#get-v1adminsigning-key)。
- [`elm key signing create`](../reference/elm-cli.md#elm-key-signing-create) と [`elm key certify`](../reference/elm-cli.md#elm-key-certify): すべてのオプション。
