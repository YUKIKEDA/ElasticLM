namespace ElasticLM.Service;

/// <summary>
/// The identity provider whose access tokens the administration API accepts (design §14.6, §14.8, decision 0.98).
/// </summary>
/// <param name="Issuer">The issuer as written: an https URL, or http on a loopback host for local tests; compared with the tokens'
/// <c>iss</c> as text.</param>
/// <param name="Audience">The value the tokens' <c>aud</c> must hold.</param>
/// <param name="CliClientId">The public client ID <c>elm admin login</c> uses.</param>
/// <param name="CliScopes">The scopes <c>elm admin login</c> asks for.</param>
/// <param name="RoleClaim">The claim whose values name roles.</param>
/// <param name="RoleMap">The claim values and the roles they give.</param>
internal sealed record OidcSettings(
    string Issuer,
    string Audience,
    string CliClientId,
    IReadOnlyList<string> CliScopes,
    string RoleClaim,
    IReadOnlyDictionary<string, string> RoleMap
)
{
    private const int MaxTextLength = 255;
    private const int MaxIssuerLength = 2048;
    private const int MaxScopes = 20;
    private const int MaxScopeLength = 100;
    private const int MaxRoleMapEntries = 100;

    /// <summary>Gets the scopes asked for when <c>oidc.cliScopes</c> is left out.</summary>
    public static IReadOnlyList<string> DefaultScopes { get; } = ["openid", "offline_access"];

    /// <summary>
    /// Reads the <c>oidc</c> settings from the file and the environment; <see langword="null"/> when no issuer is set.
    /// </summary>
    /// <param name="section">The <c>oidc</c> section of the settings file, or <see langword="null"/>.</param>
    /// <param name="environment">Reads a non-empty environment variable, or gives <see langword="null"/>.</param>
    /// <exception cref="ServiceStartException">A value is missing, out of its range, or not valid.</exception>
    public static OidcSettings? Load(OidcSection? section, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? issuerText = environment("ELM_OIDC_ISSUER") ?? section?.Issuer;
        string? audience = environment("ELM_OIDC_AUDIENCE") ?? section?.Audience;
        string? clientId = environment("ELM_OIDC_CLICLIENTID") ?? section?.CliClientId;
        IReadOnlyList<string>? scopes = environment("ELM_OIDC_CLISCOPES") is string scopeText
            ? scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : section?.CliScopes;
        string? roleClaim = environment("ELM_OIDC_ROLECLAIM") ?? section?.RoleClaim;
        IReadOnlyDictionary<string, string>? roleMap = environment("ELM_OIDC_ROLEMAP") is string mapText ? ParseRoleMap(mapText) : section?.RoleMap;

        if (issuerText is null)
        {
            return audience is null && clientId is null && scopes is null && roleClaim is null && roleMap is null
                ? null
                : throw new ServiceStartException("The oidc settings need oidc.issuer.");
        }

        string issuer = ParseIssuer(issuerText);
        Text(audience, "oidc.audience");
        Text(clientId, "oidc.cliClientId");
        Text(roleClaim ?? "groups", "oidc.roleClaim");
        scopes ??= DefaultScopes;
        if (scopes.Count is < 1 or > MaxScopes || scopes.Any(s => !IsScope(s)))
        {
            throw new ServiceStartException("oidc.cliScopes must hold 1 to 20 scopes, each 1 to 100 printable ASCII characters without spaces.");
        }

        if (roleMap is not { Count: >= 1 and <= MaxRoleMapEntries })
        {
            throw new ServiceStartException("oidc.roleMap must map 1 to 100 claim values to roles.");
        }

        foreach ((string value, string role) in roleMap)
        {
            if (value is not { Length: >= 1 and <= MaxTextLength } || Roles.Rank(role) < 0)
            {
                throw new ServiceStartException(
                    $"oidc.roleMap must map values of 1 to 255 characters to owner, admin, support, or viewer: '{value}' to '{role}'."
                );
            }
        }

        // design §14.6 (T13): with one value for both, the CLI client's ID tokens would pass as access tokens.
        if (audience == clientId)
        {
            throw new ServiceStartException(
                "oidc.audience must differ from oidc.cliClientId, or the client's ID tokens would pass as access tokens."
            );
        }

        return new OidcSettings(issuer, audience!, clientId!, [.. scopes], roleClaim ?? "groups", roleMap);
    }

    /// <summary>
    /// Returns whether an issuer URL is acceptable: https, or http on a loopback host; no query, fragment, or user info.
    /// </summary>
    public static bool IsIssuerUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0;

    private static string ParseIssuer(string text) =>
        text.Length <= MaxIssuerLength && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && IsIssuerUrl(uri)
            ? text
            : throw new ServiceStartException(
                $"oidc.issuer must be an https URL (http only on a loopback host) of up to 2048 characters without query or fragment: '{text}'."
            );

    private static void Text(string? value, string key)
    {
        if (value is not { Length: >= 1 and <= MaxTextLength })
        {
            throw new ServiceStartException($"{key} must be 1 to 255 characters.");
        }
    }

    private static bool IsScope(string scope) =>
        scope is { Length: >= 1 and <= MaxScopeLength } && scope.All(c => c is > ' ' and <= '~' and not '"' and not '\\');

    // ELM_OIDC_ROLEMAP: value=role pairs separated by ';'.
    private static Dictionary<string, string> ParseRoleMap(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = pair.LastIndexOf('=');
            if (equals <= 0 || !map.TryAdd(pair[..equals], pair[(equals + 1)..]))
            {
                throw new ServiceStartException($"ELM_OIDC_ROLEMAP must hold value=role pairs separated by ';', each value once: '{pair}'.");
            }
        }

        return map;
    }
}

/// <summary>
/// The administrators' roles in order of strength (design §14.6).
/// </summary>
internal static class Roles
{
    /// <summary>Returns a role's strength: <c>owner</c> 3, <c>admin</c> 2, <c>support</c> 1, <c>viewer</c> 0; another name -1.</summary>
    public static int Rank(string role) =>
        role switch
        {
            Permissions.RoleOwner => 3,
            Permissions.RoleAdmin => 2,
            Permissions.RoleSupport => 1,
            Permissions.RoleViewer => 0,
            _ => -1,
        };
}
