using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using ElasticLM.Client;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm server</c>: the local administration of an on-premises server (design §13.7, §16). It runs on the server's machine,
/// reads the administrator token from the server's data folder, and calls the administration API on the socket file
/// <c>admin.sock</c> in the same folder.
/// </summary>
internal static class ServerCommands
{
    public static Command Create(CliHost host)
    {
        var server = new Command("server", "Administer an on-premises license server on this machine.");
        server.Subcommands.Add(
            Simple(
                host,
                "status",
                "Show the server license, the reclaim window, and the use of each feature.",
                HttpMethod.Get,
                "admin/v1/status",
                PrintStatus
            )
        );
        server.Subcommands.Add(Simple(host, "leases", "List the leases the server holds.", HttpMethod.Get, "admin/v1/leases", PrintLeases));
        server.Subcommands.Add(
            Simple(host, "reload", "Read the server license file again.", HttpMethod.Post, "admin/v1/license/reload", PrintReload)
        );
        server.Subcommands.Add(
            Simple(host, "seal", "Sign the pending lines of the usage log now.", HttpMethod.Post, "admin/v1/log/seal", (_, _) => { })
        );

        var idArgument = new Argument<string>("lease-id") { Description = "The lease ID, as 'elm server leases' shows it." };
        var release = new Command("release", "Release a lease (a borrow too) on the server's side.") { idArgument };
        AddCommon(release, out Option<string?> product, out Option<string?> data, out Option<bool> json);
        release.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                    Call(
                        host,
                        parse.GetValue(product),
                        parse.GetValue(data),
                        HttpMethod.Delete,
                        "admin/v1/leases/" + Uri.EscapeDataString(parse.GetValue(idArgument)!),
                        parse.GetValue(json),
                        (_, writer) => writer.WriteLine("released")
                    )
            )
        );
        server.Subcommands.Add(release);
        ServerLicenseCommands.Add(server, host);
        return server;
    }

    private static Command Simple(
        CliHost host,
        string name,
        string description,
        HttpMethod method,
        string path,
        Action<AdminAnswer, TextWriter> print
    )
    {
        var command = new Command(name, description);
        AddCommon(command, out Option<string?> product, out Option<string?> data, out Option<bool> json);
        command.SetAction(parse =>
            CommonOptions.Run(host, () => Call(host, parse.GetValue(product), parse.GetValue(data), method, path, parse.GetValue(json), print))
        );
        return command;
    }

    private static void AddCommon(Command command, out Option<string?> product, out Option<string?> data, out Option<bool> json)
    {
        product = new Option<string?>("--product") { Description = "The server's product ID, to find its data folder at the default place." };
        data = new Option<string?>("--data")
        {
            Description = "The server's data folder (dataDirectory), which holds the administrator token and the administration socket.",
        }.AsPath();
        json = CommonOptions.Json();
        command.Options.Add(product);
        command.Options.Add(data);
        command.Options.Add(json);
    }

    // One administration call. Exit codes: 0 done, 1 the server refused, 2 bad input or no token, 3 the server is unreachable.
    private static int Call(
        CliHost host,
        string? product,
        string? data,
        HttpMethod method,
        string path,
        bool json,
        Action<AdminAnswer, TextWriter> print
    )
    {
        // design §17.3 (V1c10): the key store's own refusal of an empty product ID does not name the option; an empty --data
        // is a parse error (CliPaths).
        string folder =
            data
            ?? (
                product is not null
                    ? new InstallationKeyStore(CliParse.NotEmpty(product, "--product"), KeyScope.Machine).Directory
                    : throw new CliException("Pass --data or --product.")
            );

        // design §13.7 (V1d2): the length of the socket's path is checked before the token is read, so a folder whose path
        // is too long is reported as that, with or without a token in it.
        (string socketPath, UnixDomainSocketEndPoint endPoint) = ServerAdminApi.SocketOf(folder);
        string token =
            AdminToken.Read(folder)
            ?? throw new CliException($"The administrator token cannot be read in '{folder}'. Run elm server as the server's user, on its machine.");
        if (!File.Exists(socketPath))
        {
            throw new CliException($"The server cannot be reached: the administration socket '{socketPath}' does not exist.", ExitCodes.NetworkError);
        }

        BoundedAnswer answer;
        try
        {
            answer = ServerAdminApi.Send(host, endPoint, method, path, token);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException)
        {
            // The HTTP client adds the request's host to a connection error's sentence, and the host is not where the
            // request goes; the socket's own sentence says what happened.
            string reason = e.InnerException is SocketException socket ? socket.Message : e.Message;
            throw new CliException("The server cannot be reached: " + reason, ExitCodes.NetworkError);
        }

        if (answer is not BoundedAnswer.Whole whole)
        {
            throw new CliException(ServerAdminApi.AnswerTooLarge, ExitCodes.NetworkError);
        }

        // design §13.7 (V1d6): the body is checked and read without a document model, which would take many times its length.
        AdminAnswer value = AdminAnswer.Of(whole);
        if (!value.IsJson)
        {
            throw new CliException($"The server answered {(int)value.Status} with a body that is not JSON.", ExitCodes.NetworkError);
        }

        if (!value.IsSuccess)
        {
            // The same line as the server license commands print for an answer that is not the one asked for.
            host.Error.WriteLine("error: " + ServerAdminApi.Unexpected(value));
            return ExitCodes.VerificationFailed;
        }

        if (json && !value.IsEmpty)
        {
            value.WriteIndented(host.Out);
        }
        else
        {
            print(value, host.Out);
        }

        return ExitCodes.Success;
    }

    private static string Time(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("u", CultureInfo.InvariantCulture);

    private static void PrintStatus(AdminAnswer answer, TextWriter writer)
    {
        AdminStatus status = answer.Read(AdminJsonContext.Default.AdminStatus);
        writer.WriteLine($"product: {status.Product}");
        writer.WriteLine($"license: {status.License} (token {status.LicenseToken})");

        // elm-server leaves licenseExpires out when the server license has no expiry (design §13.7).
        writer.WriteLine("expires: " + (status.LicenseExpires is long expires ? Time(expires) : "never"));
        writer.WriteLine($"key: {status.Key}");
        writer.WriteLine(status.Recovering ? $"reclaiming leases until {Time(status.RecoveryEnds)}" : "serving");
        writer.WriteLine($"leases: {status.Leases}");
        answer.ReadEach(
            "features",
            AdminJsonContext.Default.AdminFeature,
            feature =>
                writer.WriteLine(
                    $"  {feature.Feature}: {feature.InUse} of {feature.Seats} seats in use "
                        + $"({feature.Allowed} with overage, {feature.Borrowed} borrowed)"
                )
        );
    }

    // design §13.7 (V1d6): the leases are read and printed one at a time; the list is the answer that grows with the use.
    private static void PrintLeases(AdminAnswer answer, TextWriter writer) =>
        answer.ReadEach(
            "leases",
            AdminJsonContext.Default.AdminLease,
            lease =>
            {
                string kind = lease.Borrow ? "borrow" : "lease";
                string stopped = lease.Stopped ? " stopped" : string.Empty;
                writer.WriteLine($"{lease.Id} {kind}{stopped} {lease.Feature} x{lease.Seats} key {lease.Key} until {Time(lease.Expires)}");
            }
        );

    private static void PrintReload(AdminAnswer answer, TextWriter writer)
    {
        AdminReload reload = answer.Read(AdminJsonContext.Default.AdminReload);
        writer.WriteLine($"reloaded: license {reload.License}, token {reload.LicenseToken}");
    }
}
