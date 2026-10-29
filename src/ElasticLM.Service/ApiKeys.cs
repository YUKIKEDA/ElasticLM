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
/// A key to store: the key itself (only its hash and first 8 characters are kept), its name, permissions, and lifetime in days.
/// </summary>
internal sealed record NewApiKey(string Key, string Name, IEnumerable<string> Permissions, int Days);

/// <summary>
/// An authenticated caller of the administration API (design §14.6): an API key or an OIDC access token (M5a).
/// </summary>
/// <param name="AdminId">The administrator the caller acts for.</param>
/// <param name="Permissions">The permissions the caller holds now.</param>
/// <param name="ActorType">The audit log's actor type: <c>apikey</c> or <c>oidc</c> (design §18.2).</param>
/// <param name="ActorId">The audit log's actor: the API key's ID, or the token's <c>iss</c> and <c>sub</c> joined by a space.</param>
/// <param name="RateKey">What the per-caller rate limit counts by: the API key's ID or the administrator's ID (design §14.7).</param>
internal sealed record Caller(Guid AdminId, IReadOnlySet<string> Permissions, string ActorType, string ActorId, Guid RateKey);

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
    public const int MaxPermissions = 32;

    // "elm_" and 43 base64url characters for 32 bytes.
    private const int KeyLength = 47;

    /// <summary>Creates a new key.</summary>
    public static string NewKey() => Prefix + System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Returns the hash a key is stored and looked up by.</summary>
    public static byte[] HashOf(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>
    /// Builds the record of a new key.
    /// </summary>
    public static ApiKeyRecord NewRecord(Guid adminId, NewApiKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ApiKeyRecord(
            Guid.CreateVersion7(now),
            adminId,
            key.Name,
            key.Key[..8],
            Service.Permissions.Split(Service.Permissions.Join(key.Permissions)),
            now.ToUnixTimeSeconds(),
            now.AddDays(key.Days).ToUnixTimeSeconds(),
            null
        );
    }

    /// <summary>
    /// Finds the caller of a presented key. Unknown, stopped, and expired keys, keys of a disabled administrator, and keys
    /// the 90-day rule stops give <see langword="null"/>, one answer for all (design §14.5, §14.6).
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="presented">The presented key, or <see langword="null"/>.</param>
    /// <param name="now">The time.</param>
    /// <param name="inactiveAdminDays">The days without a login after which a key stops; 0 for no such rule (decision 0.66).</param>
    /// <param name="stopped">Told the key and the administrator when the 90-day rule stops a key, for the service log.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task<Caller?> AuthenticateAsync(
        Database database,
        string? presented,
        DateTimeOffset now,
        int inactiveAdminDays = 0,
        Action<Guid, Guid>? stopped = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        if (presented is not { Length: KeyLength } || !presented.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        KeyHolder? holder = await database.FindKeyAsync(HashOf(presented), cancellationToken).ConfigureAwait(false);
        if (holder is not { Enabled: true, Key.RevokedAt: null } || now.ToUnixTimeSeconds() >= holder.Key.ExpiresAt)
        {
            return null;
        }

        if (IsInactive(holder, now, inactiveAdminDays))
        {
            stopped?.Invoke(holder.Key.Id, holder.Key.AdminId);
            return null;
        }

        IReadOnlySet<string> allowed = Service.Permissions.OfRole(holder.Role);
        ApiKeyRecord key = holder.Key;
        return new Caller(
            key.AdminId,
            key.Permissions.Where(allowed.Contains).ToHashSet(StringComparer.Ordinal),
            AuditActors.ApiKey,
            key.Id.ToString("D"),
            key.Id
        );
    }

    /// <summary>
    /// Returns whether the 90-day rule stops a key (design §14.6): the latest of the administrator's last login, the
    /// administrator's creation, and the key's creation, plus the days, is not after now.
    /// </summary>
    public static bool IsInactive(KeyHolder holder, DateTimeOffset now, int inactiveAdminDays)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (inactiveAdminDays <= 0)
        {
            return false;
        }

        long since = Math.Max(Math.Max(holder.LastLoginAt ?? 0, holder.AdminCreatedAt), holder.Key.CreatedAt);
        return now.ToUnixTimeSeconds() >= since + (inactiveAdminDays * 86_400L);
    }

    /// <summary>Reads a stored key.</summary>
    public static ApiKeyRecord FromRow(ApiKeyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(
            row.Id,
            row.AdminId,
            row.Name,
            row.Prefix,
            Service.Permissions.Split(row.Permissions),
            row.CreatedAt,
            row.ExpiresAt,
            row.RevokedAt
        );
    }
}
