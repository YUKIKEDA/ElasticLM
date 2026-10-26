using System.Buffers.Text;
using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm license ...</c>: issue, inspect, and verify license files without the hosted service (design §16).
/// </summary>
internal static class LicenseCommands
{
    private const int MaxLicenseFileBytes = 64 * 1024;

    public static Command Create(CliHost host)
    {
        var license = new Command("license", "Issue, inspect, and verify license files.");
        license.Subcommands.Add(IssueCommand(host));
        license.Subcommands.Add(InspectCommand(host));
        license.Subcommands.Add(VerifyCommand(host));
        return license;
    }

    private static Command IssueCommand(CliHost host)
    {
        var signingOption = new Option<string>("--signing-key") { Description = "The encrypted signing key file.", Required = true };
        var certificateOption = new Option<string[]>("--certificate")
        {
            Description = "A certificate file of the signing key. Repeat for several (at most four).",
            Required = true,
        };
        var productOption = new Option<string>("--product") { Description = "The product ID.", Required = true };
        var licenseIdOption = new Option<string>("--license-id") { Description = "The license ID.", Required = true };
        var kindOption = new Option<string>("--kind")
        {
            Description = "standard, trial, or server. Default: standard.",
            DefaultValueFactory = _ => "standard",
        };
        var featureOption = new Option<string[]>("--feature") { Description = "A feature: ID[;ver=RANGE][;seats=N][;exp=DATE]. Repeat for several." };
        var expiresOption = new Option<string>("--expires")
        {
            Description = "Token expiry (ISO 8601), or 'never'. A license without expiry cannot be revoked.",
            Required = true,
        };
        var graceOption = new Option<double?>("--grace-days") { Description = "Grace period in days. Default: 7, or 0 for trials." };
        var refreshOption = new Option<double?>("--refresh-days") { Description = "Days before expiry when clients start refreshing online." };
        var bindOption = new Option<string?>("--bind-key") { Description = "The installation key thumbprint to bind to." };
        var fingerprintOption = new Option<string[]>("--fingerprint") { Description = "A machine fingerprint to bind to. Repeat for several." };
        var matchingOption = new Option<string>("--fingerprint-matching")
        {
            Description = "any, all, or ignore. Default: any.",
            DefaultValueFactory = _ => "any",
        };
        var leaseOption = new Option<double?>("--lease-minutes") { Description = "Server licenses: lease duration in minutes. Default: 10." };
        var borrowOption = new Option<double?>("--borrow-days")
        {
            Description = "Server licenses: borrow limit in days; 0 disallows borrowing. Default: 0.",
        };
        var overageOption = new Option<int?>("--overage") { Description = "Server licenses: overage percentage. Default: 0." };
        var borrowReturnsOption = new Option<int?>("--borrow-returns")
        {
            Description = "Server licenses: early returns of borrows one installation may make in 30 days. Default: 2.",
        };
        var cloneOption = new Option<string?>("--clone") { Description = "Server licenses: deny or flag a detected clone. Default: deny." };
        var metaOption = new Option<string[]>("--meta") { Description = "Vendor metadata key=value. Repeat for several." };
        var outOption = new Option<string>("--out") { Description = "The license file to write.", Required = true };
        var issueTrustOption = new Option<string[]>("--trust")
        {
            Description =
                $"A root public key file; the license is verified with it before it is written. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        };
        var issueRevokedOption = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        Option<string> passphraseOption = CommonOptions.PassphraseEnvironment();
        Option<bool> forceOption = CommonOptions.Force();
        Option<bool> jsonOption = CommonOptions.Json();

        var issue = new Command("issue", "Issue a license file signed by a certified signing key.")
        {
            signingOption,
            certificateOption,
            productOption,
            licenseIdOption,
            kindOption,
            featureOption,
            expiresOption,
            graceOption,
            refreshOption,
            bindOption,
            fingerprintOption,
            matchingOption,
            leaseOption,
            borrowOption,
            overageOption,
            cloneOption,
            borrowReturnsOption,
            metaOption,
            outOption,
            issueTrustOption,
            issueRevokedOption,
            passphraseOption,
            forceOption,
            jsonOption,
        };
        issue.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    LicenseKind kind = ParseKind(parse.GetValue(kindOption)!);
                    bool isServer = kind == LicenseKind.Server;
                    if (
                        !isServer
                        && (
                            parse.GetValue(leaseOption) is not null
                            || parse.GetValue(borrowOption) is not null
                            || parse.GetValue(overageOption) is not null
                            || parse.GetValue(cloneOption) is not null
                            || parse.GetValue(borrowReturnsOption) is not null
                        )
                    )
                    {
                        throw new CliException(
                            "--lease-minutes, --borrow-days, --overage, --clone, and --borrow-returns apply to server licenses only."
                        );
                    }

                    string expiresText = parse.GetValue(expiresOption)!;
                    DateTimeOffset? expires = expiresText == "never" ? null : CliParse.Time(expiresText, "--expires");
                    double graceDays = parse.GetValue(graceOption) ?? (kind == LicenseKind.Trial ? 0 : 7);
                    var spec = new LicenseSpec
                    {
                        LicenseId = parse.GetValue(licenseIdOption)!,
                        ProductId = parse.GetValue(productOption)!,
                        Kind = kind,
                        Features = [.. (parse.GetValue(featureOption) ?? []).Select(CliParse.Feature)],
                        ExpiresAt = expires,
                        GracePeriod = TimeSpan.FromDays(graceDays),
                        RefreshWindow = parse.GetValue(refreshOption) is double refresh ? TimeSpan.FromDays(refresh) : null,
                        InstallationKeyThumbprint = parse.GetValue(bindOption),
                        Fingerprints = parse.GetValue(fingerprintOption) ?? [],
                        FingerprintMatching = ParseMatching(parse.GetValue(matchingOption)!),
                        LeaseDuration = isServer ? TimeSpan.FromMinutes(parse.GetValue(leaseOption) ?? 10) : null,
                        BorrowLimit = isServer ? TimeSpan.FromDays(parse.GetValue(borrowOption) ?? 0) : null,
                        OveragePercent = isServer ? parse.GetValue(overageOption) ?? 0 : null,
                        BorrowReturnLimit = isServer ? parse.GetValue(borrowReturnsOption) ?? 2 : null,
                        CloneStrategy = isServer ? ParseClone(parse.GetValue(cloneOption) ?? "deny") : null,
                        Metadata = parse.GetValue(metaOption) is { Length: > 0 } meta
                            ? new Dictionary<string, string>(meta.Select(CliParse.Meta), StringComparer.Ordinal)
                            : null,
                    };

                    string[] certificates = [.. parse.GetValue(certificateOption)!.Select(p => CliFiles.ReadText(p, MaxLicenseFileBytes))];
                    using ECDsa signing = CommonOptions.ReadPrivateKey(host, parse.GetValue(signingOption)!, parse.GetValue(passphraseOption)!);
                    TrustStore trust = TrustStore.Create(
                        TrustKeys.Read(host, parse.GetValue(issueTrustOption) ?? []),
                        parse.GetValue(issueRevokedOption) ?? []
                    );
                    IssuedLicense issued = LicenseIssuer.Issue(signing, certificates, spec, host.Time, trust);
                    string outPath = parse.GetValue(outOption)!;
                    CliFiles.Write(outPath, issued.File, parse.GetValue(forceOption), secret: false);

                    foreach (string warning in issued.Warnings)
                    {
                        host.Error.WriteLine("warning: " + warning);
                    }

                    var output = new IssueOutput(outPath, spec.LicenseId, issued.TokenId, [.. issued.Warnings]);
                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.IssueOutput));
                    }
                    else
                    {
                        host.Out.WriteLine($"license file: {outPath}");
                        host.Out.WriteLine($"license ID: {output.LicenseId}");
                        host.Out.WriteLine($"token ID: {output.TokenId}");
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return issue;
    }

    private static Command InspectCommand(CliHost host)
    {
        var fileArgument = new Argument<string>("license-file") { Description = "The license file." };
        Option<bool> jsonOption = CommonOptions.Json();
        var inspect = new Command("inspect", "Show the contents of a license file without verifying it.") { fileArgument, jsonOption };
        inspect.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string text = CliFiles.ReadText(parse.GetValue(fileArgument)!, MaxLicenseFileBytes);
                    InspectOutput output = Decode(text);
                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.InspectOutput));
                        return ExitCodes.Success;
                    }

                    host.Out.WriteLine("NOT VERIFIED: use 'elm license verify' to check the signatures.");
                    host.Out.WriteLine("license header: " + output.License.Header.GetRawText());
                    host.Out.WriteLine("license payload:");
                    host.Out.WriteLine(Pretty(output.License.Payload));
                    for (int i = 0; i < output.Certificates.Length; i++)
                    {
                        host.Out.WriteLine($"certificate {i + 1} header: " + output.Certificates[i].Header.GetRawText());
                        host.Out.WriteLine($"certificate {i + 1} payload:");
                        host.Out.WriteLine(Pretty(output.Certificates[i].Payload));
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return inspect;
    }

    private static Command VerifyCommand(CliHost host)
    {
        var fileArgument = new Argument<string>("license-file") { Description = "The license file." };
        var trustOption = new Option<string[]>("--trust")
        {
            Description = $"A root public key file to trust. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        };
        var revokedOption = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        var productOption = new Option<string>("--product") { Description = "The product ID the license must be for.", Required = true };
        var kindOption = new Option<string>("--kind")
        {
            Description = "standard, trial, or server. Default: standard.",
            DefaultValueFactory = _ => "standard",
        };
        var featureOption = new Option<string?>("--feature") { Description = "A feature the license must grant." };
        var versionOption = new Option<string?>("--version") { Description = "The feature version to check (major.minor[.patch])." };
        var keyOption = new Option<string?>("--key-thumbprint") { Description = "The installation key thumbprint, for bound licenses." };
        var fingerprintOption = new Option<string[]>("--fingerprint") { Description = "A current machine fingerprint. Repeat for several." };
        var thisMachineOption = new Option<bool>("--this-machine") { Description = "Use the fingerprints of this machine for the product." };
        Option<bool> jsonOption = CommonOptions.Json();

        var verify = new Command("verify", "Verify a license file against a trust store.")
        {
            fileArgument,
            trustOption,
            revokedOption,
            productOption,
            kindOption,
            featureOption,
            versionOption,
            keyOption,
            fingerprintOption,
            thisMachineOption,
            jsonOption,
        };
        verify.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string product = parse.GetValue(productOption)!;
                    TrustStore trust = TrustStore.Create(
                        TrustKeys.Read(host, parse.GetValue(trustOption) ?? []),
                        parse.GetValue(revokedOption) ?? []
                    );
                    string[] fingerprints = parse.GetValue(thisMachineOption)
                        ? [.. MachineFingerprint.Compute(product)]
                        : parse.GetValue(fingerprintOption) ?? [];
                    string? keyThumbprint = parse.GetValue(keyOption);
                    InstallationIdentity? installation =
                        keyThumbprint is not null ? new InstallationIdentity(keyThumbprint, fingerprints)
                        : fingerprints.Length > 0 ? throw new CliException("--fingerprint and --this-machine need --key-thumbprint.")
                        : null;
                    var requirement = new LicenseRequirement(
                        product,
                        ParseKind(parse.GetValue(kindOption)!),
                        parse.GetValue(featureOption),
                        parse.GetValue(versionOption),
                        installation
                    );

                    var verifier = new LicenseVerifier(trust, host.Time, StatelessClockGuard.Instance);
                    LicenseVerificationResult result = verifier.Verify(
                        CliFiles.ReadBytes(parse.GetValue(fileArgument)!, MaxLicenseFileBytes),
                        requirement
                    );
                    VerifyOutput output = result switch
                    {
                        LicenseVerificationResult.Valid valid => new VerifyOutput(
                            true,
                            null,
                            null,
                            valid.License.LicenseId,
                            valid.License.ProductId,
                            valid.License.Kind.ToString().ToLowerInvariant(),
                            valid.License.ExpiresAt,
                            valid.InGracePeriod,
                            [.. valid.License.Features.Select(f => f.Id)]
                        ),
                        LicenseVerificationResult.Invalid invalid => new VerifyOutput(
                            false,
                            invalid.Error.WireName,
                            invalid.Error.Message,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null
                        ),
                        _ => throw new InvalidOperationException("The verification result has an unknown type."),
                    };

                    if (parse.GetValue(jsonOption))
                    {
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.VerifyOutput));
                    }
                    else if (output.Valid)
                    {
                        host.Out.WriteLine($"valid: license {output.LicenseId}, product {output.ProductId}, kind {output.Kind}");
                        host.Out.WriteLine(
                            "expires: "
                                + (output.ExpiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "never")
                                + (output.InGracePeriod == true ? " (in grace period)" : string.Empty)
                        );
                        host.Out.WriteLine($"features: {string.Join(", ", output.Features!)}");
                    }
                    else
                    {
                        host.Out.WriteLine($"invalid: {output.Code}: {output.Message}");
                    }

                    return output.Valid ? ExitCodes.Success : ExitCodes.VerificationFailed;
                }
            )
        );
        return verify;
    }

    private static CloneStrategy ParseClone(string text) =>
        text switch
        {
            "deny" => CloneStrategy.Deny,
            "flag" => CloneStrategy.Flag,
            _ => throw new CliException("--clone must be deny or flag."),
        };

    private static LicenseKind ParseKind(string text) =>
        text switch
        {
            "standard" => LicenseKind.Standard,
            "trial" => LicenseKind.Trial,
            "server" => LicenseKind.Server,
            _ => throw new CliException($"The kind '{text}' is not standard, trial, or server."),
        };

    private static FingerprintMatching ParseMatching(string text) =>
        text switch
        {
            "any" => FingerprintMatching.Any,
            "all" => FingerprintMatching.All,
            "ignore" => FingerprintMatching.Ignore,
            _ => throw new CliException($"The fingerprint matching '{text}' is not any, all, or ignore."),
        };

    private static InspectOutput Decode(string fileText)
    {
        try
        {
            using JsonDocument file = JsonDocument.Parse(fileText, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = file.RootElement;
            TokenView license = DecodeToken(root.GetProperty("license").GetString() ?? string.Empty);
            TokenView[] certificates = [.. root.GetProperty("certificates").EnumerateArray().Select(c => DecodeToken(c.GetString() ?? string.Empty))];
            return new InspectOutput(false, license, certificates);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CliException("The file is not an ElasticLM license file.");
        }
    }

    private static TokenView DecodeToken(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("A token is not a compact JWS.");
        }

        return new TokenView(ParseSegment(parts[0]), ParseSegment(parts[1]));
    }

    private static JsonElement ParseSegment(string segment)
    {
        using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(segment), new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.Clone();
    }

    private static string Pretty(JsonElement element) => JsonSerializer.Serialize(element, CliJsonContext.Default.JsonElement);
}
