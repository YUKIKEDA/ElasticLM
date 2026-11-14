---
sidebar_position: 13
---

# エラーを処理する

このガイドは、ElasticLM のアクティベーションをアプリに組み込み、アクティベーション、確認、トークンの更新が失敗したときにアプリが何をするかを決める開発者に向けている。あとで再試行するか、ユーザーに頼むか、ベンダーに伝えるかだ。このガイドは、`elm-service` が `http://127.0.0.1:8080` で動いていて、製品がある状態から始める。[SaaS 型のアクティベーションサービスを動かして .NET のアプリをオンラインでアクティベーションする](../tutorials/activate-an-app-online.md)で、これを準備できる。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

<!-- steps:after page=docs/guide/tutorials/activate-an-app-online.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、チュートリアルのフォルダーにあるルート公開鍵 `root.pub.pem`、2 つの ElasticLM のパッケージが入ったフォルダー `packages`、ファイル `nuget.config` を使う。サービスが止まっていたら、[サービスを起動する](../tutorials/activate-an-app-online.md#サービスを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。

例では、チュートリアルの値を使う。製品 `acme-cad`、その機能 `pro`、`http://127.0.0.1:8080` のサービスだ。自分の値に置き換える。

## 失敗がアプリに届く形

ElasticLM の API は、失敗を例外ではなく結果として伝える。

- `ActivationClient.TryCreate` は、クライアントを作れないときに `false` を返し、`out` 引数に `LicenseError` を入れる。たとえば、データフォルダーにあるインストール鍵を読めないときは `ELM_INSTALLATION_KEY_UNREADABLE` になる。Windows では、別のマシンやユーザーからコピーしたデータフォルダーでも、このコードになる。
- `ActivationClient.ActivateAsync`、`StartTrialAsync`、`RefreshAsync` は `ActivationResult` を返す。ライセンスを持つ `Activated` か、`LicenseError` と省略可能な `RetryAfter` を持つ `Failed` のどちらかだ。
- `ActivationClient.Check` は `LicenseVerificationResult` を返す。ライセンスと `InGracePeriod` を持つ `Valid` か、`LicenseError` を持つ `Invalid` のどちらかだ。
- `LicenseError` は、`Code`（`ErrorCode` の値）、`ELM_LICENSE_NOT_FOUND` のような `WireName`、`Message` を持つ。`Message` はログとサポートのための英語の一文で、アプリの処理の分岐に使うものではない。

例外は、呼び出し自体の誤りを表す。範囲外の引数（`ArgumentException`）や、破棄したクライアント（`ObjectDisposedException`）などだ。[失敗の伝え方](../reference/dotnet-api.md#失敗の伝え方)にそれらを挙げている。アプリは操作と `Code` で判断し、`Message` では判断しない。たとえば `ELM_NETWORK_ERROR` のメッセージには OS 自身の言葉が入り、Windows、Linux、macOS で違う。

クライアントは 2 つの場面で自動で再試行するので、そこではアプリが再試行しなくてよい。`ELM_NONCE_REQUIRED` のあとにはリクエストをもう一度だけ送る。また、サービスの URL を持って動いているあいだは、保存したアクティベーションのトークンの更新に失敗すると、1 時間後にもう一度更新する。詳しくは[クライアントが自動で再試行するもの](../reference/error-codes.md#クライアントが自動で再試行するもの)に挙げている。

## 操作とコードで判断する

同じコードでも、操作によってすべきことが違う。`ELM_LICENSE_NOT_FOUND` は、アクティベーションで返ればキーが間違っていることを、`Check` で返ればまだ何もアクティベーションしていないことを表す。両方で判断する。

| 操作 | コード | アプリがすること |
| --- | --- | --- |
| アクティベーション、トライアルの開始 | [`ELM_NETWORK_ERROR`](../reference/error-codes.md)、[`ELM_RATE_LIMITED`](../reference/error-codes.md) | あとで再試行する。`RetryAfter` が設定されていれば、少なくともその時間は待つ。 |
| トライアルの開始 | `RetryAfter` が数秒より長い [`ELM_RATE_LIMITED`](../reference/error-codes.md) | 製品が直近 24 時間のトライアルをすべて出し終えていて、待ち時間は最大 1 日になる。ループで再試行しない。いつ再試行できるかをユーザーに示すか、ライセンスの購入を勧める。 |
| アクティベーション | [`ELM_LICENSE_NOT_FOUND`](../reference/error-codes.md)、[`ELM_WRONG_PRODUCT`](../reference/error-codes.md)、[`ELM_LICENSE_SUSPENDED`](../reference/error-codes.md)、[`ELM_LICENSE_REVOKED`](../reference/error-codes.md)、[`ELM_EXPIRED`](../reference/error-codes.md) | 別のライセンスキーを入力するか、ベンダーに問い合わせるよう、ユーザーに頼む。 |
| アクティベーション | [`ELM_ACTIVATION_LIMIT`](../reference/error-codes.md) | 先に別のデバイスでライセンスのアクティベーションを解除するよう、ユーザーに頼む。 |
| トライアルの開始 | [`ELM_TRIAL_USED`](../reference/error-codes.md) | ライセンスの購入を勧める。 |
| トライアルの開始 | [`ELM_TRIAL_NOT_OFFERED`](../reference/error-codes.md) | 製品 ID が間違っているか、ベンダーがトライアルを提供していない。アプリの製品 ID を確かめ、ベンダーに伝える。 |
| アクティベーション、トライアルの開始 | [`ELM_INVALID_STATE`](../reference/error-codes.md) | アクティベーションがすでに保存されているか、アプリの別のプロセスがデータフォルダーを使っている。先に確認するか、別のプロセスが終わってから再試行する。どちらの場合かはメッセージに書いてある。 |
| 確認 | [`ELM_LICENSE_NOT_FOUND`](../reference/error-codes.md) | 何もアクティベーションしていない。アクティベーションするよう、ユーザーに頼む。 |
| 確認 | [`ELM_EXPIRED`](../reference/error-codes.md) | 保存したファイルの期限も猶予期間も終わった。トークンを更新する。それにはネットワークが要る。 |
| 確認 | [`ELM_CLOCK_TAMPERED`](../reference/error-codes.md) | 時計が、クライアントが以前に見た時刻より前になっている。時計を直すようユーザーに頼むか、オンラインでトークンを更新する。 |
| 確認 | [`ELM_FEATURE_NOT_LICENSED`](../reference/error-codes.md)、[`ELM_FEATURE_EXPIRED`](../reference/error-codes.md) | その機能を外す。 |
| 確認 | [`ELM_WRONG_INSTALLATION`](../reference/error-codes.md)、[`ELM_FINGERPRINT_MISMATCH`](../reference/error-codes.md) | 保存したファイルがこのインストールのものでないか、マシンが変わりすぎた。`DeactivateAsync` でアクティベーションを解除してから、もう一度アクティベーションする。 |
| 確認 | [`ELM_WRONG_KIND`](../reference/error-codes.md) | 保存したファイルの種類が、アプリが求めた種類と違う。たとえば、トライアルを `Standard` として確かめた。アプリがアクティベーションした種類を渡す。 |
| トークンの更新 | [`ELM_NETWORK_ERROR`](../reference/error-codes.md)、[`ELM_RATE_LIMITED`](../reference/error-codes.md) | 保存したファイルで動き続ける。クライアントが自分で再試行する。 |
| トークンの更新 | [`ELM_LICENSE_SUSPENDED`](../reference/error-codes.md)、[`ELM_LICENSE_REVOKED`](../reference/error-codes.md)、[`ELM_EXPIRED`](../reference/error-codes.md) | 保存したファイルは期限まで使える。ベンダーに問い合わせるよう、ユーザーに頼む。 |
| トークンの更新 | [`ELM_ACTIVATION_NOT_FOUND`](../reference/error-codes.md) | ベンダーがアクティベーションを消し、クライアントは保存したファイルを消した。もう一度アクティベーションするよう、ユーザーに頼む。 |
| トークンの更新 | [`ELM_INVALID_STATE`](../reference/error-codes.md) | アクティベーションが保存されていない。アクティベーションするよう、ユーザーに頼む。ほかの場合は、メッセージに書いてある。 |
| すべて | [`ELM_INTERNAL_ERROR`](../reference/error-codes.md) | 3 つの場合があり、どれなのかはメッセージに書いてある。クライアントがデータフォルダーに書き込めないとき: フォルダーの権限とディスクの空き容量を確かめる。サービスの処理が失敗したとき: ベンダーに伝える。失敗したリクエストへのレスポンスに、クライアントがサーバーから受け取るエラーコードがなかったとき（メッセージは「without a known error code」で終わる。相手がサービスでも、メッセージには「server」と書いてある）: URL が、プロキシやほかのプログラムではなく、サーバーやサービスそのものに届いているか、クライアントのライブラリが相手より古くないかを確かめる。どちらも問題がなければ、メッセージを添えてベンダーに伝える。 |
| すべて | それ以外のコード。[`ELM_CLONE_DETECTED`](../reference/error-codes.md) や [`ELM_PROOF_INVALID`](../reference/error-codes.md) など | コードとメッセージを添えて、ベンダーに伝える。 |

`ELM_INVALID_STATE` は、サービスの URL を持たないクライアントで、`RefreshAsync` のようにサービスの URL が要るメソッドをアプリが呼んだときにも返る。これはアプリのコードの誤りだ。16 件のリクエストが続けてレスポンスを受け取らなかったあとにも返り、このときはベンダーの管理者の対応が要る。どちらなのかはメッセージに書いてある。各コードが返るすべての場合は、[エラーコード](../reference/error-codes.md)に挙げている。

## アプリを作る

下のアプリは、アクティベーション、確認、トークンの更新をし、失敗ごとに、アプリならどうするかを上の表に沿って表示する。フォルダー `resilient` にアプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。

<!-- steps:run -->
```shell
dotnet new console -o resilient
dotnet add resilient package ElasticLM.Client --prerelease --source packages
```

`dotnet add` は、パッケージのコピーを NuGet のグローバルパッケージフォルダーにも置く。その場所は `dotnet nuget locals global-packages --list` で表示でき、環境変数 `NUGET_PACKAGES` で場所を変えたときも同じように表示できる。このフォルダーには、このマシンのすべてのプロジェクトのパッケージが入っている。ElasticLM を試し終えたら、そこにある ElasticLM のフォルダー `elasticlm.licensing` と `elasticlm.client` だけを削除する。

`resilient/Program.cs` の中身を、次のコードに置き換える。

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

各コマンドは、まず判断を 1 行で表示する。この行はどのシステムでも同じで、たとえば `retry later: ELM_NETWORK_ERROR` のようになる。次に、`RetryAfter` があればそれを表示し、最後に理由を表示する。`check` は、サービスに接続しないことを示すために、クライアントにサービスの URL を渡さない。実際のアプリは、確認だけをするときもサービスの URL を渡し、クライアントがバックグラウンドでトークンを更新するようにする。[ネットワークがなくても動かす](run-without-a-network.md)では `check` を使う。

## 判断を見る

新しいデータフォルダー `errors-data` を、サービスの知らないキーでアクティベーションする。

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem errors-data activate http://127.0.0.1:8080 ELM-AAAAA-AAAAA-AAAAA-AAAAA
```

終了コード 1 で終わり、次のように表示する。

<!-- steps:expect -->
```text
ask for another key: ELM_LICENSE_NOT_FOUND
reason: No license has this key.
```

応答しないアドレス `http://127.0.0.1:8081` に対してアクティベーションする。ネットワークが落ちている状態の代わりに使うアドレスだ。

<!-- steps:run exit=1 -->
```shell
dotnet run --project resilient -- root.pub.pem errors-data activate http://127.0.0.1:8081 ELM-AAAAA-AAAAA-AAAAA-AAAAA
```

終了コード 1 で終わり、次のように表示する。理由の部分は、手元の OS の言葉になる。

```text
retry later: ELM_NETWORK_ERROR
reason: The service cannot be reached: Connection refused (127.0.0.1:8081)
```

<!-- steps:expect -->
```text
retry later: ELM_NETWORK_ERROR
```

どちらの試みでも、`errors-data` にアクティベーションは保存されていない。ただし、クライアントはそこに自分のインストール鍵を作った。[ネットワークがなくても動かす](run-without-a-network.md)では、本物のキーでアクティベーションし、トークンの更新に失敗したときにアプリの手元に何が残るかを示す。

## ライセンスサーバーからのリース

フローティングライセンスのクライアントである `LicenseClient` も、同じように失敗を伝える。`AcquireLeaseAsync` と `BorrowLeaseAsync` は `LeaseResult` を返す。`Acquired` か、`LicenseError` と `RetryAfter` を持つ `Failed` のどちらかだ。クライアントは自分では待たない。`ELM_SERVER_RECOVERING` は、サーバーが起動してからリースの期間 1 回分（既定では 10 分）のあいだ送るコードだ。これが返ったら、`RetryAfter` の時間だけ待ってから、もう一度リースを求める。[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)のアプリも、そうしている。`ELM_NO_SEATS` は、すべてのシートが使われていることを表す。ユーザーに伝えるか、あとで再試行する。クライアントがすでに持っているリースは、自動で更新される。その更新が失敗したことは、リースの状態と `LastError` でわかる。リースの状態は[フローティングライセンス](../explanation/floating-licenses.md)で説明している。

## C では

`libelasticlm` は、コードを `int32_t` で返す。成功したときは `ELM_OK`（0）だ。番号は .NET の `ErrorCode` と同じで、`ELM_NETWORK_ERROR` のような `elasticlm.h` のマクロとも同じだ。`elm_last_error_message` は、呼び出したスレッドで最後に失敗したときのメッセージを返す。待つ時間を付けて拒否されることがある関数は、その時間を `retry_after_seconds` に返す。上と同じように、コードで判断する。コードをその名前に変える関数はない。関数は [C API](../reference/c-api.md#エラー) に挙げている。

## 関連するページ

- [エラーコード](../reference/error-codes.md): すべてのコードと、その番号、HTTP の状態、すべきこと。
- [ネットワークがなくても動かす](run-without-a-network.md): ネットワークが使えないあいだ、クライアントが何を保存し、何をするか。
- [.NET API](../reference/dotnet-api.md): すべての結果の型とメソッド。
