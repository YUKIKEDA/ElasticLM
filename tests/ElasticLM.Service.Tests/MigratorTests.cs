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
    /// - One run applied version 1 and the other nothing; the third applied nothing; one row; wal.
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
        Assert.Equal([1], runs.SelectMany(r => r).Order());
        Assert.Empty(await Migrator.RunAsync(database, migrations, test.Time, cancel));
        await using DbConnection connection = await database.OpenAsync(cancel);
        await using DbCommand count = Database.Command(connection, null, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(cancel), CultureInfo.InvariantCulture));
        if (test.Provider == DatabaseProvider.Sqlite)
        {
            await using DbCommand mode = Database.Command(connection, null, "PRAGMA journal_mode");
            Assert.Equal("wal", await mode.ExecuteScalarAsync(cancel));
        }
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
