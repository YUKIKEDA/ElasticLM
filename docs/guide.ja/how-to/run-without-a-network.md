---
sidebar_position: 14
---

# ネットワークがなくても動かす

このガイドは、`elm-service` でオンラインでアクティベーションし、ネットワークやサービスが使えないときも動き続けなければならないアプリの開発者に向けている。アプリは、アクティベーションのライセンスファイルをデータフォルダーに保存し、起動のたびにネットワークなしでそれを確かめ、ときどきオンラインでトークンを更新する。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、ターミナルの環境変数 `ELM_API_KEY` にサービスの API キーが 1 つ入っていて、サービスに機能を持つ製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これをすべて準備できる。トークンの更新は[アクティベーション](../explanation/activation.md)で説明している。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、チュートリアルのフォルダーにあるルート公開鍵 `root.pub.pem`、2 つの ElasticLM のパッケージが入ったフォルダー `packages`、ファイル `nuget.config` を使う。

- サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。
- ターミナルを閉じたなどの理由で `ELM_API_KEY` が設定されていなければ、`elm-service setup --config service.json` で新しい API キーを作り、[サービスを準備する](../tutorials/activate-an-app-online.md#サービスを準備する)のとおりに `ELM_API_KEY` に読み込む。

例では、チュートリアルの値を使う。製品 `acme-cad`、その機能 `pro`、`http://127.0.0.1:8080` のサービスだ。自分の値に置き換える。下のファイルの `<product-id>` は製品の `id` で、次のコマンドを打つと、製品の `code` と一緒に表示される。

<!-- steps:run -->
```shell
elm admin request GET /v1/admin/products --url http://127.0.0.1:8080
```

`elm` は製品の一覧を 1 ページ分表示する。

```text
{"items":[{"id":"01960000-0000-7000-8000-000000000101","code":"acme-cad","name":"ACME CAD","trialPolicyId":null,"createdAt":"2026-01-15T09:30:00Z"}]}
```

下の出力にある ID、ライセンスキー、日時は架空の値だ。手元のマシンでは毎回違う値になる。

## 試すためのライセンスを作る

このガイドでチュートリアルのライセンスのアクティベーションの枠を使わないように、ポリシーと、その下で機能 `pro` を持つライセンスを作る。ポリシーには名前しかないので、既定の値になる。ライセンス自体には期限がない。一方、サービスがアクティベーションごとに発行する各ライセンスファイルは、30 日後に期限が切れ、猶予期間が 7 日あり、期限の 20 日前からトークンを更新する。

<!-- steps:file path=resilient-policy.json -->
```json
{"productId":"<product-id>","name":"Standard, offline check"}
```

<!-- steps:run capture=resilient-policy-id pattern="([0-9a-f]{8}-[0-9a-f-]{27})" -->
```shell
elm admin request POST /v1/admin/policies --body resilient-policy.json --url http://127.0.0.1:8080
```

レスポンスの最初にある `id` がポリシーの ID で、次のファイルの `<resilient-policy-id>` だ。

<!-- steps:file path=resilient-license.json -->
```json
{"policyId":"<resilient-policy-id>","name":"Offline check customer","features":[{"code":"pro"}]}
```

<!-- steps:run capture=resilient-license-key pattern="(ELM(?:-[0-9A-Z]{5}){4})" -->
```shell
elm admin request POST /v1/admin/licenses --body resilient-license.json --url http://127.0.0.1:8080
```

レスポンスの最後にある `key` がライセンスキーで、下の `<resilient-license-key>` だ。

## アプリを作る

[エラーを処理する](handle-errors.md#アプリを作る)で `resilient` を作ったなら、それを使い、[オンラインでアクティベーションする](#オンラインでアクティベーションする)に進む。`dotnet new` は、同じフォルダーにもう一度作ろうとすると拒否する。まだ作っていなければ、フォルダー `resilient` にアプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

<!-- steps:run -->
```shell
dotnet new console -o resilient
dotnet add resilient package ElasticLM.Client --prerelease --source packages
```

`dotnet add` は、パッケージのコピーを NuGet のグローバルパッケージフォルダーにも置く。その場所は `dotnet nuget locals global-packages --list` で表示でき、環境変数 `NUGET_PACKAGES` で場所を変えたときも同じように表示できる。このフォルダーには、このマシンのすべてのプロジェクトのパッケージが入っている。ElasticLM を試し終えたら、そこにある ElasticLM のフォルダー `elasticlm.licensing` と `elasticlm.client` だけを削除する。

`resilient/Program.cs` の中身を、次のコードに置き換える。[エラーを処理する](handle-errors.md#アプリを作る)と同じコードだ。

<!-- sample: samples/ResilientActivation/Program.cs -->
<!-- steps:file path=resilient/Program.cs -->
```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

// Activates an installation of acme-cad online, checks the kept license file with no network, and refreshes it, and prints
// for each failure what the app does about it, decided by the operation and the error code.
//
//   app <root-public-key.pem> <data-folder> activate <service-url> <key>
//   app <root-public-key.pem> <data-folder> check
//   app <root-public-key.pem> <data-folder> refresh <service-url>
//
// Give refresh the service that activated the installation, or an address that does not answer to try it without a
// network: another service does not know the activation and removes it. A real app embeds the root public key and the
// service URL instead of reading them from the command line.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

string? command = args.Length > 2 ? args[2] : null;
int length = command switch
{
    "activate" => 5,
    "refresh" => 4,
    "check" => 3,
    _ => -1,
};
if (args.Length != length)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder> <command> [arguments]");
    Console.Error.WriteLine("commands: activate <service-url> <key>, check, refresh <service-url>");
    return 2;
}

try
{
    // check works with files alone, and this one gives the client no service URL to show that. A real app gives the URL
    // also when it only checks: Check still sends nothing, and the client then refreshes the activation in the background.
    var options = new ActivationClientOptions
    {
        ProductId = ProductId,
        ServiceUrl = command == "check" ? null : new Uri(args[3]),
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
        "activate" => await ActivateAsync(client, args[4]),
        "refresh" => await RefreshAsync(client),
        _ => Check(client),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException)
{
    // A file that cannot be read, a first argument that is not a public key in PEM, or a service URL that is neither an
    // https URL nor an http URL on a loopback address.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Activates with the license key. A failure is either worth trying again later, or needs the user, or the vendor.
static async Task<int> ActivateAsync(ActivationClient client, string licenseKey)
{
    switch (await client.ActivateAsync(licenseKey))
    {
        case ActivationResult.Activated activated:
            Console.WriteLine($"activated: {FeatureId}");
            PrintExpiry(activated.License);
            return 0;
        case ActivationResult.Failed failed:
            string decision = failed.Error.Code switch
            {
                // No answer, or a busy service: the same request may work later.
                ErrorCode.NetworkError or ErrorCode.RateLimited => "retry later",

                // The key does not open a license of this product that can be activated now.
                ErrorCode.LicenseNotFound or ErrorCode.WrongProduct or ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired =>
                    "ask for another key",

                // Every device the license allows is activated.
                ErrorCode.ActivationLimit => "ask to deactivate another device",

                // An activation is kept already, or another process of the app holds the data folder.
                ErrorCode.InvalidState => "already activated or busy",
                _ => "tell the vendor",
            };
            return Decide(decision, failed.Error, failed.RetryAfter);
        default:
            return 1;
    }
}

// Checks the kept license file for the feature, as the app does at each start; it reaches no service.
static int Check(ActivationClient client)
{
    switch (client.Check(FeatureId, version: null))
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine(valid.InGracePeriod ? $"valid in the grace period: {FeatureId}" : $"valid: {FeatureId}");
            PrintExpiry(valid.License);
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            string decision = invalid.Error.Code switch
            {
                // Nothing is kept yet: the app asks the user to activate.
                ErrorCode.LicenseNotFound => "not activated",

                // The kept file ended, grace period included: it needs a refresh, which needs the network.
                ErrorCode.Expired => "refresh needed",

                // The clock is earlier than a time the client saw before.
                ErrorCode.ClockTampered => "ask to correct the clock",

                // The license does not grant the feature: the app leaves it out.
                ErrorCode.FeatureNotLicensed or ErrorCode.FeatureExpired => "feature not licensed",

                // The kept file is not this installation's, or the machine changed too much: the app deactivates
                // (DeactivateAsync) and activates again.
                ErrorCode.WrongInstallation or ErrorCode.FingerprintMismatch => "deactivate and activate again",
                _ => "tell the vendor",
            };
            return Decide(decision, invalid.Error, null);
        default:
            return 1;
    }
}

// Refreshes the kept activation now. When it fails, the kept license file still works until it ends, and the client tries
// again on its own every hour while it runs; State says what this process saw.
static async Task<int> RefreshAsync(ActivationClient client)
{
    switch (await client.RefreshAsync())
    {
        case ActivationResult.Activated refreshed:
            Console.WriteLine($"refreshed: {FeatureId}");
            PrintExpiry(refreshed.License);
            return 0;
        case ActivationResult.Failed failed:
            string decision = failed.Error.Code switch
            {
                ErrorCode.NetworkError or ErrorCode.RateLimited => "keeps working",

                // The kept file works until it ends; the license itself needs the vendor.
                ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => "ask to contact the vendor",

                // Nothing is kept to refresh, among other cases that the reason names.
                ErrorCode.InvalidState => "activate first",

                // The service no longer has the activation, and the client removed the kept file.
                ErrorCode.ActivationNotFound => "activate again",
                _ => "tell the vendor",
            };
            int code = Decide(decision, failed.Error, failed.RetryAfter);
            Console.WriteLine($"state: {client.State}");
            return code;
        default:
            return 1;
    }
}

// Prints the decision on one line that does not change between systems, then the reason, which may.
static int Decide(string decision, LicenseError error, TimeSpan? retryAfter)
{
    Console.WriteLine($"{decision}: {error.WireName}");
    if (retryAfter is TimeSpan wait)
    {
        Console.WriteLine($"retry after: {wait.TotalSeconds:0} s");
    }

    Console.WriteLine($"reason: {error.Message}");
    return 1;
}

// The kept license file works until its expiry, and then for its grace period.
static void PrintExpiry(VerifiedLicense license)
{
    if (license.ExpiresAt is DateTimeOffset expires)
    {
        Console.WriteLine($"expires: {expires:u}");
        Console.WriteLine($"grace period ends: {expires + license.GracePeriod:u}");
    }
    else
    {
        Console.WriteLine("expires: never");
    }
}
```

アプリには 3 つのコマンドがある。`activate` は `ActivateAsync` を、`check` は `Check` を、`refresh` は `RefreshAsync` を呼ぶ。成功したときは、それぞれ結果と、保存したライセンスファイルの期限を表示する。失敗したときは、まずアプリがすることを 1 行で表示する。この行はどのシステムでも同じで、たとえば `keeps working: ELM_NETWORK_ERROR` のようになる。次に理由を表示する。理由には OS 自身の言葉が入ることがある。`refresh` では、最後にクライアントの状態も表示する。`check` は、サービスに接続しないことを示すために、クライアントにサービスの URL を渡さない。実際のアプリは、確認だけをするときもサービスの URL を渡し、クライアントがバックグラウンドでトークンを更新するようにする。

## オンラインでアクティベーションする

サービスに接続できるうちに、ライセンスキーでデータフォルダー `resilient-data` をアクティベーションする。

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data activate http://127.0.0.1:8080 <resilient-license-key>
```

アプリは次のように表示する。

```text
activated: pro
expires: 2026-02-14 09:30:00Z
grace period ends: 2026-02-21 09:30:00Z
```

<!-- steps:expect -->
```text
activated: pro
```

クライアントは、サービスが署名したアクティベーションのライセンスファイルを `resilient-data` に保存する。このファイルはアクティベーションの 30 日後に期限が切れ、そのあと猶予期間の 7 日のあいだも、アプリはこれを使える。

## ネットワークなしで確かめる

アプリは起動のたびに、保存したライセンスファイルを確かめる。`check` はクライアントにサービスの URL を渡さないので、ネットワークには何も送らない。

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data check
```

<!-- steps:expect -->
```text
valid: pro
```

上と同じ期限も表示する。`Check` は、ルート公開鍵、製品、機能、このインストールの鍵とマシンフィンガープリントに照らしてファイルを確かめる。どれもこのマシンの中で済む。マシンがオフラインでも、サービスが止まっているあいだも、同じように動く。

## トークンの更新の失敗を見る

サービスに接続できないときに何が起きるかを見るために、応答しないアドレス `http://127.0.0.1:8081` に対してトークンを更新する。実際のアプリはサービスの URL を埋め込む。このガイドでここだけ URL を変えるのは、ネットワークが落ちている状態の代わりにするためだ。`refresh` には、データフォルダーをアクティベーションしたサービスか、応答しないアドレスだけを渡す。別のサービスはそのアクティベーションを知らないので、クライアントは保存したファイルを消してしまう。

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data refresh http://127.0.0.1:8081
```

終了コード 1 で終わり、次のように表示する。理由の部分は、手元の OS の言葉になる。

```text
keeps working: ELM_NETWORK_ERROR
reason: The service cannot be reached: Connection refused (127.0.0.1:8081)
state: RefreshFailing
```

<!-- steps:expect -->
```text
keeps working: ELM_NETWORK_ERROR
state: RefreshFailing
```

保存したライセンスファイルは変わっていない。もう一度確かめる。

<!-- steps:run -->
```shell
dotnet run --project resilient -- root.pub.pem resilient-data check
```

<!-- steps:expect -->
```text
valid: pro
```

## ネットワークが使えないあいだのクライアントの動き

- **保存したライセンスファイルは使い続けられる。** 期限まで使え、そのあとも猶予期間のあいだ使える。ポリシーの既定の値では 30 日と 7 日だ。猶予期間のあいだ、`Check` は `InGracePeriod` が `true` の `Valid` を返す。猶予期間を過ぎると `Check` は `ELM_EXPIRED` で失敗し、アプリにはトークンの更新が要る。それにはネットワークが要る。これらの時間は[有効期限と猶予期間](../explanation/license-files-and-verification.md#有効期限と猶予期間)で説明している。
- **クライアントは自分でトークンを更新する。** アプリが動いているあいだ、トークンの更新を始める期間（既定では期限の 20 日前から）に入ると更新する。更新に失敗すると、1 時間後に再試行する。サービスの `Retry-After` のほうが長ければ、その時間のあとに再試行する（最大 1 日）。そのあいだもアプリは動き続ける。これには、サービスの URL を持つクライアントが要る。`RefreshAsync` は、すぐにトークンを更新する。
- **トークンの更新がどうなっているかは、`State` と `StateChanged` でわかる。** 失敗したあとは `RefreshFailing`、更新が成功すると再び `Active`、ライセンスが失効、一時停止、期限切れになったときは `Refused`、サービスがコピーを見つけたときは `CloneDetected` になる。これは動いているクライアントの状態だ。新しいプロセスは、保存したアクティベーションがあれば必ず `Active` から始まるので、このガイドの `check` ではこの状態は見えない。
- **時計も確かめる。** クライアントは最後に見た時刻を保存していて、時計がそれより前に戻されると、`Check` は `ELM_CLOCK_TAMPERED` で失敗する。トークンの更新が成功すると、これはリセットされる。データフォルダーにある時刻巻き戻し対策の状態を削除すれば、ライセンスファイルの発行時刻までは時計を戻せる。[時刻巻き戻しの検出と、その限界](../explanation/license-files-and-verification.md#時刻巻き戻しの検出とその限界)で説明している。
- **一度もオンラインにならないデバイス**は、代わりにファイルでアクティベーションし、トークンを更新する。[ネットワークにつながらないデバイスをアクティベーションする](activate-a-device-without-a-network.md)を参照。

`elm-server` のフローティングライセンスは、動きが違う。アプリがリースを持っているあいだは、リースを更新し続けなければならない。サーバーに接続できないと、クライアントはリースの更新を再試行し（15 秒後、30 秒後、そのあとは 60 秒ごと）、リースの状態は `Retrying` になる。リースの期限が切れると `Lost` になる。ネットワークから離れて使うには、アプリは離れる前に `BorrowLeaseAsync` でシートを借用し、次に起動したときに、ネットワークなしで `GetBorrowedLeases` で借用を読む。リースと借用は[フローティングライセンス](../explanation/floating-licenses.md)で説明している。

## 関連するページ

- [エラーを処理する](handle-errors.md): エラーごとにアプリがすること。
- [アクティベーション](../explanation/activation.md): アクティベーション、トークンの更新、ポリシーの時間。
- [.NET API](../reference/dotnet-api.md#activationclient): `ActivationClient`、`ActivationState`、各メソッドが返すもの。
