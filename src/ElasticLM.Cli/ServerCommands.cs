using System.CommandLine;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ElasticLM.Client;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm server</c>: the local administration of an on-premises server (design §13.7, §16). It runs on the server's machine,
/// reads the administrator token from the server's data folder, and calls the administration API on a loopback address.
/// </summary>
internal static class ServerCommands
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

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
        AddCommon(release, out Option<string?> url, out Option<string?> product, out Option<string?> data, out Option<bool> json);
        release.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                    Call(
                        host,
                        parse.GetValue(url),
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
        return server;
    }

    private static Command Simple(
        CliHost host,
        string name,
        string description,
        HttpMethod method,
        string path,
        Action<JsonElement, TextWriter> print
    )
    {
        var command = new Command(name, description);
        AddCommon(command, out Option<string?> url, out Option<string?> product, out Option<string?> data, out Option<bool> json);
        command.SetAction(parse =>
            CommonOptions.Run(
                host,
                () => Call(host, parse.GetValue(url), parse.GetValue(product), parse.GetValue(data), method, path, parse.GetValue(json), print)
            )
        );
        return command;
    }

    private static void AddCommon(
        Command command,
        out Option<string?> url,
        out Option<string?> product,
        out Option<string?> data,
        out Option<bool> json
    )
    {
        url = new Option<string?>("--url") { Description = "The administration API. Default: http://127.0.0.1:7071 (admin.listen)." };
        product = new Option<string?>("--product") { Description = "The server's product ID, to find its data folder at the default place." };
        data = new Option<string?>("--data") { Description = "The server's data folder (dataDirectory), which holds the administrator token." };
        json = CommonOptions.Json();
        command.Options.Add(url);
        command.Options.Add(product);
        command.Options.Add(data);
        command.Options.Add(json);
    }

    // One administration call. Exit codes: 0 done, 1 the server refused, 2 bad input or no token, 3 the server is unreachable.
    private static int Call(
        CliHost host,
        string? url,
        string? product,
        string? data,
        HttpMethod method,
        string path,
        bool json,
        Action<JsonElement, TextWriter> print
    )
    {
        string folder =
            data
            ?? (
                product is not null
                    ? new InstallationKeyStore(product, KeyScope.Machine).Directory
                    : throw new CliException("Pass --data or --product.")
            );
        string token =
            AdminToken.Read(folder)
            ?? throw new CliException($"The administrator token cannot be read in '{folder}'. Run elm server as the server's user, on its machine.");
        if (!Uri.TryCreate(url ?? "http://127.0.0.1:7071/", UriKind.Absolute, out Uri? baseUri) || baseUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new CliException($"--url must be an http URL: '{url}'.");
        }

        using var http = new HttpClient { BaseAddress = baseUri, Timeout = RequestTimeout };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        string body;
        try
        {
            response = http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            body = reader.ReadToEnd();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new CliException("The server cannot be reached: " + e.Message, ExitCodes.NetworkError);
        }

        using (response)
        {
            JsonElement value = default;
            if (body.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    value = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    throw new CliException($"The server answered {(int)response.StatusCode} with a body that is not JSON.", ExitCodes.NetworkError);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                string code = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "?" : "?";
                string detail =
                    value.ValueKind == JsonValueKind.Object && value.TryGetProperty("detail", out JsonElement d)
                        ? d.GetString() ?? string.Empty
                        : string.Empty;
                host.Error.WriteLine($"error: {code}: {detail}");
                return ExitCodes.VerificationFailed;
            }

            if (json && value.ValueKind != JsonValueKind.Undefined)
            {
                host.Out.WriteLine(JsonSerializer.Serialize(value, CliJsonContext.Default.JsonElement));
            }
            else
            {
                print(value, host.Out);
            }

            return ExitCodes.Success;
        }
    }

    private static string Time(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("u", CultureInfo.InvariantCulture);

    private static void PrintStatus(JsonElement status, TextWriter writer)
    {
        writer.WriteLine($"product: {status.GetProperty("product").GetString()}");
        writer.WriteLine($"license: {status.GetProperty("license").GetString()} (token {status.GetProperty("licenseToken").GetString()})");
        JsonElement expires = status.GetProperty("licenseExpires");
        writer.WriteLine("expires: " + (expires.ValueKind == JsonValueKind.Number ? Time(expires.GetInt64()) : "never"));
        writer.WriteLine($"key: {status.GetProperty("key").GetString()}");
        writer.WriteLine(
            status.GetProperty("recovering").GetBoolean()
                ? $"reclaiming leases until {Time(status.GetProperty("recoveryEnds").GetInt64())}"
                : "serving"
        );
        writer.WriteLine($"leases: {status.GetProperty("leases").GetInt32()}");
        foreach (JsonElement feature in status.GetProperty("features").EnumerateArray())
        {
            writer.WriteLine(
                $"  {feature.GetProperty("feature").GetString()}: {feature.GetProperty("inUse").GetInt64()} of {feature.GetProperty("seats").GetInt32()} seats in use "
                    + $"({feature.GetProperty("allowed").GetInt64()} with overage, {feature.GetProperty("borrowed").GetInt64()} borrowed)"
            );
        }
    }

    private static void PrintLeases(JsonElement answer, TextWriter writer)
    {
        foreach (JsonElement lease in answer.GetProperty("leases").EnumerateArray())
        {
            string kind = lease.GetProperty("borrow").GetBoolean() ? "borrow" : "lease";
            string stopped = lease.GetProperty("stopped").GetBoolean() ? " stopped" : string.Empty;
            writer.WriteLine(
                $"{lease.GetProperty("id").GetString()} {kind}{stopped} {lease.GetProperty("feature").GetString()} x{lease.GetProperty("seats").GetInt32()} "
                    + $"key {lease.GetProperty("key").GetString()} until {Time(lease.GetProperty("expires").GetInt64())}"
            );
        }
    }

    private static void PrintReload(JsonElement answer, TextWriter writer) =>
        writer.WriteLine($"reloaded: license {answer.GetProperty("license").GetString()}, token {answer.GetProperty("licenseToken").GetString()}");
}
