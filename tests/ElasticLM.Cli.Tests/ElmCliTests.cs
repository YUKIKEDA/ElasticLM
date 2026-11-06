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
        Assert.Equal(0, Run(Server("flag.elmlic", "--clone", "flag", "--borrow-returns", "5")).Code);

        Assert.Contains("\"clone\": \"deny\"", Run("license", "inspect", P("deny.elmlic")).Out, StringComparison.Ordinal);
        Assert.Contains("\"brl\": 2", Run("license", "inspect", P("deny.elmlic")).Out, StringComparison.Ordinal);
        Assert.Contains("\"brl\": 5", Run("license", "inspect", P("flag.elmlic")).Out, StringComparison.Ordinal);
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
        AssertInputError(Run([.. Issue("x.elmlic"), "--borrow-returns", "3"]), "server licenses only");
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

    /// <summary>
    /// secret create writes 32 random bytes in base64 on one line, and prints only the file's path.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder.
    ///
    /// Steps:
    /// - secret create --out first.secret, then --out second.secret.
    ///
    /// Expected:
    /// - Exit 0 and the standard output "secret file: &lt;path&gt;" alone, with nothing on the standard error. Each file is
    ///   one base64 line and a line break, which decodes to 32 bytes (the service's minimum, design §14.8). The two secrets
    ///   differ, and no output holds the secret.
    /// </remarks>
    [Fact]
    public void SecretCreate_WritesBase64Of32Bytes_AndPrintsOnlyThePath()
    {
        (int code, string output, string error) = Run("secret", "create", "--out", P("first.secret"));
        (int secondCode, string secondOutput, _) = Run("secret", "create", "--out", P("second.secret"));

        Assert.Equal(0, code);
        Assert.Equal(0, secondCode);
        Assert.Equal($"secret file: {P("first.secret")}{Environment.NewLine}", output);
        Assert.Equal(string.Empty, error);
        string first = File.ReadAllText(P("first.secret"));
        string second = File.ReadAllText(P("second.secret"));
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Single(first.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(32, Convert.FromBase64String(first.TrimEnd('\n')).Length);
        Assert.Equal(32, Convert.FromBase64String(second.TrimEnd('\n')).Length);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(first.TrimEnd('\n'), output, StringComparison.Ordinal);
        Assert.DoesNotContain(second.TrimEnd('\n'), secondOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// secret create --json writes an object with the file's path only, never the secret.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder.
    ///
    /// Steps:
    /// - secret create --out json.secret --json.
    ///
    /// Expected:
    /// - Exit 0; the output is a JSON object whose only property is "file" with the path; it does not hold the secret.
    /// </remarks>
    [Fact]
    public void SecretCreate_Json_WritesTheFileOnly()
    {
        (int code, string output, _) = Run("secret", "create", "--out", P("json.secret"), "--json");

        Assert.Equal(0, code);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonProperty property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("file", property.Name);
        Assert.Equal(P("json.secret"), property.Value.GetString());
        Assert.DoesNotContain(File.ReadAllText(P("json.secret")).TrimEnd('\n'), output, StringComparison.Ordinal);
    }

    /// <summary>
    /// secret create refuses a file that exists, and overwrites it with --force.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file "old" at the output path.
    ///
    /// Steps:
    /// - secret create without --force, then with --force.
    ///
    /// Expected:
    /// - Exit 2 with "exists. Pass --force", nothing on the standard output, and the file unchanged; then exit 0 and a new
    ///   32-byte secret in the file.
    /// </remarks>
    [Fact]
    public void SecretCreate_ExistingFile_RefusedWithoutForce()
    {
        File.WriteAllText(P("taken.secret"), "old");

        (int code, string output, string error) = Run("secret", "create", "--out", P("taken.secret"));
        AssertInputError((code, output, error), "exists. Pass --force to overwrite it.");
        Assert.Equal(string.Empty, output);
        Assert.Equal("old", File.ReadAllText(P("taken.secret")));

        Assert.Equal(0, Run("secret", "create", "--out", P("taken.secret"), "--force").Code);
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(P("taken.secret")).TrimEnd('\n')).Length);
    }

    /// <summary>
    /// On Linux and macOS a secret file is created with owner-only access.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A non-Windows machine.
    ///
    /// Steps:
    /// - secret create into a new file.
    ///
    /// Expected:
    /// - The file mode is 0600.
    /// </remarks>
    [Fact]
    public void SecretCreate_NewFile_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows uses access rules inherited from the folder.");
            return;
        }

        Assert.Equal(0, Run("secret", "create", "--out", P("owner.secret")).Code);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(P("owner.secret")));
    }

    /// <summary>
    /// On Linux and macOS, a secret written over an existing 0644 file with --force leaves the file at 0600.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A non-Windows machine; a file with mode 0644 at the output path (the mode a new file gets under umask 022).
    ///
    /// Steps:
    /// - Cases: secret create --force, and key root create --force (the private key file); both write through the same
    ///   file writer (design §16).
    ///
    /// Expected:
    /// - Exit 0 and the file mode 0600: the mode of the file that was there does not stay.
    /// </remarks>
    /// <param name="command">The command that writes a secret file.</param>
    [Theory]
    [InlineData("secret")]
    [InlineData("key")]
    public void SecretFile_ForceOverExistingFile_BecomesOwnerOnly(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows uses access rules inherited from the folder.");
            return;
        }

        string path = P("existing.pem");
        File.WriteAllText(path, "old");
        const UnixFileMode Wide = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(path, Wide);
        Assert.Equal(Wide, File.GetUnixFileMode(path));

        (int code, _, string error) =
            command == "secret"
                ? Run("secret", "create", "--out", path, "--force")
                : Run("key", "root", "create", "--out", path, "--public-out", P("existing.pub.pem"), "--force");

        Assert.True(code == 0, error);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    /// <summary>
    /// On Windows, a secret written over an existing file with --force does not keep the explicit access rules of that file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a file at the output path with an explicit rule that lets Everyone read it.
    ///
    /// Steps:
    /// - secret create --force over the file.
    ///
    /// Expected:
    /// - Exit 0, a new secret in the file, and no explicit rule for Everyone: the file has only the rules of its folder
    ///   (design §16).
    /// </remarks>
    [Fact]
    public void SecretCreate_ForceOverFileWithExplicitRule_DropsTheRule()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Explicit access rules are a Windows matter; Linux and macOS have the mode tests.");
            return;
        }

        string path = P("shared.secret");
        File.WriteAllText(path, "old");
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        var info = new FileInfo(path);
        System.Security.AccessControl.FileSecurity security = info.GetAccessControl();
        security.AddAccessRule(
            new System.Security.AccessControl.FileSystemAccessRule(
                everyone,
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow
            )
        );
        info.SetAccessControl(security);
        Assert.Contains(everyone.Value, ExplicitRuleSids(path));

        (int code, _, string error) = Run("secret", "create", "--out", path, "--force");

        Assert.True(code == 0, error);
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(path).TrimEnd('\n')).Length);
        Assert.DoesNotContain(everyone.Value, ExplicitRuleSids(path));

        // The SIDs of the rules set on the file itself, not inherited from its folder.
        static string[] ExplicitRuleSids(string file)
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }

            var sids = new List<string>();
            foreach (
                System.Security.AccessControl.FileSystemAccessRule rule in new FileInfo(file)
                    .GetAccessControl()
                    .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier))
            )
            {
                sids.Add(rule.IdentityReference.Value);
            }

            return [.. sids];
        }
    }

    /// <summary>
    /// On Linux and macOS, a process that opened the old file before --force does not read the new secret through it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A non-Windows machine; a file "old" at the output path, opened for reading by the test.
    ///
    /// Steps:
    /// - secret create --force over the file; read the open handle from its start; read the path.
    ///
    /// Expected:
    /// - Exit 0; the open handle still reads "old"; the path reads the new 32-byte secret (design §16).
    /// </remarks>
    [Fact]
    public void SecretCreate_ForceWhileOldFileIsOpen_OldHandleKeepsOldContent()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A file that another process holds open is a Linux and macOS matter here; Windows has the access rule test.");
            return;
        }

        string path = P("open.secret");
        File.WriteAllText(path, "old");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        (int code, _, string error) = Run("secret", "create", "--out", path, "--force");

        Assert.True(code == 0, error);
        held.Position = 0;
        using var reader = new StreamReader(held);
        Assert.Equal("old", reader.ReadToEnd());
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(path).TrimEnd('\n')).Length);
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
