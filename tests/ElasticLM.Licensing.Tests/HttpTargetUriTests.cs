namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="HttpTargetUri"/> (design §12.1).
/// </summary>
public sealed class HttpTargetUriTests
{
    /// <summary>
    /// Scheme and host become lowercase, default ports go, other ports stay, percent-encodings become uppercase, and the
    /// query and fragment go.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - URIs in several forms of the same target, and with a non-default port.
    ///
    /// Steps:
    /// - Normalize each.
    ///
    /// Expected:
    /// - The listed text.
    /// </remarks>
    [Theory]
    [InlineData("HTTPS://Licenses.Example.COM:443/v1/leases?x=1#f", "https://licenses.example.com/v1/leases")]
    [InlineData("https://licenses.example.com/v1/leases", "https://licenses.example.com/v1/leases")]
    [InlineData("https://licenses.example.com:7070/v1/leases", "https://licenses.example.com:7070/v1/leases")]
    [InlineData("http://licenses.example.com:80/v1/nonce", "http://licenses.example.com/v1/nonce")]
    [InlineData("https://licenses.example.com/v1/leases/a%2fb", "https://licenses.example.com/v1/leases/a%2Fb")]
    [InlineData("https://[::1]:7070/v1/leases", "https://[::1]:7070/v1/leases")]
    [InlineData("https://licenses.example.com/v1/%6Ceases", "https://licenses.example.com/v1/leases")]
    [InlineData("https://licenses.example.com/v1/./leases", "https://licenses.example.com/v1/leases")]
    [InlineData("https://licenses.example.com/v1/x/../leases", "https://licenses.example.com/v1/leases")]
    public void Normalize_Forms_GiveOneText(string uri, string expected)
    {
        Assert.Equal(expected, HttpTargetUri.Normalize(new Uri(uri)));
    }

    /// <summary>
    /// Relative URIs and schemes other than http and https are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A relative URI and an ftp URI.
    ///
    /// Steps:
    /// - Normalize each.
    ///
    /// Expected:
    /// - ArgumentException.
    /// </remarks>
    [Fact]
    public void Normalize_NotHttp_Throws()
    {
        Assert.Throws<ArgumentException>(() => HttpTargetUri.Normalize(new Uri("/v1/leases", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => HttpTargetUri.Normalize(new Uri("ftp://example.com/x")));
    }
}
