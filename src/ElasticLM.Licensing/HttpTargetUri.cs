using System.Globalization;
using System.Text;

namespace ElasticLM.Licensing;

/// <summary>
/// Normalizes the target URI that a request proof signs as <c>htu</c> (design §12.1). Clients and servers apply the same
/// steps, so a proof made for a URI matches the server's own form of it.
/// </summary>
public static class HttpTargetUri
{
    /// <summary>
    /// Returns the normalized form of an absolute http or https URI: scheme and host in lowercase, the default port left
    /// out, percent-encodings in uppercase hex, and no query or fragment.
    /// </summary>
    /// <param name="uri">The absolute URI.</param>
    /// <returns>The normalized URI text.</returns>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is not an absolute http or https URI.</exception>
    public static string Normalize(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The URI must be an absolute http or https URI.", nameof(uri));
        }

        var text = new StringBuilder();
        text.Append(uri.Scheme.ToLowerInvariant()).Append("://").Append(uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort)
        {
            text.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        string path = uri.AbsolutePath;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 2 < path.Length)
            {
                text.Append('%').Append(char.ToUpperInvariant(path[i + 1])).Append(char.ToUpperInvariant(path[i + 2]));
                i += 2;
            }
            else
            {
                text.Append(path[i]);
            }
        }

        return text.ToString();
    }
}
