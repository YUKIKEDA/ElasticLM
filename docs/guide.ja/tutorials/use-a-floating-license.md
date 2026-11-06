---
sidebar_position: 3
---

# ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける

このチュートリアルでは、顧客が自分のネットワークで動かすライセンスサーバー `elm-server` を動かし、ある機能のシートを 2 つ含むサーバーライセンスを与える。そして、シートのリースを受けて返却し、1 時間だけシートを借用する小さな .NET のコンソールアプリを書く。フローティングライセンスは、アプリをどのマシンで動かすかではなく、アプリのコピーをいくつ同時に動かすかを制限する。詳しくは[フローティングライセンス](../explanation/floating-licenses.md)で説明している。ElasticLM を初めて使う人のうち、ターミナルと `dotnet` コマンドを使える人に向けている。このチュートリアルは単独で進められるので、ほかのチュートリアルを先に終えておく必要はない。

## 始める前に

次のものが要る。

- .NET 10 SDK。`dotnet --list-sdks` の一覧に、`10.` で始まるバージョンがなければならない。
- マシンのポート 7070 と 7071 が空いていること。サーバーはこの両方で待ち受ける。両方が空いていれば、PowerShell の `Get-NetTCPConnection -LocalPort 7070,7071 -ErrorAction SilentlyContinue` も、Linux と macOS の `lsof -i :7070 -i :7071` も、何も表示しない。
- ダウンロードしたファイルを確かめるための [GitHub CLI](https://cli.github.com/)（`gh`）。このチュートリアルで `gh` を使うのは、この確認のときだけだ。

このチュートリアル用に、空のフォルダーを作る。そのフォルダーでターミナルを開き、このページのコマンドはすべてそこで打つ。

Linux と macOS では、このフォルダーにグループやほかのユーザーが書き込めてはならない。サーバーが自分の秘密鍵を、この中のフォルダーに置くからだ。そのフォルダーで `chmod go-w .` と打てば、グループとほかのユーザーは確実に書き込めなくなる。

`elm` ツール、`elm-server`、`ElasticLM.Licensing` と `ElasticLM.Client` のパッケージは、GitHub のプレリリースからダウンロードする。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)を開き、「Pre-release」のラベルが付いたリリースのうち、いちばん新しいものを使う。GitHub が「Latest」のラベルを付けるのはプレリリースでないリリースだけなので、そのラベルを目安にしない。

リリースの Assets から、次のファイルをチュートリアルのフォルダーに保存する。

- 自分のシステムに合うアーカイブ `elm-<version>-<RID>` と `elm-server-<version>-<RID>`。`<RID>` は `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`、`osx-x64` のどれかだ。Windows 用は `.zip` ファイル、それ以外は `.tar.gz` ファイルになっている。名前が `-symbols` で終わるアーカイブにはデバッグシンボルしか入っていないので、そうでないほうを取る。
- `ElasticLM.Client.<version>.nupkg` と `ElasticLM.Licensing.<version>.nupkg`。アプリは `ElasticLM.Client` を使い、`ElasticLM.Client` は `ElasticLM.Licensing` に依存するので、両方が要る。同じ名前の `.snupkg` ファイルは、パッケージのデバッグシンボルなので要らない。
- ファイルのハッシュの一覧 `SHA256SUMS`。

展開したり使ったりする前に、チュートリアルのフォルダーにあるアーカイブとパッケージを、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめる。この節には、ダウンロードしたプログラムを macOS で動かす方法と、プログラムに必要な Linux と macOS のバージョンも書いてある。

2 つのアーカイブを `tools` という名前のフォルダーに展開する。Windows の PowerShell では `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` と打ってから `Expand-Archive elm-server-<version>-<RID>.zip -DestinationPath tools -Force` と打つ。どちらのアーカイブにも同じ `LICENSE` ファイルが入っていて、`-Force` を付けると 2 つ目で上書きできる。Linux と macOS では `mkdir -p tools` と打ってから、`tar -xzf elm-<version>-<RID>.tar.gz -C tools` と `tar -xzf elm-server-<version>-<RID>.tar.gz -C tools` を打つ。これで `tools` には `elm` と `elm-server`（Windows では `elm.exe` と `elm-server.exe`）が入る。`elm` は鍵を作りライセンスを発行するコマンドラインツールで、`elm-server` はライセンスサーバーだ。

<!-- steps:prebuilt name=elm -->
<!-- steps:prebuilt name=elm-server -->

`elm` と `elm-server` というコマンドで `tools` の中のプログラムが動くように、このターミナルで `tools` フォルダーを `PATH` に足す。Windows の PowerShell では `$env:PATH = "$PWD/tools;$env:PATH"` と打ち、Bash か zsh では `export PATH="$PWD/tools:$PATH"` と打つ。

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

鍵は 2 つ要る。アプリとサーバーが信頼するルート鍵と、ライセンスに署名する署名鍵だ。署名鍵が署名したものが受け入れられるように、ルート鍵が署名鍵を証明する。

`elm` は、それぞれの秘密鍵をパスフレーズで暗号化したファイルに保存する。鍵を作るときは、12 文字以上の新しいパスフレーズを尋ね、もう一度入力させる。あとのコマンドがその鍵を使うときは、そのパスフレーズをまた尋ねる。入力中は何も表示されない。このチュートリアルでは、2 つの鍵に同じパスフレーズを使う。環境変数 `ELM_KEY_PASSPHRASE` が設定されていれば、`elm` はパスフレーズをそこから取り、尋ねない。そのため、下の出力にあるパスフレーズを尋ねる行は表示されない。このターミナルで設定するには、PowerShell では `$env:ELM_KEY_PASSPHRASE = '<passphrase>'`、Bash か zsh では `export ELM_KEY_PASSPHRASE='<passphrase>'` と打つ。これはスクリプトや、誰も入力できないターミナルのためのものだ。実際の運用では、スクリプトにパスフレーズを書いておかない。詳しくは、`elm` のリファレンスの[パスフレーズ](../reference/elm-cli.md#パスフレーズ)を参照。

下の出力にある鍵 ID、サーバーのインストール鍵、トークン ID、マシンフィンガープリント、日時は架空の値だ。手元のマシンでは毎回違う値になる。

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

ライセンスの署名に使うために、ルート鍵で署名鍵を証明する。

<!-- steps:run -->
```shell
elm key certify --root root.key.pem --public signing.pub.pem --use license --out signing.cert
```

`elm` は次のように表示する。

```text
Passphrase for 'root.key.pem':
certificate: signing.cert
signing key ID: FAKE-SIGNING-KEY-ID-000000000000000000000S1
root key ID: FAKE-ROOT-KEY-ID-000000000000000000000000R1
uses: license
valid: 2026-01-15 09:30:00Z to 2027-01-15 09:30:00Z
```

`signing.cert` が鍵証明書だ。有効期間は 1 年で、そのあいだ署名鍵は新しいライセンスに署名してよい。この有効期間は、署名鍵が署名したライセンスがいつまで有効かを制限しないので、ライセンスの期限はずっとあとでもよい。この点は、[鍵証明書](../explanation/trust-chain.md#鍵証明書)の「有効期間と発行時刻」で説明している。

## サーバーを準備する

チュートリアルのフォルダーに、次の内容で `server.json` という名前のファイルを作る。これがサーバーの設定だ。

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  }
}
```

- `product` は、サーバーがシートを貸し出す製品 `acme-cad` だ。
- `listen` は、アプリがサーバーに接続する場所だ。ポート 7070 で、自分のマシンからだけ接続できる。
- `license` は、下で発行するサーバーライセンスのファイルだ。
- `dataDirectory` は、サーバーが自分の鍵と記録を置くフォルダーだ。
- `trust.rootKeys` は、サーバーがサーバーライセンスを受け入れるルート公開鍵の一覧だ。

パスは、サーバーを起動したフォルダーからの相対パスだ。また、`elm server` のコマンドのために、サーバーは `http://127.0.0.1:7071` でも待ち受ける。これは既定の値なので、設定は要らない。すべての設定は [elm-server の設定](../reference/server-configuration.md)に挙げている。

サーバーライセンスは、サーバーのインストール鍵を通して 1 つのサーバーに紐付く。インストール鍵は、サーバーが初めて起動したときに作る鍵ペアだ。鍵を作り、そのサムプリント（鍵の短い名前）を見るために、サーバーを一度起動する。

<!-- steps:run capture=server-key stream=stderr exit=1 pattern="installation key ([A-Za-z0-9_-]{43})" -->
```shell
elm-server --config server.json
```

サーバーはまだサーバーライセンスを持たないので、終了コード 1 ですぐに止まり、次のエラーを表示する。これは想定どおりだ。

```text
error: The server license 'server.elmlic' does not exist. Ask the vendor for a server license bound to this server: installation key FAKE-SERVER-KEY-00000000000000000000000000K, fingerprints 00000000000000000000000000000001, 00000000000000000000000000000002. Or activate one with elm server activate, or elm server activate-request offline.
```

`installation key` のあとの値がサムプリントだ。43 文字で、`-` と `_` を含むことがある。鍵そのものは `server-data` に残る。`fingerprints` のあとの値は、このマシンのマシンフィンガープリントだ。`--fingerprint` を付けて発行したライセンスはマシンにも紐付くが、このチュートリアルではこの値を使わない。実際の運用では、顧客の管理者がこのサムプリントをベンダーに送り、ベンダーがサーバーライセンスを発行する。

Linux と macOS では、チュートリアルのフォルダーにグループやほかのユーザーが書き込めると、サーバーは代わりに `error: The folder '…/server-data' is not safe for secrets: its parent folder '…' is writable by group or others.` と、それに続くもう 1 文を表示し、やはり終了コード 1 で終わる。チュートリアルのフォルダーで `chmod go-w .` と打ち、同じ `elm-server` コマンドをもう一度打つ。先に `server-data` を消す必要はない。

サーバーライセンスを発行する。機能 `pro` のシートを 2 つ含み、サーバーの鍵に紐付けたライセンスだ。`<server-key>` は、サーバーが表示したサムプリントに置き換える。43 文字すべてを正確に写す。

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id floating-server --kind server --feature "pro;seats=2" --expires 2099-12-31T00:00:00Z --bind-key <server-key> --lease-minutes 1 --borrow-days 1 --trust root.pub.pem --out server.elmlic
```

`elm` は次のように表示する。

```text
Passphrase for 'signing.key.pem':
license file: server.elmlic
license ID: floating-server
token ID: FAKEjti000000000000002
```

- `--kind server` はサーバーライセンスを作る。サーバーライセンスを使うのは `elm-server` だけだ。
- `--feature "pro;seats=2"` は、機能 `pro` をシート 2 つで与える。この機能を同時に持てるアプリのコピーは 2 つまでだ。
- `--expires` は、ライセンスの期限を UTC の 2099 年 12 月 31 日 00:00 にする。上で説明したとおり、鍵証明書の 1 年はこの期限を制限しない。
- `--bind-key` は、ライセンスをサーバーのインストール鍵に紐付ける。そのため、ライセンスはそのサーバーでだけ有効だ。
- `--lease-minutes 1` は、各リースの期間を 1 分にする。既定は 10 分だ。次の節で説明するとおり、待つ時間を短くするために、このチュートリアルでは 1 分にしている。
- `--borrow-days 1` は、アプリがシートを最長 1 日借用できるようにする。これがなければ借用はできない。
- `--trust root.pub.pem` を付けると、`elm` は新しいライセンスをルート公開鍵で検証し、検証に通ったときだけファイルを書く。そのため、コマンドがエラーなしで終われば、できたライセンスは検証に通る。

## サーバーを起動する

チュートリアルのフォルダーで 2 つ目のターミナルを開き、1 つ目のターミナルと同じ方法で、`tools` フォルダーをその `PATH` にも足す。`PATH` はターミナルごとに別だからだ。そこでサーバーを起動する。

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

サーバーは動き続け、ログを表示する。各エントリーは、`info:` で始まってエントリーの出どころを示す行と、そのあとにインデントして続くメッセージからなる。最初のいくつかのエントリーは、サーバーが動く Web ホストが出す。待ち受ける 2 つのアドレス、`Application started. Press Ctrl+C to shut down.`、ホスティング環境、`Content root path` だ。`Content root path` は `elm-server` の実行ファイルがあるフォルダーで、`server.json` の中のパスとは関係ない。探す行は `server.started` で、サーバーが起動したことを示す。

```text
info: elm-server[1]
      server.started: listening on port 7070 as https://127.0.0.1:7070/, key FAKE-SERVER-KEY-00000000000000000000000000K, license floating-server, in 68 ms; reclaim window until 2026-01-15T09:41:00.0000000+00:00
```

最後の時刻は、`+00:00` が示すとおり UTC だ。

ポート 7070 か 7071 がふさがっていると、サーバーは代わりに、`Unhandled exception. System.IO.IOException: Failed to bind to address https://127.0.0.1:7070: address already in use.`（または `http://127.0.0.1:7071`）で始まる長いエラーを表示して、すぐに止まる。そのポートを使っているプログラムを止めてから、サーバーをもう一度起動する。

起動してから最初のリースの期間（このライセンスでは 1 分）のあいだ、サーバーは新しいリースを拒否する。再起動のあと、それまでリースを保持していたアプリがリースを再登録するのに、その時間が要るからだ。サーバーは動かしたままにして、残りの手順は 1 つ目のターミナルで進める。アプリを作るあいだに、この 1 分のほとんどが過ぎる。

## アプリを作る

フォルダー `app` にコンソールアプリを作り、`packages` から `ElasticLM.Client` パッケージを追加する。`ElasticLM.Licensing` も一緒に入る。`packages` にあるのはプレリリースのバージョンだけなので、`--prerelease` を付けてプレリリースのバージョンも選べるようにする。`--source packages` を付けると、`dotnet add` はバージョンを `packages` の中だけで探す。

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Client --prerelease --source packages
```

`app/Program.cs` の中身を、次のコードに置き換える。

<!-- sample: samples/FloatingLease/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

// Leases the feature pro from the license server, returns the lease, and borrows the feature for one hour.
//
//   app <root-public-key.pem> <data-folder>
//
// A real app embeds the root public key instead of reading it from a file.
const string ProductId = "acme-cad";
const string FeatureId = "pro";
const int MaxAttempts = 3;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <data-folder>");
    return 2;
}

try
{
    var options = new LicenseClientOptions
    {
        ProductId = ProductId,
        ServerUrl = new Uri("https://127.0.0.1:7070/"),
        Trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []),
        DataDirectory = args[1],
    };
    if (!LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error))
    {
        Console.Error.WriteLine($"error: {error!.WireName}: {error.Message}");
        return 2;
    }

    // Disposing the client returns the leases it still holds, but not a borrow.
    await using LicenseClient client = created!;

    switch (await WithRetryAsync(() => client.AcquireLeaseAsync(FeatureId)))
    {
        case LeaseResult.Acquired acquired:
            Console.WriteLine($"leased: {FeatureId}");

            // The app uses the feature here, while the client renews the lease in the background.
            if (await client.ReleaseLeaseAsync(acquired.Lease) is LicenseError releaseError)
            {
                return Refused(releaseError);
            }

            break;
        case LeaseResult.Failed failed:
            return Refused(failed.Error);
    }

    switch (await WithRetryAsync(() => client.BorrowLeaseAsync(FeatureId, TimeSpan.FromHours(1))))
    {
        case LeaseResult.Acquired:
            Console.WriteLine($"borrowed: {FeatureId} for 1 hour");
            return 0;
        case LeaseResult.Failed failed:
            return Refused(failed.Error);
        default:
            return 1;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
{
    // A file that cannot be read, or a first argument that is not a public key in PEM.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// A server that has just started refuses new leases until its clients have had time to reclaim theirs, and says how long
// to wait. This waits that long and asks again, at most MaxAttempts times in all.
static async Task<LeaseResult> WithRetryAsync(Func<Task<LeaseResult>> request)
{
    for (int attempt = 1; ; attempt++)
    {
        LeaseResult result = await request();
        if (attempt == MaxAttempts || result is not LeaseResult.Failed { Error.Code: ErrorCode.ServerRecovering, RetryAfter: TimeSpan wait })
        {
            return result;
        }

        Console.WriteLine($"waiting {wait.TotalSeconds:0} s: the server is reclaiming leases after its start");
        await Task.Delay(wait);
    }
}

// The server refused the request: print its error code and reason.
static int Refused(LicenseError error)
{
    Console.WriteLine($"{error.WireName}: {error.Message}");
    return 1;
}
```

アプリは、ルート公開鍵をトラストストア（アプリが信頼するルート鍵の集まり）に読み込み、製品 `acme-cad` とサーバー `https://127.0.0.1:7070/` に対する `LicenseClient` を作る。クライアントは、2 つ目の引数のデータフォルダーに自分のファイルを置く。置くのは、サーバーがこのアプリのコピーを見分けるためのクライアント自身のインストール鍵、最初に確かめたあとのサーバーの鍵の記録、借用だ。

そのあとアプリは 3 つのリクエストを送る。どれも `WithRetryAsync` を通す。`WithRetryAsync` は、サーバーの起動から 1 分が過ぎるまでのあいだ、待ってから尋ね直す。

1. `AcquireLeaseAsync` は、`pro` のシート 1 つのリースを受ける。最初のリクエストでは、クライアントはサーバーライセンスも取得し、ルート公開鍵で確かめる。こうしてサーバーは、ベンダーからシートを与えられたことを証明する。接続は HTTPS で、サーバーが自分のインストール鍵から作る自己署名証明書を使う。サーバーライセンスがその鍵を指しているので、クライアントはこの証明書を受け入れる。この仕組みは [TLS](../explanation/floating-licenses.md#tls) で説明している。リースは、返却するまでクライアントがバックグラウンドで更新する。
2. `ReleaseLeaseAsync` はリースを返却する。これでシートはふたたび空く。
3. `BorrowLeaseAsync` は、シートを 1 時間借用する。借用は更新しない。ネットワークがなくても期限が切れるまで有効で、クライアントはそれをデータフォルダーに保存する。

最後に `await using` がクライアントを破棄し、まだ保持しているリースを返却する。借用は残る。`LeaseResult` は、リースを持つ `Acquired` か、エラーを持つ `Failed` のどちらかで、サーバーが拒否したときはアプリがエラーコードと理由を表示する。このアプリは、チュートリアルを短くするためにルート公開鍵をファイルから読む。実際のアプリは、`root.pub.pem` のテキストを文字列の定数などとしてコードに埋め込む。

## アプリを動かす

ルート公開鍵と、クライアントが作るデータフォルダー `client-data` を渡して、アプリを動かす。

<!-- steps:run -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

最初の `dotnet run` はアプリをビルドするので、何も表示しないまま数秒かかる。サーバーが起動してから 1 分経っていなければ、アプリはそのあと `waiting 48 s: the server is reclaiming leases after its start` のような行を表示して待つ。それから次のように表示する。

<!-- steps:expect -->
```text
leased: pro
borrowed: pro for 1 hour
```

そのあいだ、サーバーのターミナルにはリクエストが表示される。Web ホストは、リクエストごとに、`Request starting` から HTTP のステータスを含む `Request finished` までの `info:` のエントリーをいくつか出す。その中に次の行がある。ここではほかの行を省き、ID は架空の値にしている。

```text
      Request finished HTTP/1.1 POST https://127.0.0.1:7070/v1/leases - 503 - application/problem+json 0.9008ms
      lease.acquired: FAKE-LEASE-ID-00000001 pro x1 for key FAKE-APP-KEY-00000000000000000000000000000A
      lease.released: FAKE-LEASE-ID-00000001
      lease.acquired: FAKE-LEASE-ID-00000002 pro x1 for key FAKE-APP-KEY-00000000000000000000000000000A
```

リースを求める最初のリクエストは、サーバーがまだリースを再登録している最中だったので、ステータス 503 で終わった。アプリが待つと表示したのは、このためだ。そのあとサーバーがリースを貸し出し、アプリがそれを返却し、サーバーが借用を貸し出した。ログでは借用も `lease.acquired` と呼ぶ。行の最後の鍵は、アプリのインストール鍵だ。アプリが待たなかったときは、503 の行はない。

代わりにアプリがエラーコードを表示したときは、そのコードで、何が失敗したかがわかる。

| コード | このチュートリアルで考えられる原因 | 対処 |
| --- | --- | --- |
| `ELM_NETWORK_ERROR` | サーバーが動いていないか、別のアドレスで待ち受けている。 | [サーバーを起動する](#サーバーを起動する)のとおりに 2 つ目のターミナルでサーバーを起動し、`server.json` の `listen` が `https://127.0.0.1:7070` であることを確かめる。 |
| `ELM_NO_SEATS` | 1 時間以内にアプリを 2 回以上動かしたので、両方のシートが借用されている。 | 次の節を参照。 |
| `ELM_SERVER_PIN_MISMATCH` | 前に動かしたあとで `server-data` を消したので、サーバーのインストール鍵が新しくなったが、`client-data` には古いサーバーの鍵が残っている。 | [サーバーを確かめる](#サーバーを確かめる)の最後に書いた方法で、最初からやり直す。 |
| `ELM_UNTRUSTED_KEY` | 1 つ目の引数が、`signing.pub.pem` のような、`root.pub.pem` 以外の公開鍵になっている。 | `root.pub.pem` を渡す。 |

アプリが `usage: app <root-public-key.pem> <data-folder>` と表示したときは、引数がちょうど 2 つではなかった。`error: A root key is not a P-256 public key in PEM.` で始まる行を表示したときは、1 つ目の引数が公開鍵のファイルではない。ファイルが見つからないという `error:` の行には、開こうとしたパスが出る。アクセスが拒否されたという `error:` の行は、たいてい 2 つの引数の順序が逆になっていることを示す。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げている。

## サーバーを確かめる

サーバーが何を保持しているかを尋ねる。

<!-- steps:run -->
```shell
elm server status --data server-data
```

`elm` は、サーバーの管理者トークンを `server-data` から読み、ポート 7071 でサーバーに尋ねる。ほかの行に混じって、次の行を表示する。

<!-- steps:expect -->
```text
serving
leases: 1
  pro: 1 of 2 seats in use (2 with overage, 1 borrowed)
```

これらの行の前には、製品、サーバーライセンスの ID とトークン ID、その有効期限、サーバーの鍵を表示する。`serving` は、サーバーがもうリースを再登録していないことを示す。起動後の 1 分が終わったか、それより前に再登録されたリースですべてのシートが埋まったかのどちらかだ。アプリが返却したリースはもうない。残っている 1 つのリースは借用で、1 時間後に期限が切れるまで、2 つのシートのうち 1 つを使う。ライセンスは上限を超えるシートを認めていないので、超過分を含めた上限も 2 だ。

その 1 時間のうちにアプリを動かすたびに、借用するシートが 1 つ増える。2 回目で両方のシートが借用され、3 回目は `ELM_NO_SEATS: No seats are free for the feature.` と表示して終了コード 1 で終わる。借用したシートも使用中と数えるからだ。

サーバーを止める。2 つ目のターミナルで Ctrl+C を押す。サーバーは `Application is shutting down...` と `server.stopped: 1 leases were not returned` を表示して終わる。この 1 つのリースは借用だ。サーバーは借用の記録を `server-data` に残すので、ふたたび起動しても、借用の期限が切れるまではそれを数える。

最初からやり直すには、サーバーを止めた状態で、チュートリアルのフォルダーを丸ごと消す。そうすれば、`server-data` にあるサーバーの借用の記録と、`client-data` にあるアプリの借用のファイルの両方が消える。そのあと、`tools` と `packages` へのダウンロードも含めて、[始める前に](#始める前に)からやり直す。`server-data` だけを消してはいけない。そうすると、サーバーが新しいインストール鍵を作るので、新しいサーバーライセンスが要る。また、古い `client-data` を持つアプリは、最初に確かめたサーバーの鍵を保持しているので、新しいサーバーを `ELM_SERVER_PIN_MISMATCH` で拒否する。

## 作ったもの

ルート鍵と署名鍵を作り、サーバーの鍵に紐付けたシート 2 つのサーバーライセンスで `elm-server` を準備し、シートのリースを受けて返却し、1 時間シートを借用する .NET のアプリを書いた。実際の運用では、ベンダーがサーバーライセンスを発行し、顧客の管理者が、リースの期間を既定の 10 分にしたサーバーを自分のネットワークで動かす。

さらに知るには、次のページを読む。

- [フローティングライセンス](../explanation/floating-licenses.md): リース、リースの更新、サーバーが再起動したときに何が起きるか、借用。
- [elm-server の設定](../reference/server-configuration.md): サーバーのすべての設定と、そのデータフォルダー。
- [.NET API](../reference/dotnet-api.md#licenseclient): `LicenseClient`、そのオプション、各メソッドが返すもの。
- [ライセンスを発行して .NET のアプリで確かめる](verify-a-license-offline.md): サーバーなしで、アプリが自分で確かめるライセンス。
