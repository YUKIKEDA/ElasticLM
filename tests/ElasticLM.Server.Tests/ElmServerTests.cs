using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// Start errors of <see cref="ElmServer"/> from its settings: a <see cref="ServerStartException"/>, which elm-server prints
/// as one "error:" line before it exits with 1, with no .NET parameter name in it (design §13.1, §17.3, V1c10).
/// </summary>
public sealed class ElmServerTests : IAsyncDisposable
{
    // design §8.6 (V1d24c): the operator's files that elm-server reads at its start.
    private const int RootKeyFileLimit = 16 * 1024;

    private const int OperatorFileLimit = 64 * 1024;

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// Trust settings that the trust store refuses stop the start with the trust store's sentence, and an empty revoked key
    /// ID with a sentence that names the setting.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; a trust.rootKeys file with a P-384 public key.
    ///
    /// Steps:
    /// - Start with that file as trust.rootKeys; start with trust.revokedKeyIds of the root key's ID and "".
    ///
    /// Expected:
    /// - ServerStartException "The trust settings are not valid: A root key is not a P-256 public key in PEM."; and
    ///   ServerStartException "trust.revokedKeyIds must not hold an empty or null entry.".
    /// </remarks>
    [Fact]
    public async Task Start_TrustSettingsNotValid_ThrowsWithoutParameterName()
    {
        string data = _deployment.NewFolder();
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string p384File = _deployment.FilePath("p384.pub.pem");
        File.WriteAllText(p384File, p384.ExportSubjectPublicKeyInfoPem() + "\n");

        var notP256 = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { TrustRootKeyFiles = [p384File] })
        );
        var emptyId = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { TrustRevokedKeyIds = [_deployment.Trust.RootKeyIds.Single(), string.Empty] })
        );

        Assert.Equal("The trust settings are not valid: A root key is not a P-256 public key in PEM.", notP256.Message);
        Assert.Equal("trust.revokedKeyIds must not hold an empty or null entry.", emptyId.Message);
    }

    /// <summary>
    /// An empty dataDirectory, or one of white space only, stops the start with a sentence that names the setting, not with
    /// the key store's ArgumentException (design §17.3, V1c10 R5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, whose path the settings then replace.
    ///
    /// Steps:
    /// - Start with dataDirectory ""; start with dataDirectory of three spaces.
    ///
    /// Expected:
    /// - ServerStartException "dataDirectory must not be empty or only white space." both times.
    /// </remarks>
    [Fact]
    public async Task Start_DataDirectoryBlank_ThrowsNamingTheSetting()
    {
        string data = _deployment.NewFolder();
        foreach (string blank in new[] { string.Empty, "   " })
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(data, s => s with { DataDirectory = blank })
            );
            Assert.Equal("dataDirectory must not be empty or only white space.", refused.Message);
        }
    }

    /// <summary>
    /// An empty trust.rootKeys entry, tls.certificate, or tls.key, or one of white space only, stops the start with a sentence
    /// that names the setting, not with the ArgumentException of the file system (design §17.3, V1c10 A1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license.
    ///
    /// Steps:
    /// - Start with trust.rootKeys of the root key file and the blank value; with tls.certificate the blank value; with
    ///   tls.certificate a file name and tls.key the blank value. The blank value is "" and then three spaces.
    ///
    /// Expected:
    /// - ServerStartException "trust.rootKeys must not hold an empty, white-space-only, or null entry.", "tls.certificate must
    ///   not be empty or only white space.", and "tls.key must not be empty or only white space.", for both blank values.
    /// </remarks>
    [Fact]
    public async Task Start_TrustOrTlsPathBlank_ThrowsNamingTheSetting()
    {
        string data = _deployment.NewFolder();
        foreach (string blank in new[] { string.Empty, "   " })
        {
            Assert.Equal(
                "trust.rootKeys must not hold an empty, white-space-only, or null entry.",
                await RefusedAsync(data, s => s with { TrustRootKeyFiles = [_deployment.RootKeyFile, blank] })
            );
            Assert.Equal("tls.certificate must not be empty or only white space.", await RefusedAsync(data, s => s with { TlsCertificate = blank }));
            Assert.Equal(
                "tls.key must not be empty or only white space.",
                await RefusedAsync(data, s => s with { TlsCertificate = _deployment.FilePath("tls.crt"), TlsKey = blank })
            );
        }
    }

    /// <summary>
    /// An empty license setting, or one of white space only, stops the start with a sentence that names the setting, not with
    /// the ArgumentException of the file system (design §17.3, V1c10 D3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, whose license path the settings then replace.
    ///
    /// Steps:
    /// - Start with license ""; start with license of three spaces.
    ///
    /// Expected:
    /// - ServerStartException "license must not be empty or only white space." both times.
    /// </remarks>
    [Fact]
    public async Task Start_LicenseBlank_ThrowsNamingTheSetting()
    {
        string data = _deployment.NewFolder();
        foreach (string blank in new[] { string.Empty, "   " })
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(data, s => s with { LicensePath = blank })
            );
            Assert.Equal("license must not be empty or only white space.", refused.Message);
        }
    }

    /// <summary>
    /// With service.url, the start's check that the license path can be written deletes a symbolic link it finds at its
    /// temporary path and leaves the file the link points to alone (design §12.6.2, §13.7, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; a file victim.txt ("victim") in another folder, and a symbolic link to it at
    ///   server.elmlic.tmp beside the license. Where the test cannot make a link (Windows without the right to), it is
    ///   skipped and says so; GitHub's runners can.
    ///
    /// Steps:
    /// - Start the server with service.url (an address that no one answers; the start does not call it without a kept
    ///   activation).
    ///
    /// Expected:
    /// - The server starts; victim.txt still holds "victim"; nothing is left at server.elmlic.tmp.
    /// </remarks>
    [Fact]
    public async Task Start_LinkAtTheLicenseProbe_LeavesTheLinkTargetAlone()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string victim = _deployment.FilePath("victim.txt");
        File.WriteAllText(victim, "victim");
        string probe = Path.Combine(data, "server.elmlic.tmp");
        try
        {
            File.CreateSymbolicLink(probe, victim);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run: " + e.Message);
            Assert.Skip("This user cannot make a symbolic link: " + e.Message);
        }

        await _deployment.StartServerAsync(data, s => s with { ServiceUrl = new Uri("https://127.0.0.1:9/") });

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Null(new FileInfo(probe).LinkTarget);
        Assert.False(File.Exists(probe));
    }

    /// <summary>
    /// The operator's files are read up to their limits (design §8.6, V1d24c): a trust.rootKeys file of 16 KiB and the TLS
    /// certificate and key of 64 KiB each start the server.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license. A trust.rootKeys file with the vendor's root key and as many other P-256 public
    ///   keys as fit, then line breaks up to 16,384 bytes; a self-signed certificate for 127.0.0.1 and its key as PEM files,
    ///   each with line breaks up to 65,536 bytes.
    ///
    /// Steps:
    /// - Start with those files.
    ///
    /// Expected:
    /// - The files have those lengths; a server that listens on https.
    /// </remarks>
    [Fact]
    public async Task Start_OperatorFilesAtTheirLimits_Starts()
    {
        string roots = _deployment.FilePath("roots.pub.pem");
        WriteRootKeysAtLimit(roots, File.ReadAllText(_deployment.RootKeyFile), RootKeyFileLimit);
        (string certificate, string key) = WriteTlsAtLimit("tls-limit", OperatorFileLimit);

        ElmServer server = await _deployment.StartServerAsync(change: s =>
            s with
            {
                TrustRootKeyFiles = [roots],
                TlsCertificate = certificate,
                TlsKey = key,
            }
        );

        Assert.Equal([RootKeyFileLimit, OperatorFileLimit, OperatorFileLimit], new[] { roots, certificate, key }.Select(p => new FileInfo(p).Length));
        Assert.Equal(Uri.UriSchemeHttps, server.PublicUrls[0].Scheme);
    }

    /// <summary>
    /// An operator's file one byte past its limit stops the start with a sentence that names the file, a PKCS#12 file too;
    /// a PKCS#12 file at the limit is read and refused for what it holds (design §8.6, V1d24c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; the files of Start_OperatorFilesAtTheirLimits_Starts, each at its limit; then
    ///   one of them with one more line break. For PKCS#12, a file of 65,537 zero bytes, and one of 65,536.
    ///
    /// Steps:
    /// - Start with those files, for each file in turn: trust.rootKeys, the TLS certificate, the TLS key, and as tls.certificate
    ///   without tls.key the PKCS#12 files.
    ///
    /// Expected:
    /// - ServerStartException "trust.rootKeys file '{path}' is larger than 16 KiB.", "The TLS certificate '{path}' is larger
    ///   than 64 KiB.", "The TLS key '{path}' is larger than 64 KiB.", "The TLS certificate '{path}' is larger than 64 KiB.";
    ///   for the PKCS#12 file at the limit, a message that begins "The TLS certificate cannot be read: ".
    /// </remarks>
    /// <param name="file">The file one byte past its limit, or the PKCS#12 file at it.</param>
    [Theory]
    [InlineData("roots")]
    [InlineData("tls-certificate")]
    [InlineData("tls-key")]
    [InlineData("pkcs12")]
    [InlineData("pkcs12-at-limit")]
    public async Task Start_OperatorFileOneBytePastItsLimit_Throws(string file)
    {
        string data = _deployment.NewFolder();
        string roots = _deployment.FilePath("roots.pub.pem");
        WriteRootKeysAtLimit(roots, File.ReadAllText(_deployment.RootKeyFile), RootKeyFileLimit);
        (string certificate, string? key) = WriteTlsAtLimit("tls-limit", OperatorFileLimit);
        string pkcs12 = _deployment.FilePath("tls-limit.pfx");
        File.WriteAllBytes(pkcs12, new byte[file == "pkcs12-at-limit" ? OperatorFileLimit : OperatorFileLimit + 1]);
        switch (file)
        {
            case "roots":
                File.AppendAllText(roots, "\n");
                break;
            case "tls-certificate":
                File.AppendAllText(certificate, "\n");
                break;
            case "tls-key":
                File.AppendAllText(key!, "\n");
                break;
            default:
                (certificate, key) = (pkcs12, null);
                break;
        }

        string refused = await RefusedAsync(data, s => s with { TrustRootKeyFiles = [roots], TlsCertificate = certificate, TlsKey = key });

        if (file == "pkcs12-at-limit")
        {
            Assert.StartsWith("The TLS certificate cannot be read: ", refused, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(
            file switch
            {
                "roots" => $"trust.rootKeys file '{roots}' is larger than 16 KiB.",
                "tls-key" => $"The TLS key '{key}' is larger than 64 KiB.",
                _ => $"The TLS certificate '{certificate}' is larger than 64 KiB.",
            },
            refused
        );
    }

    // The vendor's root key first, then other P-256 public keys while a whole one fits, then line breaks up to the length.
    private static void WriteRootKeysAtLimit(string path, string rootPem, int length)
    {
        var text = new System.Text.StringBuilder(rootPem.TrimEnd() + "\n");
        while (true)
        {
            using ECDsa other = KeyFiles.CreateKey();
            string next = KeyFiles.ExportPublic(other) + "\n";
            if (text.Length + next.Length > length)
            {
                break;
            }

            text.Append(next);
        }

        File.WriteAllText(path, PadWithLineBreaks(text.ToString(), length));
    }

    private static string PadWithLineBreaks(string text, int length) =>
        text + new string('\n', length - System.Text.Encoding.UTF8.GetByteCount(text));

    private (string Certificate, string Key) WriteTlsAtLimit(string name, int length)
    {
        using ECDsa key = KeyFiles.CreateKey();
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(Deployment.Start.AddDays(-1), Deployment.Start.AddDays(30));
        string pem = _deployment.FilePath(name + ".crt");
        string keyFile = _deployment.FilePath(name + ".key");
        File.WriteAllText(pem, PadWithLineBreaks(certificate.ExportCertificatePem() + "\n", length));
        File.WriteAllText(keyFile, PadWithLineBreaks(key.ExportPkcs8PrivateKeyPem() + "\n", length));
        return (pem, keyFile);
    }

    private async Task<string> RefusedAsync(string data, Func<ServerSettings, ServerSettings> change) =>
        (await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data, change))).Message;
}
