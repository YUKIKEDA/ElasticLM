using System.Net;

namespace ElasticLM.Service.Tests;

/// <summary>
/// A browser for the admin UI's sign-in tests (design §15.3): it keeps cookies per host and port, sends <c>Secure</c> cookies
/// over loopback http (as Chrome and Firefox do for <c>Secure</c> cookies there), and follows no redirect, so each step of
/// the sign-in can be read. <see cref="CookieContainer"/> does not send <c>Secure</c> cookies over http, so the cookies are
/// kept here.
/// </summary>
internal sealed class WebBrowser : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Dictionary<string, Dictionary<string, string>> _jars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the Set-Cookie headers of the last answer.</summary>
    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    /// <summary>Returns the cookies kept for a URL's host and port.</summary>
    public Dictionary<string, string> Cookies(Uri url)
    {
        string key = url.GetLeftPart(UriPartial.Authority);
        if (!_jars.TryGetValue(key, out Dictionary<string, string>? jar))
        {
            jar = new Dictionary<string, string>(StringComparer.Ordinal);
            _jars[key] = jar;
        }

        return jar;
    }

    /// <summary>Sends a request with the kept cookies and keeps the cookies of the answer.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, Action<HttpRequestMessage>? change = null)
    {
        using var request = new HttpRequestMessage(method, url);
        Dictionary<string, string> jar = Cookies(url);
        if (jar.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(c => c.Key + "=" + c.Value)));
        }

        change?.Invoke(request);
        HttpResponseMessage answer = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        LastSetCookies = answer.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? set) ? [.. set] : [];
        foreach (string cookie in LastSetCookies)
        {
            string first = cookie.Split(';')[0];
            int equals = first.IndexOf('=', StringComparison.Ordinal);
            string name = first[..equals];
            string value = first[(equals + 1)..];
            bool removed =
                cookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
                || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
            if (removed || value.Length == 0)
            {
                _ = jar.Remove(name);
            }
            else
            {
                jar[name] = value;
            }
        }

        return answer;
    }

    /// <summary>Follows one redirect: the Location of an answer, resolved against the request's URL.</summary>
    public Task<HttpResponseMessage> FollowAsync(HttpResponseMessage answer)
    {
        Uri location = answer.Headers.Location ?? throw new InvalidOperationException("The answer is not a redirect.");
        Uri next = location.IsAbsoluteUri ? location : new Uri(answer.RequestMessage!.RequestUri!, location);
        return SendAsync(HttpMethod.Get, next);
    }

    public void Dispose() => _http.Dispose();
}
