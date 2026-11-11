using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

    // How long a call to the server's local administration API waits for the whole answer.
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
                    CheckSocketPath(parse, common);
                    Uri url = ServiceUrl(parse.GetValue(service)!);
                    string? licenseKey = activate ? CliParse.NotEmpty(parse.GetValue(key)!, "<license-key>") : null;
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
        var answer = new Argument<string>("answer") { Description = "The service's answer file (.elmresp)." }.AsPath();
        var command = new Command("import", "Import the service's answer to the waiting request and write the license file.") { answer };
        Common common = AddCommon(command);
        Option<string> license = LicenseOption(command);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    CheckSocketPath(parse, common);
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
        var output = new Option<string>("--out") { Description = "Where to write the deactivation proof (.elmdeact).", Required = true }.AsPath();
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
                {
                    CheckSocketPath(parse, common);
                    return WithClient(
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
                    );
                }
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
        var output = new Option<string>("--out") { Description = "Where to write the request file.", Required = true }.AsPath();
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
                    string? licenseKey = activate ? CliParse.NotEmpty(parse.GetValue(key)!, "--key") : null;
                    if (Status(host, common, parse) is true)
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
                            OfflineRequestResult result = licenseKey is not null
                                ? client.CreateActivationRequestAsync(licenseKey, parse.GetValue(renew)).GetAwaiter().GetResult()
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
        string product = Product(parse, common);
        if (!new InstallationKeyStore(product, KeyScope.Machine, parse.GetValue(common.Data)).HasKey)
        {
            // design §13.7: the server makes its key at its first start; a key made here could be the wrong folder's (R5).
            throw new CliException("The server's installation key does not exist; start elm-server once to make it.");
        }

        var options = new ActivationClientOptions
        {
            ProductId = product,
            ServiceUrl = service,
            Trust = TrustKeys.Store(host, parse.GetValue(common.Trust), parse.GetValue(common.Revoked)),
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
        const string Written = "The license file is written; the server did not confirm a reload.";
        (HttpStatusCode Status, JsonElement Answer)? sent;
        try
        {
            sent = Send(host, common, parse, HttpMethod.Post, "admin/v1/license/reload");
        }
        catch (CliException e) when (e.ExitCode == ExitCodes.NetworkError)
        {
            // Something took the connection to the socket and gave no answer that can be read: the file is written all the same.
            host.Error.WriteLine($"error: {e.Message} {Written}");
            return ExitCodes.NetworkError;
        }

        if (sent is not (HttpStatusCode status, JsonElement answer))
        {
            host.Out.WriteLine("The server is not running; it reads the license when it starts.");
            return ExitCodes.Success;
        }

        // 409 with an error code is the server's refusal of the file. Any other answer that is not the reload's own says
        // nothing about the file, so the line names the answer and says that the file is written.
        if (status == HttpStatusCode.Conflict && Text(answer, "code") is string refused)
        {
            host.Error.WriteLine($"error: the server kept its license: {refused}: {Text(answer, "detail")}");
            return ExitCodes.VerificationFailed;
        }

        if (!IsSuccess(status) || Text(answer, "licenseToken") is not string loaded)
        {
            host.Error.WriteLine($"error: {ServerAdminApi.Unexpected(status, answer)} {Written}");
            return ExitCodes.VerificationFailed;
        }

        if (loaded != tokenId)
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

    // Whether the running server refreshes its license on its own (the status's autoRefresh), or null when no server runs
    // (no administrator token that can be read, no socket file, or a socket file that refuses the connection). An answer
    // that is not a status (a wrong token, another program on the socket), and no answer from something that listens, do
    // not show that no server runs, so the command stops: going on would skip the checks that a running server calls for
    // (R8, R9).
    private static bool? Status(CliHost host, Common common, ParseResult parse)
    {
        if (Send(host, common, parse, HttpMethod.Get, "admin/v1/status") is not (HttpStatusCode status, JsonElement answer))
        {
            return null;
        }

        return
            IsSuccess(status)
            && answer.ValueKind == JsonValueKind.Object
            && answer.TryGetProperty("autoRefresh", out JsonElement auto)
            && auto.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? auto.GetBoolean()
            : throw new CliException(ServerAdminApi.Unexpected(status, answer), ExitCodes.VerificationFailed);
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    // design §13.7 (V1d2): the commands that do not ask the server before their own work check the length of the socket's
    // path first, as the others do through Send, so that a folder whose path is too long is never taken for one whose
    // server does not run.
    private static void CheckSocketPath(ParseResult parse, Common common) => _ = ServerAdminApi.SocketOf(Folder(parse, common));

    // One call to the local administration API, on the socket file of the folder that holds the token; null when the server
    // does not run: the folder has no administrator token that can be read, it has no socket file, or the connection to
    // the socket file is refused (a file that a killed server left). The server's lock file is not looked at. Every other
    // connection error, and something that listens there and gives no whole answer in time, stops the call with a network
    // error: a server that is slow or stopping still runs.
    private static (HttpStatusCode Status, JsonElement Answer)? Send(CliHost host, Common common, ParseResult parse, HttpMethod method, string path)
    {
        string folder = Folder(parse, common);
        (string socketPath, UnixDomainSocketEndPoint endPoint) = ServerAdminApi.SocketOf(folder);

        // Without the file, a connection is refused on Windows (measured on Windows 11). On Linux and macOS it fails with
        // ENOENT, which .NET maps to SocketError.AddressNotAvailable, not ConnectionRefused (SocketErrorPal.Unix.cs,
        // dotnet/runtime release/10.0), and would read as a network error below. So the file is looked for first.
        if (AdminToken.Read(folder) is not string token || !File.Exists(socketPath))
        {
            return null;
        }

        // The wait is counted on the host's clock, not on the HTTP client's own timer.
        using HttpClient http = ServerAdminApi.CreateClient(host, endPoint, Timeout.InfiniteTimeSpan);
        using var waited = new CancellationTokenSource(RequestTimeout, host.Time);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using HttpResponseMessage response = http.Send(request, waited.Token);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            string body = reader.ReadToEnd();
            try
            {
                using var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                return (response.StatusCode, document.RootElement.Clone());
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                // Another program on the socket answers an error with a page; the status code is all there is to tell.
                return (response.StatusCode, default);
            }
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return null;
        }
        catch (HttpRequestException)
        {
            // Every other failure to get an HTTP answer: something took the connection, so a server may run there.
            throw new CliException("The server's administration API gave no whole answer.", ExitCodes.NetworkError);
        }
        catch (OperationCanceledException)
        {
            throw new CliException("The server's administration API did not answer in time.", ExitCodes.NetworkError);
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
        new InstallationKeyStore(Product(parse, common), KeyScope.Machine, parse.GetValue(common.Data)).Directory;

    // design §17.3 (V1c10): the key store's own refusal of an empty product ID does not name the option; an empty --data is a
    // parse error (CliPaths).
    private static string Product(ParseResult parse, Common common) => CliParse.NotEmpty(parse.GetValue(common.Product)!, "--product");

    private static Common AddCommon(Command command)
    {
        var product = new Option<string>("--product") { Description = "The server's product ID (its setting product).", Required = true };
        var data = new Option<string?>("--data")
        {
            Description = "The server's data folder (dataDirectory); default: the machine-wide one of --product.",
        }.AsPath();
        var trust = new Option<string[]>("--trust")
        {
            Description = $"A root public key file to trust. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        }.AsPath();
        var revoked = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        command.Options.Add(product);
        command.Options.Add(data);
        command.Options.Add(trust);
        command.Options.Add(revoked);
        return new Common(product, data, trust, revoked);
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
        var license = new Option<string>("--license") { Description = "The server's license file (its setting license).", Required = true }.AsPath();
        command.Options.Add(license);
        return license;
    }

    private static Option<string?> RemovedLicenseOption(Command command)
    {
        var license = new Option<string?>("--license") { Description = "The server's license file, removed when it is the activation's." }.AsPath();
        command.Options.Add(license);
        return license;
    }

    private sealed record Common(Option<string> Product, Option<string?> Data, Option<string[]> Trust, Option<string[]> Revoked);
}
