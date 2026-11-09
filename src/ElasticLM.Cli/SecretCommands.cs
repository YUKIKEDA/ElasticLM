using System.Buffers.Text;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm secret create</c>: writes a random secret of 32 bytes for <c>elm-service</c>, which serves as the nonce secret
/// (<c>nonce.secretFile</c>) and as the admin UI sign-in's key encryption key (<c>dataProtection.kekFile</c>) (design §14.8,
/// §15.3, §16).
/// </summary>
internal static class SecretCommands
{
    public static Command Create(CliHost host)
    {
        var outOption = new Option<string>("--out") { Description = "The secret file to write.", Required = true }.AsPath();
        Option<bool> forceOption = CommonOptions.Force();
        Option<bool> jsonOption = CommonOptions.Json();

        var create = new Command(
            "create",
            "Create a file with a random secret for elm-service: the nonce secret (nonce.secretFile) or the admin UI sign-in's key encryption key (dataProtection.kekFile)."
        )
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

                    // The service takes at least this many bytes for the nonce secret and exactly as many for the key
                    // encryption key (WebLoginSettings.KekLength), so the length is not an option. The secret and its text
                    // are only in these two arrays, which are cleared after the write.
                    byte[] secret = RandomNumberGenerator.GetBytes(NonceService.MinSecretLength);
                    byte[] text = new byte[Base64.GetMaxEncodedToUtf8Length(secret.Length) + 1];
                    try
                    {
                        Base64.EncodeToUtf8(secret, text, out _, out int written);
                        text[written] = (byte)'\n';
                        CliFiles.Write(outPath, text, force, secret: true);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(secret);
                        CryptographicOperations.ZeroMemory(text);
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
