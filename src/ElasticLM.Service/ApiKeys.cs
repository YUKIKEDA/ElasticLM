using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Service;

/// <summary>
/// A stored API key (design §14.6). The key itself is never stored: only its SHA-256 and its first 8 characters.
/// </summary>
internal sealed record ApiKeyRecord(
    Guid Id,
    Guid AdminId,
    string Name,
    string Prefix,
    string[] Permissions,
    long CreatedAt,
    long ExpiresAt,
    long? RevokedAt
);

/// <summary>
/// An authenticated caller: the API key, its administrator, and the permissions both allow (design §14.6).
/// </summary>
internal sealed record Caller(ApiKeyRecord Key, Guid AdminId, IReadOnlySet<string> Permissions);

/// <summary>
/// API keys (design §14.6): <c>elm_</c> and a 256-bit random value in base64url, kept as a SHA-256 hash, with permissions and
/// an expiry. A key's permissions are always cut to its administrator's current role, and a disabled administrator's keys
/// stop working.
/// </summary>
internal static class ApiKeys
{
    public const string Prefix = "elm_";
    public const int DefaultDays = 90;
    public const int MaxDays = 365;
    public const int MaxNameLength = 64;
    public const int MaxNameBytes = 256;
    public const int MaxPermissions = 32;

    // "elm_" and 43 base64url characters for 32 bytes.
    private const int KeyLength = 47;

    private const string InsertSql =
        "INSERT INTO api_keys (id, admin_id, name, prefix, hash, permissions, created_at, expires_at) "
        + "VALUES (@id, @admin, @name, @prefix, @hash, @permissions, @created, @expires)";

    private const string AuthenticateSql =
        "SELECT k.id, k.admin_id, k.name, k.prefix, k.permissions, k.created_at, k.expires_at, k.revoked_at, a.role, a.enabled "
        + "FROM api_keys k JOIN admins a ON a.id = k.admin_id WHERE k.hash = @hash";

    /// <summary>Creates a new key.</summary>
    public static string NewKey() => Prefix + System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Returns the hash a key is stored and looked up by.</summary>
    public static byte[] HashOf(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>
    /// Stores a new key for an administrator inside the caller's transaction.
    /// </summary>
    public static async Task<ApiKeyRecord> InsertAsync(
        Database database,
        DbConnection connection,
        DbTransaction transaction,
        Guid adminId,
        string name,
        IEnumerable<string> permissions,
        string key,
        DateTimeOffset now,
        int days,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(key);
        var record = new ApiKeyRecord(
            Guid.CreateVersion7(now),
            adminId,
            name,
            key[..8],
            Service.Permissions.Split(Service.Permissions.Join(permissions)),
            now.ToUnixTimeSeconds(),
            now.AddDays(days).ToUnixTimeSeconds(),
            null
        );
        await using DbCommand insert = Database.Command(
            connection,
            transaction,
            InsertSql,
            ("id", database.Id(record.Id)),
            ("admin", database.Id(adminId)),
            ("name", name),
            ("prefix", record.Prefix),
            ("hash", HashOf(key)),
            ("permissions", string.Join(' ', record.Permissions)),
            ("created", record.CreatedAt),
            ("expires", record.ExpiresAt)
        );
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Finds the caller of a presented key. Unknown, stopped, and expired keys, and keys of a disabled administrator, give
    /// <see langword="null"/>, one answer for all (design §14.5).
    /// </summary>
    public static async Task<Caller?> AuthenticateAsync(
        Database database,
        string? presented,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        if (presented is not { Length: KeyLength } || !presented.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand select = Database.Command(connection, null, AuthenticateSql, ("hash", HashOf(presented)));
        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        ApiKeyRecord key = ReadKey(database, reader);
        string role = reader.GetString(8);
        bool enabled = reader.GetBoolean(9);
        if (!enabled || key.RevokedAt is not null || now.ToUnixTimeSeconds() >= key.ExpiresAt)
        {
            return null;
        }

        IReadOnlySet<string> allowed = Service.Permissions.OfRole(role);
        return new Caller(key, key.AdminId, key.Permissions.Where(allowed.Contains).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns keys after an ID, oldest first (IDs are UUID version 7, so their order is the order of creation).
    /// </summary>
    public static async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(
        Database database,
        Guid? after,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        string where = after is null ? string.Empty : "WHERE id > @after ";
        await using DbCommand select = Database.Command(
            connection,
            null,
            "SELECT id, admin_id, name, prefix, permissions, created_at, expires_at, revoked_at FROM api_keys " + where + "ORDER BY id LIMIT @limit",
            ("after", after is Guid id ? database.Id(id) : null),
            ("limit", limit)
        );
        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<ApiKeyRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(ReadKey(database, reader));
        }

        return keys;
    }

    /// <summary>
    /// Stops a key inside the caller's transaction. A stopped key stays stopped at its first time.
    /// </summary>
    /// <returns><see langword="false"/> when no key has the ID.</returns>
    public static async Task<bool> RevokeAsync(
        Database database,
        DbConnection connection,
        DbTransaction transaction,
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbCommand exists = Database.Command(connection, transaction, "SELECT 1 FROM api_keys WHERE id = @id", ("id", database.Id(id)));
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await using DbCommand update = Database.Command(
            connection,
            transaction,
            "UPDATE api_keys SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL",
            ("at", now.ToUnixTimeSeconds()),
            ("id", database.Id(id))
        );
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ApiKeyRecord ReadKey(Database database, DbDataReader reader) =>
        new(
            database.ReadId(reader, 0),
            database.ReadId(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            Service.Permissions.Split(reader.GetString(4)),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7)
        );
}
