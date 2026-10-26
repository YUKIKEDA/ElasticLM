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
    /// A server license states what the server does with a clone (design §8.4.2): deny unless --clone says flag.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys and a certificate from the offline flow.
    ///
    /// Steps:
    /// - Issue a server license (pro, 5 seats) without --clone and with --clone flag; inspect both.
    ///
    /// Expected:
    /// - Exit 0; the payloads hold "clone": "deny" and "clone": "flag".
    /// </remarks>
    [Fact]
    public void IssueServer_Clone_DefaultsToDeny()
    {
        IssueSample();
        string[] Server(string file, params string[] extra)
        {
            string[] args = [.. Issue(file), "--kind", "server", .. extra];
            args[Array.IndexOf(args, "--feature") + 1] = "pro;seats=5";
            return args;
        }

        Assert.Equal(0, Run(Server("deny.elmlic")).Code);
        Assert.Equal(0, Run(Server("flag.elmlic", "--clone", "flag")).Code);

        Assert.Contains("\"clone\": \"deny\"", Run("license", "inspect", P("deny.elmlic")).Out, StringComparison.Ordinal);
        Assert.Contains("\"clone\": \"flag\"", Run("license", "inspect", P("flag.elmlic")).Out, StringComparison.Ordinal);
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
        AssertInputError(Run([.. Issue("x.elmlic"), "--clone", "flag"]), "server licenses only");
        AssertInputError(Run([.. Issue("x.elmlic"), "--kind", "server", "--clone", "maybe"]), "deny or flag");
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
    /// Without --trust, issue and verify read the root keys from ELM_TRUST_ROOT_KEYS, which may hold several PEM blocks;
    /// with neither, they exit with 2 and issue writes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys and a certificate from the offline flow; an issued license; the root public key.
    ///
    /// Steps:
    /// - Issue and verify without --trust and with the variable holding another key and the root key.
    /// - Issue with the variable holding only the other key.
    /// - Issue and verify with no variable.
    ///
    /// Expected:
    /// - Exit 0 with both keys in the variable; exit 2 with ELM_UNTRUSTED_KEY with the other key only; exit 2 and a message
    ///   naming the variable without it. A refused issue leaves no file.
    /// </remarks>
    [Fact]
    public void TrustFromEnvironment_IsUsed_ByIssueAndVerify()
    {
        string license = IssueSample();
        string[] verify = WithoutTrust(Verify(license));
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string otherPem = other.ExportSubjectPublicKeyInfoPem();

        _environment["ELM_TRUST_ROOT_KEYS"] = otherPem + Environment.NewLine + File.ReadAllText(P("root.pub.pem"));
        Assert.Equal(0, Run(verify).Code);
        Assert.Equal(0, Run(WithoutTrust(Issue("env.elmlic"))).Code);
        Assert.True(File.Exists(P("env.elmlic")));

        _environment["ELM_TRUST_ROOT_KEYS"] = otherPem;
        AssertInputError(Run(WithoutTrust(Issue("other.elmlic"))), "ELM_UNTRUSTED_KEY");
        Assert.False(File.Exists(P("other.elmlic")));

        _environment.Remove("ELM_TRUST_ROOT_KEYS");
        AssertInputError(Run(verify), "ELM_TRUST_ROOT_KEYS");
        AssertInputError(Run(WithoutTrust(Issue("none.elmlic"))), "ELM_TRUST_ROOT_KEYS");
        Assert.False(File.Exists(P("none.elmlic")));

        static string[] WithoutTrust(string[] args)
        {
            int trust = Array.IndexOf(args, "--trust");
            return [.. args[..trust], .. args[(trust + 2)..]];
        }
    }

    /// <summary>
    /// key thumbprint prints the same key ID as key root create --json, and exits with 2 for a missing file or a file that
    /// is not a public key. The --json output of certify and issue is valid JSON with the same IDs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder.
    ///
    /// Steps:
    /// - Create a root key with --json; run key thumbprint on its public key, on a missing file, and on the encrypted private
    ///   key; certify and issue with --json.
    ///
    /// Expected:
    /// - The thumbprint equals keyId; exit 2 with "does not exist" and "not one P-256 public key"; certify and issue JSON
    ///   carry the root key ID and the license ID.
    /// </remarks>
    [Fact]
    public void Thumbprint_AndJsonOutput_MatchKeyIds()
    {
        (int code, string output, _) = Run("key", "root", "create", "--out", P("root.key.pem"), "--public-out", P("root.pub.pem"), "--json");
        Assert.Equal(0, code);
        string rootKeyId;
        using (JsonDocument json = JsonDocument.Parse(output))
        {
            rootKeyId = json.RootElement.GetProperty("keyId").GetString()!;
        }

        (int thumbCode, string thumbOut, _) = Run("key", "thumbprint", P("root.pub.pem"));
        Assert.Equal(0, thumbCode);
        Assert.Equal(rootKeyId, thumbOut.Trim());
        AssertInputError(Run("key", "thumbprint", P("missing.pem")), "does not exist");
        AssertInputError(Run("key", "thumbprint", P("root.key.pem")), "not one P-256 public key");

        Assert.Equal(0, Run("key", "signing", "create", "--out", P("signing.key.pem")).Code);
        (int certCode, string certOut, _) = Run(
            "key",
            "certify",
            "--root",
            P("root.key.pem"),
            "--public",
            P("signing.key.pem.pub.pem"),
            "--out",
            P("signing.cert.jws"),
            "--not-before",
            "2026-09-01",
            "--json"
        );
        Assert.Equal(0, certCode);
        using (JsonDocument json = JsonDocument.Parse(certOut))
        {
            Assert.Equal(rootKeyId, json.RootElement.GetProperty("rootKeyId").GetString());
        }

        (int issueCode, string issueOut, _) = Run([.. Issue("license.elmlic"), "--json"]);
        Assert.Equal(0, issueCode);
        using (JsonDocument json = JsonDocument.Parse(issueOut))
        {
            Assert.Equal("lic-1", json.RootElement.GetProperty("licenseId").GetString());
        }
    }

    /// <summary>
    /// Malformed input from files and options is reported with exit 2 and its own message, never as an exception
    /// (.cursor/rules/security.mdc).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keys and a certificate from the offline flow; files with broken license contents.
    ///
    /// Steps:
    /// - license inspect on: not JSON, license not a string, certificates not an array, a null certificate, a token with two
    ///   segments, a segment that is not base64url. license issue with: a feature without ID, an unknown feature part, seats
    ///   not a number, an expiry that is not a date (in a feature and in --expires), metadata without '=', a public key as the
    ///   certificate, --trust with another root. key certify with --days 0 and 732 and a bad --not-before.
    ///
    /// Expected:
    /// - Exit 2 and the message part named in each call.
    /// </remarks>
    [Fact]
    public void Errors_MalformedInput_ExitWithTwoAndMessage()
    {
        IssueSample();
        string token = File.ReadAllText(P("signing.cert.jws")).Trim();
        void Inspect(string content)
        {
            File.WriteAllText(P("bad.elmlic"), content);
            AssertInputError(Run("license", "inspect", P("bad.elmlic")), "not an ElasticLM license file");
        }

        Inspect("{not json");
        Inspect("{\"format\":\"elasticlm-license\",\"version\":1,\"license\":5,\"certificates\":[]}");
        Inspect($"{{\"format\":\"elasticlm-license\",\"version\":1,\"license\":\"{token}\",\"certificates\":{{}}}}");
        Inspect($"{{\"format\":\"elasticlm-license\",\"version\":1,\"license\":\"{token}\",\"certificates\":[null]}}");
        Inspect("{\"format\":\"elasticlm-license\",\"version\":1,\"license\":\"aaaa.bbbb\",\"certificates\":[]}");
        Inspect("{\"format\":\"elasticlm-license\",\"version\":1,\"license\":\"a*a.bbbb.cccc\",\"certificates\":[]}");

        string[] WithFeature(string feature)
        {
            string[] args = Issue("f.elmlic");
            args[Array.IndexOf(args, "--feature") + 1] = feature;
            return args;
        }

        AssertInputError(Run(WithFeature(";ver=1")), "has no ID");
        AssertInputError(Run(WithFeature("pro;foo=1")), "unknown or invalid part");
        AssertInputError(Run(WithFeature("pro;seats=abc")), "unknown or invalid part");
        AssertInputError(Run(WithFeature("pro;exp=tomorrow")), "not an ISO 8601");
        string[] badExpiry = Issue("e.elmlic");
        badExpiry[Array.IndexOf(badExpiry, "--expires") + 1] = "tomorrow";
        AssertInputError(Run(badExpiry), "not an ISO 8601");
        AssertInputError(Run([.. Issue("m.elmlic"), "--meta", "novalue"]), "not key=value");
        string[] publicAsCertificate = Issue("c.elmlic");
        publicAsCertificate[Array.IndexOf(publicAsCertificate, "--certificate") + 1] = P("root.pub.pem");
        AssertInputError(Run(publicAsCertificate), "can be read as an elm-cert+jws");
        using (ECDsa otherRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(P("other.pub.pem"), otherRoot.ExportSubjectPublicKeyInfoPem());
        }

        string[] otherTrust = Issue("t.elmlic");
        otherTrust[Array.IndexOf(otherTrust, "--trust") + 1] = P("other.pub.pem");
        AssertInputError(Run(otherTrust), "ELM_UNTRUSTED_KEY");
        Assert.False(File.Exists(P("t.elmlic")));

        string[] Certify(params string[] extra) =>
            ["key", "certify", "--root", P("root.key.pem"), "--public", P("signing.key.pem.pub.pem"), "--out", P("c9.jws"), .. extra];
        AssertInputError(Run(Certify("--days", "0")), "validity must be positive");
        AssertInputError(Run(Certify("--days", "732")), "validity must be positive");
        AssertInputError(Run(Certify("--not-before", "someday")), "not an ISO 8601");
    }

    /// <summary>
    /// key create checks both output files before writing, so a refused public key file leaves no private key behind; a
    /// --trust file may hold several PEM keys.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An existing file at the public key path; a license issued under the test root.
    ///
    /// Steps:
    /// - Create a root key whose public key path exists, without --force. Verify with a --trust file holding another key
    ///   and the root key.
    ///
    /// Expected:
    /// - Exit 2 and no private key file; the verification exits 0.
    /// </remarks>
    [Fact]
    public void KeyCreate_ExistingPublicFile_WritesNothing_AndTrustFileHoldsSeveralKeys()
    {
        File.WriteAllText(P("taken.pub.pem"), "x");
        AssertInputError(Run("key", "root", "create", "--out", P("fresh.key.pem"), "--public-out", P("taken.pub.pem")), "exists");
        Assert.False(File.Exists(P("fresh.key.pem")));

        string license = IssueSample();
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(P("both.pub.pem"), other.ExportSubjectPublicKeyInfoPem() + Environment.NewLine + File.ReadAllText(P("root.pub.pem")));
        string[] args = Verify(license);
        args[Array.IndexOf(args, "--trust") + 1] = P("both.pub.pem");

        Assert.Equal(0, Run(args).Code);
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
            "--trust",
            P("root.pub.pem"),
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
