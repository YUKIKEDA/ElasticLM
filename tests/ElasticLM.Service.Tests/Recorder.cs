using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// A request a client sent through a <see cref="Recorder"/>.
/// </summary>
/// <param name="Method">The method.</param>
/// <param name="Path">The path.</param>
/// <param name="Body">The body, or an empty text.</param>
/// <param name="ProofId">The <c>jti</c> of the request's proof, or <see langword="null"/> without a proof.</param>
internal sealed record SentRequest(string Method, string Path, string Body, string? ProofId);

/// <summary>
/// A successful answer to an acquisition or a renewal, as the server gave it: what a relay on the path can keep and hand
/// out again (design §5.3 T17).
/// </summary>
/// <param name="Path">The path of the request it answered.</param>
/// <param name="Status">The status.</param>
/// <param name="Body">The body: the lease token and, from <c>elm-service</c>, the certificates.</param>
/// <param name="ServerLicense">The <c>ELM-Server-License</c> header of an on-premises server, or <see langword="null"/>.</param>
internal sealed record KeptAnswer(string Path, HttpStatusCode Status, string Body, string? ServerLicense);

/// <summary>
/// Sits between a client and the network (<c>LicenseClientOptions.WrapHandler</c>), for one client or for several that
/// share it: records each request, can make the renewals fail as if the network were down, can lose the answer of a
/// renewal the server handled, can rewrite the answer to an acquisition, keeps the successful lease answers, can
/// answer the next acquisition or renewal with an answer it is given, as a relay that replays genuine answers does, and
/// can answer the next renewal with a body over the client's limit.
/// </summary>
internal sealed class Recorder
{
    private const string OversizeAfter = "after";
    private const string OversizeInstead = "instead";

    private readonly List<SentRequest> _sent = [];
    private readonly List<KeptAnswer> _answers = [];
    private volatile bool _failRenewals;
    private volatile bool _loseNextRenewalAnswer;
    private KeptAnswer? _acquisitionAnswer;
    private KeptAnswer? _renewalAnswer;
    private volatile string _oversizeRenewal = string.Empty;
    private int _replayed;

    /// <summary>Gets or sets a value indicating whether renewals throw <see cref="HttpRequestException"/> before they are sent.</summary>
    public bool FailRenewals
    {
        get => _failRenewals;
        set => _failRenewals = value;
    }

    /// <summary>Gets or sets a value indicating whether the answer of the next renewal is thrown away after the server
    /// handled it, as <see cref="HttpRequestException"/>.</summary>
    public bool LoseNextRenewalAnswer
    {
        get => _loseNextRenewalAnswer;
        set => _loseNextRenewalAnswer = value;
    }

    /// <summary>Gets or sets the rewrite of a successful acquisition's answer, or <see langword="null"/>.</summary>
    public Func<string, string>? Rewrite { get; set; }

    /// <summary>Gets or sets the rewrite of the next successful renewal's answer, applied once after the server handled the
    /// renewal, or <see langword="null"/>.</summary>
    public Func<string, string>? RewriteNextRenewal { get; set; }

    /// <summary>Gets or sets a value indicating whether a request that gets a replayed answer is first passed to the server,
    /// whose own answer is then thrown away. Otherwise the server never sees the request.</summary>
    public bool PassReplacedOn { get; set; }

    /// <summary>Gets the number of requests answered with a replayed answer.</summary>
    public int Replayed => Volatile.Read(ref _replayed);

    /// <summary>Returns a handler over the client's own handler.</summary>
    public HttpMessageHandler Over(HttpMessageHandler inner) => new Handler(this) { InnerHandler = inner };

    /// <summary>Answers the next acquisition or borrow with <paramref name="answer"/>, once.</summary>
    public void ReplayToNextAcquisition(KeptAnswer answer) => Volatile.Write(ref _acquisitionAnswer, answer);

    /// <summary>Answers the next renewal with <paramref name="answer"/>, once.</summary>
    public void ReplayToNextRenewal(KeptAnswer answer) => Volatile.Write(ref _renewalAnswer, answer);

    /// <summary>
    /// Answers the next renewal, once, with a body of 256 KiB and a byte, which is over the client's limit (design §8.6).
    /// </summary>
    /// <param name="afterTheServer"><see langword="true"/> to pass the renewal to the server first and keep the status and
    /// headers of its answer; <see langword="false"/> to answer with status 503 without the server seeing the renewal.</param>
    public void OversizeNextRenewal(bool afterTheServer) => _oversizeRenewal = afterTheServer ? OversizeAfter : OversizeInstead;

    /// <summary>Returns the requests sent so far: method, path, and body.</summary>
    public (string Method, string Path, string Body)[] Sent()
    {
        lock (_sent)
        {
            return [.. _sent.Select(r => (r.Method, r.Path, r.Body))];
        }
    }

    /// <summary>Returns the requests sent so far, with the <c>jti</c> of their proofs.</summary>
    public SentRequest[] Requests()
    {
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    /// <summary>Returns the successful answers the server gave to acquisitions and renewals so far, in order. An answer that
    /// was replaced by a replayed one is not among them.</summary>
    public KeptAnswer[] Answers()
    {
        lock (_answers)
        {
            return [.. _answers];
        }
    }

    /// <summary>Returns a lease answer whose token has one character of its signature changed.</summary>
    public static string WithBrokenSignature(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        string token = document.RootElement.GetProperty("lease").GetString()!;
        string changed = token[..^4] + (token[^4] == 'A' ? 'B' : 'A') + token[^3..];
        return answer.Replace(token, changed, StringComparison.Ordinal);
    }

    private static string? ProofIdOf(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("ELM-Proof", out IEnumerable<string>? proofs))
        {
            return null;
        }

        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(proofs.Single().Split('.')[1]));
        return payload.RootElement.GetProperty("jti").GetString();
    }

    private sealed class Handler(Recorder recorder) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (recorder._sent)
            {
                recorder._sent.Add(new SentRequest(request.Method.Method, path, body, ProofIdOf(request)));
            }

            bool renewal = request.Method == HttpMethod.Post && path.EndsWith("/renew", StringComparison.Ordinal);
            bool acquisition = request.Method == HttpMethod.Post && path == "/v1/leases";
            if (recorder.FailRenewals && renewal)
            {
                throw new HttpRequestException("Renewals are made to fail.");
            }

            string oversize = renewal ? Interlocked.Exchange(ref recorder._oversizeRenewal, string.Empty) : string.Empty;
            if (oversize == OversizeInstead)
            {
                return Oversized(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
            }

            if (oversize == OversizeAfter)
            {
                return Oversized(await base.SendAsync(request, cancellationToken));
            }

            KeptAnswer? replay =
                acquisition ? Interlocked.Exchange(ref recorder._acquisitionAnswer, null)
                : renewal ? Interlocked.Exchange(ref recorder._renewalAnswer, null)
                : null;
            if (replay is not null && !recorder.PassReplacedOn)
            {
                return Replayed(new HttpResponseMessage(replay.Status) { RequestMessage = request }, replay);
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (replay is not null)
            {
                return Replayed(response, replay);
            }

            if (renewal && recorder.LoseNextRenewalAnswer)
            {
                recorder.LoseNextRenewalAnswer = false;
                response.Dispose();
                throw new HttpRequestException("The answer was lost on the way.");
            }

            if ((acquisition || renewal) && response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                string? serverLicense = response.Headers.TryGetValues("ELM-Server-License", out IEnumerable<string>? values) ? values.Single() : null;
                lock (recorder._answers)
                {
                    recorder._answers.Add(new KeptAnswer(path, response.StatusCode, text, serverLicense));
                }

                if (acquisition && recorder.Rewrite is Func<string, string> rewrite)
                {
                    response.Content = new StringContent(rewrite(text), Encoding.UTF8, "application/json");
                }

                if (renewal && recorder.RewriteNextRenewal is Func<string, string> once)
                {
                    recorder.RewriteNextRenewal = null;
                    response.Content = new StringContent(once(text), Encoding.UTF8, "application/json");
                }
            }

            return response;
        }

        // The answer with a body of 256 KiB and a byte, all spaces, in place of its own; its status and headers stay.
        private static HttpResponseMessage Oversized(HttpResponseMessage response)
        {
            response.Content?.Dispose();
            response.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(new string(' ', (256 * 1024) + 1)));
            return response;
        }

        // The replayed answer in place of the server's: its status, body, and server license header. The other headers of an
        // answer the server gave, the next nonce among them, stay.
        private HttpResponseMessage Replayed(HttpResponseMessage response, KeptAnswer replay)
        {
            response.StatusCode = replay.Status;
            response.Content?.Dispose();
            response.Content = new StringContent(replay.Body, Encoding.UTF8, "application/json");
            response.Headers.Remove("ELM-Server-License");
            if (replay.ServerLicense is string serverLicense)
            {
                response.Headers.TryAddWithoutValidation("ELM-Server-License", serverLicense);
            }

            Interlocked.Increment(ref recorder._replayed);
            return response;
        }
    }
}
