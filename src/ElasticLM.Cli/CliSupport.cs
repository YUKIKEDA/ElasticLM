using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Security.Cryptography;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

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

    public static void EnsureWritable(string path, bool force)
    {
        if (!force && File.Exists(path))
        {
            throw new CliException($"The file '{path}' exists. Pass --force to overwrite it.");
        }
    }

    public static void Write(string path, string text, bool force, bool secret) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(text), force, secret);

    public static void Write(string path, byte[] bytes, bool force, bool secret)
    {
        // With --force the content goes to a new file beside the target, which then replaces it (design §16). Writing into
        // the existing file would keep what that file had: its explicit access rules on Windows, its mode on Linux and macOS,
        // and every handle another process opened on it, which could then read the new content.
        string target = force ? path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp" : path;
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
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
            using (var stream = new FileStream(target, options))
            {
                stream.Write(bytes);
            }

            if (force)
            {
                File.Move(target, path, overwrite: true);
            }
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
        finally
        {
            if (force)
            {
                DeleteQuietly(target);
            }
        }
    }

    // Removes the temporary file of a write that failed; after a move there is none. A failure here would hide the first one.
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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

    /// <summary>
    /// Returns a value that must not be empty. The library's own refusal would not say which value it is (design §17.3,
    /// V1c10).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="name">The option or argument, as the usage line names it.</param>
    public static string NotEmpty(string value, string name) => value.Length > 0 ? value : throw new CliException($"{name} must not be empty.");

    public static KeyValuePair<string, string> Meta(string text)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? new(text[..equals], text[(equals + 1)..]) : throw new CliException($"The metadata '{text}' is not key=value.");
    }
}

/// <summary>
/// The options and arguments that take a file or folder path (design §17.3, V1c10). An empty value, or one of white space
/// only, is a parse error that names the option or argument: the file system's own refusal ("The path is empty.") would not
/// say which value it is.
/// </summary>
internal static class CliPaths
{
    /// <summary>Makes an option a path option.</summary>
    /// <param name="option">The option.</param>
    /// <returns>The same option.</returns>
    public static Option<T> AsPath<T>(this Option<T> option)
    {
        option.Validators.Add(RefuseBlankOption);
        return option;
    }

    /// <summary>Makes an argument a path argument; the message names it as the help shows it, such as &lt;license-file&gt;.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The same argument.</returns>
    public static Argument<T> AsPath<T>(this Argument<T> argument)
    {
        argument.Validators.Add(RefuseBlankArgument);
        return argument;
    }

    private static void RefuseBlankOption(OptionResult result) => RefuseBlank(result, result.Option.Name);

    private static void RefuseBlankArgument(ArgumentResult result) => RefuseBlank(result, $"<{result.Argument.Name}>");

    private static void RefuseBlank(SymbolResult result, string name)
    {
        if (result.Tokens.Any(token => string.IsNullOrWhiteSpace(token.Value)))
        {
            result.AddError($"{name} must not be empty or only white space.");
        }
    }
}

/// <summary>
/// The root keys a command trusts: the <c>--trust</c> files, or the PEM keys in <c>ELM_TRUST_ROOT_KEYS</c> (design §16).
/// </summary>
internal static class TrustKeys
{
    public const string EnvironmentVariable = "ELM_TRUST_ROOT_KEYS";

    /// <summary>
    /// Creates the trust store of a command from its <c>--trust</c> files (or the variable) and its <c>--revoked</c> key IDs.
    /// </summary>
    /// <param name="host">The host, for the variable.</param>
    /// <param name="files">The <c>--trust</c> files, or <see langword="null"/>.</param>
    /// <param name="revoked">The <c>--revoked</c> key IDs, or <see langword="null"/>.</param>
    public static TrustStore Store(CliHost host, string[]? files, string[]? revoked)
    {
        string[] keys = Read(host, files ?? []);
        string[] ids = revoked ?? [];

        // design §17.3 (V1c10): the trust store's own refusal of an empty key ID does not name the option.
        if (ids.Any(id => id.Length == 0))
        {
            throw new CliException("--revoked must not be empty.");
        }

        return TrustStore.Create(keys, ids);
    }

    /// <summary>
    /// Splits a text into its PEM blocks. A file and the variable may both hold several root keys; the trust store takes one
    /// block per key.
    /// </summary>
    public static string[] SplitBlocks(string pem, string source)
    {
        string[] keys = KeyFiles.SplitPem(pem);
        return keys.Length > 0 ? keys : throw new CliException($"{source} holds no PEM public key.");
    }

    private static string[] Read(CliHost host, string[] files)
    {
        if (files.Length > 0)
        {
            return [.. files.SelectMany(f => SplitBlocks(CliFiles.ReadText(f, 64 * 1024), $"The file '{f}'"))];
        }

        string? pem = host.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new CliException($"Pass --trust, or set {EnvironmentVariable} to one or more PEM public keys.");
        }

        return SplitBlocks(pem, EnvironmentVariable);
    }
}
