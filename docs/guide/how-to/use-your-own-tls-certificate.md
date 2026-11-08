---
sidebar_position: 9
---

# Use your own TLS certificate

This guide is for a customer's administrator who runs `elm-server` and must give it a TLS certificate from the organization's certificate authority (CA) instead of the certificate that the server makes for itself. Most servers do not need this. The guide starts where you have finished [Run a license server and lease a floating license in a .NET app](../tutorials/use-a-floating-license.md): `elm-server` with a server license, and that tutorial's app. [TLS](../explanation/floating-licenses.md#tls) explains how apps check the server.

<!-- steps:after page=docs/guide/tutorials/use-a-floating-license.md -->

## Before you start

Type the commands in the folder of the tutorial, with its `tools` folder on your `PATH`. The steps below use the server's settings file `server.json`, the root public key `root.pub.pem`, and the tutorial's app in `app` with its data folder `client-data`.

If the server is stopped, start it again in a second terminal, as in [Start the server](../tutorials/use-a-floating-license.md#start-the-server). For a minute after each start, it refuses new leases, and the app waits.

The examples use the values of the tutorial: the product `acme-cad`, the feature `pro`, and the server at `https://127.0.0.1:7070`. Replace them with your own.

## Decide whether you need your own certificate

Without a `tls` setting, the server makes a self-signed certificate from its installation key each time it starts. An app accepts a certificate in one of two ways:

- Its operating system trusts the certificate. Then the app takes it as any HTTPS client does.
- The operating system does not trust it, and the certificate's key is the key that the server license names. The vendor signed the server license, so a party in the middle that presents a certificate of its own is refused.

So the self-signed certificate already protects the connection against a party in the middle whose certificate the app's operating system does not trust. A party with a certificate that the operating system trusts, such as a TLS inspection box of the organization, is accepted in both cases, and so is the server an app reaches first if the app does not name the server license it expects. [Security model](../explanation/security-model.md) lists what this does and does not protect against.

Give the server your own certificate when a rule of your organization asks for a certificate from its CA, for example so that its network tools accept the server. Every machine that runs the app must then trust that CA: the app no longer compares the certificate's key with the server license, and relies on the operating system's trust. Whoever can get a certificate from that CA for the server's name can read the traffic, but cannot make leases: leases are signed with the server's installation key.

## Make a test certificate

In real use, ask your CA for a certificate for the name or address that apps use to reach the server, with the purpose server authentication, and take the certificate and its private key as PEM files. To try the steps first, make a test CA and a certificate that it signs, for `127.0.0.1` and `localhost`, with this small app. It is for trying only.

Create the app in the folder `cert`. It uses .NET only, so it needs no package:

<!-- steps:run -->
```shell
dotnet new console -o cert
```

Replace the content of `cert/Program.cs` with this code:

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

The app makes a CA that may sign certificates of servers and other end entities, but no other CA, and a certificate for the IP address `127.0.0.1` and the name `localhost` with the purpose server authentication, both valid for 30 days. It writes the files with `FileMode.CreateNew`, so it never overwrites one, and on Linux and macOS it makes the private key readable by your user only. It keeps the CA's private key in memory only, so once the app ends, nobody can sign another certificate with this CA.

Run it with the folder `certs`:

<!-- steps:run -->
```shell
dotnet run --project cert -- certs
```

The app prints the three files, here with the `/` of Linux and macOS:

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

If it prints `error:` and a message that a file already exists, `certs` holds the files of an earlier run: use them, or delete the folder and run the app again.

## Give the server the certificate

Add `tls` to `server.json`, with the certificate and its private key. Keep every other setting as it is; with the settings of the tutorial, the file becomes:

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

The certificate's names must include the name or address in the apps' server URL, here `127.0.0.1`: otherwise the apps refuse the server with `ELM_SERVER_PIN_MISMATCH`, as below. A PKCS#12 file (`.pfx` or `.p12`) works too: name it in `tls.certificate` and leave out `tls.key`. If the file has a password, set it in the environment variable `ELM_TLS_PASSWORD` of the server's process; there is no setting for it in the file.

The server reads its certificate only when it starts, so restart it. In the second terminal, press Ctrl+C to stop the server, then start it again:

<!-- steps:background ready=https://127.0.0.1:7070/v1/server/license -->
```shell
elm-server --config server.json
```

If the server cannot use the certificate, it prints `error:` and the reason, and ends with exit code 1: for example, when a file cannot be read, when the key is not the certificate's key, or when the certificate's Extended Key Usage does not include server authentication. [Command line and exit codes](../reference/server-configuration.md#command-line-and-exit-codes) lists the reasons.

## See that the app needs to trust the CA

Run the tutorial's app again:

<!-- steps:run exit=1 -->
```shell
dotnet run --project app -- root.pub.pem client-data
```

It ends with exit code 1 and prints:

<!-- steps:expect -->
```text
ELM_SERVER_PIN_MISMATCH: The server's TLS key is not the key of its server license.
```

Your operating system does not trust the test CA, and the certificate's key is not the key that the server license names, so the app refuses the server. The message speaks of the key because that is the check that failed. The cause is that the operating system does not accept the certificate: here because it does not trust the CA, and in general also when the server's name or address is not in the certificate, or when the certificate has expired. The server is fine, and so are the server license and the app's data folder.

## Trust the CA on the machines that run the app

Every machine that runs the app must trust the CA that issued the server's certificate. With your organization's CA, the machines usually trust it already, through the organization's management of its machines; ask the people who run the CA.

A CA that you add as trusted is trusted by every program of the user or the machine, not only by your app. To try the test CA, add it in the narrowest place, and remove it when you are done:

- **Windows**: add it to the trusted root CAs of your user only, not of the machine, in PowerShell: `Import-Certificate -FilePath certs\test-ca.crt -CertStoreLocation Cert:\CurrentUser\Root`. Windows asks you to confirm. To remove it, find its thumbprint with `Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -Like '*ElasticLM test CA*'` and delete it with `Remove-Item Cert:\CurrentUser\Root\<thumbprint>`; Windows asks you to confirm this too.
- **macOS**: add it to your login keychain, trusted for TLS only: `security add-trusted-cert -r trustRoot -p ssl -k ~/Library/Keychains/login.keychain-db certs/test-ca.crt`. macOS asks for your password. To remove it, type `security remove-trusted-cert certs/test-ca.crt` and then `security delete-certificate -c "ElasticLM test CA (for trying only)" ~/Library/Keychains/login.keychain-db`.
- **Linux**: do not add the test CA to the system's store with `update-ca-certificates`, which needs root and changes the trust of every program. Give it to the app's process only, in the environment variable `SSL_CERT_FILE`, which names a file of CAs that .NET trusts: in Bash, type `SSL_CERT_FILE=certs/test-ca.crt dotnet run --project app -- root.pub.pem client-data`. That process trusts the test CA as well as the CAs of the system's certificate folder; no other program is changed, and nothing remains to remove.

Then run the app again. It prints `leased: pro` and `borrowed: pro for 1 hour`, as in the tutorial. With both seats borrowed, it prints `ELM_NO_SEATS` instead; [Check the server](../tutorials/use-a-floating-license.md#check-the-server) explains why.

## Clean up the test certificate

When you are done trying:

1. Remove the test CA from your user's trust on Windows or macOS, as above.
2. Delete the folder `certs`. It holds `server.key`, the private key of a certificate that your machine may have trusted: while the certificate is valid, whoever has the key could pose as `localhost` to programs that trust the test CA.
3. Take `tls` out of `server.json`, and restart the server. It then makes its self-signed certificate again, and the app accepts it as before. If both seats are borrowed by then, the app prints `ELM_NO_SEATS`, as above.

## See also

- [TLS](../explanation/floating-licenses.md#tls): how an app checks the server, and the first connection.
- [`tls`](../reference/server-configuration.md#tls) in the `elm-server` configuration, and its [exit codes](../reference/server-configuration.md#command-line-and-exit-codes).
- [`ELM_SERVER_PIN_MISMATCH`](../reference/error-codes.md) and the other error codes.
