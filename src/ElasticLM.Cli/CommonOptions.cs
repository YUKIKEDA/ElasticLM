using System.CommandLine;
using System.Security.Cryptography;
using ElasticLM.Issuing;

namespace ElasticLM.Cli;

/// <summary>
/// Options and steps shared by several commands.
/// </summary>
internal static class CommonOptions
{
    public static Option<string> PassphraseEnvironment() =>
        new("--passphrase-env")
        {
            Description =
                $"The environment variable that holds the key passphrase. Default: {Passphrases.DefaultVariable}. When it is not set, the passphrase is asked for.",
            DefaultValueFactory = _ => Passphrases.DefaultVariable,
        };

    public static Option<bool> Force() => new("--force") { Description = "Overwrite files that exist." };

    public static Option<bool> Json() => new("--json") { Description = "Write the result as JSON." };

    /// <summary>
    /// Runs a command body and turns its failures into a message and an exit code (design §16).
    /// </summary>
    public static int Run(CliHost host, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (CliException e)
        {
            host.Error.WriteLine("error: " + e.Message);
            return e.ExitCode;
        }
        catch (ArgumentException e)
        {
            host.Error.WriteLine("error: " + e.Message);
            return ExitCodes.InvalidInput;
        }
    }

    public static ECDsa ReadPrivateKey(CliHost host, string path, string passphraseVariable)
    {
        string pem = CliFiles.ReadText(path, 64 * 1024);
        string passphrase = Passphrases.ForExistingKey(host, passphraseVariable, path);
        return KeyFiles.TryImportEncrypted(pem, passphrase, out ECDsa? key)
            ? key!
            : throw new CliException($"The key '{path}' is not an encrypted P-256 private key, or the passphrase is wrong.");
    }

    public static ECDsa ReadPublicKey(string path)
    {
        string pem = CliFiles.ReadText(path, 64 * 1024);
        return KeyFiles.TryImportPublic(pem, out ECDsa? key)
            ? key!
            : throw new CliException($"The file '{path}' is not one P-256 public key in PEM.");
    }
}
