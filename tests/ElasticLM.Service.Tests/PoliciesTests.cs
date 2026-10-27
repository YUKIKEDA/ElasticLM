using System.Text;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The rules of a policy through the administration API (design §9.3, §14.5, decision 0.84): defaults, the range of each
/// member, and the constraints between members.
/// </summary>
public sealed class PoliciesTests
{
    private const long Day = 86_400;
    private const long HundredYears = 3_162_240_000;

    /// <summary>Gets each numeric member with its bounds and whether it takes null, from design §14.5's table.</summary>
    public static TheoryData<string, long, long, bool> Ranges =>
        new()
        {
            { "maxActivations", 1, 100_000, false },
            { "deactivationLimit", 0, 100_000, true },
            { "duration", Day, HundredYears, true },
            { "tokenLifetime", 3_600, HundredYears, false },
            { "refreshWindow", 0, HundredYears, false },
            { "gracePeriod", 0, HundredYears, false },
            { "leaseDuration", 120, Day, false },
            { "overage", 0, 1_000, true },
            { "borrowLimit", 1, HundredYears, true },
            { "borrowReturnLimit", 0, 1_000, false },
            { "trialDuration", Day, 365 * Day, false },
        };

    /// <summary>Gets each choice member with one allowed value and its full set.</summary>
    public static TheoryData<string, string> Choices =>
        new()
        {
            { "licenseKind", "standard" },
            { "binding", "nodeLocked" },
            { "expirationBasis", "fromIssue" },
            { "fingerprintMatching", "all" },
            { "cloneStrategy", "flag" },
            { "floating", "onPremises" },
            { "clientInfoStrategy", "hostAndUser" },
        };

    /// <summary>
    /// A policy with no member sent has the defaults of design §9.3.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty body.
    ///
    /// Steps:
    /// - Apply it to the defaults.
    ///
    /// Expected:
    /// - Each value as §9.3 writes it, worked out here in seconds: standard, nodeLocked, 1 activation, unlimited
    ///   deactivations, fromIssue, perpetual, 30 days, 20 days, 7 days, any, deny, none, 10 minutes, no overage, no borrowing,
    ///   2 returns, none, 14 days; no rule broken.
    /// </remarks>
    [Fact]
    public void Apply_NothingSent_GivesTheDesignDefaults()
    {
        PolicySettings settings = Apply("{}");

        Assert.Equal(
            new PolicySettings(
                "standard",
                "nodeLocked",
                1,
                null,
                "fromIssue",
                null,
                2_592_000,
                1_728_000,
                604_800,
                "any",
                "deny",
                "none",
                600,
                null,
                null,
                2,
                "none",
                1_209_600
            ),
            settings
        );
        Assert.Null(Policies.Problem(settings));
    }

    /// <summary>
    /// Each numeric member is accepted at both bounds and refused one past each, with its own message; null is taken only by
    /// the members that mean "none" with it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The member's bounds from design §14.5.
    ///
    /// Steps:
    /// - Apply the minimum, the maximum, one below, one above, and null.
    ///
    /// Expected:
    /// - The two bounds stored; ELM_INVALID_ARGUMENT naming the range for the two past them; null stored when nullable,
    ///   ELM_MALFORMED otherwise.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Ranges))]
    public void Apply_EachNumber_IsBoundedExactly(string name, long min, long max, bool nullable)
    {
        PolicyMember member = Policies.Members.Single(m => m.Name == name);
        Assert.Equal(min, member.Value(Apply($"{{\"{name}\":{min}}}")));
        Assert.Equal(max, member.Value(Apply($"{{\"{name}\":{max}}}")));
        string message = $"{name} must be between {min} and {max}{(nullable ? ", or null" : string.Empty)}.";
        Assert.Equal((ErrorCode.InvalidArgument, message), Refused($"{{\"{name}\":{min - 1}}}"));
        Assert.Equal((ErrorCode.InvalidArgument, message), Refused($"{{\"{name}\":{max + 1}}}"));
        if (nullable)
        {
            Assert.Null(member.Value(Apply($"{{\"{name}\":null}}")));
        }
        else
        {
            Assert.Equal((ErrorCode.Malformed, PolicyMember.Malformed), Refused($"{{\"{name}\":null}}"));
        }
    }

    /// <summary>
    /// A choice member takes its listed values only: an unknown value is ELM_INVALID_ARGUMENT (like an unknown permission in
    /// M4a1), a value of another type is ELM_MALFORMED.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The member and one of its values.
    ///
    /// Steps:
    /// - Apply the value; the value in upper case; a number; null.
    ///
    /// Expected:
    /// - Stored; ELM_INVALID_ARGUMENT naming the member; ELM_MALFORMED twice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Choices))]
    public void Apply_EachChoice_TakesItsValuesOnly(string name, string value)
    {
        PolicyMember member = Policies.Members.Single(m => m.Name == name);
        Assert.Equal(value, member.Value(Apply($"{{\"{name}\":\"{value}\"}}")));
        (ErrorCode code, string message) = Refused($"{{\"{name}\":\"{value.ToUpperInvariant()}\"}}");
        Assert.Equal(ErrorCode.InvalidArgument, code);
        Assert.StartsWith($"{name} must be one of ", message, StringComparison.Ordinal);
        Assert.Equal((ErrorCode.Malformed, PolicyMember.Malformed), Refused($"{{\"{name}\":1}}"));
        Assert.Equal((ErrorCode.Malformed, PolicyMember.Malformed), Refused($"{{\"{name}\":null}}"));
    }

    /// <summary>
    /// Each constraint of design §9.3 is broken alone by a policy that passes every other one, and the case one step inside it
    /// passes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Policies built from the defaults with one change.
    ///
    /// Steps:
    /// - refreshWindow equal to tokenLifetime, and one less; a trial with fingerprintMatching ignore; a trial with gracePeriod
    ///   1; a trial that is unbound; a server that is unbound; unbound with fromFirstActivation; a valid trial (any, grace 0,
    ///   nodeLocked); unbound with fromIssue.
    ///
    /// Expected:
    /// - Each broken case gives its own message; the passing cases give none.
    /// </remarks>
    [Fact]
    public void Problem_EachConstraintAlone_IsFound()
    {
        PolicySettings d = PolicySettings.Defaults;
        PolicySettings trial = d with { LicenseKind = "trial", GracePeriod = 0 };

        Assert.Equal("refreshWindow must be shorter than tokenLifetime.", Policies.Problem(d with { RefreshWindow = d.TokenLifetime }));
        Assert.Null(Policies.Problem(d with { RefreshWindow = d.TokenLifetime - 1 }));
        Assert.Equal("A trial policy needs fingerprintMatching any or all.", Policies.Problem(trial with { FingerprintMatching = "ignore" }));
        Assert.Equal("A trial policy has gracePeriod 0.", Policies.Problem(trial with { GracePeriod = 1 }));
        Assert.Equal("A trial policy is nodeLocked.", Policies.Problem(trial with { Binding = "unbound" }));
        Assert.Equal("A server policy is nodeLocked.", Policies.Problem(d with { LicenseKind = "server", Binding = "unbound" }));
        Assert.Equal(
            "An unbound policy uses expirationBasis fromIssue.",
            Policies.Problem(d with { Binding = "unbound", ExpirationBasis = "fromFirstActivation" })
        );
        Assert.Null(Policies.Problem(trial));
        Assert.Null(Policies.Problem(d with { Binding = "unbound" }));
        Assert.Null(Policies.Problem(d with { ExpirationBasis = "fromFirstActivation" }));
    }

    private static PolicySettings Apply(string json)
    {
        using RequestBody body = RequestBody.Parse(Encoding.UTF8.GetBytes(json), PolicyMember.Malformed);
        return Policies.Apply(body, PolicySettings.Defaults);
    }

    private static (ErrorCode Code, string Message) Refused(string json)
    {
        BodyException e = Assert.Throws<BodyException>(() => Apply(json));
        return (e.Code, e.Message);
    }
}
