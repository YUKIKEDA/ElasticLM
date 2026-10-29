using System.Text;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Sits between a client and the network (<c>LicenseClientOptions.WrapHandler</c>): records each request, can make the
/// renewals fail as if the network were down, and can rewrite the answer to an acquisition.
/// </summary>
internal sealed class Recorder
{
    private readonly List<(string Method, string Path, string Body)> _sent = [];
    private volatile bool _failRenewals;

    /// <summary>Gets or sets a value indicating whether renewals throw <see cref="HttpRequestException"/> before they are sent.</summary>
    public bool FailRenewals
    {
        get => _failRenewals;
        set => _failRenewals = value;
    }

    /// <summary>Gets or sets the rewrite of a successful acquisition's answer, or <see langword="null"/>.</summary>
    public Func<string, string>? Rewrite { get; set; }

    /// <summary>Returns a handler over the client's own handler.</summary>
    public HttpMessageHandler Over(HttpMessageHandler inner) => new Handler(this) { InnerHandler = inner };

    /// <summary>Returns the requests sent so far: method, path, and body.</summary>
    public (string Method, string Path, string Body)[] Sent()
    {
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    private sealed class Handler(Recorder recorder) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (recorder._sent)
            {
                recorder._sent.Add((request.Method.Method, path, body));
            }

            if (recorder.FailRenewals && path.EndsWith("/renew", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Renewals are made to fail.");
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (recorder.Rewrite is Func<string, string> rewrite && path == "/v1/leases" && response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content = new StringContent(rewrite(text), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
