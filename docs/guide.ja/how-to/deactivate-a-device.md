---
sidebar_position: 4
---

# デバイスのアクティベーションを解除する

このガイドは、`elm-service` を運用するベンダーと、アプリを作る開発者に向けている。顧客がアプリを別のデバイスに移すときや、デバイスをなくしたときのように、デバイスからライセンスのアクティベーションを外したいときに使う。アクティベーション解除をすると、ライセンスのアクティベーション数の上限のうち、そのデバイスの枠が空く。方法は 3 つある。アプリが自分でオンラインで解除する方法、デバイスがネットワークにつながらないときにアプリがアクティベーション解除のプルーフを作り、人がそれをサービスへ運ぶ方法、ベンダーの管理者がデバイスなしで解除する方法だ。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。それぞれの方法が何をするかは[アクティベーション解除](../explanation/activation.md#アクティベーション解除)で説明している。

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

下の出力にある ID、ライセンスキー、サムプリント、フィンガープリント、日時は架空の値だ。手元のマシンでは毎回違う値になる。

## 解除するデバイスを準備する

それぞれの方法を試すには、アクティベーションしたデバイスが要る。この節では、ライセンスを作り、[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md)のアプリでデータフォルダーをアクティベーションする。このガイドでは、アプリ自身によるアクティベーション解除にもこのアプリを使う。デバイスがすでにアクティベーションされているなら、必要な方法の節まで飛ばす。

ポリシーと、その下で機能 `pro` を持つライセンスを作る。ポリシーは `deactivationLimit` で、デバイスがライセンスのアクティベーションを解除できる回数の合計を制限する。管理者によるアクティベーション解除は数えない。次のポリシーでは 3 回まで許す。

<!-- steps:file path=moving-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, 3 moves","deactivationLimit":3}
```

<!-- steps:run capture=moving-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body moving-policy.json --url http://127.0.0.1:8080
```

<!-- steps:file path=moving-license.json -->
```json
{"policyId":"<moving-policy-id>","name":"Moving customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=moving-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body moving-license.json --url http://127.0.0.1:8080
```

レスポンスの最後の `key` がライセンスキーで、下の `<moving-license-key>` だ。

[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md#アプリを作る)で `offline-app` を作ったなら、それを使い、下の `device-data` のアクティベーションまで飛ばす。`dotnet new` は、同じアプリをもう一度作るのを拒否するからだ。作っていなければ、アプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` は、パッケージのコピーを NuGet のグローバルパッケージフォルダーにも置く。その場所は `dotnet nuget locals global-packages --list` で表示でき、環境変数 `NUGET_PACKAGES` で場所を変えたときも同じように表示できる。このフォルダーには、このマシンのすべてのプロジェクトのパッケージが入っている。ElasticLM を試し終えたら、そこにある ElasticLM のフォルダー `elasticlm.licensing` と `elasticlm.client` だけを削除する。

`offline-app/Program.cs` の中身を、次のコードに置き換える。[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md#アプリを作る)と同じコードだ。下の手順では、各コマンドが呼び出す `ActivationClient` のメソッドを示す。

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

ライセンスキーを使い、オフラインのやり取りでデータフォルダー `device-data` をアクティベーションする。リクエストを作り（`request` は `CreateActivationRequestAsync` を呼ぶ）、それを提出して、レスポンスを取り込む（`import` は `ImportResponseAsync` を呼ぶ）。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> first.elmreq
elm offline process --url http://127.0.0.1:8080 --in first.elmreq --out first.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import first.elmresp
```

<!-- steps:expect -->
```text
activated: pro
```

## アプリからアクティベーションを解除する

デバイスがサービスにつながるときは、アプリが `DeactivateAsync` を呼ぶ。そのためにクライアントにはサービスの URL が要るので、コマンド `deactivate` は URL を引数に取り、それを使ってクライアントを作る。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data deactivate http://127.0.0.1:8080
```

<!-- steps:expect -->
```text
deactivated
```

サービスはアクティベーションの枠を空けて、ライセンスのアクティベーション解除を 1 回と数え、クライアントは保存していたライセンスファイルを削除する。インストール鍵はデータフォルダーに残るので、アプリの同じコピーはもう一度アクティベーションできる。ライセンスが `deactivationLimit` に達しているときは、サービスは `ELM_DEACTIVATION_LIMIT` を返し、何も削除されない。その場合も、下のとおり、管理者はデバイスのアクティベーションを解除できる。

## ネットワークにつながらないデバイスのアクティベーションを解除する

サービスにつながらないデバイスは、代わりにアクティベーション解除のプルーフを作る。人がサービスへ運ぶファイルだ。試すために、`device-data` をもう一度アクティベーションする。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> second.elmreq
elm offline process --url http://127.0.0.1:8080 --in second.elmreq --out second.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import second.elmresp
```

<!-- steps:expect -->
```text
activated: pro
```

コマンド `deactivate-proof` は `CreateDeactivationProofAsync` を呼び、`request` がリクエストファイルを書き出すのと同じように、返ってきたプルーフをファイルに書き出す。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data deactivate-proof device.elmdeact
```

<!-- steps:expect -->
```text
file: device.elmdeact
```

クライアントはこの時点でライセンスファイルを削除するので、このデバイスのアプリは、すぐにそのライセンスでは動かなくなる。サービスはプルーフを受け取るまでアクティベーションを数え続けるので、クライアントはプルーフを保存し、アプリがそれを捨てるまで、もう一度アクティベーションするのを拒否する。

<!-- steps:run exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> third.elmreq
```

<!-- steps:expect -->
```text
ELM_INVALID_STATE: A deactivation proof is kept; pass it to the service and forget it first.
```

`device.elmdeact` をサービスにつながるコンピューターへ運び、提出する。提出するのは、資格情報を持たない顧客でも、ベンダーの管理者でもよい。この選び方は、[リクエストを提出する](activate-a-device-without-a-network.md#リクエストを提出する)のリクエストファイルと同じだ。プルーフにはレスポンスファイルがないので、`--out` は付けない。

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in device.elmdeact
```

<!-- steps:expect -->
```text
deactivated
```

サービスは、アプリ自身によるアクティベーション解除と同じく、アクティベーションの枠を空けて、ライセンスのアクティベーション解除を 1 回と数える。アプリには、これがいつ起きたかを知る方法がない。そのため、ユーザーに尋ね、そのあとで初めて `ForgetDeactivationProofAsync` を呼ばなければならない。コマンド `forget` がこれをする。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data forget
```

<!-- steps:expect -->
```text
forgotten
```

これで、データフォルダーをもう一度アクティベーションできる。プルーフを捨てるのは、提出したあとだけにする。提出の前にアプリがプルーフを捨てると、サービスはこのインストール鍵の古いアクティベーションをまだ持っているので、デバイスの次のアクティベーションのリクエストが示すシーケンス番号はそれと一致しない。サービスはこれをインストールのコピーと判断し（`ELM_CLONE_DETECTED`）、既定の `cloneStrategy` `deny` ではアクティベーションを止める。そのときは、下のとおり管理者がアクティベーションを解除し、デバイスはもう一度アクティベーションする。

## 管理者としてアクティベーションを解除する

ベンダーの管理者は、デバイスをなくしたときや、ライセンスが `deactivationLimit` に達したときなどに、デバイスなしでアクティベーションを解除する。この解除は上限に数えない。試すために、`device-data` をもう一度アクティベーションし、アプリが表示するライセンスの ID を控える。

<!-- steps:run capture=moving-license-id pattern="license: ([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
dotnet run --project offline-app -- root.pub.pem device-data request <moving-license-key> fourth.elmreq
elm offline process --url http://127.0.0.1:8080 --in fourth.elmreq --out fourth.elmresp
dotnet run --project offline-app -- root.pub.pem device-data import fourth.elmresp
```

下の `<moving-license-id>` がその ID で、ライセンスの `id` だ。ライセンスのアクティベーションを一覧する。

<!-- steps:run capture=moving-activation-id pattern="\x22id\x22:\x22([0-9a-f]{8}-[0-9a-f-]{27})\x22" -->
```shell
elm admin request GET /v1/admin/licenses/<moving-license-id>/activations --url http://127.0.0.1:8080
```

`elm` は、アクティベーションの 1 ページを表示する。

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000603","licenseId":"01960000-0000-7000-8000-000000000302","keyId":"q3V9xYc0lJm2nRtA8sKdE4fGhUiOpWzX1bN5vC7eY6M","fingerprints":["3f2a9c4e1b7d40a8b6e5c2d9f0a1b3c4","9b1e7c3a5d2f4e6081a7c9b3d5e7f901"],"client":null,"app":null,"seq":1,"blockedAt":null,"createdAt":"2026-01-15T09:30:00Z","updatedAt":"2026-01-15T09:30:00Z"}]}
```

各項目が 1 台のデバイスだ。`keyId` はインストール鍵のサムプリント、`fingerprints` はマシンフィンガープリント、`createdAt` はアクティベーションした日時だ。これらで解除するデバイスを探し、その `id`、ここでは `<moving-activation-id>` を取る。アクティベーションを解除する。

<!-- steps:run -->
```shell
elm admin request POST /v1/admin/activations/<moving-activation-id>/deactivate --url http://127.0.0.1:8080
```

サービスは本文のないレスポンスを返すので、`elm` は何も表示しない。枠はすぐに空く。ただし、デバイスには知らされないので、デバイスにあるライセンスファイルは期限が切れるまで有効なままだ。これについては[アクティベーション解除](../explanation/activation.md#アクティベーション解除)で説明している。デバイスの次のトークンの更新は拒否される。管理画面のライセンスのページでも、そのアクティベーションを一覧し、解除できる。

## 関連するページ

- [アクティベーション解除](../explanation/activation.md#アクティベーション解除)と[ファイルの受け渡しによるオフラインのアクティベーション](../explanation/activation.md#ファイルの受け渡しによるオフラインのアクティベーション): それぞれの方法が何をするか。
- 管理 API の [`POST /v1/admin/activations/{id}/deactivate`](../reference/administration-api.md#post-v1adminactivationsiddeactivate) と [`GET /v1/admin/licenses/{id}/activations`](../reference/administration-api.md#get-v1adminlicensesidactivations)。
- デバイスのプロトコルの [`POST /v1/activations/{id}/deactivate`](../reference/device-protocol.md#post-v1activationsiddeactivate)。
- [.NET API](../reference/dotnet-api.md#activationclient): `DeactivateAsync`、`CreateDeactivationProofAsync`、`ForgetDeactivationProofAsync`。
