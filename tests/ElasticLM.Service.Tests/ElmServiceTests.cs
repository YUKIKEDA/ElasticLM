using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;
using ElasticLM.Licensing.Tests;
using ElasticLM.Server.Tests;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The start and stop of <c>elm-service</c> (design §14.8): the trust store, the signing key (decision 0.75), listening, and
/// the timers' work.
/// </summary>
public sealed class ElmServiceTests
{
    /// <summary>
    /// The service keeps Kestrel's limits on the request line and the headers (design §5.3 T16): a request line of 8,192
    /// bytes, 100 headers, and a header section of 32 KiB and the empty line that ends it are read; a byte or a header
    /// more is refused before any route runs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A started service on loopback http. The defaults are those of Kestrel's KestrelServerLimits (aspnetcore
    ///   v10.0.14): MaxRequestLineSize 8 KiB, MaxRequestHeaderCount 100, MaxRequestHeadersTotalSize 32 KiB; the service
    ///   sets only MaxRequestBodySize. Kestrel counts the header section's bytes with their CRLFs and allows the limit plus
    ///   the 2 bytes of the empty line (Http1Connection.cs, MaxRequestHeadersTotalSize + 2).
    ///
    /// Steps:
    /// - Over a new connection each time, send GET /health with a request line of the length given (with its CRLF), or with
    ///   the given number of headers (Host among them), or with a header section (Host, one padding header, and the empty
    ///   line) of the given number of bytes; read the status line of the answer.
    ///
    /// Expected:
    /// - At each limit: 200. One past: 414 for the request line, 431 for the headers.
    /// </remarks>
    [Theory]
    [MemberData(nameof(KestrelLimits.AtAndPastEachLimit), MemberType = typeof(KestrelLimits))]
    public async Task Kestrel_RequestLineAndHeaders_KeepTheirLimits(string kind, int size, int status)
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ElmService service = await test.StartAsync();

        Assert.Equal(status, await KestrelLimits.StatusAsync(service.Url, KestrelLimits.Request("/health", kind, size)));
    }

    /// <summary>
    /// Over HTTP/2, which the service accepts on https because it sets no Protocols, Kestrel's limits on the request line
    /// and the headers hold as well (design §5.3 T16): a request line of 8,192 bytes, 100 header fields, and 32 KiB of
    /// names and values are read; a byte more of the line resets the stream, and a field or a byte more of the headers
    /// gets 431; past twice either header limit, or with one value over 32 KiB, Kestrel ends the connection.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A started service on loopback https with a self-signed certificate. The service sets only MaxRequestBodySize; the
    ///   other limits are Kestrel's defaults (aspnetcore v10.0.14): MaxRequestLineSize 8 KiB, MaxRequestHeaderCount 100,
    ///   and MaxRequestHeadersTotalSize 32 KiB of KestrelServerLimits, which HTTP/2 shares, and MaxRequestHeaderFieldSize
    ///   32 KiB of Http2Limits. Over HTTP/2 Kestrel
    ///   takes the lengths of :method, :scheme, :authority, and :path together for the request line and resets the stream
    ///   with PROTOCOL_ERROR past it (Http2Stream.cs TryValidatePseudoHeaders); it answers 431 past the header count or
    ///   size up to twice each (Http2Stream.cs TryParseRequest) and ends the connection with PROTOCOL_ERROR beyond
    ///   (Http2Connection.cs OnHeaderCore); its HPACK decoder ends the connection with COMPRESSION_ERROR for a string longer
    ///   than MaxRequestHeaderFieldSize (HPackDecoder.cs OnStringLength).
    ///
    /// Steps:
    /// - On a new TLS connection each time (ALPN h2), send GET /health as HTTP/2 frames with a request line of the length
    ///   given (made long with a query), or with the number of header fields given (the four pseudo-header fields among
    ///   them), or with names and values of the given number of bytes in all, or with one value of the given length (past
    ///   the limit, sent up to the value's length alone); read the answer's status, a RST_STREAM, or a GOAWAY. After an
    ///   answer or a RST_STREAM, send a plain GET /health on the same connection.
    ///
    /// Expected:
    /// - Line 8,192, count 100, and size 32 KiB: 200. Line 8,193: RST_STREAM with PROTOCOL_ERROR. Count 101 and 200, size
    ///   32 KiB + 1 and 64 KiB: 431. Count 201 and size 64 KiB + 1: GOAWAY with PROTOCOL_ERROR. A value of 32 KiB: 431 (the
    ///   size is past its limit). A value of 32 KiB + 1: GOAWAY with COMPRESSION_ERROR. The plain request after an answer
    ///   or a RST_STREAM gets 200.
    /// </remarks>
    /// <param name="kind">The limit.</param>
    /// <param name="size">The size of the request at or past it.</param>
    /// <param name="outcome">The status code, or the frame and the HTTP/2 error code that ended the request.</param>
    [Theory]
    [MemberData(nameof(KestrelLimits.Http2AtAndPastEachLimit), MemberType = typeof(KestrelLimits))]
    public async Task Kestrel_Http2RequestLineAndHeaders_KeepTheirLimits(string kind, int size, string outcome)
    {
        await using TestService test = TestService.Open("sqlite");
        ElmService service = await StartOnHttpsAsync(test);

        Assert.Equal(outcome, await KestrelLimits.Http2OutcomeAsync(service.Url, "/health", kind, size));
    }

    /// <summary>
    /// Over HTTP/2, Kestrel's other defaults hold on the service too (design §5.3 T16): 100 streams open at once, frames of
    /// 16,384 bytes, an HPACK table of 4,096 bytes, and receive windows of 786,432 bytes for a stream and 1,048,576 for the
    /// connection. A stream more is reset and the connection stays; a byte more of any other limit ends the connection.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A started service on loopback https with a self-signed certificate. The service sets only MaxRequestBodySize; the
    ///   rest are Kestrel's defaults (Http2Limits.cs, aspnetcore v10.0.14): MaxStreamsPerConnection 100, MaxFrameSize 16 KiB,
    ///   HeaderTableSize 4 KiB, InitialStreamWindowSize 768 KiB, InitialConnectionWindowSize 1 MiB. Kestrel resets a stream
    ///   past the first limit with REFUSED_STREAM (Http2Connection.cs StartStream), ends the connection with
    ///   FRAME_SIZE_ERROR for a longer frame (Http2FrameReader.cs TryReadFrame) and with COMPRESSION_ERROR for a larger
    ///   table size update (HPackDecoder.cs SetDynamicHeaderTableSize), and ends it with FLOW_CONTROL_ERROR for DATA past
    ///   either window, the stream's included (InputFlowControl.cs TryAdvance).
    /// - The test settings raise the rate limits of one address (RateLimitPerIp, RateLimitClientPerIp) to 1,000, above the
    ///   102 GETs of the case of 101 streams.
    ///
    /// Steps:
    /// - On a new TLS connection each time (ALPN h2), send GET /v1/nonce as HTTP/2 frames (KestrelLimits.Http2OtherOutcomeAsync):
    ///   with the client window set to 0, the number of streams given at once, each with END_STREAM, then, after the answer
    ///   (200 or reset) of the last, a RST_STREAM (CANCEL) of the first and one more stream; one HEADERS frame of the length given (past the
    ///   limit, its frame header alone); a header block that starts with a dynamic table size update of the value given;
    ///   with the client window set to 0, one stream or two without END_STREAM, and after their 200, DATA of the given
    ///   number of bytes on the stream or split over the two, and a PING. Read the answers' status, RST_STREAM, PING ACK,
    ///   or GOAWAY. After a 200 of the frame or table request, send a plain GET /v1/nonce on the same connection.
    ///
    /// Expected:
    /// - Streams 100: 200 on every stream. Streams 101: 200 on the first 100 and RST_STREAM with REFUSED_STREAM on the
    ///   101st. In both, after the first stream's reset, the next stream gets 200. Frame 16,384 and table 4,096: 200, and the
    ///   plain request gets 200. Frame 16,385: GOAWAY with FRAME_SIZE_ERROR. Table 4,097: GOAWAY with COMPRESSION_ERROR.
    ///   Stream window 786,432 and connection window 1,048,576: the PING ACK. A byte more of either: GOAWAY with
    ///   FLOW_CONTROL_ERROR.
    /// </remarks>
    /// <param name="kind">The limit.</param>
    /// <param name="size">The streams, the frame length, the table size, or the bytes of DATA at or past it.</param>
    /// <param name="outcome">The status code, the RST_STREAM, the PING ACK, or the GOAWAY that ended the requests.</param>
    [Theory]
    [MemberData(nameof(KestrelLimits.Http2AtAndPastOtherLimits), MemberType = typeof(KestrelLimits))]
    public async Task Kestrel_Http2StreamsFramesTableAndWindows_KeepTheirLimits(string kind, int size, string outcome)
    {
        await using TestService test = TestService.Open("sqlite");
        ElmService service = await StartOnHttpsAsync(test);

        Assert.Equal(outcome, await KestrelLimits.Http2OtherOutcomeAsync(service.Url, "/v1/nonce", kind, size));
    }

    /// <summary>
    /// The service's first HTTP/2 SETTINGS states Kestrel's limits (design §5.3 T16): 100 concurrent streams, a stream
    /// window of 786,432 bytes, and a header list of 32,768 bytes; the table size and the frame size are the protocol's
    /// defaults and are not sent; a WINDOW_UPDATE raises the connection window to 1,048,576 bytes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A started service on loopback https with a self-signed certificate, with Kestrel's HTTP/2 defaults (aspnetcore
    ///   v10.0.14). Kestrel sends only the values that differ from the protocol's defaults (Http2PeerSettings.cs
    ///   GetNonProtocolDefaults), then a WINDOW_UPDATE of the connection by InitialConnectionWindowSize less 65,535
    ///   (Http2Connection.cs ProcessRequestsAsync).
    ///
    /// Steps:
    /// - On a new TLS connection (ALPN h2), send the preface, an empty SETTINGS, and a PING; read until the PING ACK.
    ///
    /// Expected:
    /// - The SETTINGS, SETTINGS_ENABLE_CONNECT_PROTOCOL left out, are SETTINGS_MAX_CONCURRENT_STREAMS (3) 100,
    ///   SETTINGS_INITIAL_WINDOW_SIZE (4) 786,432, and SETTINGS_MAX_HEADER_LIST_SIZE (6) 32,768, and nothing else. The
    ///   connection's WINDOW_UPDATE increments add up to 983,041.
    /// </remarks>
    [Fact]
    public async Task Kestrel_Http2Settings_StateTheLimits()
    {
        await using TestService test = TestService.Open("sqlite");
        ElmService service = await StartOnHttpsAsync(test);

        (string settings, long increment) = await KestrelLimits.Http2ServerSettingsAsync(service.Url);

        Assert.Equal("3=100, 4=786432, 6=32768", settings);
        Assert.Equal(983_041, increment);
    }

    // Starts the service on loopback https with a self-signed certificate for 127.0.0.1 that the test makes, its PEM files
    // ending with a line break as the product's tools write them.
    private static async Task<ElmService> StartOnHttpsAsync(TestService test)
    {
        await test.SetupAsync();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=elm-service-test", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(TestService.Start.AddDays(-1), TestService.Start.AddDays(30));
        File.WriteAllText(test.FilePath("tls.crt"), certificate.ExportCertificatePem() + "\n");
        File.WriteAllText(test.FilePath("tls.key"), key.ExportPkcs8PrivateKeyPem() + "\n");
        return await test.StartAsync(s =>
            s with
            {
                Listen = new Uri("https://127.0.0.1:0"),
                TlsCertificate = test.FilePath("tls.crt"),
                TlsKey = test.FilePath("tls.key"),
            }
        );
    }

    /// <summary>
    /// The service starts only with a signing key whose certificates the trust store vouches for, for licenses and leases, at
    /// the start time; and with the right passphrase.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor, a signing key, and certificates written in the test.
    ///
    /// Steps:
    /// - Start with: a certificate for licenses only; one that ended yesterday; one that starts tomorrow; one for another key;
    ///   the right certificate and a wrong passphrase; no passphrase; the right certificate.
    ///
    /// Expected:
    /// - ServiceStartException naming Lease; naming License, Lease; the same; the same; one about the key or passphrase; one
    ///   naming the variable; a running service whose signing status names the key and both uses.
    /// </remarks>
    [Fact]
    public async Task Start_SigningKeyNotCertifiedNow_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<string> Refused(string certificate, string? passphrase = TestService.Passphrase)
        {
            ServiceSettings settings = test.Settings(s => s with { SigningCertificateFiles = [certificate] });
            return (
                await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, test.HostOptions(passphrase), cancel))
            ).Message;
        }

        Assert.Contains(
            "Lease",
            await Refused(test.WriteCertificate("license-only", KeyUses.License, TestService.Start.AddDays(-1), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "License, Lease",
            await Refused(test.WriteCertificate("ended", KeyUses.License | KeyUses.Lease, TestService.Start.AddDays(-31), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "License, Lease",
            await Refused(test.WriteCertificate("later", KeyUses.License | KeyUses.Lease, TestService.Start.AddDays(1), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        using (ECDsa other = KeyFiles.CreateKey())
        {
            string otherCertificate = test.WriteCertificate(
                "other",
                KeyUses.License | KeyUses.Lease,
                TestService.Start.AddDays(-1),
                TimeSpan.FromDays(30),
                other
            );
            Assert.Contains("License, Lease", await Refused(otherCertificate), StringComparison.Ordinal);
        }

        Assert.Equal(
            "The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.",
            await Refused(test.FilePath("signing.cert"), "wrong passphrase for the key")
        );
        Assert.Equal(
            $"Set {ServiceSettings.SigningPassphraseVariable} or signing.passphraseFile to the signing key's passphrase.",
            await Refused(test.FilePath("signing.cert"), null)
        );

        ElmService service = await test.StartAsync();
        Assert.Equal((KeyFiles.KeyId(test.SigningKey), KeyUses.License | KeyUses.Lease), (service.Signing.KeyId, service.Signing.Uses));
    }

    /// <summary>
    /// A start with certificates that end in less than 30 days writes one warning that says when they end and what to do
    /// (design §7.5); at exactly 30 days it writes none.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signing key; a certificate that ends 30 days after the test's time, and one that ends a second earlier.
    ///
    /// Steps:
    /// - Start a service with each, its log recorded.
    ///
    /// Expected:
    /// - No warning with the first. With the second, one warning: "The signing key's certificates end at 10/30/2026 23:59:59
    ///   +00:00, within 30 days; issue new certificates and restart.".
    /// </remarks>
    [Fact]
    public async Task Start_CertificatesEndWithin30Days_LogsOneWarning()
    {
        await using TestService test = TestService.Open("sqlite");
        DateTimeOffset notBefore = TestService.Start.AddDays(-1);
        KeyUses uses = KeyUses.License | KeyUses.Lease;
        async Task<string[]> Warnings(string certificate)
        {
            using var log = new RecordingLoggerProvider();
            ServiceSettings settings = test.Settings(s => s with { SigningCertificateFiles = [certificate] });
            await using (await ElmService.StartAsync(settings, test.HostOptions(log: log), TestContext.Current.CancellationToken)) { }

            return [.. log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
        }

        Assert.Empty(await Warnings(test.WriteCertificate("thirty-days", uses, notBefore, TimeSpan.FromDays(31))));
        Assert.Equal(
            ["The signing key's certificates end at 10/30/2026 23:59:59 +00:00, within 30 days; issue new certificates and restart."],
            await Warnings(test.WriteCertificate("a-second-less", uses, notBefore, TimeSpan.FromDays(31) - TimeSpan.FromSeconds(1)))
        );
    }

    /// <summary>
    /// The signing key's passphrase comes from ELM_SIGNING_PASSPHRASE or, when it is not set, from signing.passphraseFile
    /// (design §14.8, F2), so that every secret can be given as a file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor, a signing key, and certificates written in the test; a file with the passphrase and a line break.
    ///
    /// Steps:
    /// - Start with the file and no variable; with the variable and a file of a wrong passphrase; with an empty file and no
    ///   variable; with a file that does not exist.
    ///
    /// Expected:
    /// - A running service; a running service (the variable wins); ServiceStartException saying the file is empty; one
    ///   saying it does not exist.
    /// </remarks>
    [Fact]
    public async Task Start_PassphraseFromTheFile_Starts()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string File(string name, string text)
        {
            string path = test.FilePath(name);
            System.IO.File.WriteAllText(path, text);
            return path;
        }

        string right = File("passphrase", TestService.Passphrase + "\n");
        string wrong = File("wrong-passphrase", "not the passphrase\n");
        string empty = File("empty-passphrase", "\n");
        string missing = test.FilePath("no-such-passphrase");

        await using (
            ElmService fromFile = await ElmService.StartAsync(
                test.Settings(s => s with { SigningPassphraseFile = right }),
                test.HostOptions(null),
                cancel
            )
        )
        {
            Assert.Equal(KeyFiles.KeyId(test.SigningKey), fromFile.Signing.KeyId);
        }

        await using (
            ElmService fromVariable = await ElmService.StartAsync(
                test.Settings(s => s with { SigningPassphraseFile = wrong }),
                test.HostOptions(TestService.Passphrase),
                cancel
            )
        )
        {
            Assert.Equal(KeyFiles.KeyId(test.SigningKey), fromVariable.Signing.KeyId);
        }

        Assert.Equal($"signing.passphraseFile '{empty}' is empty.", await RefusedWith(empty));
        Assert.Equal($"signing.passphraseFile '{missing}' does not exist or is larger than 16 KiB.", await RefusedWith(missing));

        async Task<string> RefusedWith(string passphraseFile)
        {
            ServiceSettings settings = test.Settings(s => s with { SigningPassphraseFile = passphraseFile });
            return (await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, test.HostOptions(null), cancel))).Message;
        }
    }

    /// <summary>
    /// The root keys come from trust.rootKeys files or, when none is set, from the PEM text of ELM_TRUST_ROOT_KEYS; with
    /// neither the service does not start.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor and settings without trust.rootKeys.
    ///
    /// Steps:
    /// - Start without the variable; start with the root key's PEM in it.
    ///
    /// Expected:
    /// - ServiceStartException naming both; a running service.
    /// </remarks>
    [Fact]
    public async Task Start_RootKeysFromTheVariable_Starts()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ServiceSettings settings = test.Settings(s => s with { TrustRootKeyFiles = [] });
        ServiceHostOptions Host(string? pem) =>
            new()
            {
                Time = test.Time,
                Environment = name =>
                    name == ServiceSettings.SigningPassphraseVariable ? TestService.Passphrase
                    : name == ServiceSettings.TrustRootKeysVariable ? pem
                    : null,
            };

        Assert.Equal(
            $"Set trust.rootKeys or {ServiceSettings.TrustRootKeysVariable} to the vendor's root public keys.",
            (await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, Host(null), cancel))).Message
        );
        await using ElmService service = await ElmService.StartAsync(settings, Host(File.ReadAllText(test.FilePath("root.pub.pem"))), cancel);
        Assert.Equal(KeyFiles.KeyId(test.SigningKey), service.Signing.KeyId);
    }

    /// <summary>
    /// A start error from the trust store or the database driver carries the library's sentence without the .NET parameter
    /// name (design §17.3, V1c10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor; a trust.rootKeys file with a P-384 public key.
    ///
    /// Steps:
    /// - Start with that file as trust.rootKeys; start on PostgreSQL with the connection string Host=127.0.0.1;Port=x (read
    ///   before any connection is made).
    ///
    /// Expected:
    /// - ServiceStartException "The trust settings are not valid: A root key is not a P-256 public key in PEM."; and
    ///   ServiceStartException "The database connection string is not valid: Couldn't set port".
    /// </remarks>
    [Fact]
    public async Task Start_TrustOrConnectionNotValid_ThrowsWithoutParameterName()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string p384File = test.FilePath("p384.pub.pem");
        File.WriteAllText(p384File, p384.ExportSubjectPublicKeyInfoPem() + "\n");
        async Task<string> Refused(Func<ServiceSettings, ServiceSettings> change) =>
            (await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(test.Settings(change), test.HostOptions(), cancel))).Message;

        Assert.Equal(
            "The trust settings are not valid: A root key is not a P-256 public key in PEM.",
            await Refused(s => s with { TrustRootKeyFiles = [p384File] })
        );
        Assert.Equal(
            "The database connection string is not valid: Couldn't set port",
            await Refused(s => s with { Database = DatabaseProvider.Postgres, ConnectionString = "Host=127.0.0.1;Port=x" })
        );
    }

    /// <summary>
    /// With an https listen and PEM files, the service answers over TLS with that certificate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A self-signed certificate for 127.0.0.1 and its key, as PEM files.
    ///
    /// Steps:
    /// - Start on https://127.0.0.1:0; GET /health trusting only that certificate.
    ///
    /// Expected:
    /// - 200, and the certificate the client saw is the file's.
    /// </remarks>
    [Fact]
    public async Task Start_HttpsListen_ServesWithTheCertificate()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=elm-service-test", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(TestService.Start.AddDays(-1), TestService.Start.AddDays(30));
        File.WriteAllText(test.FilePath("tls.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(test.FilePath("tls.key"), key.ExportPkcs8PrivateKeyPem());

        ElmService service = await test.StartAsync(s =>
            s with
            {
                Listen = new Uri("https://127.0.0.1:0"),
                TlsCertificate = test.FilePath("tls.crt"),
                TlsKey = test.FilePath("tls.key"),
            }
        );
        string? seen = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            {
                seen = presented?.Thumbprint;
                return presented?.Thumbprint == certificate.Thumbprint;
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = service.Url };
        Assert.Equal(Uri.UriSchemeHttps, service.Url.Scheme);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health", cancel)).StatusCode);
        Assert.Equal(certificate.Thumbprint, seen);
    }

    /// <summary>
    /// With an https listen and a PEM file with the certificate and then an intermediate CA, the service sends the intermediate
    /// (design §13.5, V1c12).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root CA, an intermediate CA it signs, and a certificate for 127.0.0.1 the intermediate signs, valid around the real
    ///   time; a PEM file with the certificate and then the intermediate, and a PEM file with the certificate's key.
    ///
    /// Steps:
    /// - Start on https://127.0.0.1:0 with the two files, holding the mutex of the tests that change the Windows CA stores, and
    ///   with a new chain when the service stops at its own check of the intermediates (TestCertificateChain.StartAsync);
    ///   connect with SslStream, and in the validation callback read the certificates the service sent (the chain's
    ///   ExtraStore), then build the chain trusting only the root, with downloads off and no revocation check.
    ///
    /// Expected:
    /// - The service starts (on Windows, after its check that the intermediate is sent, design §13.5, V1d3a), the intermediate
    ///   is among the certificates the service sent, and the chain builds.
    /// </remarks>
    [Fact]
    public async Task Start_HttpsListenWithIntermediate_SendsTheIntermediate()
    {
        await using TestService test = TestService.Open("sqlite");
        (TestCertificateChain chain, ElmService service) = await TestCertificateChain.StartAsync(c =>
        {
            (string certificate, string key) = c.WritePem(test.FilePath("chain.crt"), test.FilePath("chain.key"), c.Intermediate);
            return test.StartAsync(s => s with { Listen = new Uri("https://127.0.0.1:0"), TlsCertificate = certificate, TlsKey = key });
        });
        try
        {
            (bool sent, bool builds, _) = await chain.ConnectAsync(service.Url, TestContext.Current.CancellationToken);

            Assert.True(sent);
            Assert.True(builds);
        }
        finally
        {
            // The service stops before the chain's certificates leave the stores and the mutex is given back.
            await service.DisposeAsync();
            chain.Dispose();
        }
    }

    /// <summary>
    /// On Windows, a service whose first handshake cannot have the intermediate stops at the start with a sentence that names
    /// the certificate left out, and leaves nothing listening (design §13.5, V1d3a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; a listener of the test that answers TLS handshakes with the chain's certificate alone, with the
    ///   options Kestrel uses, while the intermediate is in no store; one handshake with it, which sends no intermediate, and
    ///   whose connection stays open until the end of the test (TestCertificateChain.PrimeAloneAsync). From then on Windows
    ///   sends this certificate alone in this process, whatever the store holds (design §20.7, the V1d3a Grill).
    /// - A PEM file with the certificate and then the intermediate, and the key; the usage log of the node n1; a recording log
    ///   provider.
    ///
    /// Steps:
    /// - Start the service with the two files on port 0; then connect to the port the host's "Now listening on" entry names,
    ///   and read the usage log and the operational log. The port is not known before the start (the V1d3b Grill, 3b-3);
    ///   after the failed start another program could take it, and the connection would then not be refused.
    ///
    /// Expected:
    /// - ServiceStartException "Windows left the intermediate CA certificate {subject} (thumbprint {thumbprint}) of the TLS
    ///   certificate '{path}' out of the first TLS handshake, and what that handshake sent stays for as long as this process
    ///   runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate Certification
    ///   Authorities store." with the intermediate's subject and thumbprint; the connection afterwards is refused; the usage
    ///   log holds service.started and then service.stopped, and the operational log no service.started line.
    /// </remarks>
    [Fact]
    public async Task Start_FirstHandshakeWithoutTheIntermediate_Throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using TestService test = TestService.Open("sqlite");
        using var chain = new TestCertificateChain(sentByServer: true);
        await using TestCertificateChain.PrimedHandshake primed = await chain.PrimeAloneAsync(cancel);
        Assert.False(primed.Sent);

        (string certificate, string key) = chain.WritePem(test.FilePath("first.crt"), test.FilePath("first.key"), chain.Intermediate);
        var log = new RecordingLoggerProvider();
        var refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                Logged(test) with
                {
                    Listen = new Uri("https://127.0.0.1:0"),
                    TlsCertificate = certificate,
                    TlsKey = key,
                },
                test.HostOptions(log: log),
                cancel
            )
        );
        int port = log.ListeningPort("https");

        Assert.Equal(
            $"Windows left the intermediate CA certificate {chain.Intermediate.Subject} (thumbprint {chain.Intermediate.Thumbprint}) of the TLS "
                + $"certificate '{certificate}' out of the first TLS handshake, and what that handshake sent stays for as long as this "
                + "process runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate "
                + "Certification Authorities store.",
            refused.Message
        );
        using var client = new TcpClient();
        SocketException closed = await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, cancel).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, closed.SocketErrorCode);
        AssertFailedStartLeft(test, log);
    }

    /// <summary>
    /// On Windows, a start whose check of the intermediates gets no answer stops with that sentence when the clock passes the
    /// limit, and a request that comes in while the check runs gets no answer and changes nothing (design §13.5, §14.8,
    /// V1d3a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. The service set up (an API key of the bootstrap owner); a chain that a server sends; a PEM file with the
    ///   certificate and then the intermediate, and the key; the usage log; a TCP listener of the test that accepts
    ///   connections and sends nothing, to which the check is sent (ServiceHostOptions.SelfCheckTarget).
    ///
    /// Steps:
    /// - Start the service on https and port 0; once the test's listener has accepted the check's connection, read the port
    ///   from the host's "Now listening on" entry and POST /v1/admin/products (code v1d3a) to it with the API key; move the
    ///   fake clock 10 seconds. Then start the service on http with the same database, and GET /v1/admin/products.
    ///
    /// Expected:
    /// - The POST fails without an answer. ServiceStartException "The check that Windows sends the intermediate CA
    ///   certificates of the TLS certificate '{path}' did not get an answer: the TLS handshake with 127.0.0.1:{port of the
    ///   test's listener} did not finish within 10 seconds."; the usage log holds service.started and then service.stopped, and
    ///   the operational log no service.started line; the product list of the second start does not hold v1d3a.
    /// </remarks>
    [Fact]
    public async Task Start_CheckWithoutAnswer_ThrowsAndChangesNothingMeanwhile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using TestService test = TestService.Open("sqlite");
        string apiKey = await test.SetupAsync();
        using var chain = new TestCertificateChain(sentByServer: true);
        (string certificate, string key) = chain.WritePem(test.FilePath("hole.crt"), test.FilePath("hole.key"), chain.Intermediate);
        using var hole = new TcpListener(IPAddress.Loopback, 0);
        hole.Start();
        var holeEndPoint = (IPEndPoint)hole.LocalEndpoint;
        var log = new RecordingLoggerProvider();
        Task<ElmService> start = ElmService.StartAsync(
            Logged(test) with
            {
                Listen = new Uri("https://127.0.0.1:0"),
                TlsCertificate = certificate,
                TlsKey = key,
            },
            test.HostOptions(log: log, selfCheckTarget: _ => holeEndPoint),
            cancel
        );
        using TcpClient check = await hole.AcceptTcpClientAsync(cancel);
        int port = log.ListeningPort("https");
        using var https = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://127.0.0.1:{port}/v1/admin/products"))
        {
            Content = new StringContent("""{"code":"v1d3a","name":"V1d3a"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        await Assert.ThrowsAsync<HttpRequestException>(() => https.SendAsync(create, cancel));
        test.Time.Advance(TlsChainSelfCheck.Limit);
        var refused = await Assert.ThrowsAsync<ServiceStartException>(() => start.WaitAsync(TimeSpan.FromSeconds(30), cancel));
        ElmService again = await test.StartAsync();
        using var http = new HttpClient { BaseAddress = again.Url };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        string products = await http.GetStringAsync(new Uri("v1/admin/products", UriKind.Relative), cancel);

        Assert.Equal(
            $"The check that Windows sends the intermediate CA certificates of the TLS certificate '{certificate}' did not get an answer: "
                + $"the TLS handshake with {holeEndPoint} did not finish within 10 seconds.",
            refused.Message
        );
        AssertFailedStartLeft(test, log);
        Assert.DoesNotContain("v1d3a", products, StringComparison.Ordinal);
    }

    // A usage log for the node "n1", whose signing key's certificate may sign logs.
    private static ServiceSettings Logged(TestService test)
    {
        string certificate = test.WriteCertificate(
            "log.cert",
            ElasticLM.Issuing.KeyUses.License | ElasticLM.Issuing.KeyUses.Lease | ElasticLM.Issuing.KeyUses.Log,
            TestService.Start.AddDays(-1),
            TimeSpan.FromDays(365)
        );
        return test.Settings(s => s with { UsageLogDirectory = test.FilePath("usage"), NodeName = "n1", SigningCertificateFiles = [certificate] });
    }

    // What a start that stopped at the check leaves: service.started and then service.stopped in the usage log (design §14.8),
    // and no service.started line in the operational log.
    private static void AssertFailedStartLeft(TestService test, RecordingLoggerProvider log)
    {
        string usage = string.Concat(Directory.GetFiles(test.FilePath("usage"), "*.jsonl").Select(File.ReadAllText));
        int started = usage.IndexOf("\"service.started\"", StringComparison.Ordinal);
        int stopped = usage.IndexOf("\"service.stopped\"", StringComparison.Ordinal);
        Assert.True(started >= 0 && stopped > started, "The usage log does not hold service.started and then service.stopped.");
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("service.started", StringComparison.Ordinal));
    }

    /// <summary>
    /// A port another process holds stops the start with a ServiceStartException (exit 1), not a crash.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A loopback port held by a listener of the test.
    ///
    /// Steps:
    /// - Start on that port.
    ///
    /// Expected:
    /// - ServiceStartException whose message begins with the listen URL.
    /// </remarks>
    [Fact]
    public async Task Start_PortInUse_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var e = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                test.Settings(s => s with { Listen = new Uri($"http://127.0.0.1:{port}") }),
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith($"elm-service cannot listen on http://127.0.0.1:{port}/: ", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A listen address this machine does not have is a SocketException in Kestrel, not an IOException, and stops the start
    /// with the same message as a port in use (design §14.8, V1c9 U1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 192.0.2.1 (TEST-NET-1, RFC 5737), which no interface of the test machine has: a plain Socket.Bind to 192.0.2.1:0
    ///   throws SocketException first, or the test fails saying the machine allows that bind.
    ///
    /// Steps:
    /// - Start with listen http://192.0.2.1:0.
    ///
    /// Expected:
    /// - ServiceStartException whose message begins "elm-service cannot listen on http://192.0.2.1:0/: ".
    /// </remarks>
    [Fact]
    public async Task Start_AddressNotOnThisMachine_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            Exception? bind = Record.Exception(() => probe.Bind(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 0)));
            Assert.True(
                bind is SocketException,
                "This machine lets a socket bind 192.0.2.1, so the test cannot make the service fail to listen on an address it does not have."
            );
        }

        var e = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                test.Settings(s => s with { Listen = new Uri("http://192.0.2.1:0") }),
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith("elm-service cannot listen on http://192.0.2.1:0/: ", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TLS certificate whose Extended Key Usage leaves out server authentication stops the start with a reason before the
    /// service listens; one that includes it among others starts (design §13.5, §14.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Self-signed certificates for 127.0.0.1 with their keys as PEM files: one whose Extended Key Usage is client
    ///   authentication (1.3.6.1.5.5.7.3.2) only, one whose Extended Key Usage is client and server authentication.
    ///
    /// Steps:
    /// - Start on https://127.0.0.1:0 with the first; with the second.
    ///
    /// Expected:
    /// - ServiceStartException "The TLS certificate '{path}' cannot be used by a server: its Extended Key Usage does not
    ///   include server authentication (1.3.6.1.5.5.7.3.1)."; then a running service on https.
    /// </remarks>
    [Fact]
    public async Task Start_TlsCertificateWithoutServerAuthentication_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ServiceSettings Https(string name, params string[] usages)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var oids = new OidCollection();
            foreach (string usage in usages)
            {
                oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
            using X509Certificate2 certificate = request.CreateSelfSigned(TestService.Start.AddDays(-1), TestService.Start.AddDays(30));
            File.WriteAllText(test.FilePath(name + ".crt"), certificate.ExportCertificatePem() + "\n");
            File.WriteAllText(test.FilePath(name + ".key"), key.ExportPkcs8PrivateKeyPem() + "\n");
            return test.Settings(s =>
                s with
                {
                    Listen = new Uri("https://127.0.0.1:0"),
                    TlsCertificate = test.FilePath(name + ".crt"),
                    TlsKey = test.FilePath(name + ".key"),
                }
            );
        }

        var e = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(Https("client", "1.3.6.1.5.5.7.3.2"), test.HostOptions(), cancel)
        );
        Assert.Equal(
            $"The TLS certificate '{test.FilePath("client.crt")}' cannot be used by a server: its Extended Key Usage does not include "
                + "server authentication (1.3.6.1.5.5.7.3.1).",
            e.Message
        );
        await using ElmService service = await ElmService.StartAsync(
            Https("both", "1.3.6.1.5.5.7.3.2", "1.3.6.1.5.5.7.3.1"),
            test.HostOptions(),
            cancel
        );
        Assert.Equal(Uri.UriSchemeHttps, service.Url.Scheme);
    }

    /// <summary>
    /// The authentication failures counted since the last minute are written when the service stops (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup.
    ///
    /// Steps:
    /// - Call twice without a key, in less than a minute; stop the service; read the audit log from the database.
    ///
    /// Expected:
    /// - One auth.failed row with {"count":2}.
    /// </remarks>
    [Fact]
    public async Task Stop_CountedFailures_AreWritten()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient anonymous = test.Http(service);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);

        await service.StopAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        AuditRow row = Assert.Single(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);
        Assert.Equal("{\"count\":2}", row.Entry.Detail);
    }

    /// <summary>
    /// A minute's count that cannot be written is kept and written with the next row, so a database failure loses no
    /// failure from the log (design §14.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup.
    ///
    /// Steps:
    /// - Call twice without a key; rename audit_log so the write fails; advance one minute and wait for the timer's work;
    ///   rename it back; read the rows; stop the service; read the rows.
    ///
    /// Expected:
    /// - No auth.failed row before the stop (the minute's write failed); then one with {"count":2}.
    /// </remarks>
    [Fact]
    public async Task DenialWrite_DatabaseFails_KeepsTheCount()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient anonymous = test.Http(service);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);

        await test.ExecuteAsync("ALTER TABLE audit_log RENAME TO audit_log_hidden");
        test.Time.Advance(TimeSpan.FromMinutes(1));
        await service.WaitForTimersAsync();
        await test.ExecuteAsync("ALTER TABLE audit_log_hidden RENAME TO audit_log");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        Assert.DoesNotContain(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);

        await service.StopAsync();
        AuditRow row = Assert.Single(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);
        Assert.Equal("{\"count\":2}", row.Entry.Detail);
    }

    /// <summary>
    /// With audit.retentionDays set, the hourly timer removes the rows older than the period and leaves the chain checkable
    /// (design §18.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with retention of 1 day, after setup (row 1 at the start), on each engine.
    ///
    /// Steps:
    /// - Advance 1 day and 1 hour; wait for the timer's work; check the chain.
    ///
    /// Expected:
    /// - No row left; the chain intact with 0 rows checked.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Retention_TimerFires_RemovesOldRows(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await test.StartAsync(s => s with { AuditRetention = TimeSpan.FromDays(1) });
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Assert.Single(await database.ListAuditAsync(null, false, 10, cancel));

        test.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        await test.WaitWithClockAsync(async () => (await database.ListAuditAsync(null, false, 10, cancel)).Count == 0);
        Assert.Equal(new AuditCheck(0, null), await database.VerifyAuditAsync(cancel));
    }

    /// <summary>
    /// Every route of the service appears in the elm-service section of its reference page, in both languages (design §20.7,
    /// V1c2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the default test settings: no web settings, so the routes of the admin UI's sign-in are not mapped.
    /// - docs/guide/reference and docs/guide.ja/reference under the repository root.
    ///
    /// Steps:
    /// - Read every route endpoint of the running host as its method and path, such as GET /v1/nonce.
    /// - Look for the routes under /v1/admin/ and GET /health on administration-api.md, and every other route on
    ///   device-protocol.md, each in the section whose level-2 heading holds `elm-service`.
    ///
    /// Expected:
    /// - Both groups have routes.
    /// - On both pages of each group, a code span in the elm-service section is each route exactly; the message lists every
    ///   route that is not.
    /// </remarks>
    [Fact]
    public async Task Routes_AllEndpoints_AppearOnReferencePages()
    {
        await using TestService test = TestService.Open("sqlite");
        ElmService service = await test.StartAsync();
        static bool Administration(string route)
        {
            string path = route[(route.IndexOf(' ', StringComparison.Ordinal) + 1)..];
            return path.StartsWith("/v1/admin/", StringComparison.Ordinal) || path == "/health";
        }

        ILookup<bool, string> routes = EndpointRoutes.Of(service.Services).ToLookup(Administration);
        Assert.NotEmpty(routes[true]);
        Assert.NotEmpty(routes[false]);

        IReadOnlyList<string> missing =
        [
            .. ReferencePages.FindMissingInSections(
                "administration-api.md",
                2,
                new Dictionary<string, IReadOnlyList<string>> { ["elm-service"] = [.. routes[true]] }
            ),
            .. ReferencePages.FindMissingInSections(
                "device-protocol.md",
                2,
                new Dictionary<string, IReadOnlyList<string>> { ["elm-service"] = [.. routes[false]] }
            ),
        ];

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }
}
