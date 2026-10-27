using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// <c>elm-service setup</c> (design §14.6, decisions 0.63 and 0.80).
/// </summary>
public sealed class SetupTests
{
    /// <summary>
    /// setup writes only the key to standard output and its description to standard error, records itself in the audit log,
    /// may run again, and refuses without bootstrapOwner (exit 2) or with a disabled owner (exit 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty SQLite database.
    ///
    /// Steps:
    /// - Run setup without bootstrapOwner; with it, twice; disable the owner in the database and run again; run with a
    ///   connection string that is not valid.
    ///
    /// Expected:
    /// - 2 with nothing on standard output and the reason on standard error; 0 twice, each output one elm_ key of 47
    ///   characters and a line on standard error naming the owner, two different keys and two setup rows (apikey.create by
    ///   system/setup with the owner in the detail) in the audit log; 1 with the reason; 2 with the reason.
    /// </remarks>
    [Fact]
    public async Task Run_Repeated_PrintsOnlyTheKey()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<(int Code, string Out, string Error)> Run(ServiceSettings settings)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = await Setup.RunAsync(settings, test.Time, output, error, cancel);
            return (code, output.ToString(), error.ToString().Trim());
        }

        Assert.Equal(
            (2, string.Empty, "Set bootstrapOwner (iss and sub) to the first owner before running setup."),
            await Run(test.Settings(s => s with { BootstrapOwner = null }))
        );

        var keys = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            (int code, string output, string error) = await Run(test.Settings());
            Assert.Equal(0, code);
            string key = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal((47, true), (key.Length, key.StartsWith("elm_", StringComparison.Ordinal)));
            Assert.Contains("https://idp.example owner-1", error, StringComparison.Ordinal);
            keys.Add(key);
        }

        Assert.NotEqual(keys[0], keys[1]);
        ElmService service = await test.StartAsync();
        JsonElement[] setupRows =
        [
            .. (await TestService.AuditRowsAsync(test, service, keys[1])).Where(r => r.GetProperty("actorId").GetString() == "setup"),
        ];
        Assert.Equal(2, setupRows.Length);
        Assert.All(
            setupRows,
            r =>
                Assert.Equal(
                    ("system", "apikey.create", "https://idp.example owner-1"),
                    (
                        r.GetProperty("actorType").GetString(),
                        r.GetProperty("action").GetString(),
                        JsonDocument.Parse(r.GetProperty("detail").GetString()!).RootElement.GetProperty("owner").GetString()
                    )
                )
        );

        await test.ExecuteAsync("UPDATE admins SET enabled = false");
        Assert.Equal((1, "The bootstrap owner is disabled or no longer an owner; setup does not change that."), Drop(await Run(test.Settings())));
        (int badCode, _, string badError) = await Run(test.Settings(s => s with { ConnectionString = "Data Source=x;Unknown Key=1" }));
        Assert.Equal(2, badCode);
        Assert.StartsWith("The database connection string is not valid: ", badError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two setups at once for a new owner both succeed and add the owner once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty database on the provider, migrated.
    ///
    /// Steps:
    /// - Run setup twice at once; count the administrators.
    ///
    /// Expected:
    /// - 0 and 0; one administrator.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Run_TwiceAtOnce_AddsTheOwnerOnce(string provider)
    {
        await using TestService test = TestService.Open(provider);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using (Database database = Database.Create(test.Provider, test.ConnectionString))
        {
            await Migrator.RunAsync(database, Migrator.Embedded(test.Provider), test.Time, cancel);
        }

        int[] codes = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ => Task.Run(() => Setup.RunAsync(test.Settings(), test.Time, new StringWriter(), new StringWriter(), cancel), cancel))
        );
        Assert.Equal([0, 0], codes);
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM admins"));
    }

    private static (int Code, string Error) Drop((int Code, string Out, string Error) run) => (run.Code, run.Error);
}
