---
sidebar_position: 3
---

# ネットワークにつながらないデバイスをアクティベーションする

このガイドは、`elm-service` に一度もつながらないデバイスで顧客がアプリを動かすベンダーと、そのアプリを作る開発者に向けている。デバイス上のアプリがリクエストファイルを書き出し、人がそれをサービスにつながるコンピューターへ運ぶ。そのコンピューターで `elm offline process` がレスポンスファイルを受け取る。アプリはそのレスポンスを取り込み、ライセンスファイルを保存する。期限が切れる前のライセンスファイルの更新も、同じやり取りでする。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。やり取りの仕組みは[ファイルの受け渡しによるオフラインのアクティベーション](../explanation/activation.md#ファイルの受け渡しによるオフラインのアクティベーション)で説明している。

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

下の出力にある ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。ここでは、デバイスとネットワークにつながったコンピューターが同じマシンだ。実際の運用では、リクエストファイルとレスポンスファイルだけが、一方からもう一方へ移る。

## デバイス用のライセンスを作る

ネットワークにつながらないデバイスがライセンスファイルを更新できるのは、誰かがファイルを運んだときだけだ。そのため、そのライセンスには、ライセンスファイルの有効期間が長いポリシーを使う。既定の値では、各ライセンスファイルは 30 日有効で、そのあと 7 日アプリが動き続けるので、少なくとも 37 日ごとに誰かがトークンの更新のファイルを運ばなければならない。次のポリシーでは、ライセンスファイルを 90 日（`tokenLifetime`、7,776,000 秒）有効にし、最後の 30 日（`refreshWindow`、2,592,000 秒）に更新の時期が来るようにする。ほかの設定は[ポリシーを設定する](set-up-a-policy.md)で説明している。

<!-- steps:file path=offline-policy.json -->
```json
{"productId":"<product-id>","name":"Offline, 90-day files","tokenLifetime":7776000,"refreshWindow":2592000}
```

<!-- steps:run capture=offline-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body offline-policy.json --url http://127.0.0.1:8080
```

`id` が、下の `<offline-policy-id>` だ。このポリシーの下で、機能 `pro` を持つライセンスを作る。

<!-- steps:file path=offline-license.json -->
```json
{"policyId":"<offline-policy-id>","name":"Offline customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=offline-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body offline-license.json --url http://127.0.0.1:8080
```

`elm` はライセンスを表示する。最後の `key` がライセンスキーで、下の `<offline-license-key>` だ。顧客はこのキーをアプリに入力する。

```text
{"id":"01960000-0000-7000-8000-000000000302","productId":"01960000-0000-7000-8000-000000000101","policyId":"01960000-0000-7000-8000-000000000203","name":"Offline customer","features":[{"code":"pro","ver":null,"seats":null,"expiresAt":null}],"maxActivations":null,"state":"active","createdAt":"2026-01-15T09:30:00Z","expiresAt":null,"suspendedAt":null,"revokedAt":null,"meta":{},"deactivations":0,"key":"ELM-FAKE0-0000K-EY000-00001"}
```

## アプリを作る

このガイドでは、手順ごとにコマンドを 1 つ持つ小さなコンソールアプリ `offline-app` を使う。アプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

<!-- steps:run -->
```shell
dotnet new console -o offline-app
dotnet add offline-app package ElasticLM.Client --prerelease --source packages
```

`dotnet add` は、パッケージのコピーを NuGet のグローバルパッケージフォルダーにも置く。その場所は `dotnet nuget locals global-packages --list` で表示でき、環境変数 `NUGET_PACKAGES` で場所を変えたときも同じように表示できる。このフォルダーには、このマシンのすべてのプロジェクトのパッケージが入っている。ElasticLM を試し終えたら、そこにある ElasticLM のフォルダー `elasticlm.licensing` と `elasticlm.client` だけを削除する。

`offline-app/Program.cs` の中身を、次のコードに置き換える。下の手順では、各コマンドが呼び出す `ActivationClient` のメソッドを示す。

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

アプリは、サービスの URL を指定せずに `ActivationClient` を作る。そのため、クライアントはサービスに接続せず、自分からは何も更新しない。サービスの URL を渡すのはコマンド `deactivate` だけだ（[デバイスのアクティベーションを解除する](deactivate-a-device.md)で使う）。クライアントは、2 つ目の引数のデータフォルダーに、アプリのこのコピーを識別するインストール鍵と、ライセンスファイルを保存する。このガイドには、新しいデータフォルダー `offline-data` を使う。チュートリアルの `client-data` のように、アクティベーションを保存しているフォルダーでは、クライアントはアクティベーションを拒否するからだ。

## デバイスでリクエストを作る

コマンド `request` は、ライセンスキーを渡して `CreateActivationRequestAsync` を呼び、返ってきたリクエストのテキストをファイルに書き出す。リクエストを作る。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data request <offline-license-key> request.elmreq
```

<!-- steps:expect -->
```text
file: request.elmreq
```

リクエストファイルには、ライセンスキー、デバイスの公開鍵、マシンフィンガープリントが入り、インストール鍵で署名されている。期限は作成から 7 日後だ。ファイルを持っている人は誰でもライセンスキーを手にできるので、ほかの人が読めない場所に置く。デバイスがレスポンスを取り込むまで、アプリは同じリクエストをもう一度渡す。新しいリクエストを作るのは、待機中のリクエストがデバイスの時計で 7 日経ったとき、アプリが `renew: true` を渡したとき、リクエストが別のライセンスキーのものだったときだ。そのときは `replaced: discard the earlier request file` も表示する。

## リクエストを提出する

`request.elmreq` をサービスにつながるコンピューターへ運び、そこからサービスに送る。

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in request.elmreq --out response.elmresp
```

`elm` は、アクティベーション、ライセンス、ライセンスファイルのシーケンス番号を表示し、レスポンスファイルを書き出す。

```text
activation 01960000-0000-7000-8000-000000000601, license 01960000-0000-7000-8000-000000000302, seq 1: response.elmresp
```

ファイルを誰が提出するかを選ぶ。

- **顧客**が、資格情報なしで `elm` を使う。このとき `elm offline process` は、ファイルをサービスの公開エンドポイントに送る。ファイルがオンラインのリクエストと同じだけのことを証明するので、公開エンドポイントは資格情報を求めない。`elm offline process` は、`ELM_API_KEY` が設定されておらず、`elm admin login` でもログインしていないときに、この送り方をする。
- **ベンダーの管理者**が、顧客に代わって提出する。ここでのように `ELM_API_KEY` を設定しているか、`elm admin login` のあとなら、`elm offline process` はファイルを管理 API に送る。管理 API は、ファイルごとに監査ログに 1 行を書き込む。管理画面のオフラインのページも同じことをする。

デバイスが次のリクエストを作る前にレスポンスファイルをなくしたら、同じリクエストをもう一度提出する。サービスは、同じリクエストには同じレスポンスを返す。デバイスがレスポンスを取り込んだら、そのリクエストファイルは削除する。同じデバイスの新しいリクエストが処理されたあとで古いリクエストを提出すると（下のトークンの更新のあとに提出したアクティベーションのリクエストなど）、そのリクエストは、サービスがすでに先へ進めたシーケンス番号を示す。サービスはこれをインストールのコピーと判断し（`ELM_CLONE_DETECTED`）、既定の `cloneStrategy` `deny` ではアクティベーションを止める。

## デバイスでレスポンスを取り込む

`response.elmresp` をデバイスに持ち帰る。コマンド `import` は、ファイルのテキストを渡して `ImportResponseAsync` を呼ぶ。`ImportResponseAsync` は、オンラインのアクティベーションと同じようにライセンスファイルを確かめて保存する。そのあと `import` は、アプリが起動のたびにするように、機能 `pro` について `Check` を呼ぶ。レスポンスを取り込む。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data import response.elmresp
```

アプリは、機能、ライセンスの ID、ライセンスファイルのシーケンス番号、ライセンスファイルの期限を表示する。期限は、今から 90 日後の UTC の日時だ。

```text
activated: pro
license: 01960000-0000-7000-8000-000000000302
sequence: 1
expires: 2026-04-15 09:30:00Z
```

ID と日時は手元のマシンでは違うが、機能とシーケンス番号は同じになる。

<!-- steps:expect -->
```text
activated: pro
sequence: 1
```

これでデバイスはアクティベーションされ、その期限まで、さらに期限のあとは猶予期間のあいだ、ネットワークなしで動く。

## ライセンスファイルを更新する

ライセンスファイルの期限が切れる前に更新する。更新の時期が来るのは、ファイルの期限から `RefreshWindow` を引いた時刻を過ぎたときだ。アプリは、どちらの値も、検証したライセンスの `ExpiresAt` と `RefreshWindow` として読める。サービスの URL を持たないクライアントは自分からは更新しないので、アプリはユーザーにファイルを運ぶよう求めなければならない。コマンド `refresh` は、ライセンスキーの要らない `CreateRefreshRequestAsync` を呼び、`request` と同じようにリクエストをファイルに書き出す。

デバイスでトークンの更新のリクエストを作り、提出して、そのレスポンスを取り込む。手順はアクティベーションと同じだ。

<!-- steps:run -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data refresh refresh.elmreq
```

<!-- steps:expect -->
```text
file: refresh.elmreq
```

<!-- steps:run -->
```shell
elm offline process --url http://127.0.0.1:8080 --in refresh.elmreq --out refresh.elmresp
dotnet run --project offline-app -- root.pub.pem offline-data import refresh.elmresp
```

アプリは同じ 4 行を表示する。シーケンス番号は次の値になり、期限は今から 90 日後の新しい日時になる。

```text
activated: pro
license: 01960000-0000-7000-8000-000000000302
sequence: 2
expires: 2026-04-15 09:31:00Z
```

<!-- steps:expect -->
```text
activated: pro
sequence: 2
```

レスポンスを取り込んだので、`request.elmreq` と `refresh.elmreq` を削除する。ライセンスが一時停止、失効、期限切れのいずれかのときは、オンラインのトークンの更新と同じく、サービスはトークンの更新のリクエストを拒否する。

## サービスやアプリが拒否したとき

最初のレスポンスをもう一度取り込む。

<!-- steps:run exit=1 -->
```shell
dotnet run --project offline-app -- root.pub.pem offline-data import response.elmresp
```

<!-- steps:expect -->
```text
ELM_REQUEST_MISMATCH: The answer is not for the request this installation waits for.
```

| コード | 起きたこと | 対処 |
| --- | --- | --- |
| `ELM_REQUEST_MISMATCH` | レスポンスが、アプリが待っているリクエストへのレスポンスではない。 | 最新のリクエストへのレスポンスを取り込むか、新しいリクエストを作る。 |
| `ELM_REQUEST_EXPIRED` | `elm offline process` がサービスからこれを受け取った。リクエストが作成から 7 日を過ぎているか、デバイスの時計が遅れている。 | 新しいリクエストを作る。デバイスの時計が遅れているときは、アプリが `CreateActivationRequestAsync` か `CreateRefreshRequestAsync` に `renew: true` を渡して、すぐに新しいリクエストを作る。 |
| `ELM_INVALID_STATE` | データフォルダーにアクティベーションが保存されているので、アプリはアクティベーションのリクエストを作らない。 | 代わりにトークンの更新をするか、先にアクティベーション解除をする。 |
| `ELM_CLONE_DETECTED` | `elm offline process` がサービスからこれを受け取った。ファイルが、アクティベーションがすでに先へ進めたシーケンス番号を示している。新しいリクエストのあとに提出した古いリクエストや、デバイスのデータフォルダーのコピーがこれに当たる。`cloneStrategy` `deny` では、アクティベーションは止まる。 | [管理者としてアクティベーションを解除する](deactivate-a-device.md#管理者としてアクティベーションを解除する)のとおりに、管理者にアクティベーションを解除してもらう。そのあと、新しいリクエストでデバイスをもう一度アクティベーションする。 |
| `ELM_ACTIVATION_LIMIT` | ライセンスのデバイスが、ポリシーで許された台数に達している。 | [デバイスのアクティベーションを解除する](deactivate-a-device.md)のとおりに、別のデバイスのアクティベーションを解除する。 |

`elm offline process` は、サービスのコードを `error:` で始まる行に表示し、終了コード 1 で終わる。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

## 関連するページ

- [ファイルの受け渡しによるオフラインのアクティベーション](../explanation/activation.md#ファイルの受け渡しによるオフラインのアクティベーション): ファイルと、やり取りを安全に繰り返せるようにする規則。
- [`elm offline process`](../reference/elm-cli.md#elm-offline-process): すべてのオプション。
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient` と、各メソッドが返すもの。
- [トークンとファイルの形式](../reference/formats.md#オフラインのリクエスト-elm-actreqjws): リクエストファイルとレスポンスファイル。
- [デバイスのアクティベーションを解除する](deactivate-a-device.md): デバイスがネットワークにつながらないときに、アクティベーション解除のプルーフを使う方法。
