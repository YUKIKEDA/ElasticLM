using System.Globalization;
using System.Text;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>A feature a license grants (design §14.5): its code, version range, seats, and own expiry (UNIX seconds).</summary>
internal sealed record LicenseFeature(string Code, string? Ver, long? Seats, long? ExpiresAt);

/// <summary>
/// The rules of licenses through the administration API (design §9.3 to §9.5, §14.5, decisions 0.85 and 0.86): the state
/// computed from the stored marks and the clock, and the reading and checking of a license body.
/// </summary>
internal static class Licenses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Expired = "expired";
    public const string Revoked = "revoked";

    /// <summary>The detail of every <c>ELM_MALFORMED</c> answer to a license body.</summary>
    public const string Malformed = "The request body is not a valid license.";

    public const int MaxNameLength = 256;
    public const long MaxSeats = 1_000_000;
    public const long MaxActivations = 100_000;
    public const int MaxVersionLength = 64;

    /// <summary>Gets the states a list can be filtered by.</summary>
    public static IReadOnlyList<string> States { get; } = [Active, Suspended, Expired, Revoked];

    /// <summary>
    /// Returns a license's state: overlapping marks resolve as revoked, then suspended, then expired, then active (design
    /// §9.4, §14.5). Expired is not stored: the expiry has passed when it is at or before now.
    /// </summary>
    public static string State(LicenseRow row, long now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.RevokedAt is not null ? Revoked
            : row.SuspendedAt is not null ? Suspended
            : row.ExpiresAt is long expires && expires <= now ? Expired
            : Active;
    }

    /// <summary>Reads a time in the form the API writes (<c>2026-10-27T00:00:00Z</c>) as UNIX seconds.</summary>
    /// <exception cref="BodyException">Not a string in that form (<c>ELM_MALFORMED</c>).</exception>
    public static long Time(RequestBody body, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(body);
        string text = body.Text(value) ?? throw new BodyException(ErrorCode.Malformed, Malformed);
        return DateTimeOffset.TryParseExact(
            text,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset time
        )
            ? time.ToUnixTimeSeconds()
            : throw new BodyException(ErrorCode.Malformed, Malformed);
    }

    /// <summary>
    /// Reads and checks the features of a license (design §14.5): codes of the product's features, no code twice, a version
    /// range, seats for a server license or a cloud-floating one only (design §12.7.6), and an expiry after now and not after
    /// the license's.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="value">The <c>features</c> member.</param>
    /// <param name="policy">The license's policy.</param>
    /// <param name="productCodes">The codes of the product's features.</param>
    /// <param name="licenseExpiry">The license's expiry, or <see langword="null"/>.</param>
    /// <param name="now">The time now, in UNIX seconds.</param>
    /// <exception cref="BodyException">A value has the wrong type or breaks a rule.</exception>
    public static IReadOnlyList<LicenseFeature> Features(
        RequestBody body,
        JsonElement value,
        PolicyRow policy,
        IReadOnlySet<string> productCodes,
        long? licenseExpiry,
        long now
    )
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(productCodes);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        int count = value.GetArrayLength();
        if (count is < 1 or > LicenseRules.MaxFeatures)
        {
            throw new BodyException(ErrorCode.InvalidArgument, "features must hold 1 to 256 features.");
        }

        // design §12.7.6: cloud leases count the seats of the license's features, as a server license does.
        bool server = policy.LicenseKind == Policies.Server || policy.Floating == Policies.Cloud;
        var features = new List<LicenseFeature>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new BodyException(ErrorCode.Malformed, Malformed);
            }

            string code =
                item.TryGetProperty("code", out JsonElement codeValue) && body.Text(codeValue) is string c
                    ? c
                    : throw new BodyException(ErrorCode.Malformed, Malformed);
            if (!productCodes.Contains(code))
            {
                throw new BodyException(ErrorCode.InvalidArgument, $"features[].code must name a feature of the license's product: '{code}'.");
            }

            if (!seen.Add(code))
            {
                throw new BodyException(ErrorCode.InvalidArgument, $"features[].code '{code}' is given twice.");
            }

            string? ver = item.TryGetProperty("ver", out JsonElement verValue) ? body.Text(verValue) : null;
            if (ver is not null && (ver.Length > MaxVersionLength || !LicenseRules.IsVersionRange(ver)))
            {
                throw new BodyException(
                    ErrorCode.InvalidArgument,
                    "features[].ver must be a version range such as '>=2.0 <3.0', at most 64 characters."
                );
            }

            long? seats = item.TryGetProperty("seats", out JsonElement seatsValue) ? body.Integer(seatsValue) : null;
            if (server && seats is not (>= 1 and <= MaxSeats))
            {
                throw new BodyException(
                    ErrorCode.InvalidArgument,
                    "features[].seats must be between 1 and 1000000 for a server license or a cloud-floating one."
                );
            }

            if (!server && seats is not null)
            {
                throw new BodyException(ErrorCode.InvalidArgument, "features[].seats is only for a server license or a cloud-floating one.");
            }

            long? expires =
                item.TryGetProperty("expiresAt", out JsonElement expiresValue) && expiresValue.ValueKind != JsonValueKind.Null
                    ? Time(body, expiresValue)
                    : null;
            if (expires is long at)
            {
                if (policy.ExpirationBasis == Policies.FromFirstActivation)
                {
                    throw new BodyException(ErrorCode.InvalidArgument, "features[].expiresAt cannot be set before the first activation.");
                }

                if (at <= now || at > (licenseExpiry ?? LicenseRules.MaxUnixSeconds))
                {
                    throw new BodyException(
                        ErrorCode.InvalidArgument,
                        "features[].expiresAt must be later than now and no later than the license's expiry."
                    );
                }
            }

            features.Add(new LicenseFeature(code, ver, seats, expires));
        }

        return features;
    }

    /// <summary>Reads and checks <c>meta</c>: at most 32 string pairs, keys 1 to 64 bytes, values at most 1 KiB (design §8.6).</summary>
    /// <exception cref="BodyException">A value has the wrong type or breaks a limit.</exception>
    public static Dictionary<string, string> Meta(RequestBody body, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in value.EnumerateObject())
        {
            string text = body.Text(entry.Value) ?? throw new BodyException(ErrorCode.Malformed, Malformed);
            meta[entry.Name] = text;
        }

        bool fits =
            meta.Count <= LicenseRules.MaxMetaEntries
            && meta.All(e =>
                Encoding.UTF8.GetByteCount(e.Key) is >= 1 and <= LicenseRules.MaxMetaKeyBytes
                && Encoding.UTF8.GetByteCount(e.Value) <= LicenseRules.MaxMetaValueBytes
            );
        return fits
            ? meta
            : throw new BodyException(
                ErrorCode.InvalidArgument,
                "meta must hold at most 32 entries, each key 1 to 64 bytes and each value at most 1024 bytes in UTF-8."
            );
    }

    /// <summary>Reads <c>maxActivations</c>: 1 to 100,000, or <see langword="null"/> for the policy's value.</summary>
    /// <exception cref="BodyException">A value has the wrong type or is out of range.</exception>
    public static long? MaxActivationsOf(RequestBody body, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(body);
        long? number = body.Integer(value);
        return number is null or (>= 1 and <= MaxActivations)
            ? number
            : throw new BodyException(ErrorCode.InvalidArgument, "maxActivations must be between 1 and 100000, or null.");
    }

    /// <summary>Writes features as the JSON stored in the <c>features</c> column.</summary>
    public static string Store(IReadOnlyList<LicenseFeature> features) =>
        JsonSerializer.Serialize(
            [.. features.Select(f => new StoredLicenseFeature(f.Code, f.Ver, f.Seats, f.ExpiresAt))],
            ServiceJsonContext.Default.StoredLicenseFeatureArray
        );

    /// <summary>Reads the stored features.</summary>
    public static IReadOnlyList<LicenseFeature> Load(string stored) =>
        [
            .. (JsonSerializer.Deserialize(stored, ServiceJsonContext.Default.StoredLicenseFeatureArray) ?? []).Select(f => new LicenseFeature(
                f.Code,
                f.Ver,
                f.Seats,
                f.ExpiresAt
            )),
        ];

    /// <summary>Writes meta as the JSON stored in the <c>meta</c> column.</summary>
    public static string StoreMeta(IReadOnlyDictionary<string, string> meta) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(meta, StringComparer.Ordinal), ServiceJsonContext.Default.DictionaryStringString);

    /// <summary>Reads the stored meta.</summary>
    public static Dictionary<string, string> LoadMeta(string stored) =>
        JsonSerializer.Deserialize(stored, ServiceJsonContext.Default.DictionaryStringString) ?? [];
}
