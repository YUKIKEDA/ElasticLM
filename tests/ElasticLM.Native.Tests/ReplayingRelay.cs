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
/// proof and body, keeps the answer of the last successful POST to the paths it is started for (lease acquisitions by
/// default), and, when asked, answers the next such POST with the kept answer and its status without passing the request
/// on. The tests of the C functions and of elm-server's own refresh use it, since no HTTP handler can be put into a client
/// made through them. The service lists the relay's URL among its public URLs, so the proofs made for that URL verify.
/// </summary>
internal sealed class ReplayingRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _upstream = new();
    private readonly Func<string, bool> _keeps;
    private volatile KeptReply? _kept;
    private int _replayNext;
    private int _replayed;

    private ReplayingRelay(WebApplication app, Func<string, bool> keeps)
    {
        _app = app;
        _keeps = keeps;
    }

    /// <summary>Gets the URL the relay listens on.</summary>
    public Uri Url { get; private set; } = null!;

    /// <summary>Gets or sets the URL of the service the requests are passed to.</summary>
    public Uri? Target { get; set; }

    /// <summary>Gets the number of requests answered with the kept answer.</summary>
    public int Replayed => Volatile.Read(ref _replayed);

    /// <summary>Starts a relay on a loopback port the OS picks.</summary>
    /// <param name="keeps">Whether the answers to POSTs to a path are kept and replayed; <see langword="null"/> for lease
    /// acquisitions (<c>/v1/leases</c>).</param>
    /// <returns>The relay, which the caller disposes.</returns>
    public static async Task<ReplayingRelay> StartAsync(Func<string, bool>? keeps = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        var relay = new ReplayingRelay(app, keeps ?? (path => path == "/v1/leases"));
        app.Run(relay.AnswerAsync);
        await app.StartAsync(TestContext.Current.CancellationToken);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        relay.Url = new Uri(address + "/");
        return relay;
    }

    /// <summary>Answers the next POST to a kept path with the kept answer, once.</summary>
    public void ReplayToNext() => Volatile.Write(ref _replayNext, 1);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _upstream.Dispose();
    }

    private async Task AnswerAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        bool kept = request.Method == "POST" && _keeps(request.Path.Value ?? string.Empty);
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, context.RequestAborted);
        if (kept && _kept is KeptReply reply && Interlocked.Exchange(ref _replayNext, 0) == 1)
        {
            Interlocked.Increment(ref _replayed);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(reply.Body, context.RequestAborted);
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
        if (kept && answer.IsSuccessStatusCode)
        {
            _kept = new KeptReply((int)answer.StatusCode, content);
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

    // A kept answer: its status and body.
    private sealed record KeptReply(int Status, byte[] Body);
}
