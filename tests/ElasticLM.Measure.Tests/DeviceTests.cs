using System.Buffers.Text;
using System.Net;
using System.Text.Json;

namespace ElasticLM.Measure.Tests;

/// <summary>
/// <see cref="Device"/>'s requests whose answer breaks off (design §20.7, V1d31, Issue #393): they come back as an answer
/// with the code <see cref="Device.NoAnswer"/> instead of an exception, name the request that broke off, and leave the device
/// to fetch a new nonce.
/// </summary>
public sealed class DeviceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elm-measure-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A request whose answer's body breaks off gives <see cref="Device.NoAnswer"/>, and the next request fetches a new nonce.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A handler that answers <c>GET v1/nonce</c> with the nonces n1, n2, … and the first <c>POST v1/leases</c> with a body
    ///   that throws <see cref="HttpIOException"/> (<see cref="HttpRequestError.ResponseEnded"/>) while it is read, as a
    ///   connection cut in the middle of the answer does; the second <c>POST</c> with 200 and an <c>ELM-Nonce</c>.
    ///
    /// Steps:
    /// - Post twice.
    ///
    /// Expected:
    /// - The first answer has the status 0, the code <c>HttpRequestException</c>, an empty body, the failed request
    ///   <c>POST v1/leases</c>, and the messages of the exception and of the one inside it.
    /// - The second post is preceded by a second <c>GET v1/nonce</c>, and the two proofs carry n1 and n2.
    /// </remarks>
    [Fact]
    public async Task SendAsync_AnswerBreaksOff_GivesNoAnswerAndFetchesNewNonce()
    {
        var handler = new Handler(post => post == 1 ? Handler.Broken() : Handler.Ok());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        using var device = new Device(http, "acme-cad", _folder);

        Answer first = await device.PostAsync("v1/leases", "{}", includeKey: true);
        Answer second = await device.PostAsync("v1/leases", "{}", includeKey: false);

        Assert.Equal((HttpStatusCode)0, first.Status);
        Assert.Equal(Device.NoAnswer, first.Code);
        Assert.Equal("HttpRequestException", first.Code);
        Assert.Equal(string.Empty, first.Body);
        Assert.Equal("POST v1/leases", first.FailedRequest);
        Assert.Equal("Error while copying content to a stream. ---> The response ended prematurely. (ResponseEnded)", first.Error);
        Assert.Null(second.Code);
        Assert.Equal(["GET v1/nonce", "POST v1/leases", "GET v1/nonce", "POST v1/leases"], handler.Requests);
        Assert.Equal(["n1", "n2"], handler.ProofNonces);
    }

    /// <summary>A nonce fetch that breaks off names the nonce request and the request it was for.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A handler whose <c>GET v1/nonce</c> throws <see cref="HttpRequestException"/> with the message "refused".
    ///
    /// Steps:
    /// - Post to <c>v1/activations</c>.
    ///
    /// Expected:
    /// - The code is <c>HttpRequestException</c>, the failed request is <c>GET v1/nonce (before POST v1/activations)</c>, the
    ///   error is "refused", and no <c>POST</c> was sent.
    /// - <see cref="Device.Describe"/> names the code, the failed request, and the error.
    /// </remarks>
    [Fact]
    public async Task SendAsync_NonceFetchBreaksOff_NamesNonceRequest()
    {
        var handler = new Handler(_ => Handler.Ok()) { NonceFails = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        using var device = new Device(http, "acme-cad", _folder);

        Answer answer = await device.PostAsync("v1/activations", "{}", includeKey: true);

        Assert.Equal(Device.NoAnswer, answer.Code);
        Assert.Equal("GET v1/nonce (before POST v1/activations)", answer.FailedRequest);
        Assert.Equal("refused", answer.Error);
        Assert.Equal(["GET v1/nonce"], handler.Requests);
        Assert.Equal("HttpRequestException at GET v1/nonce (before POST v1/activations): refused", Device.Describe(answer));
    }

    /// <summary>A refusal keeps its code and status, has no failed request, and keeps the nonce of its answer.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A handler that answers the first <c>POST</c> with 409 <c>{"code":"ELM_CLONE_DETECTED"}</c> and an <c>ELM-Nonce</c>.
    ///
    /// Steps:
    /// - Post twice.
    ///
    /// Expected:
    /// - The code is <c>ELM_CLONE_DETECTED</c>, with no failed request and no error; <see cref="Device.Describe"/> gives
    ///   <c>ELM_CLONE_DETECTED (409)</c>.
    /// - Only one <c>GET v1/nonce</c> was sent: the second post used the nonce of the refusal.
    /// </remarks>
    [Fact]
    public async Task SendAsync_Refused_KeepsCodeAndNonce()
    {
        var handler = new Handler(post => post == 1 ? Handler.Refused() : Handler.Ok());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        using var device = new Device(http, "acme-cad", _folder);

        Answer first = await device.PostAsync("v1/leases", "{}", includeKey: true);
        await device.PostAsync("v1/leases", "{}", includeKey: false);

        Assert.Equal("ELM_CLONE_DETECTED", first.Code);
        Assert.Null(first.FailedRequest);
        Assert.Null(first.Error);
        Assert.Equal("ELM_CLONE_DETECTED (409)", Device.Describe(first));
        Assert.Equal(["GET v1/nonce", "POST v1/leases", "POST v1/leases"], handler.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // Answers the device's requests in memory: nonces n1, n2, … for GET v1/nonce, and the answer the test picks for each POST.
    private sealed class Handler(Func<int, HttpResponseMessage> post) : HttpMessageHandler
    {
        private int _nonces;
        private int _posts;

        public List<string> Requests { get; } = [];

        public List<string> ProofNonces { get; } = [];

        public bool NonceFails { get; init; }

        public static HttpResponseMessage Ok() => WithNonce(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        public static HttpResponseMessage Refused() =>
            WithNonce(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"code":"ELM_CLONE_DETECTED"}""") });

        public static HttpResponseMessage Broken() => new(HttpStatusCode.OK) { Content = new BrokenContent() };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add($"{request.Method.Method} {path}");
            if (path == "v1/nonce")
            {
                if (NonceFails)
                {
                    throw new HttpRequestException("refused");
                }

                string nonce = "n" + ++_nonces;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"nonce":"{{nonce}}"}""") });
            }

            string proof = request.Headers.GetValues("ELM-Proof").Single();
            using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(proof.Split('.')[1]));
            ProofNonces.Add(claims.RootElement.GetProperty("nonce").GetString()!);
            return Task.FromResult(post(++_posts));
        }

        private static HttpResponseMessage WithNonce(HttpResponseMessage response)
        {
            response.Headers.Add("ELM-Nonce", "from-answer");
            return response;
        }
    }

    // A body that ends before its end, as a connection cut in the middle of the answer does.
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
