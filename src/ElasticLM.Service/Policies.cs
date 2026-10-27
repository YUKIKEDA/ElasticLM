using System.Globalization;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>
/// The rules of a policy (design §9.3, decision 0.84): the choices as their API names (camelCase), durations in seconds, and
/// <see langword="null"/> for the design's "none" (<c>Perpetual</c>, <c>Unlimited</c>, <c>Disabled</c>, <c>Overage: None</c>).
/// </summary>
internal sealed record PolicySettings(
    string LicenseKind,
    string Binding,
    long MaxActivations,
    long? DeactivationLimit,
    string ExpirationBasis,
    long? Duration,
    long TokenLifetime,
    long RefreshWindow,
    long GracePeriod,
    string FingerprintMatching,
    string CloneStrategy,
    string Floating,
    long LeaseDuration,
    long? Overage,
    long? BorrowLimit,
    long BorrowReturnLimit,
    string ClientInfoStrategy,
    long TrialDuration
)
{
    /// <summary>Gets the defaults of design §9.3.</summary>
    public static PolicySettings Defaults { get; } =
        new(
            Policies.Standard,
            Policies.NodeLocked,
            1,
            null,
            Policies.FromIssue,
            null,
            30 * Policies.Day,
            20 * Policies.Day,
            7 * Policies.Day,
            "any",
            "deny",
            "none",
            10 * 60,
            null,
            null,
            2,
            "none",
            14 * Policies.Day
        );
}

/// <summary>
/// Reading, checking, and writing policies through the administration API (design §14.5, decisions 0.84 and 0.86).
/// </summary>
internal static class Policies
{
    public const long Day = 86_400;

    /// <summary>100 years of 366 days: the bound of every duration claim (design §8.6).</summary>
    public const long HundredYears = 3_162_240_000;

    public const string Standard = "standard";
    public const string Trial = "trial";
    public const string Server = "server";
    public const string Unbound = "unbound";
    public const string NodeLocked = "nodeLocked";
    public const string FromIssue = "fromIssue";
    public const string FromFirstActivation = "fromFirstActivation";

    /// <summary>The members a <c>PATCH</c> cannot change (decision 0.68).</summary>
    public static readonly string[] Fixed = ["productId", "licenseKind", "binding"];

    /// <summary>Gets the members of design §14.5's policy table after <c>productId</c> and <c>name</c>, in its order.</summary>
    public static IReadOnlyList<PolicyMember> Members { get; } =
    [
        Choice("licenseKind", [Standard, Trial, Server], p => p.LicenseKind, (p, v) => p with { LicenseKind = v }),
        Choice("binding", [Unbound, NodeLocked], p => p.Binding, (p, v) => p with { Binding = v }),
        Number("maxActivations", 1, 100_000, false, p => p.MaxActivations, (p, v) => p with { MaxActivations = v!.Value }),
        Number("deactivationLimit", 0, 100_000, true, p => p.DeactivationLimit, (p, v) => p with { DeactivationLimit = v }),
        Choice("expirationBasis", [FromIssue, FromFirstActivation], p => p.ExpirationBasis, (p, v) => p with { ExpirationBasis = v }),
        Number("duration", Day, HundredYears, true, p => p.Duration, (p, v) => p with { Duration = v }),
        Number("tokenLifetime", 3_600, HundredYears, false, p => p.TokenLifetime, (p, v) => p with { TokenLifetime = v!.Value }),
        Number("refreshWindow", 0, HundredYears, false, p => p.RefreshWindow, (p, v) => p with { RefreshWindow = v!.Value }),
        Number("gracePeriod", 0, HundredYears, false, p => p.GracePeriod, (p, v) => p with { GracePeriod = v!.Value }),
        Choice("fingerprintMatching", ["ignore", "any", "all"], p => p.FingerprintMatching, (p, v) => p with { FingerprintMatching = v }),
        Choice("cloneStrategy", ["deny", "flag"], p => p.CloneStrategy, (p, v) => p with { CloneStrategy = v }),
        Choice("floating", ["none", "onPremises", "cloud"], p => p.Floating, (p, v) => p with { Floating = v }),
        Number("leaseDuration", 120, Day, false, p => p.LeaseDuration, (p, v) => p with { LeaseDuration = v!.Value }),
        Number("overage", 0, 1_000, true, p => p.Overage, (p, v) => p with { Overage = v }),
        Number("borrowLimit", 1, HundredYears, true, p => p.BorrowLimit, (p, v) => p with { BorrowLimit = v }),
        Number("borrowReturnLimit", 0, 1_000, false, p => p.BorrowReturnLimit, (p, v) => p with { BorrowReturnLimit = v!.Value }),
        Choice("clientInfoStrategy", ["none", "hostAndUser"], p => p.ClientInfoStrategy, (p, v) => p with { ClientInfoStrategy = v }),
        Number("trialDuration", Day, 365 * Day, false, p => p.TrialDuration, (p, v) => p with { TrialDuration = v!.Value }),
    ];

    /// <summary>
    /// Applies the members of a body to a policy: each one sent replaces the value, each one left out keeps it.
    /// </summary>
    /// <exception cref="BodyException">A value has the wrong type (<c>ELM_MALFORMED</c>) or is outside its range or set
    /// (<c>ELM_INVALID_ARGUMENT</c>).</exception>
    public static PolicySettings Apply(RequestBody body, PolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (PolicyMember member in Members)
        {
            if (body.TryGet(member.Name, out JsonElement value))
            {
                settings = member.Apply(body, value, settings);
            }
        }

        return settings;
    }

    /// <summary>
    /// Returns the first rule of design §9.3 that the policy breaks, or <see langword="null"/>.
    /// </summary>
    public static string? Problem(PolicySettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.RefreshWindow >= p.TokenLifetime)
        {
            return "refreshWindow must be shorter than tokenLifetime.";
        }

        if (p.LicenseKind == Trial && p.FingerprintMatching == "ignore")
        {
            return "A trial policy needs fingerprintMatching any or all.";
        }

        if (p.LicenseKind == Trial && p.GracePeriod != 0)
        {
            return "A trial policy has gracePeriod 0.";
        }

        if (p.LicenseKind is Trial or Server && p.Binding != NodeLocked)
        {
            return $"A {p.LicenseKind} policy is nodeLocked.";
        }

        if (p.Binding == Unbound && p.ExpirationBasis == FromFirstActivation)
        {
            return "An unbound policy uses expirationBasis fromIssue.";
        }

        return null;
    }

    /// <summary>Returns the rules of a stored policy.</summary>
    public static PolicySettings Of(PolicyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(
            row.LicenseKind,
            row.Binding,
            row.MaxActivations,
            row.DeactivationLimit,
            row.ExpirationBasis,
            row.Duration,
            row.TokenLifetime,
            row.RefreshWindow,
            row.GracePeriod,
            row.FingerprintMatching,
            row.CloneStrategy,
            row.Floating,
            row.LeaseDuration,
            row.Overage,
            row.BorrowLimit,
            row.BorrowReturnLimit,
            row.ClientInfoStrategy,
            row.TrialDuration
        );
    }

    /// <summary>Returns the row of a policy.</summary>
    public static PolicyRow ToRow(Guid id, Guid productId, string name, PolicySettings p, long createdAt, long updatedAt)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(
            id,
            productId,
            name,
            p.LicenseKind,
            p.Binding,
            p.MaxActivations,
            p.DeactivationLimit,
            p.ExpirationBasis,
            p.Duration,
            p.TokenLifetime,
            p.RefreshWindow,
            p.GracePeriod,
            p.FingerprintMatching,
            p.CloneStrategy,
            p.Floating,
            p.LeaseDuration,
            p.Overage,
            p.BorrowLimit,
            p.BorrowReturnLimit,
            p.ClientInfoStrategy,
            p.TrialDuration,
            createdAt,
            updatedAt
        );
    }

    /// <summary>Writes the named members of a policy as JSON properties, for an audit row.</summary>
    public static void Write(Utf8JsonWriter writer, PolicySettings settings, Func<PolicyMember, bool> include)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(include);
        foreach (PolicyMember member in Members.Where(include))
        {
            member.Write(writer, settings);
        }
    }

    private static PolicyMember Choice(
        string name,
        string[] allowed,
        Func<PolicySettings, string> get,
        Func<PolicySettings, string, PolicySettings> set
    )
    {
        string message = $"{name} must be one of {string.Join(", ", allowed)}.";
        return new PolicyMember(
            name,
            (body, value, p) =>
            {
                string text = body.Text(value) ?? throw new BodyException(ErrorCode.Malformed, PolicyMember.Malformed);
                return allowed.Contains(text, StringComparer.Ordinal) ? set(p, text) : throw new BodyException(ErrorCode.InvalidArgument, message);
            },
            (writer, p) => writer.WriteString(name, get(p)),
            p => get(p)
        );
    }

    private static PolicyMember Number(
        string name,
        long min,
        long max,
        bool nullable,
        Func<PolicySettings, long?> get,
        Func<PolicySettings, long?, PolicySettings> set
    )
    {
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"{name} must be between {min} and {max}{(nullable ? ", or null" : string.Empty)}."
        );
        return new PolicyMember(
            name,
            (body, value, p) =>
            {
                long? number = body.Integer(value);
                if (number is null)
                {
                    return nullable ? set(p, null) : throw new BodyException(ErrorCode.Malformed, PolicyMember.Malformed);
                }

                return number >= min && number <= max ? set(p, number) : throw new BodyException(ErrorCode.InvalidArgument, message);
            },
            (writer, p) =>
            {
                if (get(p) is long number)
                {
                    writer.WriteNumber(name, number);
                }
                else
                {
                    writer.WriteNull(name);
                }
            },
            p => get(p)
        );
    }
}

/// <summary>
/// One member of a policy in the administration API: how it is read, checked, and written.
/// </summary>
/// <param name="Name">The JSON name.</param>
/// <param name="Apply">Reads and checks the member and returns the policy with it.</param>
/// <param name="Write">Writes the member.</param>
/// <param name="Value">Returns the member's value, to compare two policies.</param>
internal sealed record PolicyMember(
    string Name,
    Func<RequestBody, JsonElement, PolicySettings, PolicySettings> Apply,
    Action<Utf8JsonWriter, PolicySettings> Write,
    Func<PolicySettings, object?> Value
)
{
    /// <summary>The detail of every <c>ELM_MALFORMED</c> answer to a policy body.</summary>
    public const string Malformed = "The request body is not a valid policy.";
}
