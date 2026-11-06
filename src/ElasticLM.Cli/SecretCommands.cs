using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm secret create</c>: writes a random secret for the nonces of <c>elm-service</c> (design §14.8, §16).
/// </summary>
internal static class SecretCommands
{
    public static Command Create(CliHost host)
    {
        var outOption = new Option<string>("--out") { Description = "The secret file to write.", Required = true };
        Option<bool> forceOption = CommonOptions.Force();
        Option<bool> jsonOption = CommonOptions.Json();

        var create = new Command("create", "Create a file with a random secret for the nonces of elm-service (nonce.secretFile).")
        {
            outOption,
            forceOption,
            jsonOption,
        };
        create.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string outPath = parse.GetValue(outOption)!;
                    bool force = parse.GetValue(forceOption);
                    CliFiles.EnsureWritable(outPath, force);

                    // The service takes at least this many bytes; the length is not an option.
                    byte[] secret = RandomNumberGenerator.GetBytes(NonceService.MinSecretLength);
                    try
                    {
                        CliFiles.Write(outPath, Convert.ToBase64String(secret) + "\n", force, secret: true);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secret);
                    }

                    // The secret itself is never printed.
                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(new SecretOutput(outPath), CliJsonContext.Default.SecretOutput));
                    }
                    else
                    {
                        host.Out.WriteLine($"secret file: {outPath}");
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return new Command("secret", "Create secrets for elm-service.") { create };
    }
}
