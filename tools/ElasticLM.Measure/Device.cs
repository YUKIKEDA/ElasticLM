using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Measure;

/// <summary>
/// The answer to one signed request: its status, its error code if any, its body, and how long it took. A request that got
/// no whole answer has the status 0, the code <see cref="Device.NoAnswer"/>, an empty body, and the exception's messages.
/// </summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The error code of a refusal, <see cref="Device.NoAnswer"/>, or <see langword="null"/>.</param>
/// <param name="Body">The answer's body.</param>
/// <param name="Milliseconds">The time from building the proof to the answer.</param>
/// <param name="RetryAfter">The answer's <c>Retry-After</c>, if any.</param>
/// <param name="Error">The messages of the exception of a request without a whole answer, or <see langword="null"/>.</param>
/// <param name="FailedRequest">
/// The method and path of the request without a whole answer: the request itself, or the nonce's
/// <c>GET v1/nonce (before …)</c> when that broke off first; otherwise <see langword="null"/>.
/// </param>
internal sealed record Answer(
    HttpStatusCode Status,
    string? Code,
    string Body,
    double Milliseconds,
    TimeSpan? RetryAfter,
    string? Error = null,
    string? FailedRequest = null
);

/// <summary>
/// One installation key and the requests it signs (design §12.2), one at a time, with the nonce of its last answer, as a
/// client does (design §12.7). The proofs are made by <see cref="ProofBuilder"/>.
/// </summary>
internal sealed class Device : IDisposable
{
    /// <summary>The code of a request that got no whole answer: the client threw <see cref="HttpRequestException"/>.</summary>
    public const string NoAnswer = nameof(HttpRequestException);

    private readonly HttpClient _http;
    private readonly InstallationKey _key;
    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _nonce;

    /// <summary>Initializes a new instance of the <see cref="Device"/> class with a new key in a folder of its own.</summary>
    /// <param name="http">The client, whose base address is the server's.</param>
    /// <param name="product">The product the key is made for.</param>
    /// <param name="folder">The key's folder.</param>
    public Device(HttpClient http, string product, string folder)
    {
        _http = http;
        if (!new InstallationKeyStore(product, KeyScope.User, folder).TryLoadOrCreate(out InstallationKey? key, out LicenseError? error))
        {
            throw new InvalidOperationException("The installation key could not be made: " + error?.Message);
        }

        _key = key!;
    }

    /// <summary>Gets the key's thumbprint, the installation a lease is for.</summary>
    public string Thumbprint => _key.Thumbprint;

    /// <summary>Gets a nonce for the next request now, so that the timed request does not wait for one.</summary>
    public async Task PrepareAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync("v1/nonce");
        response.EnsureSuccessStatusCode();
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _nonce = answer.RootElement.GetProperty("nonce").GetString();
    }

    /// <summary>Deletes a path with a proof and an empty body (a release, design §12.7.3).</summary>
    /// <param name="path">The path.</param>
    public Task<Answer> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, string.Empty, includeKey: false);

    /// <summary>Posts a body with a proof, after the request before it from this key has ended.</summary>
    /// <param name="path">The path.</param>
    /// <param name="body">The JSON body.</param>
    /// <param name="includeKey">Whether the proof carries the public key (a first request).</param>
    public Task<Answer> PostAsync(string path, string body, bool includeKey) => SendAsync(HttpMethod.Post, path, body, includeKey);

    private async Task<Answer> SendAsync(HttpMethod method, string path, string body, bool includeKey)
    {
        await _one.WaitAsync();
        long start = TimeProvider.System.GetTimestamp();
        string request = method.Method + " " + path;
        string sending = request;
        try
        {
            if (_nonce is null)
            {
                sending = $"GET v1/nonce (before {request})";
                await PrepareAsync();
                sending = request;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            start = TimeProvider.System.GetTimestamp();
            string proof = ProofBuilder.Create(
                _key,
                method.Method,
                new Uri(_http.BaseAddress!, path),
                _nonce!,
                bytes,
                includeKey,
                TimeProvider.System
            );
            using var message = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(bytes) };
            message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            message.Headers.Add(ProofBuilder.HeaderName, proof);
            using HttpResponseMessage response = await _http.SendAsync(message);
            string text = await response.Content.ReadAsStringAsync();
            double milliseconds = TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
            _nonce = response.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? values) ? values.First() : null;
            string? code = null;
            if (!response.IsSuccessStatusCode)
            {
                code = text.Length > 0 && JsonDocument.Parse(text).RootElement.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;
                code ??= ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return new Answer(response.StatusCode, code, text, milliseconds, response.Headers.RetryAfter?.Delta);
        }
        catch (HttpRequestException exception)
        {
            // design §20.7 (V1d31, Issue #393): a request whose answer broke off is a failed request of the load, not the end
            // of the run. The server may have taken its nonce, so the next request gets a new one.
            _nonce = null;
            return new Answer(
                0,
                NoAnswer,
                string.Empty,
                TimeProvider.System.GetElapsedTime(start).TotalMilliseconds,
                null,
                Messages(exception),
                sending
            );
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Returns a refusal's code and status, and for a request without a whole answer the request and the messages.</summary>
    /// <param name="answer">The answer.</param>
    public static string Describe(Answer answer) =>
        answer.Code == NoAnswer ? $"{answer.Code} at {answer.FailedRequest}: {answer.Error}" : $"{answer.Code} ({(int)answer.Status})";

    // The messages of an exception and of the exceptions inside it, which name where the request broke off.
    private static string Messages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" ---> ", messages);
    }

    /// <summary>Returns the lease token of a lease answer.</summary>
    /// <param name="body">The answer's body.</param>
    public static string Token(string body)
    {
        using JsonDocument answer = JsonDocument.Parse(body);
        return answer.RootElement.GetProperty("lease").GetString()!;
    }

    /// <summary>Reads the lease ID (<c>lse</c>) and the sequence number of a lease answer's token (design §8.4.3).</summary>
    /// <param name="body">The answer's body.</param>
    public static (string Id, long Seq) Lease(string body)
    {
        using JsonDocument answer = JsonDocument.Parse(body);
        string token = answer.RootElement.GetProperty("lease").GetString()!;
        string payload = token.Split('.')[1];
        using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
        return (claims.RootElement.GetProperty("lse").GetString()!, claims.RootElement.GetProperty("seq").GetInt64());
    }

    public void Dispose()
    {
        _key.Dispose();
        _one.Dispose();
    }
}
