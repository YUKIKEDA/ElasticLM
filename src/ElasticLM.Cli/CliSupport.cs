using System.Globalization;
using ElasticLM.Issuing;

namespace ElasticLM.Cli;

/// <summary>
/// The exit codes of design §16.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int VerificationFailed = 1;
    public const int InvalidInput = 2;
    public const int NetworkError = 3;
}

/// <summary>
/// A failure the command reports to the person in one sentence, with its exit code.
/// </summary>
internal sealed class CliException(string message, int exitCode = ExitCodes.InvalidInput) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// File access for the commands. Private keys are written only with owner access, and no file is overwritten without
/// <c>--force</c>.
/// </summary>
internal static class CliFiles
{
    public static string ReadText(string path, int maxBytes = 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new CliException($"The file '{path}' does not exist.");
            }

            if (info.Length > maxBytes)
            {
                throw new CliException($"The file '{path}' is larger than {maxBytes} bytes.");
            }

            return File.ReadAllText(path);
        }
        catch (IOException e)
        {
            throw new CliException($"The file '{path}' cannot be read: {e.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException($"The file '{path}' cannot be read: access is denied.");
        }
    }

    public static byte[] ReadBytes(string path, int maxBytes) => System.Text.Encoding.UTF8.GetBytes(ReadText(path, maxBytes));

    public static void Write(string path, string text, bool force, bool secret) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(text), force, secret);

    public static void Write(string path, byte[] bytes, bool force, bool secret)
    {
        var options = new FileStreamOptions
        {
            Mode = force ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (secret && !OperatingSystem.IsWindows())
        {
            // Owner read and write only (0600). On Windows the file inherits the folder's access rules.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(path, options);
            stream.Write(bytes);
        }
        catch (IOException) when (!force && File.Exists(path))
        {
            throw new CliException($"The file '{path}' exists. Pass --force to overwrite it.");
        }
        catch (IOException e)
        {
            throw new CliException($"The file '{path}' cannot be written: {e.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException($"The file '{path}' cannot be written: access is denied.");
        }
    }
}

/// <summary>
/// Reads the passphrase of a key file from an environment variable, or from the person when nobody set it.
/// </summary>
internal static class Passphrases
{
    public const string DefaultVariable = "ELM_KEY_PASSPHRASE";

    public static string ForNewKey(CliHost host, string variable)
    {
        string? fromEnvironment = host.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return CheckLength(fromEnvironment);
        }

        if (host.ReadSecret is null)
        {
            throw new CliException($"Set the passphrase in the environment variable {variable}; input is not interactive.");
        }

        string first = CheckLength(host.ReadSecret("Passphrase for the new key: ") ?? string.Empty);
        string second = host.ReadSecret("Repeat the passphrase: ") ?? string.Empty;
        return first == second ? first : throw new CliException("The passphrases do not match.");
    }

    public static string ForExistingKey(CliHost host, string variable, string path)
    {
        string? fromEnvironment = host.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (host.ReadSecret is null)
        {
            throw new CliException($"Set the passphrase in the environment variable {variable}; input is not interactive.");
        }

        return host.ReadSecret($"Passphrase for '{path}': ") ?? string.Empty;
    }

    private static string CheckLength(string passphrase) =>
        passphrase.Length >= KeyFiles.MinPassphraseLength
            ? passphrase
            : throw new CliException($"The passphrase must have at least {KeyFiles.MinPassphraseLength} characters.");
}

/// <summary>
/// Parses option values that System.CommandLine leaves as strings.
/// </summary>
internal static class CliParse
{
    /// <summary>
    /// Parses an ISO 8601 date or date-time. A date without a time is midnight UTC; a time without an offset is UTC.
    /// </summary>
    public static DateTimeOffset Time(string text, string option) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value
        )
            ? value
            : throw new CliException($"The value of {option} is not an ISO 8601 date or date-time: '{text}'.");

    /// <summary>
    /// Parses a feature: <c>id</c>, then optional <c>;ver=RANGE</c>, <c>;seats=N</c>, <c>;exp=DATE</c>.
    /// </summary>
    public static FeatureSpec Feature(string text)
    {
        string[] parts = text.Split(';');
        string id = parts[0].Trim();
        if (id.Length == 0)
        {
            throw new CliException($"The feature '{text}' has no ID.");
        }

        string? range = null;
        int? seats = null;
        DateTimeOffset? expires = null;
        foreach (string part in parts.Skip(1))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? part : part[..equals];
            string value = equals < 0 ? string.Empty : part[(equals + 1)..];
            switch (key.Trim())
            {
                case "ver":
                    range = value;
                    break;
                case "seats" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed):
                    seats = parsed;
                    break;
                case "exp":
                    expires = Time(value, "--feature exp");
                    break;
                default:
                    throw new CliException($"The feature '{text}' has an unknown or invalid part '{part}'. Use ver=, seats=, or exp=.");
            }
        }

        return new FeatureSpec(id, range, seats, expires);
    }

    public static KeyValuePair<string, string> Meta(string text)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? new(text[..equals], text[(equals + 1)..]) : throw new CliException($"The metadata '{text}' is not key=value.");
    }
}

/// <summary>
/// The root keys a command trusts: the <c>--trust</c> files, or the PEM keys in <c>ELM_TRUST_ROOT_KEYS</c> (design §16).
/// </summary>
internal static class TrustKeys
{
    public const string EnvironmentVariable = "ELM_TRUST_ROOT_KEYS";

    public static string[] Read(CliHost host, string[] files)
    {
        if (files.Length > 0)
        {
            return [.. files.Select(f => CliFiles.ReadText(f, 64 * 1024))];
        }

        string? pem = host.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new CliException($"Pass --trust, or set {EnvironmentVariable} to one or more PEM public keys.");
        }

        // The variable may hold several PEM blocks; the trust store takes one block per key.
        var keys = new List<string>();
        ReadOnlySpan<char> rest = pem;
        while (System.Security.Cryptography.PemEncoding.TryFind(rest, out System.Security.Cryptography.PemFields fields))
        {
            keys.Add(rest[fields.Location].ToString());
            rest = rest[fields.Location.End..];
        }

        return keys.Count > 0 ? [.. keys] : throw new CliException($"{EnvironmentVariable} holds no PEM public key.");
    }
}
