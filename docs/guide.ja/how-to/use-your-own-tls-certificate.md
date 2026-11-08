---
sidebar_position: 9
---

# 自分の TLS 証明書を使う

このガイドは、`elm-server` を動かしていて、サーバーが自分で作る証明書の代わりに、組織の認証局（CA）が発行した TLS 証明書をサーバーに渡さなければならない顧客の管理者に向けている。ほとんどのサーバーでは、これは要らない。このガイドは、[ライセンスサーバーを動かして .NET のアプリでフローティングライセンスのリースを受ける](../tutorials/use-a-floating-license.md)を終えた状態から始める。サーバーライセンスを持つ `elm-server` と、そのチュートリアルのアプリがある状態だ。アプリがサーバーをどう確かめるかは [TLS](../explanation/floating-licenses.md#tls) で説明している。

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## 始める前に

コマンドは、チュートリアルのフォルダーで、その `tools` フォルダーを `PATH` に足した状態で打つ。下の手順では、サーバーの設定ファイル `server.json`、ルート公開鍵 `root.pub.pem`、`app` にあるチュートリアルのアプリとそのデータフォルダー `client-data` を使う。

サーバーが止まっていたら、[サーバーを起動する](../tutorials/use-a-floating-license.md#サーバーを起動する)のとおりに、2 つ目のターミナルでもう一度起動する。サーバーは起動するたびに、そのあと 1 分のあいだ新しいリースを拒否し、アプリは待つ。

例では、チュートリアルの値を使う。製品 `acme-cad`、機能 `pro`、`https://127.0.0.1:7070` のサーバーだ。自分の値に置き換える。

## 自分の証明書が要るかを決める

`tls` の設定がなければ、サーバーは起動するたびに、自分のインストール鍵から自己署名証明書を作る。アプリは、次の 2 つのどちらかで証明書を受け入れる。

- OS がその証明書を信頼している。このときアプリは、ほかの HTTPS のクライアントと同じようにその証明書を受け入れる。
- OS はその証明書を信頼していないが、証明書の鍵が、サーバーライセンスが指す鍵になっている。サーバーライセンスにはベンダーが署名したので、通信の途中にいる第三者が自分の証明書を示しても拒否される。

そのため、自己署名証明書でも、アプリの OS が信頼しない証明書を持つ第三者が通信の途中に入ることは防げる。組織の TLS 検査装置のように、OS が信頼する証明書を持つ第三者は、どちらの場合も受け入れられる。アプリが期待するサーバーライセンスを指定していないときに、アプリが最初につながったサーバーも同じだ。これで何を防げて何を防げないかは、[セキュリティモデル](../explanation/security-model.md)に挙げている。

組織の決まりで、その CA が発行した証明書が求められるときは、サーバーに自分の証明書を渡す。たとえば、組織のネットワークのツールにサーバーを受け入れさせるためだ。この場合、アプリを動かすすべてのマシンが、その CA を信頼していなければならない。アプリは証明書の鍵をサーバーライセンスと比べなくなり、OS の信頼に頼るからだ。その CA からサーバーの名前の証明書を得られる人は通信を読めるが、リースは作れない。リースには、サーバーのインストール鍵で署名するからだ。

## テスト用の証明書を作る

実際の運用では、アプリがサーバーに接続するときの名前かアドレスについて、サーバー認証の用途で証明書を発行するよう CA に頼み、証明書とその秘密鍵を PEM ファイルで受け取る。先に手順を試すなら、この小さなアプリで、テスト用の CA と、その CA が署名した `127.0.0.1` と `localhost` の証明書を作る。これは試すためだけのものだ。

`cert` フォルダーにアプリを作る。.NET の機能しか使わないので、パッケージは要らない。

<!-- steps:run -->
```shell
dotnet new console -o cert
```

`cert/Program.cs` の中身を、次のコードに置き換える。

<!-- sample: samples/MakeTestCertificate/Program.cs -->
<!-- steps:file path=cert/Program.cs -->
```csharp
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Makes a test certificate authority and a TLS certificate for elm-server that the authority signs. For trying a
// certificate of your own only: in real use, the server's certificate comes from your organization's certificate
// authority.
//
//   cert <out-folder>
//
// Writes test-ca.crt (the authority's certificate), server.crt (the server's certificate), and server.key (the server's
// private key). The authority's private key is never written, so nothing else can be signed with it.
const int ValidDays = 30;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: cert <out-folder>");
    return 2;
}

try
{
    string folder = args[0];
    Directory.CreateDirectory(folder);
    DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
    DateTimeOffset notAfter = notBefore.AddDays(ValidDays);

    // The authority: it may sign certificates of servers and other end entities, but no other authority (path length 0),
    // and nothing but certificates and lists of revoked certificates.
    using ECDsa authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var authorityRequest = new CertificateRequest("CN=ElasticLM test CA (for trying only)", authorityKey, HashAlgorithmName.SHA256);
    authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
    authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
    authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, critical: false));
    using X509Certificate2 authority = authorityRequest.CreateSelfSigned(notBefore, notAfter);

    // The server: the names a client connects to, the IP address 127.0.0.1 and the DNS name localhost, for TLS servers only.
    using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    names.AddDnsName("localhost");
    serverRequest.CertificateExtensions.Add(names.Build());
    serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
    serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
    serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
    serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
    using X509Certificate2 server = serverRequest.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));

    Write(Path.Combine(folder, "test-ca.crt"), authority.ExportCertificatePem(), secret: false);
    Write(Path.Combine(folder, "server.crt"), server.ExportCertificatePem(), secret: false);
    Write(Path.Combine(folder, "server.key"), serverKey.ExportPkcs8PrivateKeyPem(), secret: true);
    Console.WriteLine($"test CA: {Path.Combine(folder, "test-ca.crt")}");
    Console.WriteLine($"server certificate: {Path.Combine(folder, "server.crt")}");
    Console.WriteLine($"server private key: {Path.Combine(folder, "server.key")}");
    Console.WriteLine($"valid for {ValidDays} days, for 127.0.0.1 and localhost; for trying only");
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    // A file that is already there, or a folder that cannot be written.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Writes a new file and never overwrites one. The private key is readable by its owner only on Linux and macOS.
static void Write(string path, string pem, bool secret)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (secret && !OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    using var file = new FileStream(path, options);
    using var writer = new StreamWriter(file);
    writer.Write(pem);
}
```

このアプリは、サーバーやほかのエンドエンティティの証明書には署名できるが、ほかの CA の証明書には署名できない CA と、IP アドレス `127.0.0.1` と名前 `localhost` に対するサーバー認証の用途の証明書を作る。どちらも 30 日のあいだ有効だ。ファイルは `FileMode.CreateNew` で書くので、既存のファイルを上書きすることはない。Linux と macOS では、秘密鍵を自分のユーザーだけが読めるようにする。CA の秘密鍵はメモリーにしか置かないので、アプリが終わると、この CA でほかの証明書に署名することは誰にもできない。

フォルダー `certs` を指定して実行する。

<!-- steps:run -->
```shell
dotnet run --project cert -- certs
```

アプリは 3 つのファイルを表示する。ここでは Linux と macOS の `/` で示す。

```text
test CA: certs/test-ca.crt
server certificate: certs/server.crt
server private key: certs/server.key
valid for 30 days, for 127.0.0.1 and localhost; for trying only
```

<!-- steps:expect -->
```text
valid for 30 days, for 127.0.0.1 and localhost; for trying only
```

`error:` と、ファイルがすでにあるというメッセージを表示したときは、`certs` に前に実行したときのファイルが入っている。そのファイルを使うか、フォルダーを削除してアプリをもう一度実行する。

## サーバーに証明書を渡す

`server.json` に `tls` を足し、証明書とその秘密鍵を指定する。ほかの設定はそのままにする。チュートリアルの設定なら、ファイルは次のようになる。

<!-- steps:file path=server.json -->
```json
{
  "product": "acme-cad",
  "listen": "https://127.0.0.1:7070",
  "license": "server.elmlic",
  "dataDirectory": "server-data",
  "trust": {
    "rootKeys": ["root.pub.pem"]
  },
  "tls": {
    "certificate": "certs/server.crt",
    "key": "certs/server.key"
  }
}
```

証明書の名前には、アプリが使うサーバーの URL の名前かアドレス（ここでは `127.0.0.1`）が含まれていなければならない。含まれていないと、下のとおり、アプリは `ELM_SERVER_PIN_MISMATCH` でサーバーを拒否する。

CA が中間 CA でサーバーの証明書に署名しているなら、中間 CA の証明書を、同じ PEM ファイルのサーバーの証明書のあとに入れる。サーバーは自分の証明書と一緒にそれを送る。ルート CA は要らない。ファイルにないと、アプリを動かすマシンが中間 CA の証明書を自分で見つけなければならず、見つけられないと同じようにサーバーを拒否する。サーバーの証明書のあとにある証明書が CA の証明書でないと、サーバーは終了コード 1 で止まる。Windows では、サーバーのマシンが中間 CA を中間証明機関の証明書ストアに残す。このことと、足りない証明書のダウンロードは、[中間 CA の証明書](../reference/server-configuration.md#tls)で説明している。

PKCS#12 のファイル（`.pfx` か `.p12`）も使える。`tls.certificate` にそのファイルを指定し、`tls.key` は省く。中間 CA の証明書も同じファイルに入れられる。ファイルにパスワードがあるときは、サーバーのプロセスの環境変数 `ELM_TLS_PASSWORD` に設定する。パスワードを設定ファイルに書く設定はない。

サーバーは証明書を起動時にしか読まないので、再起動する。2 つ目のターミナルで Ctrl+C を押してサーバーを止め、もう一度起動する。

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

サーバーが証明書を使えないときは、`error:` と理由を表示し、終了コード 1 で終わる。たとえば、ファイルを読めないとき、鍵が証明書の鍵でないとき、証明書の拡張キー使用法（Extended Key Usage）にサーバー認証が含まれていないときだ。理由は[コマンドラインと終了コード](../reference/server-configuration.md#コマンドラインと終了コード)に挙げている。

## アプリが CA を信頼する必要があることを確かめる

チュートリアルのアプリをもう一度動かす。

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

アプリは終了コード 1 で終わり、次のように表示する。

<!-- steps:expect -->
```text
ELM_SERVER_PIN_MISMATCH: The server's TLS key is not the key of its server license.
```

OS がテスト用の CA を信頼しておらず、証明書の鍵もサーバーライセンスが指す鍵ではないので、アプリはサーバーを拒否する。メッセージが鍵のことを言うのは、失敗したのがその確認だからだ。原因は、OS が証明書を受け入れないことにある。ここでは CA を信頼していないからだが、一般には、サーバーの名前やアドレスが証明書にないときや、証明書の期限が切れているときにも同じことが起きる。サーバーにも、サーバーライセンスにも、アプリのデータフォルダーにも問題はない。

## アプリを動かすマシンで CA を信頼する

アプリを動かすすべてのマシンが、サーバーの証明書を発行した CA を信頼していなければならない。組織の CA なら、組織がマシンを管理する仕組みを通して、たいていのマシンはすでに信頼している。CA を運用している担当者に尋ねる。

信頼する CA として追加すると、その CA は自分のアプリだけでなく、そのユーザーかマシンのすべてのプログラムから信頼される。テスト用の CA を試すときは、いちばん狭い場所に追加し、終わったら削除する。

- **Windows**: マシンではなく自分のユーザーだけの、信頼されたルート CA に追加する。PowerShell で `Import-Certificate -FilePath certs\test-ca.crt -CertStoreLocation Cert:\CurrentUser\Root` と打つ。Windows が確認を求める。削除するには、`Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -Like '*ElasticLM test CA*'` でサムプリントを調べ、`Remove-Item Cert:\CurrentUser\Root\<thumbprint>` で削除する。このときも Windows が確認を求める。
- **macOS**: ログインキーチェーンに追加し、基本的な証明書の確認と TLS の用途に限って信頼する。`security add-trusted-cert -r trustRoot -p basic -p ssl -k ~/Library/Keychains/login.keychain-db certs/test-ca.crt` と打つ。これは、`dotnet dev-certs https --trust` が .NET の開発用証明書に与えるのと同じ信頼の設定だ。macOS がパスワードを尋ねる。削除するには、`security remove-trusted-cert certs/test-ca.crt` と打ってから、`security delete-certificate -c "ElasticLM test CA (for trying only)" ~/Library/Keychains/login.keychain-db` と打つ。
- **Linux**: テスト用の CA を `update-ca-certificates` でシステムのストアに追加しない。root 権限が要り、すべてのプログラムの信頼が変わるからだ。代わりに、環境変数 `SSL_CERT_FILE` で、アプリのプロセスにだけ渡す。この変数は、.NET が信頼する CA のファイルを指す。Bash で `SSL_CERT_FILE=certs/test-ca.crt dotnet run --project app -- root.pub.pem client-data` と打つ。そのプロセスは、システムの証明書フォルダーにある CA に加えて、テスト用の CA も信頼する。ほかのプログラムは何も変わらず、あとで削除するものも残らない。

そのあと、アプリをもう一度動かす。チュートリアルと同じように、`leased: pro` と `borrowed: pro for 1 hour` を表示する。両方のシートが借用されていれば、代わりに `ELM_NO_SEATS` を表示する。その理由は[サーバーを確かめる](../tutorials/use-a-floating-license.md#サーバーを確かめる)で説明している。

## テスト用の証明書を片付ける

試し終えたら、次のようにする。

1. 上のとおりに、Windows か macOS で、自分のユーザーの信頼からテスト用の CA を削除する。
2. フォルダー `certs` を削除する。そこにある `server.key` は、自分のマシンが信頼していたかもしれない証明書の秘密鍵だ。証明書が有効なあいだは、この鍵を持つ人なら誰でも、テスト用の CA を信頼するプログラムに対して `localhost` になりすませる。
3. `server.json` から `tls` を外し、サーバーを再起動する。サーバーはふたたび自己署名証明書を作り、アプリは前と同じようにそれを受け入れる。そのときまでに両方のシートが借用されていれば、上のとおり、アプリは `ELM_NO_SEATS` を表示する。

## 関連するページ

- [TLS](../explanation/floating-licenses.md#tls): アプリがサーバーをどう確かめるかと、最初の接続。
- `elm-server` の設定の [`tls`](../reference/server-configuration.md#tls) と、その[終了コード](../reference/server-configuration.md#コマンドラインと終了コード)。
- [`ELM_SERVER_PIN_MISMATCH`](../reference/error-codes.md) とほかのエラーコード。
