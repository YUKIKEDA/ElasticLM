---
sidebar_position: 7
---

# ライセンスを失効させる

このガイドは、`elm-service` を動かしていて、ライセンスを完全に終わらせるベンダーに向けている。契約が終わったとき、支払いが返金されたとき、購入していない人たちにライセンスキーが共有されたときに使う。失効したライセンスには、新しいライセンスファイルもリースも出ず、再開もできない。しばらく止めるだけなら、代わりに [`POST /v1/admin/licenses/{id}/suspend`](../reference/administration-api.md#post-v1adminlicensesidsuspend) で一時停止する。失効がデバイスに届くのは、デバイスが次にアクティベーションかトークンの更新をするときだ。デバイスがすでに保存しているライセンスファイルは、その期限まで有効なままだ。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。それぞれの状態で何ができるかは[ライセンスの状態](../explanation/activation.md#ライセンスの状態)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下のアプリは、チュートリアルのフォルダーにあるルート公開鍵 `root.pub.pem`、2 つの ElasticLM のパッケージが入ったフォルダー `packages`、ファイル `nuget.config` も使う。

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

下の出力にある ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。ここでは、デバイスとネットワークにつながったコンピューターが同じマシンだ。

## 失効させるライセンスを準備する

失効で何が起きるかを見るため、この節では、ライセンスを作り、[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md)のオフラインのやり取りで、そのライセンスでデバイスをアクティベーションする。このガイドでも、そのガイドのアプリを使う。手元にあるライセンスを失効させるときは、[`GET /v1/admin/licenses`](../reference/administration-api.md#get-v1adminlicenses) でその `id` を調べ、[このライセンスを失効させる](#このライセンスを失効させる)まで飛ばす。

ポリシーと、その下で機能 `pro` を持つライセンスを作る。ポリシーには名前しかないので、ほかは既定の値になる。

<!-- steps:file path=revoke-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, revocation check"}
```

<!-- steps:run capture=revoke-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body revoke-policy.json --url http://127.0.0.1:8080
```

レスポンスの最初の `id` がポリシーの ID で、次のファイルの `<revoke-policy-id>` だ。

<!-- steps:file path=revoke-license.json -->
```json
{"policyId":"<revoke-policy-id>","name":"Former customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=revoke-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body revoke-license.json --url http://127.0.0.1:8080
```

レスポンスの最後の `key` がライセンスキーで、下の `<revoke-license-key>` だ。

[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md#アプリを作る)や、[署名鍵を入れ替える](rotate-the-signing-key.md)のような別のガイドで `offline-app` を作ったなら、それを使い、下の `customer-data` のアクティベーションまで飛ばす。`dotnet new` は、同じアプリをもう一度作るのを拒否するからだ。作っていなければ、アプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

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

ライセンスキーを使って、データフォルダー `customer-data` をアクティベーションする。リクエストを作り（`request` は `CreateActivationRequestAsync` を呼ぶ）、`elm offline process` で提出して、レスポンスを取り込む（`import` は `ImportResponseAsync` を呼び、続けて `Check` を呼ぶ）。アプリが表示するライセンスの ID を控える。

<!-- steps:run capture=revoke-license-id pattern="license: ([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
dotnet run --project offline-app -- root.pub.pem customer-data request <revoke-license-key> customer.elmreq
elm offline process --url http://127.0.0.1:8080 --in customer.elmreq --out customer.elmresp
dotnet run --project offline-app -- root.pub.pem customer-data import customer.elmresp
```

アプリはリクエストファイルを表示する。`elm offline process` は、アクティベーション、ライセンス、レスポンスのシーケンス番号を表示する。そのあとアプリは、機能、ライセンスの ID、シーケンス番号、期限を表示する。

```text
file: customer.elmreq
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000304, seq 1: customer.elmresp
activated: pro
license: 01960000-0000-7000-8000-000000000304
sequence: 1
expires: 2026-02-14 09:30:00Z
```

下の `<revoke-license-id>` がその ID で、ライセンスの `id` だ。レスポンスを取り込んだので、`customer.elmreq` を削除する。リクエストファイルにはライセンスキーが入っている。

## このライセンスを失効させる

ライセンスを、その ID で失効させる。リクエストに本文はない。

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/licenses/<revoke-license-id>/revoke --url http://127.0.0.1:8080
```

`elm` はライセンスを表示する。`state` は `revoked` になり、`revokedAt` に日時が入っている。

```text
{"id":"01960000-0000-7000-8000-000000000304","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Former customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"revoked","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":"2026-01-15T09:31:00Z","meta":{},"deactivations":0}
```

失効は取り消せない。ライセンスの再開や延長は `409` と `ELM_CONFLICT` で拒否され、もう一度失効させても何も変わらない。管理画面のライセンスのページからも失効させられる。

## デバイスが拒否されることを確かめる

サービスは、デバイスの台数を数える前に、そのライセンスでの新しいアクティベーションをすべて拒否する。別のデバイスがするように、新しいデータフォルダー `new-device-data` でアクティベーションのリクエストを作り、提出する。

<!-- steps:run capture=activation-refused stream=stderr pattern="(ELM_LICENSE_REVOKED):" exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem new-device-data request <revoke-license-key> new-device.elmreq
elm offline process --url http://127.0.0.1:8080 --in new-device.elmreq --out new-device.elmresp
```

アプリはサービスに問い合わせずにリクエストファイルを作る。`elm offline process` はサービスの拒否を表示し、終了コード 1 で終わる。

```text
file: new-device.elmreq
error: ELM_LICENSE_REVOKED: The license is revoked.
```

サービスは、アクティベーション済みのデバイスのトークンの更新も同じように拒否する。`customer-data` の更新のリクエストを作り（`refresh` は `CreateRefreshRequestAsync` を呼ぶ）、提出する。

<!-- steps:run capture=refresh-refused stream=stderr pattern="(ELM_LICENSE_REVOKED):" exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem customer-data refresh customer-refresh.elmreq
elm offline process --url http://127.0.0.1:8080 --in customer-refresh.elmreq --out customer-refresh.elmresp
```

```text
file: customer-refresh.elmreq
error: ELM_LICENSE_REVOKED: The license is revoked.
```

`new-device.elmreq` と `customer-refresh.elmreq` を削除する。これらへのレスポンスは来ず、ファイルにはライセンスキーが入っている。

オンラインでアクティベーションやトークンの更新をするアプリは、`ActivateAsync` や `RefreshAsync` から同じコードを受け取る。自分からトークンを更新するクライアントは、状態 `Refused` になる。

## 失効が届かないもの

- **デバイスにあるライセンスファイル。** デバイスは手元のライセンスファイルを持ち続け、`Check` はそのファイルを、期限まで、さらに期限のあとは猶予期間のあいだ受け入れる。既定の値では、最長で 30 日と、さらに 7 日だ。デバイスが取得する失効リストはないので、デバイスには知らされない。失効をもっと早くデバイスに届けるには、ライセンスを販売する前に、[ポリシーを設定する](set-up-a-policy.md)のとおり、`tokenLifetime` と `gracePeriod` を短くしたポリシーをライセンスに使う。この限界は[セキュリティモデル](../explanation/security-model.md#ライセンシーのマシン上)で説明している。
- **手作業で発行したライセンスファイル。** `elm license issue` が署名したライセンスファイルや、サービスが [`POST /v1/admin/licenses/{id}/file`](../reference/administration-api.md#post-v1adminlicensesidfile) で発行した、マシンに紐付けないライセンスファイルは、それ自身の期限まで有効なままだ。期限のないファイルを止めるには、それに署名した鍵を失効させるしかなく、そうするとその鍵のほかのファイルもすべて止まる。[署名鍵を失効させる](revoke-a-signing-key.md)を読む。

| コード | 起きたこと | 対処 |
| --- | --- | --- |
| `ELM_LICENSE_REVOKED` | ライセンスが失効している。サービスは、そのライセンスのアクティベーション、トークンの更新、リースを拒否する。 | デバイスでできることはない。顧客には別のライセンスが要る。 |
| `ELM_LICENSE_NOT_FOUND` | `elm admin request` が `404` とともにこれを受け取った。パスの ID を持つライセンスがない。 | `GET /v1/admin/licenses` でライセンスの `id` を調べる。 |

すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

## 関連するページ

- [ライセンスの状態](../explanation/activation.md#ライセンスの状態): 有効、一時停止、期限切れ、失効。
- 管理 API の [`POST /v1/admin/licenses/{id}/revoke`](../reference/administration-api.md#post-v1adminlicensesidrevoke) と [`POST /v1/admin/licenses/{id}/suspend`](../reference/administration-api.md#post-v1adminlicensesidsuspend)。
- [トークンの更新](../explanation/activation.md#トークンの更新): 失効がデバイスに届くとき。
