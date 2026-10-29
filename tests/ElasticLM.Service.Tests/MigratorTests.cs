using System.Data.Common;
using System.Globalization;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The schema migrations (design §14.2, decision 0.69): each version once, in order, checked by its SHA-256.
/// </summary>
public sealed class MigratorTests
{
    /// <summary>
    /// The embedded migrations run from an empty database once; a second run applies nothing; two runs at once apply each
    /// version once; on SQLite the file is left in write-ahead logging.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty database on the provider.
    ///
    /// Steps:
    /// - Run the migrations twice in parallel; run them again; count the rows of schema_migrations; on SQLite, read
    ///   journal_mode.
    ///
    /// Expected:
    /// - Between them the two runs applied versions 1 to 8 once each; the third applied nothing; eight rows; wal.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Run_TwiceAtOnce_AppliesEachVersionOnce(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<Migration> migrations = Migrator.Embedded(test.Provider);

        IReadOnlyList<int>[] runs = await Task.WhenAll(
            Task.Run(() => Migrator.RunAsync(database, migrations, test.Time, cancel), cancel),
            Task.Run(() => Migrator.RunAsync(database, migrations, test.Time, cancel), cancel)
        );
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], runs.SelectMany(r => r).Order());
        Assert.Empty(await Migrator.RunAsync(database, migrations, test.Time, cancel));
        await using DbConnection connection = await database.OpenAsync(cancel);
        await using DbCommand count = Database.Command(connection, null, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(8L, Convert.ToInt64(await count.ExecuteScalarAsync(cancel), CultureInfo.InvariantCulture));
        if (test.Provider == DatabaseProvider.Sqlite)
        {
            await using DbCommand mode = Database.Command(connection, null, "PRAGMA journal_mode");
            Assert.Equal("wal", await mode.ExecuteScalarAsync(cancel));
        }
    }

    /// <summary>Gets each engine with each version a database may stand at before the last.</summary>
    public static TheoryData<string, int> EarlierVersions
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string provider in new[] { "sqlite", "postgres" })
            {
                for (int version = 1; version < Migrator.Embedded(DatabaseProvider.Sqlite).Count; version++)
                {
                    data.Add(provider, version);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A database at any earlier version is brought to the last version with what it holds kept.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database on the provider migrated up to the case's version (1 for M4a1, 2 for M4a2, and on as versions are added).
    ///
    /// Steps:
    /// - Insert an administrator into version 1's tables; run the embedded migrations; count the administrators and the
    ///   products.
    ///
    /// Expected:
    /// - The versions after the case's applied; one administrator; no product.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public async Task Run_FromAnEarlierVersion_AppliesTheRest(string provider, int version)
    {
        await using TestService test = TestService.Open(provider);
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<Migration> migrations = Migrator.Embedded(test.Provider);
        await Migrator.RunAsync(database, [.. migrations.Take(version)], test.Time, cancel);
        await test.ExecuteAsync(
            test.Provider == DatabaseProvider.Sqlite
                ? "INSERT INTO admins (id, issuer, subject, role, enabled, created_at) VALUES ('0196f3a0-0000-7000-8000-000000000001', 'i', 's', 'owner', 1, 0)"
                : "INSERT INTO admins (id, issuer, subject, role, enabled, created_at) VALUES ('0196f3a0-0000-7000-8000-000000000001', 'i', 's', 'owner', true, 0)"
        );

        Assert.Equal([.. migrations.Skip(version).Select(m => m.Version)], await Migrator.RunAsync(database, migrations, test.Time, cancel));
        Assert.Equal((1L, 0L), (await test.ScalarAsync("SELECT COUNT(*) FROM admins"), await test.ScalarAsync("SELECT COUNT(*) FROM products")));
    }

    /// <summary>
    /// Version 4 builds the SQLite licenses table again (its key becomes nullable); a license stored at version 3 keeps every
    /// column, starts with no deactivation, and a second license without a key can be stored.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database on the provider at version 3 with a product, a policy, and a license with every column set.
    ///
    /// Steps:
    /// - Run the rest; read the license's columns; insert two licenses without a key.
    ///
    /// Expected:
    /// - Versions 4 to 8 applied; the same values, deactivations 0; both inserts succeed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Run_FromVersion3_KeepsTheLicenses(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<Migration> migrations = Migrator.Embedded(test.Provider);
        await Migrator.RunAsync(database, [.. migrations.Take(3)], test.Time, cancel);
        const string Product = "'0196f3a0-0000-7000-8000-000000000001'";
        const string Policy = "'0196f3a0-0000-7000-8000-000000000002'";
        await test.ExecuteAsync($"INSERT INTO products (id, code, name, created_at) VALUES ({Product}, 'app', 'App', 1)");
        await test.ExecuteAsync(
            "INSERT INTO policies (id, product_id, name, license_kind, binding, max_activations, expiration_basis, token_lifetime, "
                + "refresh_window, grace_period, fingerprint_matching, clone_strategy, floating, lease_duration, borrow_return_limit, "
                + $"client_info_strategy, trial_duration, created_at, updated_at) VALUES ({Policy}, {Product}, 'p', 'standard', "
                + "'nodeLocked', 1, 'fromIssue', 3600, 0, 0, 'any', 'deny', 'none', 600, 2, 'none', 86400, 1, 1)"
        );
        string hash = test.Provider == DatabaseProvider.Sqlite ? "x'0102'" : "decode('0102', 'hex')";
        await test.ExecuteAsync(
            "INSERT INTO licenses (id, product_id, policy_id, name, key_hash, max_activations, expires_at, suspended_at, revoked_at, "
                + $"features, meta, created_at, updated_at) VALUES ('0196f3a0-0000-7000-8000-000000000003', {Product}, {Policy}, 'n', "
                + $"{hash}, 3, 100, 50, 60, '[]', '{{}}', 10, 20)"
        );

        Assert.Equal([4, 5, 6, 7, 8], await Migrator.RunAsync(database, migrations, test.Time, cancel));
        Assert.Equal(
            (3L, 100L, 50L, 60L, 10L, 20L, 0L, 1L),
            (
                await test.ScalarAsync("SELECT max_activations FROM licenses"),
                await test.ScalarAsync("SELECT expires_at FROM licenses"),
                await test.ScalarAsync("SELECT suspended_at FROM licenses"),
                await test.ScalarAsync("SELECT revoked_at FROM licenses"),
                await test.ScalarAsync("SELECT created_at FROM licenses"),
                await test.ScalarAsync("SELECT updated_at FROM licenses"),
                await test.ScalarAsync("SELECT deactivations FROM licenses"),
                await test.ScalarAsync("SELECT COUNT(*) FROM licenses WHERE length(key_hash) = 2")
            )
        );
        foreach (string id in new[] { "'0196f3a0-0000-7000-8000-000000000004'", "'0196f3a0-0000-7000-8000-000000000005'" })
        {
            await test.ExecuteAsync(
                "INSERT INTO licenses (id, product_id, policy_id, name, features, meta, created_at, updated_at) "
                    + $"VALUES ({id}, {Product}, {Policy}, 'trial', '[]', '{{}}', 1, 1)"
            );
        }

        Assert.Equal(3L, await test.ScalarAsync("SELECT COUNT(*) FROM licenses"));
    }

    /// <summary>
    /// A recorded migration whose file changed, or one this build does not have, stops the start; file names and numbering
    /// are checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database migrated with the embedded version 1.
    ///
    /// Steps:
    /// - Run with version 1's text changed by one space; insert a recorded version 9 and run the embedded migrations; parse
    ///   names 1_a.sql, 0001-a.sql, 0001_A.sql; order versions 1 and 3; 1 and 1.
    ///
    /// Expected:
    /// - ServiceStartException naming the changed file; one naming version 9; one naming each file name; one naming the file
    ///   out of place for each order.
    /// </remarks>
    [Fact]
    public async Task Run_ChangedOrUnknownVersion_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Migration original = Migrator.Embedded(test.Provider)[0];
        await Migrator.RunAsync(database, [original], test.Time, cancel);

        var changed = await Assert.ThrowsAsync<ServiceStartException>(() =>
            Migrator.RunAsync(database, [original with { Sql = original.Sql + " " }], test.Time, cancel)
        );
        Assert.Equal("The migration '0001_foundation.sql' changed after it was applied; restore the file as it was.", changed.Message);

        await test.ExecuteAsync("INSERT INTO schema_migrations (version, name, sha256, applied_at) VALUES (9, '0009_later.sql', x'00', 0)");
        var unknown = await Assert.ThrowsAsync<ServiceStartException>(() => Migrator.RunAsync(database, [original], test.Time, cancel));
        Assert.Equal("The database has schema version 9, which this elm-service does not know; it is older than the database.", unknown.Message);

        foreach (string name in new[] { "1_a.sql", "0001-a.sql", "0001_A.sql" })
        {
            Assert.Equal(
                $"The migration '{name}' is not named NNNN_name.sql.",
                Assert.Throws<ServiceStartException>(() => Migrator.Parse(name, string.Empty)).Message
            );
        }

        var gap = Assert.Throws<ServiceStartException>(() =>
            Migrator.Order([new Migration(1, "0001_a.sql", string.Empty), new Migration(3, "0003_c.sql", string.Empty)])
        );
        Assert.Equal("The migrations must be numbered from 1 without gaps; '0003_c.sql' is out of place.", gap.Message);
        var repeat = Assert.Throws<ServiceStartException>(() =>
            Migrator.Order([new Migration(1, "0001_a.sql", string.Empty), new Migration(1, "0001_b.sql", string.Empty)])
        );
        Assert.Equal("The migrations must be numbered from 1 without gaps; '0001_b.sql' is out of place.", repeat.Message);
    }
}
