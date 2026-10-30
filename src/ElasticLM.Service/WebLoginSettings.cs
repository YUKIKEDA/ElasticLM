namespace ElasticLM.Service;

/// <summary>
/// The admin UI's sign-in through the service (design §14.8, §15.3, decision 0.99, M5b): the confidential web client, the key
/// encryption key of the Data Protection keys, and the sessions' lifetimes.
/// </summary>
/// <param name="ClientId">The web client's ID (<c>oidc.webClientId</c>).</param>
/// <param name="ClientSecret">The web client's secret.</param>
/// <param name="Kek">The key encryption key: 32 bytes.</param>
/// <param name="Idle">How long a session lasts without a request (<c>session.idleMinutes</c>).</param>
/// <param name="Lifetime">The longest a session lasts (<c>session.maxHours</c>).</param>
internal sealed record WebLoginSettings(string ClientId, string ClientSecret, byte[] Kek, TimeSpan Idle, TimeSpan Lifetime)
{
    /// <summary>The variable that holds the web client's secret, instead of <c>oidc.webClientSecretFile</c>.</summary>
    public const string ClientSecretVariable = "ELM_OIDC_WEBCLIENTSECRET";

    /// <summary>The variable that holds the key encryption key in base64, instead of <c>dataProtection.kekFile</c>.</summary>
    public const string KekVariable = "ELM_DATAPROTECTION_KEK";

    /// <summary>The key encryption key's length in bytes (AES-256).</summary>
    public const int KekLength = 32;

    private const int MaxClientIdLength = 255;
    private const int MaxSecretLength = 1024;

    /// <summary>
    /// Reads the web sign-in's settings; <see langword="null"/> when none of them is set (design §15.3, the enabling
    /// conditions). Every refusal is a settings error (exit code 2).
    /// </summary>
    /// <param name="file">The settings file.</param>
    /// <param name="env">Reads a non-empty environment variable, or gives <see langword="null"/>.</param>
    /// <param name="readSecret">Reads a secret file: its text without a trailing line break, 16 KiB at most.</param>
    /// <param name="oidc">The OIDC settings, or <see langword="null"/>.</param>
    /// <param name="listen">Where the service listens.</param>
    /// <param name="publicUrls">The public URLs.</param>
    /// <exception cref="ServiceStartException">A value is missing, out of its range, or not valid.</exception>
    public static WebLoginSettings? Load(
        SettingsFile file,
        Func<string, string?> env,
        Func<string, string, string> readSecret,
        OidcSettings? oidc,
        Uri listen,
        IReadOnlyList<Uri> publicUrls
    )
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(readSecret);
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(publicUrls);
        string? clientId = env("ELM_OIDC_WEBCLIENTID") ?? file.Oidc?.WebClientId;
        string? secret =
            env(ClientSecretVariable)
            ?? (file.Oidc?.WebClientSecretFile is string secretFile ? readSecret(secretFile, "oidc.webClientSecretFile") : null);
        string? kekText = env(KekVariable) ?? (file.DataProtection?.KekFile is string kekFile ? readSecret(kekFile, "dataProtection.kekFile") : null);
        int? idleMinutes = env("ELM_SESSION_IDLEMINUTES") is string idleText ? Whole(idleText, "session.idleMinutes") : file.Session?.IdleMinutes;
        int? maxHours = env("ELM_SESSION_MAXHOURS") is string maxText ? Whole(maxText, "session.maxHours") : file.Session?.MaxHours;

        if (clientId is null && secret is null)
        {
            // design §15.3: without the web sign-in, a key encryption key or a session setting is a mistake, not a default.
            return kekText is null && idleMinutes is null && maxHours is null
                ? null
                : throw new ServiceStartException(
                    "dataProtection.kekFile and session.* are for the admin UI's sign-in; set oidc.webClientId and its secret, or leave them out."
                );
        }

        if (clientId is null || secret is null)
        {
            throw new ServiceStartException(
                $"The admin UI's sign-in needs both oidc.webClientId and its secret ({ClientSecretVariable} or oidc.webClientSecretFile)."
            );
        }

        if (oidc is null)
        {
            throw new ServiceStartException("oidc.webClientId needs oidc.issuer.");
        }

        if (clientId.Length is < 1 or > MaxClientIdLength)
        {
            throw new ServiceStartException("oidc.webClientId must be 1 to 255 characters.");
        }

        if (clientId == oidc.CliClientId || clientId == oidc.Audience)
        {
            // design §15.3, T13: one registration for both would let one kind of token pass as the other.
            throw new ServiceStartException("oidc.webClientId must differ from oidc.cliClientId and from oidc.audience.");
        }

        if (secret.Length is < 1 or > MaxSecretLength || !secret.All(c => c is > ' ' and <= '~'))
        {
            throw new ServiceStartException("The web client's secret must be 1 to 1024 printable ASCII characters without spaces.");
        }

        byte[] kek = ParseKek(kekText);
        int idle = idleMinutes ?? 30;
        if (idle is < 5 or > 120)
        {
            throw new ServiceStartException("session.idleMinutes must be between 5 and 120.");
        }

        int max = maxHours ?? 12;
        if (max is < 1 or > 24)
        {
            throw new ServiceStartException("session.maxHours must be between 1 and 24.");
        }

        // design §15.3: the UI is served at the first public URL only, whose path is "/" (__Host- cookies need Path=/), over
        // https; http only on a loopback listen without public URLs, for tests without a browser.
        Uri ui = publicUrls.Count > 0 ? publicUrls[0] : listen;
        bool secure = ui.Scheme == Uri.UriSchemeHttps || (ui.Scheme == Uri.UriSchemeHttp && (ui.IsLoopback || ui.Host == "localhost"));
        if (!secure || ui.AbsolutePath != "/")
        {
            throw new ServiceStartException($"The admin UI's sign-in needs the first public URL to be https with the path /: '{ui}'.");
        }

        return new WebLoginSettings(clientId, secret, kek, TimeSpan.FromMinutes(idle), TimeSpan.FromHours(max));
    }

    private static byte[] ParseKek(string? text)
    {
        if (text is null)
        {
            throw new ServiceStartException($"The admin UI's sign-in needs the key encryption key: set {KekVariable} or dataProtection.kekFile.");
        }

        try
        {
            byte[] kek = Convert.FromBase64String(text.Trim());
            return kek.Length == KekLength ? kek : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new ServiceStartException("The key encryption key must be 32 bytes in base64.");
        }
    }

    private static int Whole(string text, string key) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ServiceStartException($"{key} must be a whole number: '{text}'.");
}
