using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Native.Tests;

/// <summary>
/// A relay on a loopback port between a client and <c>elm-service</c> (design §5.3 T17): it passes each request on with its
/// proof and body, keeps the answer of the last successful acquisition, and, when asked, answers the next acquisition with
/// the kept answer without passing the request on. The tests of the C functions use it, since no HTTP handler can be put
/// into a client made through them. The service lists the relay's URL among its public URLs, so the proofs made for that
/// URL verify.
/// </summary>
internal sealed class ReplayingRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _upstream = new();
    private volatile byte[]? _kept;
    private int _replayNext;
    private int _replayed;

    private ReplayingRelay(WebApplication app) => _app = app;

    /// <summary>Gets the URL the relay listens on.</summary>
    public Uri Url { get; private set; } = null!;

    /// <summary>Gets or sets the URL of the service the requests are passed to.</summary>
    public Uri? Target { get; set; }

    /// <summary>Gets the number of acquisitions answered with the kept answer.</summary>
    public int Replayed => Volatile.Read(ref _replayed);

    /// <summary>Starts a relay on a loopback port the OS picks.</summary>
    /// <returns>The relay, which the caller disposes.</returns>
    public static async Task<ReplayingRelay> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        var relay = new ReplayingRelay(app);
        app.Run(relay.AnswerAsync);
        await app.StartAsync(TestContext.Current.CancellationToken);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        relay.Url = new Uri(address + "/");
        return relay;
    }

    /// <summary>Answers the next acquisition with the kept answer, once.</summary>
    public void ReplayToNextAcquisition() => Volatile.Write(ref _replayNext, 1);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _upstream.Dispose();
    }

    private async Task AnswerAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        bool acquisition = request.Method == "POST" && request.Path == "/v1/leases";
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, context.RequestAborted);
        if (acquisition && _kept is byte[] kept && Interlocked.Exchange(ref _replayNext, 0) == 1)
        {
            Interlocked.Increment(ref _replayed);
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(kept, context.RequestAborted);
            return;
        }

        using var passed = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(Target!, request.Path.Value!.TrimStart('/')));
        if (request.Headers.TryGetValue("ELM-Proof", out Microsoft.Extensions.Primitives.StringValues proof))
        {
            passed.Headers.Add("ELM-Proof", proof.ToString());
        }

        if (body.Length > 0)
        {
            passed.Content = new ByteArrayContent(body.ToArray());
            passed.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        using HttpResponseMessage answer = await _upstream.SendAsync(passed, context.RequestAborted);
        byte[] content = await answer.Content.ReadAsByteArrayAsync(context.RequestAborted);
        if (acquisition && answer.IsSuccessStatusCode)
        {
            _kept = content;
        }

        context.Response.StatusCode = (int)answer.StatusCode;
        if (answer.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? nonces))
        {
            context.Response.Headers["ELM-Nonce"] = nonces.Single();
        }

        if (answer.Content.Headers.ContentType is { } type)
        {
            context.Response.ContentType = type.ToString();
        }

        if (content.Length > 0)
        {
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
        }
    }
}
