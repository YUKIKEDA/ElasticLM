using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace ElasticLM.Service;

/// <summary>
/// <c>elm-service setup</c> (design §14.6, decisions 0.63 and 0.80): before OIDC (M5), the first API key is made on the
/// service's host. The key belongs to the configured <c>bootstrapOwner</c>, holds the <c>owner</c> permissions, and expires
/// in 90 days; it is written to standard output alone, so <c>elm-service setup &gt; key.txt</c> hands it on. The key ID,
/// expiry, and owner go to standard error. Each run makes a new key and records it in the audit log.
/// </summary>
internal static class Setup
{
    public const int ExitOk = 0;
    public const int ExitDatabase = 1;
    public const int ExitSettings = 2;

    /// <summary>
    /// Runs setup.
    /// </summary>
    /// <returns>0 on success, 1 when the database fails, 2 when the settings are not valid.</returns>
    public static async Task<int> RunAsync(
        ServiceSettings settings,
        TimeProvider time,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (settings.BootstrapOwner is not AdminIdentity owner)
        {
            await error.WriteLineAsync("Set bootstrapOwner (iss and sub) to the first owner before running setup.").ConfigureAwait(false);
            return ExitSettings;
        }

        Database database;
        try
        {
            database = Database.Create(settings.Database, settings.ConnectionString);
        }
        catch (ServiceStartException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return ExitSettings;
        }

        await using (database)
        {
            return await RunCoreAsync(database, settings.Database, owner, time, output, error, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunCoreAsync(
        Database database,
        DatabaseProvider provider,
        AdminIdentity owner,
        TimeProvider time,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Migrator.RunAsync(database, Migrator.Embedded(provider), time, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = time.GetUtcNow();
            string key = ApiKeys.NewKey();
            ApiKeyRecord record = await database
                .SetupKeyAsync(
                    owner,
                    new NewApiKey(key, "setup", Permissions.All, ApiKeys.DefaultDays),
                    created => new AuditEntry(
                        "system",
                        "setup",
                        AuditActions.ApiKeyCreate,
                        created.Id.ToString("D"),
                        "ELM_OK",
                        null,
                        AuditLog.KeyCreatedDetail(created, owner)
                    ),
                    now,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await output.WriteLineAsync(key).ConfigureAwait(false);
            await error
                .WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"API key {record.Id:D} ({record.Prefix}…) for {owner.Issuer} {owner.Subject}, owner permissions, expires {AdminApi.Time(record.ExpiresAt)}."
                    )
                )
                .ConfigureAwait(false);
            return ExitOk;
        }
        catch (ServiceStartException e)
        {
            // A migration that changed or is unknown, or a disabled owner: the database is not in a state setup can use.
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return ExitDatabase;
        }
        catch (DbException e)
        {
            await error.WriteLineAsync("The database cannot be used: " + e.Message).ConfigureAwait(false);
            return ExitDatabase;
        }
        catch (JsonException e)
        {
            await error.WriteLineAsync("The setup record cannot be written: " + e.Message).ConfigureAwait(false);
            return ExitDatabase;
        }
    }
}
