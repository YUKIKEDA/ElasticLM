using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// Tests of the <c>elm</c> commands, run in process against files in a temporary folder.
/// </summary>
public sealed class ElmCliTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-cli-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal) { ["ELM_KEY_PASSPHRASE"] = Passphrase };

    public ElmCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// The whole offline flow works: create root and signing keys, certify, issue, inspect, and verify.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder; the passphrase in ELM_KEY_PASSPHRASE.
    ///
    /// Steps:
    /// - key root create, key signing create, key certify, license issue (bound, feature pro >=2.0 &lt;3.0), license inspect,
    ///   license verify with the matching installation, then with another product, then after one byte of the file changes.
    ///
    /// Expected:
    /// - Exit 0 until the matching verify (valid); exit 1 for the other product (ELM_WRONG_PRODUCT) and for the changed file.
    ///   The private key files are encrypted.
    /// </remarks>
    [Fact]
    public void OfflineFlow_CreateCertifyIssueVerify_Works()
    {
        string licensePath = IssueSample();

        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", File.ReadAllText(P("root.key.pem")), StringComparison.Ordinal);
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", File.ReadAllText(P("signing.key.pem")), StringComparison.Ordinal);

        (int inspectCode, string inspectOut, _) = Run("license", "inspect", licensePath);
        Assert.Equal(0, inspectCode);
        Assert.Contains("NOT VERIFIED", inspectOut, StringComparison.Ordinal);
        Assert.Contains("\"lic\": \"lic-1\"", inspectOut, StringComparison.Ordinal);

        (int code, string output, _) = Run([.. Verify(licensePath), "--json"]);
        Assert.Equal(0, code);
        using (JsonDocument json = JsonDocument.Parse(output))
        {
            Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
            Assert.Equal("lic-1", json.RootElement.GetProperty("licenseId").GetString());
        }

        (int otherCode, string otherOut, _) = Run([.. Verify(licensePath, product: "acme-cam")]);
        Assert.Equal(1, otherCode);
        Assert.Contains("ELM_WRONG_PRODUCT", otherOut, StringComparison.Ordinal);

        byte[] bytes = File.ReadAllBytes(licensePath);
        int index = Array.IndexOf(bytes, (byte)'.') + 5;
        bytes[index] = bytes[index] == (byte)'A' ? (byte)'B' : (byte)'A';
        File.WriteAllBytes(licensePath, bytes);
        Assert.Equal(1, Run([.. Verify(licensePath)]).Code);
    }

    /// <summary>
    /// Input errors exit with 2 and a message: a missing option, an existing file without --force, a wrong passphrase, a
    /// server option on a standard license, no passphrase without interactive input, a short passphrase, and an unknown use.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys and a certificate from the offline flow.
    ///
    /// Steps:
    /// - Run each faulty command.
    ///
    /// Expected:
    /// - Exit 2 and a line starting with "error:" on the error output each time.
    /// </remarks>
    [Fact]
    public void Errors_InvalidInput_ExitWithTwo()
    {
        IssueSample();

        // Each case runs with the right passphrase and is checked for its own message, so it cannot pass for another reason.
        AssertInputError(Run("key", "root", "create"), "--out");
        AssertInputError(Run("key", "root", "create", "--out", P("root.key.pem")), "exists");
        AssertInputError(Run([.. Issue("x.elmlic"), "--lease-minutes", "5"]), "server licenses only");
        AssertInputError(
            Run("key", "certify", "--root", P("root.key.pem"), "--public", P("signing.key.pem.pub.pem"), "--use", "everything", "--out", P("c2.jws")),
            "'everything'"
        );
        AssertInputError(Run([.. Issue("y.elmlic"), "--kind", "enterprise"]), "'enterprise'");
        Assert.False(File.Exists(P("x.elmlic")));

        _environment["ELM_KEY_PASSPHRASE"] = "wrong horse battery staple";
        AssertInputError(Run([.. Issue("z.elmlic")]), "passphrase is wrong");

        _environment.Remove("ELM_KEY_PASSPHRASE");
        Assert.Equal(2, Run("key", "root", "create", "--out", P("new.key.pem")).Code);
        _environment["ELM_KEY_PASSPHRASE"] = "short";
        Assert.Equal(2, Run("key", "root", "create", "--out", P("new2.key.pem")).Code);
        Assert.False(File.Exists(P("new.key.pem")));
        Assert.False(File.Exists(P("new2.key.pem")));
    }

    /// <summary>
    /// Without --trust, verify reads the root keys from ELM_TRUST_ROOT_KEYS, which may hold several PEM blocks; with neither,
    /// it exits with 2.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An issued license; the root public key.
    ///
    /// Steps:
    /// - Verify without --trust and with the variable holding another key and the root key; then with no variable.
    ///
    /// Expected:
    /// - Exit 0 with the variable; exit 2 and a message naming the variable without it.
    /// </remarks>
    [Fact]
    public void Verify_TrustFromEnvironment_IsUsed()
    {
        string license = IssueSample();
        string[] args = Verify(license);
        int trust = Array.IndexOf(args, "--trust");
        string[] withoutTrust = [.. args[..trust], .. args[(trust + 2)..]];
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        _environment["ELM_TRUST_ROOT_KEYS"] = other.ExportSubjectPublicKeyInfoPem() + Environment.NewLine + File.ReadAllText(P("root.pub.pem"));
        Assert.Equal(0, Run(withoutTrust).Code);

        _environment.Remove("ELM_TRUST_ROOT_KEYS");
        AssertInputError(Run(withoutTrust), "ELM_TRUST_ROOT_KEYS");
    }

    /// <summary>
    /// A license without expiry is issued with a warning, and verifies.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys and a certificate.
    ///
    /// Steps:
    /// - Issue with --expires never; verify.
    ///
    /// Expected:
    /// - Exit 0, a warning naming the missing expiry; verification exit 0 with "expires: never".
    /// </remarks>
    [Fact]
    public void Issue_NoExpiry_WarnsAndVerifies()
    {
        IssueSample();
        string[] issue = Issue("never.elmlic");
        issue[Array.IndexOf(issue, "--expires") + 1] = "never";

        (int code, _, string error) = Run(issue);
        Assert.Equal(0, code);
        Assert.Contains("warning: The license has no expiry", error, StringComparison.Ordinal);

        (int verifyCode, string verifyOut, _) = Run([.. Verify(P("never.elmlic"))]);
        Assert.Equal(0, verifyCode);
        Assert.Contains("expires: never", verifyOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// On Linux and macOS a private key file is created with owner-only access.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A non-Windows machine.
    ///
    /// Steps:
    /// - Create a root key.
    ///
    /// Expected:
    /// - The private key file mode is 0600.
    /// </remarks>
    [Fact]
    public void KeyCreate_PrivateKeyFile_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows uses access rules inherited from the folder.");
            return;
        }

        Assert.Equal(0, Run("key", "root", "create", "--out", P("owner.key.pem")).Code);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(P("owner.key.pem")));
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static void AssertInputError((int Code, string Out, string Error) result, string expected)
    {
        Assert.True(result.Code == 2, $"exit {result.Code}: {result.Error}");
        Assert.Contains("error:", result.Error, StringComparison.Ordinal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    private string IssueSample()
    {
        Assert.Equal(0, Run("key", "root", "create", "--out", P("root.key.pem"), "--public-out", P("root.pub.pem")).Code);
        Assert.Equal(0, Run("key", "signing", "create", "--out", P("signing.key.pem")).Code);
        (int certifyCode, _, string certifyError) = Run(
            "key",
            "certify",
            "--root",
            P("root.key.pem"),
            "--public",
            P("signing.key.pem.pub.pem"),
            "--use",
            "license",
            "--out",
            P("signing.cert.jws"),
            "--not-before",
            "2026-09-01"
        );
        Assert.True(certifyCode == 0, certifyError);
        (int code, _, string error) = Run([.. Issue("license.elmlic")]);
        Assert.True(code == 0, error);
        return P("license.elmlic");
    }

    private string[] Issue(string file) =>
        [
            "license",
            "issue",
            "--signing-key",
            P("signing.key.pem"),
            "--certificate",
            P("signing.cert.jws"),
            "--product",
            "acme-cad",
            "--license-id",
            "lic-1",
            "--feature",
            "pro;ver=>=2.0 <3.0",
            "--expires",
            "2026-12-31",
            "--bind-key",
            "installation-key-thumbprint",
            "--fingerprint",
            "2b9b86465b5d4fbda9c050845613fa89",
            "--out",
            P(file),
        ];

    private string[] Verify(string file, string product = "acme-cad") =>
        [
            "license",
            "verify",
            file,
            "--trust",
            P("root.pub.pem"),
            "--product",
            product,
            "--feature",
            "pro",
            "--version",
            "2.5",
            "--key-thumbprint",
            "installation-key-thumbprint",
            "--fingerprint",
            "2b9b86465b5d4fbda9c050845613fa89",
        ];

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = _time,
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            ReadSecret = null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
