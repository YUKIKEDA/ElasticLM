using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The security of the PostgreSQL connection (design §14.2, T30): the settings refuse a connection to another machine that does
/// not check the server's certificate, and files the connection string names that do not exist; the service negotiates TLS;
/// a database error names its inner reasons.
/// </summary>
public sealed class PostgresConnectionTests : IDisposable
{
    /// <summary>The PEM file of the CA that signed the certificate of the PostgreSQL server of <see cref="TestService.PostgresVariable"/>.</summary>
    public const string RootVariable = "ELM_TEST_POSTGRES_ROOT";

    private readonly string _folder = Path.Combine(
        TestFolders.Root,
        "elm-pg-connection-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))
    );

    public PostgresConnectionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// A connection to another machine is refused unless SSL Mode is VerifyFull, VerifyCA, or Disable; a connection to this
    /// machine (loopback addresses, localhost, Unix sockets) takes any SSL Mode; names are not looked up.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Check each connection string of the table.
    ///
    /// Expected:
    /// - The strings marked refused throw ServiceStartException naming the SSL Mode in use; the others pass.
    /// </remarks>
    [Theory]
    [InlineData("Host=db", true)]
    [InlineData("Host=db;SSL Mode=Prefer", true)]
    [InlineData("Host=db;SSL Mode=Allow", true)]
    [InlineData("Host=db;SSL Mode=Require", true)]
    [InlineData("Host=db;SSL Mode=Require;Root Certificate=", true)]
    [InlineData("Host=10.0.0.5:5433;SSL Mode=Require", true)]
    [InlineData("Host=db;SSL Mode=VerifyCA", false)]
    [InlineData("Host=db;SSL Mode=VerifyFull", false)]
    [InlineData("Host=db;SSL Mode=Disable", false)]
    [InlineData("Host=127.0.0.1", false)]
    [InlineData("Host=127.1.2.3;SSL Mode=Require", false)]
    [InlineData("Host=::1;SSL Mode=Prefer", false)]
    [InlineData("Host=[::1]:5433", false)]
    [InlineData("Host=localhost", false)]
    [InlineData("Host=LocalHost:5433;SSL Mode=Allow", false)]
    [InlineData("Host=/var/run/postgresql", false)]
    [InlineData("Host=@pg", false)]
    [InlineData("Host=localhost, 127.0.0.1:5433", false)]
    [InlineData("Host=localhost,db", true)]
    [InlineData("Host=db,localhost;SSL Mode=Require", true)]
    [InlineData("Host=127.0.0.1.nip.io", true)]
    [InlineData("Host=localhost.example", true)]
    [InlineData("Host=[2001:db8::1]:5432", true)]
    public void Check_HostAndSslMode_RefusesAnUncheckedServerOnAnotherMachine(string connection, bool refused)
    {
        ServiceStartException? error = Record.Exception(() => PostgresConnection.Check(connection)) as ServiceStartException;
        if (refused)
        {
            Assert.NotNull(error);
            string mode = new NpgsqlConnectionStringBuilder(connection).SslMode.ToString();
            Assert.Equal(
                "The database connection string does not check the certificate of a server on another machine (SSL Mode is "
                    + mode
                    + "): set SSL Mode to VerifyFull or VerifyCA, or to Disable to connect without TLS.",
                error.Message
            );
        }
        else
        {
            Assert.Null(error);
        }
    }

    /// <summary>
    /// A connection string without a host, or with a port that is not a number, is refused as not valid before Npgsql would
    /// throw when the data source is built.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Check each connection string of the table.
    ///
    /// Expected:
    /// - ServiceStartException with the message of the table.
    /// </remarks>
    [Theory]
    [InlineData("Database=elm", "The database connection string is not valid: Host is not set.")]
    [InlineData("Host= , ;SSL Mode=VerifyFull", "The database connection string is not valid: Host is not set.")]
    [InlineData("Host=localhost:abc", "The database connection string is not valid: the port of the host 'localhost:abc' is not a number.")]
    [InlineData(
        "Host=db:5432,db2:x;SSL Mode=VerifyFull",
        "The database connection string is not valid: the port of the host 'db2:x' is not a number."
    )]
    [InlineData("Host=[::1]:p", "The database connection string is not valid: the port of the host '[::1]:p' is not a number.")]
    public void Check_NoHostOrBadPort_IsNotValid(string connection, string message) =>
        Assert.Equal(message, Assert.Throws<ServiceStartException>(() => PostgresConnection.Check(connection)).Message);

    /// <summary>
    /// A Root Certificate, SSL Certificate, or SSL Key that names no file is refused with the key and the path; files that
    /// exist pass; a connection string that is not valid is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with one file in it.
    ///
    /// Steps:
    /// - Check a VerifyFull string with each key naming a missing file; with all three naming the file; with an unknown key.
    ///
    /// Expected:
    /// - Each missing file is refused with its key and path; the existing files pass; the unknown key is refused as not valid.
    /// </remarks>
    [Fact]
    public void Check_CertificateFiles_MustExist()
    {
        string present = Path.Combine(_folder, "present.pem");
        File.WriteAllText(present, "x");
        string missing = Path.Combine(_folder, "missing.pem");
        foreach (string key in new[] { "Root Certificate", "SSL Certificate", "SSL Key" })
        {
            var error = Assert.Throws<ServiceStartException>(() => PostgresConnection.Check($"Host=db;SSL Mode=VerifyFull;{key}={missing}"));
            Assert.Equal($"The database connection string names a {key} file that does not exist: '{missing}'.", error.Message);
        }

        PostgresConnection.Check($"Host=db;SSL Mode=VerifyFull;Root Certificate={present};SSL Certificate={present};SSL Key={present}");
        var invalid = Assert.Throws<ServiceStartException>(() => PostgresConnection.Check("Host=db;SSL Mode=VerifyFull;Unknown Key=1"));
        Assert.StartsWith("The database connection string is not valid: ", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The settings run the check for PostgreSQL only, and a refused string stops the load.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Load settings from variables with provider postgres and Host=db; with provider sqlite and a SQLite string; with provider
    ///   postgres and Host=db;SSL Mode=VerifyFull.
    ///
    /// Expected:
    /// - The first throws ServiceStartException about SSL Mode; the other two load.
    /// </remarks>
    [Fact]
    public void Load_Postgres_IsChecked()
    {
        ServiceSettings Load(string provider, string connection) =>
            ServiceSettings.Load(
                null,
                name =>
                    name switch
                    {
                        "ELM_DATABASE_PROVIDER" => provider,
                        ServiceSettings.ConnectionVariable => connection,
                        _ => null,
                    },
                requireSigning: false
            );

        var error = Assert.Throws<ServiceStartException>(() => Load("postgres", "Host=db"));
        Assert.Contains("(SSL Mode is Prefer)", error.Message, StringComparison.Ordinal);
        Assert.Equal(DatabaseProvider.Sqlite, Load("sqlite", "Data Source=elm.db").Database);
        Assert.Equal(DatabaseProvider.Postgres, Load("postgres", "Host=db;SSL Mode=VerifyFull").Database);
    }

    /// <summary>
    /// The text of a database error carries the text of every inner exception, in order.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Describe an NpgsqlException with two inner exceptions, and one with none.
    ///
    /// Expected:
    /// - "a: b: c" and "a".
    /// </remarks>
    [Fact]
    public void Describe_InnerExceptions_FollowTheMessage()
    {
        Assert.Equal("a: b: c", Database.Describe(new NpgsqlException("a", new IOException("b", new FileNotFoundException("c")))));
        Assert.Equal("a", Database.Describe(new NpgsqlException("a")));
    }

    /// <summary>
    /// On a PostgreSQL server with TLS, setup connects with TLS under VerifyFull and under Require, and a root certificate that
    /// did not sign the server's certificate ends setup with exit code 1 and the reason.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - <see cref="TestService.PostgresVariable"/> names a server with TLS whose certificate <see cref="RootVariable"/>'s CA
    ///   signed for localhost and 127.0.0.1; a schema of this test.
    ///
    /// Steps:
    /// - Run setup with SSL Mode=VerifyFull and the root; read pg_stat_ssl through the service's data source; do the same with
    ///   SSL Mode=Require; run setup with VerifyFull and a CA made by the test.
    ///
    /// Expected:
    /// - 0 and an encrypted session twice; then 1 with "The database cannot be used: Exception while performing SSL handshake: "
    ///   and the inner reason after it.
    /// </remarks>
    [Fact]
    public async Task Setup_PostgresWithTls_ConnectsOrNamesTheReason()
    {
        string? root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipUnless(root is { Length: > 0 }, $"Set {RootVariable} and {TestService.PostgresVariable} to a PostgreSQL server with TLS.");
        await using TestService test = TestService.Open("postgres");
        CancellationToken cancel = TestContext.Current.CancellationToken;

        string With(SslMode mode, string certificate) =>
            new NpgsqlConnectionStringBuilder(test.ConnectionString) { SslMode = mode, RootCertificate = certificate }.ConnectionString;

        async Task<(int Code, string Error)> Run(string connection)
        {
            var error = new StringWriter();
            int code = await Setup.RunAsync(
                test.Settings(s => s with { ConnectionString = connection }),
                test.Time,
                new StringWriter(),
                error,
                cancel
            );
            return (code, error.ToString().Trim());
        }

        foreach (SslMode mode in new[] { SslMode.VerifyFull, SslMode.Require })
        {
            string connection = With(mode, root!);
            (int code, string error) = await Run(connection);
            Assert.True(code == 0, error);
            await using Database database = Database.Create(DatabaseProvider.Postgres, connection);
            await using DbConnection open = await database.OpenAsync(cancel);
            await using DbCommand query = Database.Command(open, null, "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()");
            Assert.Equal(true, await query.ExecuteScalarAsync(cancel));
        }

        string other = Path.Combine(_folder, "other-ca.pem");
        using (ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request = new CertificateRequest("CN=elm test other CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using X509Certificate2 ca = request.CreateSelfSigned(
                TimeProvider.System.GetUtcNow().AddMinutes(-5),
                TimeProvider.System.GetUtcNow().AddHours(1)
            );
            File.WriteAllText(other, ca.ExportCertificatePem());
        }

        (int refusedCode, string refusedError) = await Run(With(SslMode.VerifyFull, other));
        Assert.Equal(1, refusedCode);
        const string Prefix = "The database cannot be used: Exception while performing SSL handshake: ";
        Assert.StartsWith(Prefix, refusedError, StringComparison.Ordinal);
        Assert.True(refusedError.Length > Prefix.Length, refusedError);
    }

    /// <summary>
    /// On a PostgreSQL server with TLS, a client certificate that Npgsql cannot read (a missing file, a file that is not a
    /// certificate) ends setup with exit code 1 and the reason, and stops the start with the same line, instead of an
    /// unhandled exception. The files are given through the connection string, which skips the settings' check, as a
    /// missing PGSSLCERT file or a broken postgresql.crt would.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As for <see cref="Setup_PostgresWithTls_ConnectsOrNamesTheReason"/>; a file "not a certificate" in the test's folder.
    ///
    /// Steps:
    /// - Run setup with SSL Mode=Require and SSL Certificate naming a missing file; then naming the file that is not a
    ///   certificate; start the service with the second string.
    ///
    /// Expected:
    /// - Exit code 1 and "The database cannot be used: " with the file's path; exit code 1 and the same start; a
    ///   ServiceStartException whose message starts the same way.
    /// </remarks>
    [Fact]
    public async Task Open_ClientCertificateNpgsqlCannotRead_NamesTheReason()
    {
        string? root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipUnless(root is { Length: > 0 }, $"Set {RootVariable} and {TestService.PostgresVariable} to a PostgreSQL server with TLS.");
        await using TestService test = TestService.Open("postgres");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string missing = Path.Combine(_folder, "missing-client.pem");
        string broken = Path.Combine(_folder, "broken-client.pem");
        File.WriteAllText(broken, "not a certificate\n");

        string With(string certificate) =>
            new NpgsqlConnectionStringBuilder(test.ConnectionString) { SslMode = SslMode.Require, SslCertificate = certificate }.ConnectionString;

        const string Prefix = "The database cannot be used: ";
        foreach (string certificate in new[] { missing, broken })
        {
            var error = new StringWriter();
            int code = await Setup.RunAsync(
                test.Settings(s => s with { ConnectionString = With(certificate) }),
                test.Time,
                new StringWriter(),
                error,
                cancel
            );
            string text = error.ToString().Trim();
            Assert.Equal(1, code);
            Assert.StartsWith(Prefix, text, StringComparison.Ordinal);
            Assert.True(text.Length > Prefix.Length, text);
        }

        var started = await Assert.ThrowsAsync<ServiceStartException>(() => test.StartAsync(s => s with { ConnectionString = With(broken) }));
        Assert.StartsWith(Prefix, started.Message, StringComparison.Ordinal);
    }
}
