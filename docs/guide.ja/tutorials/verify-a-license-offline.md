---
sidebar_position: 1
---

# ライセンスを発行して .NET のアプリで確かめる

このチュートリアルでは、ベンダーの鍵を作り、その鍵でライセンスを発行する。そして、そのライセンスを手元のマシンで、ネットワークを使わずに確かめる小さな .NET のコンソールアプリを書く。ElasticLM を初めて使う人のうち、ターミナルと `dotnet` コマンドを使える人に向けている。

## 始める前に

.NET 10 SDK が要る。`dotnet --list-sdks` の一覧に、`10.` で始まるバージョンがなければならない。ダウンロードしたファイルを確かめるには、[GitHub CLI](https://cli.github.com/)（`gh`）も要る。このチュートリアルで `gh` を使うのは、この確認のときだけだ。

このチュートリアル用に、空のフォルダーを作る。そのフォルダーでターミナルを開き、このページのコマンドはすべてそこで打つ。

`elm` ツールと `ElasticLM.Licensing` パッケージは、GitHub のプレリリースからダウンロードする。[Releases のページ](https://github.com/YUKIKEDA/ElasticLM/releases)を開き、「Pre-release」のラベルが付いたリリースのうち、いちばん新しいものを使う。GitHub が「Latest」のラベルを付けるのはプレリリースでないリリースだけなので、そのラベルを目安にしない。

リリースの Assets から、次の 3 つのファイルをチュートリアルのフォルダーに保存する。

- 自分のシステムに合うアーカイブ `elm-<version>-<RID>`。`<RID>` は `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`linux-musl-x64`、`osx-arm64`、`osx-x64` のどれかだ。Windows 用は `.zip` ファイル、それ以外は `.tar.gz` ファイルになっている。名前が `-symbols` で終わるアーカイブにはデバッグシンボルしか入っていないので、そうでないほうを取る。
- アプリが使うパッケージ `ElasticLM.Licensing.<version>.nupkg`。隣にある `ElasticLM.Licensing.<version>.snupkg` は、パッケージのデバッグシンボルなので要らない。
- ファイルのハッシュの一覧 `SHA256SUMS`。

展開したり使ったりする前に、チュートリアルのフォルダーにあるアーカイブとパッケージを、`SECURITY.md` の [Checking a download](https://github.com/YUKIKEDA/ElasticLM/blob/main/SECURITY.md#checking-a-download) のとおりに確かめる。この節には、ダウンロードしたプログラムを macOS で動かす方法と、プログラムに必要な Linux と macOS のバージョンも書いてある。

アーカイブを `tools` という名前のフォルダーに展開する。Windows の PowerShell では `Expand-Archive elm-<version>-<RID>.zip -DestinationPath tools` と打つ。Linux と macOS では `mkdir -p tools` と打ってから `tar -xzf elm-<version>-<RID>.tar.gz -C tools` と打つ。これで `tools` には、鍵を作り、ライセンスを発行するコマンドラインツール `elm`（Windows では `elm.exe`）と、`LICENSE` が入る。

<!-- steps:prebuilt name=elm -->

`elm` と打つだけで動くように、このターミナルで `tools` フォルダーを `PATH` に足す。Windows の PowerShell では `$env:PATH = "$PWD/tools;$env:PATH"` と打ち、Bash か zsh では `export PATH="$PWD/tools:$PATH"` と打つ。

パッケージを `packages` という名前のフォルダーに移す。PowerShell では `New-Item -ItemType Directory packages` と打ってから `Move-Item ElasticLM.Licensing.*.nupkg packages` と打つ。Bash か zsh では `mkdir -p packages` と打ってから `mv ElasticLM.Licensing.*.nupkg packages/` と打つ。

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

鍵は 2 つ要る。アプリが信頼するルート鍵と、ライセンスに署名する署名鍵だ。署名鍵が署名したものをアプリが受け入れるように、ルート鍵が署名鍵を証明する。

`elm` は、それぞれの秘密鍵をパスフレーズで暗号化したファイルに保存する。鍵を作るときは、12 文字以上の新しいパスフレーズを尋ね、もう一度入力させる。あとのコマンドがその鍵を使うときは、そのパスフレーズをまた尋ねる。入力中は何も表示されない。このチュートリアルでは、2 つの鍵に同じパスフレーズを使う。環境変数 `ELM_KEY_PASSPHRASE` が設定されていれば、`elm` はパスフレーズをそこから取り、尋ねない。そのため、下の出力にあるパスフレーズを尋ねる行は表示されない。このターミナルで設定するには、PowerShell では `$env:ELM_KEY_PASSPHRASE = '<passphrase>'`、Bash か zsh では `export ELM_KEY_PASSPHRASE='<passphrase>'` と打つ。これはスクリプトや、誰も入力できないターミナルのためのものだ。実際の運用では、スクリプトにパスフレーズを書いておかない。詳しくは、`elm` のリファレンスの[パスフレーズ](../reference/elm-cli.md#パスフレーズ)を参照。

下の出力にある鍵 ID、トークン ID、日時は架空の値だ。手元のマシンでは毎回違う値になる。

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

`root.key.pem` が秘密鍵で、`root.pub.pem` がその公開鍵だ。鍵 ID は、公開鍵から計算した短い名前だ。

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

## ライセンスを発行する

製品 `acme-cad` に機能 `pro` を付けたライセンスを発行する。期限は UTC の 2099 年 12 月 31 日 00:00 だ。

<!-- steps:run -->
```shell
elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id first-license --feature pro --expires 2099-12-31T00:00:00Z --trust root.pub.pem --out first.elmlic
```

`elm` は次のように表示する。

```text
Passphrase for 'signing.key.pem':
license file: first.elmlic
license ID: first-license
token ID: FAKEjti000000000000001
```

`first.elmlic` がライセンスファイルだ。署名したライセンスと、署名鍵の鍵証明書が入っている。トークン ID は、`elm` が署名するライセンスごとに付けるランダムな ID だ。`--trust` を付けると、`elm` は新しいライセンスをルート公開鍵で検証し、検証に通ったときだけファイルを書く。そのため、コマンドがエラーなしで終われば、できたライセンスは検証に通る。このライセンスはデバイスに紐付けていないので、どのマシンでも有効だ。

あとで作るアプリと同じように、`elm` でライセンスを検証する。

<!-- steps:run -->
```shell
elm license verify first.elmlic --trust root.pub.pem --product acme-cad --feature pro
```

`elm` は次のように表示する。

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, kind standard
expires: 2099-12-31 00:00:00Z
features: pro
```

## アプリを作る

フォルダー `app` にコンソールアプリを作り、`packages` から `ElasticLM.Licensing` パッケージを追加する。`packages` にあるのはプレリリースのバージョンだけなので、`--prerelease` を付けてプレリリースのバージョンも選べるようにする。`--source packages` を付けると、`dotnet add` はバージョンを `packages` の中だけで探す。

<!-- steps:run -->
```shell
dotnet new console -o app
dotnet add app package ElasticLM.Licensing --prerelease --source packages
```

`app/Program.cs` の中身を、次のコードに置き換える。

<!-- sample: samples/VerifyOffline/Program.cs -->
<!-- steps:file path=app/Program.cs -->
```csharp
using ElasticLM.Licensing;

// Verifies a license file offline and prints whether it is valid.
//
//   app <root-public-key.pem> <license.elmlic>
//
// A real app embeds the root public key instead of reading it from a file.
const string ProductId = "acme-cad";
const string FeatureId = "pro";

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: app <root-public-key.pem> <license.elmlic>");
    return 2;
}

try
{
    TrustStore trust = TrustStore.Create([File.ReadAllText(args[0])], revokedKeyIds: []);
    var verifier = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
    var requirement = new LicenseRequirement(ProductId, LicenseKind.Standard, FeatureId, FeatureVersion: null, Installation: null);
    LicenseVerificationResult result = verifier.Verify(File.ReadAllBytes(args[1]), requirement);

    switch (result)
    {
        case LicenseVerificationResult.Valid valid:
            Console.WriteLine($"valid: license {valid.License.LicenseId}, product {valid.License.ProductId}, feature {FeatureId}");
            return 0;
        case LicenseVerificationResult.Invalid invalid:
            Console.WriteLine($"invalid: {invalid.Error.WireName}: {invalid.Error.Message}");
            return 1;
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
```

アプリは、ルート公開鍵をトラストストア（アプリが信頼するルート鍵の集まり）に読み込む。失効した鍵 ID の一覧は空にしている。失効した鍵がないからだ。次に、ライセンスファイルを検証する。署名、鍵証明書、有効期限、製品 `acme-cad`、機能 `pro` を確かめる。`FeatureVersion: null` は機能のバージョンの確認を省き、`Installation: null` は、アプリがデバイスに紐付けていないライセンスだけを使うことを示す。最後に、有効なら `valid` とライセンスの ID を、そうでなければ `invalid` とエラーコード、理由を表示する。

このアプリは、チュートリアルを短くするためにルート公開鍵をファイルから読む。実際のアプリは、`root.pub.pem` のテキストを文字列の定数などとしてコードに埋め込む。また、このアプリが渡す `StatelessClockGuard` は、時計を戻されても気づかない。戻されたことに気づける時刻巻き戻し対策は、[時刻巻き戻しの検出と、その限界](../explanation/license-files-and-verification.md#時刻巻き戻しの検出とその限界)で説明している。

## アプリを動かす

ルート公開鍵とライセンスファイルを渡して、アプリを動かす。

<!-- steps:run -->
```shell
dotnet run --project app -- root.pub.pem first.elmlic
```

アプリは次のように表示する。

<!-- steps:expect -->
```text
valid: license first-license, product acme-cad, feature pro
```

代わりに `invalid:` と表示されたときは、そのあとに続くコードで、何が失敗したかがわかる。

| コード | このチュートリアルで考えられる原因 | 対処 |
| --- | --- | --- |
| `ELM_UNTRUSTED_KEY` | 1 つ目の引数が、`signing.pub.pem` のような、`root.pub.pem` 以外の公開鍵になっている。 | `root.pub.pem` を渡す。 |
| `ELM_WRONG_PRODUCT` | ライセンスを、`acme-cad` 以外の `--product` で発行した。 | `--product acme-cad` を付けてライセンスを発行し直す。`first.elmlic` を上書きするために `--force` も付ける。 |
| `ELM_FEATURE_NOT_LICENSED` | ライセンスを `--feature pro` なしで発行した。 | `--feature pro` と `--force` を付けてライセンスを発行し直す。 |
| `ELM_MALFORMED` | 2 つ目の引数が、`signing.cert` のような、ライセンスファイルでないものになっている。 | `first.elmlic` を渡す。 |

アプリが `error: A root key is not a P-256 public key in PEM.` で始まる行を表示したときは、1 つ目の引数が公開鍵のファイルではない。2 つの引数の順序を確かめる。ファイルが見つからないという `error:` の行には、開こうとしたパスが出る。すべてのコードは[エラーコード](../reference/error-codes.md)に挙げてあり、それぞれの確認に何が必要かは[起こりうる問題と対処](../explanation/license-files-and-verification.md#起こりうる問題と対処)に書いてある。

## 作ったもの

ルート鍵と署名鍵を作り、署名鍵を証明し、ライセンスを発行して、.NET のアプリでオフラインで検証した。実際の製品では、`root.key.pem` をオフラインに置く。この鍵は署名鍵を証明するためだけに使い、これを持つ人は誰でも、アプリが受け入れるライセンスを作れるからだ。

[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](use-a-floating-license.md)では、ライセンスサーバーからアプリに機能のシートを貸し出す。[SaaS 型のサービスを動かして .NET のアプリをオンラインでアクティベーションする](activate-an-app-online.md)では、デバイスをアクティベーションするサービスを動かし、アプリでトライアルを始めてから、ライセンスキーでアクティベーションする。

さらに知るには、次のページを読む。

- [ライセンスファイルと検証](../explanation/license-files-and-verification.md): ライセンスファイルに何が入っているかと、アプリが行ったすべての確認。
- [トラストチェーン](../explanation/trust-chain.md): ルート鍵と署名鍵、その鍵を安全に保つ方法。
- [.NET API](../reference/dotnet-api.md): アプリが使ったすべての型と、`LicenseVerifier` のオプション。
