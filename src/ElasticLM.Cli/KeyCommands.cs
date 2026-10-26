using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm key ...</c>: create root and signing keys, certify a signing key, and print a key ID (design §7, §16).
/// </summary>
internal static class KeyCommands
{
    public static Command Create(CliHost host)
    {
        var key = new Command("key", "Create and certify keys.");
        key.Subcommands.Add(CreateKeyCommand(host, "root", "Create a root key. Keep it offline; it only certifies signing keys."));
        key.Subcommands.Add(CreateKeyCommand(host, "signing", "Create a signing key, which signs licenses once certified."));
        key.Subcommands.Add(CertifyCommand(host));
        key.Subcommands.Add(ThumbprintCommand(host));
        return key;
    }

    private static Command CreateKeyCommand(CliHost host, string name, string description)
    {
        var outOption = new Option<string>("--out") { Description = "The encrypted private key file to write.", Required = true };
        var publicOption = new Option<string?>("--public-out") { Description = "The public key file to write. Default: <out>.pub.pem." };
        Option<string> passphraseOption = CommonOptions.PassphraseEnvironment();
        Option<bool> forceOption = CommonOptions.Force();
        Option<bool> jsonOption = CommonOptions.Json();

        var create = new Command("create", description) { outOption, publicOption, passphraseOption, forceOption, jsonOption };
        var group = new Command(name, description) { create };
        create.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string outPath = parse.GetValue(outOption)!;
                    string publicPath = parse.GetValue(publicOption) ?? outPath + ".pub.pem";
                    bool force = parse.GetValue(forceOption);

                    // Check both paths before writing either, so a refused public key file never leaves a private key behind.
                    CliFiles.EnsureWritable(outPath, force);
                    CliFiles.EnsureWritable(publicPath, force);
                    string passphrase = Passphrases.ForNewKey(host, parse.GetValue(passphraseOption)!);

                    using ECDsa created = KeyFiles.CreateKey();
                    CliFiles.Write(outPath, KeyFiles.ExportEncrypted(created, passphrase), force, secret: true);
                    CliFiles.Write(publicPath, KeyFiles.ExportPublic(created), force, secret: false);
                    var output = new KeyOutput(KeyFiles.KeyId(created), outPath, publicPath);
                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.KeyOutput));
                    }
                    else
                    {
                        host.Out.WriteLine($"key ID: {output.KeyId}");
                        host.Out.WriteLine($"private key (encrypted): {outPath}");
                        host.Out.WriteLine($"public key: {publicPath}");
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return group;
    }

    private static Command CertifyCommand(CliHost host)
    {
        var rootOption = new Option<string>("--root") { Description = "The encrypted root key file.", Required = true };
        var publicOption = new Option<string>("--public") { Description = "The public key file of the signing key to certify.", Required = true };
        var useOption = new Option<string[]>("--use")
        {
            Description = "What the key may sign: license, lease, log. Repeat for several. Default: license.",
            AllowMultipleArgumentsPerToken = true,
        };
        var notBeforeOption = new Option<string?>("--not-before") { Description = "Start of validity (ISO 8601). Default: now." };
        var daysOption = new Option<int>("--days") { Description = "Validity in days, at most 731. Default: 365.", DefaultValueFactory = _ => 365 };
        var outOption = new Option<string>("--out") { Description = "The certificate file to write.", Required = true };
        Option<string> passphraseOption = CommonOptions.PassphraseEnvironment();
        Option<bool> forceOption = CommonOptions.Force();
        Option<bool> jsonOption = CommonOptions.Json();

        var certify = new Command("certify", "Certify a signing key with the root key.")
        {
            rootOption,
            publicOption,
            useOption,
            notBeforeOption,
            daysOption,
            outOption,
            passphraseOption,
            forceOption,
            jsonOption,
        };
        certify.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    KeyUses uses = ParseUses(parse.GetValue(useOption) ?? []);
                    DateTimeOffset notBefore = parse.GetValue(notBeforeOption) is { } text
                        ? CliParse.Time(text, "--not-before")
                        : host.Time.GetUtcNow();
                    int days = parse.GetValue(daysOption);
                    string rootPath = parse.GetValue(rootOption)!;
                    using ECDsa root = CommonOptions.ReadPrivateKey(host, rootPath, parse.GetValue(passphraseOption)!);
                    using ECDsa signing = CommonOptions.ReadPublicKey(parse.GetValue(publicOption)!);

                    string certificate = CertificateIssuer.Issue(root, signing, uses, notBefore, TimeSpan.FromDays(days), host.Time);
                    string outPath = parse.GetValue(outOption)!;
                    CliFiles.Write(outPath, certificate + "\n", parse.GetValue(forceOption), secret: false);

                    var output = new CertificateOutput(
                        outPath,
                        KeyFiles.KeyId(signing),
                        KeyFiles.KeyId(root),
                        [.. UseNames(uses)],
                        notBefore,
                        notBefore.AddDays(days)
                    );
                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CertificateOutput));
                    }
                    else
                    {
                        host.Out.WriteLine($"certificate: {outPath}");
                        host.Out.WriteLine($"signing key ID: {output.SubjectKeyId}");
                        host.Out.WriteLine($"root key ID: {output.RootKeyId}");
                        host.Out.WriteLine($"uses: {string.Join(", ", output.Uses)}");
                        host.Out.WriteLine(
                            $"valid: {output.NotBefore.ToString("u", CultureInfo.InvariantCulture)} to {output.ExpiresAt.ToString("u", CultureInfo.InvariantCulture)}"
                        );
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return certify;
    }

    private static Command ThumbprintCommand(CliHost host)
    {
        var fileArgument = new Argument<string>("public-key-file") { Description = "A public key PEM file." };
        var thumbprint = new Command("thumbprint", "Print the key ID (JWK thumbprint) of a public key.") { fileArgument };
        thumbprint.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    using ECDsa key = CommonOptions.ReadPublicKey(parse.GetValue(fileArgument)!);
                    host.Out.WriteLine(KeyFiles.KeyId(key));
                    return ExitCodes.Success;
                }
            )
        );
        return thumbprint;
    }

    private static KeyUses ParseUses(string[] names)
    {
        if (names.Length == 0)
        {
            return KeyUses.License;
        }

        KeyUses uses = KeyUses.None;
        foreach (string name in names.SelectMany(n => n.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            uses |= name switch
            {
                "license" => KeyUses.License,
                "lease" => KeyUses.Lease,
                "log" => KeyUses.Log,
                _ => throw new CliException($"The use '{name}' is not license, lease, or log."),
            };
        }

        return uses;
    }

    private static IEnumerable<string> UseNames(KeyUses uses)
    {
        if (uses.HasFlag(KeyUses.License))
        {
            yield return "license";
        }

        if (uses.HasFlag(KeyUses.Lease))
        {
            yield return "lease";
        }

        if (uses.HasFlag(KeyUses.Log))
        {
            yield return "log";
        }
    }
}
