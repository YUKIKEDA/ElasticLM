using System.Buffers.Text;
using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm offline process</c> (design §12.8.4, §16): passes an offline request file to <c>elm-service</c> and writes the
/// answer the device imports. With <c>ELM_API_KEY</c>, or a saved <c>elm admin login</c> for the same origin (M5a), it uses the
/// administration entry, otherwise the public one.
/// </summary>
internal static class OfflineCommands
{
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxAnswerBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static Command Create(CliHost host)
    {
        var url = new Option<string>("--url") { Description = "The elm-service URL (https; http only on a loopback address).", Required = true };
        var input = new Option<string>("--in") { Description = "The request file (.elmreq) or deactivation proof (.elmdeact).", Required = true };
        var output = new Option<string?>("--out") { Description = "Where to write the answer (.elmresp) of an activation or refresh request." };
        Option<bool> force = CommonOptions.Force();
        Option<bool> json = CommonOptions.Json();
        var process = new Command("process", "Send an offline activation, refresh, or deactivation file to the service.")
        {
            url,
            input,
            output,
            force,
            json,
        };
        process.SetAction(parse =>
            CommonOptions.Run(
                host,
                () => Process(host, parse.GetValue(url)!, parse.GetValue(input)!, parse.GetValue(output), parse.GetValue(force), parse.GetValue(json))
            )
        );
        var offline = new Command("offline", "Pass offline request files to an ElasticLM service.");
        offline.Subcommands.Add(process);
        return offline;
    }

    // Exit codes: 0 done, 1 the service refused, 2 bad input, 3 the service is unreachable (design §16).
    private static int Process(CliHost host, string url, string input, string? output, bool force, bool json)
    {
        Uri service = ServiceUrl(url);
        string text = CliFiles.ReadText(input, MaxRequestBytes).Trim();
        bool deactivation = TypeOf(text) switch
        {
            "elm-actreq+jws" => false,
            "elm-deact+jws" => true,
            _ => throw new CliException($"The file '{input}' is not an offline request (elm-actreq+jws or elm-deact+jws)."),
        };
        if (deactivation && output is not null)
        {
            throw new CliException("A deactivation proof gets no answer file; leave out --out.");
        }

        if (!deactivation && output is null)
        {
            throw new CliException("An activation or refresh request needs --out for its answer.");
        }

        if (output is not null)
        {
            CliFiles.EnsureWritable(output, force);
        }

        // design §12.8.4, §16: without credentials the public entry, as before M5a.
        string? apiKey = AdminCredentials.ApiKey(host) ?? AdminCredentials.AccessToken(host, service);

        // design §12.1: a redirect would send the request file, the license key in it, to another place; a 3xx is an error.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(service, apiKey is null ? "v1/offline" : "v1/admin/offline"))
        {
            Content = new StringContent(text, Encoding.ASCII, "application/jose"),
        };
        if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        int status;
        byte[] body;
        try
        {
            using HttpResponseMessage response = http.Send(request);
            status = (int)response.StatusCode;
            body = ReadLimited(response.Content.ReadAsStream());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new CliException("The service cannot be reached: " + e.Message, ExitCodes.NetworkError);
        }

        if (status is < 200 or >= 300)
        {
            (string code, string detail) = Problem(body);
            host.Error.WriteLine($"error: {code}: {detail}");
            return ExitCodes.VerificationFailed;
        }

        if (deactivation)
        {
            host.Out.WriteLine(json ? Json(new OfflineOutput(true, null, null, null, null)) : "deactivated");
            return ExitCodes.Success;
        }

        (string activationId, string licenseId, long seq) = Issued(body);
        CliFiles.Write(output!, body, force, secret: false);
        host.Out.WriteLine(
            json
                ? Json(new OfflineOutput(false, activationId, licenseId, seq, output))
                : $"activation {activationId}, license {licenseId}, seq {seq}: {output}"
        );
        return ExitCodes.Success;
    }

    private static string Json(OfflineOutput output) => JsonSerializer.Serialize(output, CliJsonContext.Default.OfflineOutput);

    // design §12.8.4: https, or http on a loopback address only, as ActivationClient takes it.
    private static Uri ServiceUrl(string url)
    {
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? service)
            || !(service.Scheme == Uri.UriSchemeHttps || (service.Scheme == Uri.UriSchemeHttp && service.IsLoopback))
        )
        {
            throw new CliException($"--url must be an https URL, or http on a loopback address: '{url}'.");
        }

        return service.AbsoluteUri.EndsWith('/') ? service : new Uri(service.AbsoluteUri + "/");
    }

    // The typ of a compact JWS's header, read only to choose how to send it; the service checks the signature.
    private static string? TypeOf(string text)
    {
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        try
        {
            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(text.AsSpan(0, dot)));
            return
                header.RootElement.ValueKind == JsonValueKind.Object
                && header.RootElement.TryGetProperty("typ", out JsonElement typ)
                && typ.ValueKind == JsonValueKind.String
                ? typ.GetString()
                : null;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    private static byte[] ReadLimited(Stream stream)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxAnswerBytes)
            {
                throw new CliException($"The service's answer is larger than {MaxAnswerBytes} bytes.", ExitCodes.NetworkError);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static (string Code, string Detail) Problem(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string code = root.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "?";
            string detail = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;
            return (code, detail);
        }
        catch (JsonException)
        {
            return ("?", string.Empty);
        }
    }

    // The issued answer {activationId, license}; the license ID and seq are read from the token without verifying it, only to
    // show them: the device verifies the file when it imports it (design §12.8.1 step 4).
    private static (string ActivationId, string LicenseId, long Seq) Issued(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string activationId = root.GetProperty("activationId").GetString()!;
            string token = root.GetProperty("license").GetProperty("license").GetString()!;
            string[] parts = token.Split('.');
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            return (activationId, payload.RootElement.GetProperty("lic").GetString()!, payload.RootElement.GetProperty("seq").GetInt64());
        }
        catch (Exception e)
            when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        {
            throw new CliException("The service's answer is not an issued license.", ExitCodes.NetworkError);
        }
    }
}
