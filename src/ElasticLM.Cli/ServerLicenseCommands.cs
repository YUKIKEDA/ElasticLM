using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Cli;

/// <summary>
/// The server license commands of <c>elm server</c> (design §13.7, M4c3): they use an <see cref="ActivationClient"/> in the
/// server's data folder, so they work while the server is stopped (it does not start without a license, §13.1); a new file
/// is written to the server's license path and a running server is asked to reload it.
/// </summary>
internal static class ServerLicenseCommands
{
    private const int MaxAnswerBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static void Add(Command server, CliHost host)
    {
        server.Subcommands.Add(Online(host, "activate", "Activate this server's license online and write the license file.", activate: true));
        server.Subcommands.Add(Online(host, "refresh", "Refresh this server's license online and write the license file.", activate: false));
        server.Subcommands.Add(Deactivate(host));
        server.Subcommands.Add(Request(host, "activate-request", "Make an offline activation request file (.elmreq).", activate: true));
        server.Subcommands.Add(Request(host, "refresh-request", "Make an offline refresh request file (.elmreq).", activate: false));
        server.Subcommands.Add(Import(host));
        server.Subcommands.Add(Proof(host));
        server.Subcommands.Add(Forget(host));
    }

    // activate and refresh: online, with --service; the new file goes to --license.
    private static Command Online(CliHost host, string name, string description, bool activate)
    {
        var command = new Command(name, description);
        var key = new Argument<string>("license-key") { Description = "The license key of a server license." };
        if (activate)
        {
            command.Arguments.Add(key);
        }

        Common common = AddCommon(command);
        Option<string> service = ServiceOption(command);
        Option<string> license = LicenseOption(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    Uri url = ServiceUrl(parse.GetValue(service)!);
                    string? licenseKey = activate ? parse.GetValue(key) : null;
                    return Kept(
                        host,
                        common,
                        parse,
                        url,
                        client => licenseKey is null ? client.RefreshAsync() : client.ActivateAsync(licenseKey),
                        parse.GetValue(license)!
                    );
                }
            )
        );
        return command;
    }

    private static Command Import(CliHost host)
    {
        var answer = new Argument<string>("answer") { Description = "The service's answer file (.elmresp)." };
        var command = new Command("import", "Import the service's answer to the waiting request and write the license file.") { answer };
        Common common = AddCommon(command);
        Option<string> license = LicenseOption(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string text = CliFiles.ReadText(parse.GetValue(answer)!, MaxAnswerBytes);
                    return Kept(host, common, parse, null, client => client.ImportResponseAsync(text), parse.GetValue(license)!);
                }
            )
        );
        return command;
    }

    // design §13.7: refused while the server runs, which would lend on while the service frees its place (R8).
    private static Command Deactivate(CliHost host)
    {
        var command = new Command("deactivate", "Deactivate this server's license online and remove its license file. Stop the server first.");
        Common common = AddCommon(command);
        Option<string> service = ServiceOption(command);
        Option<string?> license = RemovedLicenseOption(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    Uri url = ServiceUrl(parse.GetValue(service)!);
                    StopFirst(host, common, parse);
                    byte[]? kept = KeptLicense(host, common, parse);
                    int code = WithClient(
                        host,
                        common,
                        parse,
                        url,
                        client => client.DeactivateAsync().GetAwaiter().GetResult() is LicenseError error ? Refused(host, error) : ExitCodes.Success
                    );
                    if (code != ExitCodes.Success)
                    {
                        return code;
                    }

                    RemoveIfKept(parse.GetValue(license), kept);
                    host.Out.WriteLine("deactivated");
                    return ExitCodes.Success;
                }
            )
        );
        return command;
    }

    private static Command Proof(CliHost host)
    {
        var output = new Option<string>("--out") { Description = "Where to write the deactivation proof (.elmdeact).", Required = true };
        Option<bool> force = CommonOptions.Force();
        var command = new Command(
            "deactivate-proof",
            "Make an offline deactivation proof and remove the activation and its license file. Stop the server first."
        )
        {
            output,
            force,
        };
        Common common = AddCommon(command);
        Option<string?> license = RemovedLicenseOption(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    StopFirst(host, common, parse);
                    CliFiles.EnsureWritable(parse.GetValue(output)!, parse.GetValue(force));
                    byte[]? kept = KeptLicense(host, common, parse);
                    string? proof = null;
                    int code = WithClient(
                        host,
                        common,
                        parse,
                        null,
                        client =>
                        {
                            OfflineRequestResult result = client.CreateDeactivationProofAsync().GetAwaiter().GetResult();
                            if (result is OfflineRequestResult.Failed failed)
                            {
                                return Refused(host, failed.Error);
                            }

                            proof = ((OfflineRequestResult.Created)result).File;
                            return ExitCodes.Success;
                        }
                    );
                    if (code != ExitCodes.Success)
                    {
                        return code;
                    }

                    CliFiles.Write(parse.GetValue(output)!, proof!, parse.GetValue(force), secret: false);
                    RemoveIfKept(parse.GetValue(license), kept);
                    host.Out.WriteLine($"proof: {parse.GetValue(output)}");
                    return ExitCodes.Success;
                }
            )
        );
        return command;
    }

    private static Command Forget(CliHost host)
    {
        var command = new Command("forget-proof", "Forget the deactivation proof once the service has it, so the server may activate again.");
        Common common = AddCommon(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                    WithClient(
                        host,
                        common,
                        parse,
                        null,
                        client =>
                        {
                            if (client.ForgetDeactivationProofAsync().GetAwaiter().GetResult() is LicenseError error)
                            {
                                return Refused(host, error);
                            }

                            host.Out.WriteLine("forgotten");
                            return ExitCodes.Success;
                        }
                    )
            )
        );
        return command;
    }

    // activate-request and refresh-request; refused while the running server refreshes on its own (R9).
    private static Command Request(CliHost host, string name, string description, bool activate)
    {
        var command = new Command(name, description);
        var key = new Option<string>("--key") { Description = "The license key of a server license.", Required = true };
        if (activate)
        {
            command.Options.Add(key);
        }

        Common common = AddCommon(command);
        var output = new Option<string>("--out") { Description = "Where to write the request file.", Required = true };
        var renew = new Option<bool>("--renew") { Description = "Make a new request before the waiting one expires." };
        Option<bool> force = CommonOptions.Force();
        command.Options.Add(output);
        command.Options.Add(renew);
        command.Options.Add(force);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    if (
                        Status(host, common, parse) is JsonElement status
                        && status.TryGetProperty("autoRefresh", out JsonElement auto)
                        && auto.GetBoolean()
                    )
                    {
                        return Refused(
                            host,
                            new LicenseError(ErrorCode.InvalidState, "The server refreshes its license online; use elm server refresh.")
                        );
                    }

                    CliFiles.EnsureWritable(parse.GetValue(output)!, parse.GetValue(force));
                    OfflineRequestResult.Created? created = null;
                    int code = WithClient(
                        host,
                        common,
                        parse,
                        null,
                        client =>
                        {
                            OfflineRequestResult result = activate
                                ? client.CreateActivationRequestAsync(parse.GetValue(key)!, parse.GetValue(renew)).GetAwaiter().GetResult()
                                : client.CreateRefreshRequestAsync(parse.GetValue(renew)).GetAwaiter().GetResult();
                            if (result is OfflineRequestResult.Failed failed)
                            {
                                return Refused(host, failed.Error);
                            }

                            created = (OfflineRequestResult.Created)result;
                            return ExitCodes.Success;
                        }
                    );
                    if (code != ExitCodes.Success)
                    {
                        return code;
                    }

                    CliFiles.Write(parse.GetValue(output)!, created!.File, parse.GetValue(force), secret: false);
                    host.Out.WriteLine($"request: {parse.GetValue(output)}");
                    if (created.Replaced)
                    {
                        // design §5.4: an earlier request file passed after this one is taken for a clone.
                        host.Out.WriteLine("The earlier request is replaced; discard its file.");
                    }

                    return ExitCodes.Success;
                }
            )
        );
        return command;
    }

    // An operation that keeps a new license file: once the client is closed, the file goes to --license, and a running server
    // reloads it; the token it then holds must be the one written (R6).
    private static int Kept(
        CliHost host,
        Common common,
        ParseResult parse,
        Uri? service,
        Func<ActivationClient, Task<ActivationResult>> operation,
        string licensePath
    )
    {
        VerifiedLicense? license = null;
        int code = WithClient(
            host,
            common,
            parse,
            service,
            client =>
            {
                ActivationResult result = operation(client).GetAwaiter().GetResult();
                if (result is ActivationResult.Failed failed)
                {
                    return Refused(host, failed.Error);
                }

                license = ((ActivationResult.Activated)result).License;
                return ExitCodes.Success;
            }
        );
        if (code != ExitCodes.Success)
        {
            return code;
        }

        byte[]? file = KeptLicense(host, common, parse);
        try
        {
            LicenseFiles.Write(licensePath, file ?? throw new IOException("The activation file holds no license."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            host.Error.WriteLine(
                $"error: The activation is kept, but the license file cannot be written: {e.Message} Fix the path and run elm server refresh."
            );
            return ExitCodes.VerificationFailed;
        }

        host.Out.WriteLine($"license {license!.LicenseId}, seq {license.Sequence}: {licensePath}");
        return Reload(host, common, parse, license.TokenId);
    }

    private static int WithClient(CliHost host, Common common, ParseResult parse, Uri? service, Func<ActivationClient, int> body)
    {
        string product = parse.GetValue(common.Product)!;
        if (!new InstallationKeyStore(product, KeyScope.Machine, parse.GetValue(common.Data)).HasKey)
        {
            // design §13.7: the server makes its key at its first start; a key made here could be the wrong folder's (R5).
            throw new CliException("The server's installation key does not exist; start elm-server once to make it.");
        }

        var options = new ActivationClientOptions
        {
            ProductId = product,
            ServiceUrl = service,
            Trust = TrustStore.Create(TrustKeys.Read(host, parse.GetValue(common.Trust) ?? []), parse.GetValue(common.Revoked) ?? []),
            KeyScope = KeyScope.Machine,
            DataDirectory = parse.GetValue(common.Data),
            TimeProvider = host.Time,
            RequiredKind = LicenseKind.Server,
            CreateKey = false,
        };
        if (!ActivationClient.TryCreate(options, out ActivationClient? client, out LicenseError? error))
        {
            return Refused(host, error!);
        }

        try
        {
            return body(client!);
        }
        finally
        {
            client!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // design §13.7: a running server reads the new file; one that is not running reads it when it starts.
    private static int Reload(CliHost host, Common common, ParseResult parse, string tokenId)
    {
        if (Send(common, parse, HttpMethod.Post, "admin/v1/license/reload") is not (bool ok, JsonElement answer))
        {
            host.Out.WriteLine("The server is not running; it reads the license when it starts.");
            return ExitCodes.Success;
        }

        if (!ok)
        {
            host.Error.WriteLine($"error: the server kept its license: {Text(answer, "code")}: {Text(answer, "detail")}");
            return ExitCodes.VerificationFailed;
        }

        if (Text(answer, "licenseToken") != tokenId)
        {
            host.Error.WriteLine("error: The server reads another license file than --license; pass its setting license.");
            return ExitCodes.VerificationFailed;
        }

        host.Out.WriteLine("The server reloaded the license.");
        return ExitCodes.Success;
    }

    // design §13.7 R8: the license of a running server is deactivated only once it has stopped.
    private static void StopFirst(CliHost host, Common common, ParseResult parse)
    {
        if (Status(host, common, parse) is not null)
        {
            throw new CliException("Stop elm-server before deactivating its license.", ExitCodes.VerificationFailed);
        }
    }

    // The running server's status, or null when it does not run (no administrator token, nothing answers).
    private static JsonElement? Status(CliHost host, Common common, ParseResult parse) =>
        Send(common, parse, HttpMethod.Get, "admin/v1/status") is (true, JsonElement status) ? status : null;

    // One call to the local administration API; null when the server does not run.
    private static (bool Ok, JsonElement Answer)? Send(Common common, ParseResult parse, HttpMethod method, string path)
    {
        string folder = Folder(parse, common);
        string? token = AdminToken.Read(folder);
        string text = parse.GetValue(common.Admin) ?? "http://127.0.0.1:7071/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? baseUri) || baseUri.Scheme != Uri.UriSchemeHttp || !baseUri.IsLoopback)
        {
            throw new CliException($"--url must be an http URL on a loopback address: '{text}'.");
        }

        if (token is null)
        {
            return null;
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = RequestTimeout };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            string body = reader.ReadToEnd();
            using var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            return (response.IsSuccessStatusCode, document.RootElement.Clone());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            throw new CliException("The server's administration API answered with a body that is not JSON.", ExitCodes.NetworkError);
        }
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement member) && member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;

    // design §16, §13.7: the network is exit code 3; anything the service, the checks, or the kept state said is 1.
    private static int Refused(CliHost host, LicenseError error)
    {
        host.Error.WriteLine($"error: {ErrorCodes.ToWireName(error.Code)}: {error.Message}");
        return error.Code == ErrorCode.NetworkError ? ExitCodes.NetworkError : ExitCodes.VerificationFailed;
    }

    private static byte[]? KeptLicense(CliHost host, Common common, ParseResult parse) =>
        new ActivationStore(Folder(parse, common), host.Time).Read(out _)?.License;

    // design §13.7 R13: only the activation's own file is removed, not one an administrator put there by hand.
    private static void RemoveIfKept(string? licensePath, byte[]? kept)
    {
        if (licensePath is null || kept is null || !File.Exists(licensePath))
        {
            return;
        }

        try
        {
            if (File.ReadAllBytes(licensePath).AsSpan().SequenceEqual(kept))
            {
                File.Delete(licensePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"The license file '{licensePath}' cannot be removed: {e.Message}", ExitCodes.VerificationFailed);
        }
    }

    private static Uri ServiceUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? url)
        && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            ? url
            : throw new CliException($"--service must be an https URL, or http on a loopback address: '{text}'.");

    private static string Folder(ParseResult parse, Common common) =>
        new InstallationKeyStore(parse.GetValue(common.Product)!, KeyScope.Machine, parse.GetValue(common.Data)).Directory;

    private static Common AddCommon(Command command)
    {
        var product = new Option<string>("--product") { Description = "The server's product ID (its setting product).", Required = true };
        var data = new Option<string?>("--data")
        {
            Description = "The server's data folder (dataDirectory); default: the machine-wide one of --product.",
        };
        var trust = new Option<string[]>("--trust")
        {
            Description = $"A root public key file to trust. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        };
        var revoked = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        var admin = new Option<string?>("--url") { Description = "The server's administration API. Default: http://127.0.0.1:7071." };
        command.Options.Add(product);
        command.Options.Add(data);
        command.Options.Add(trust);
        command.Options.Add(revoked);
        command.Options.Add(admin);
        return new Common(product, data, trust, revoked, admin);
    }

    private static Option<string> ServiceOption(Command command)
    {
        var service = new Option<string>("--service")
        {
            Description = "The elm-service URL (https; http only on a loopback address).",
            Required = true,
        };
        command.Options.Add(service);
        return service;
    }

    private static Option<string> LicenseOption(Command command)
    {
        var license = new Option<string>("--license") { Description = "The server's license file (its setting license).", Required = true };
        command.Options.Add(license);
        return license;
    }

    private static Option<string?> RemovedLicenseOption(Command command)
    {
        var license = new Option<string?>("--license") { Description = "The server's license file, removed when it is the activation's." };
        command.Options.Add(license);
        return license;
    }

    private sealed record Common(
        Option<string> Product,
        Option<string?> Data,
        Option<string[]> Trust,
        Option<string[]> Revoked,
        Option<string?> Admin
    );
}
