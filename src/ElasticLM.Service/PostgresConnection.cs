using System.Net;
using ElasticLM.Licensing;
using Npgsql;

namespace ElasticLM.Service;

/// <summary>
/// The checks of a PostgreSQL connection string that the settings make before anything connects (design §14.2, T30): a
/// connection to a host that is neither loopback nor a Unix socket checks the server's certificate, or says that it runs
/// without TLS; and the certificate and key files the string names exist.
/// </summary>
internal static class PostgresConnection
{
    /// <summary>
    /// Checks a PostgreSQL connection string.
    /// </summary>
    /// <remarks>
    /// Npgsql 10.0.3 does not check the server's certificate under <c>Prefer</c> and <c>Require</c>, even when a root
    /// certificate is given (<c>src/Npgsql/Internal/NpgsqlConnector.cs</c>, <c>NegotiateEncryption</c>), and under
    /// <c>Prefer</c>, its default, it goes on without TLS when the server says no. Npgsql reads the certificate and key files
    /// at each connection, and a missing one fails inside the TLS handshake.
    /// </remarks>
    /// <param name="connectionString">The connection string.</param>
    /// <exception cref="ServiceStartException">The string is not valid, it would connect to another machine without checking
    /// the server's certificate, or a file it names does not exist.</exception>
    public static void Check(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException e)
        {
            throw new ServiceStartException("The database connection string is not valid: " + ArgumentMessages.WithoutParameterName(e));
        }

        if (
            builder.SslMode is not (SslMode.VerifyFull or SslMode.VerifyCA or SslMode.Disable)
            && builder.Host is { Length: > 0 } host
            && !host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(IsOnThisMachine)
        )
        {
            throw new ServiceStartException(
                $"The database connection string does not check the certificate of a server on another machine (SSL Mode is "
                    + $"{builder.SslMode}): set SSL Mode to VerifyFull or VerifyCA, or to Disable to connect without TLS."
            );
        }

        File(builder.RootCertificate, "Root Certificate");
        File(builder.SslCertificate, "SSL Certificate");
        File(builder.SslKey, "SSL Key");
    }

    /// <summary>
    /// Returns whether one entry of <c>Host</c> is a Unix socket or a loopback address: a rooted path or a name that starts with
    /// <c>@</c>, as Npgsql's <c>IsUnixSocket</c> reads it; an IP address that <see cref="IPAddress.IsLoopback"/> accepts; or the
    /// name <c>localhost</c>. Other names are not looked up, so the answer does not change with DNS or the hosts file.
    /// </summary>
    /// <param name="entry">The entry, perhaps with <c>:port</c> after it.</param>
    public static bool IsOnThisMachine(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length == 0)
        {
            return false;
        }

        if (entry[0] == '@' || Path.IsPathRooted(entry))
        {
            return true;
        }

        string name = WithoutPort(entry);
        if (name.Length > 1 && name[0] == '[' && name[^1] == ']')
        {
            name = name[1..^1];
        }

        return IPAddress.TryParse(name, out IPAddress? address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    // As Npgsql's TrySplitHostPort reads "host:port", "[v6]:port", and a bare IPv6 address (src/Npgsql/NpgsqlConnectionStringBuilder.cs).
    private static string WithoutPort(string entry)
    {
        int separator = entry.LastIndexOf(':');
        if (separator == -1)
        {
            return entry;
        }

        int otherColon = entry.AsSpan(0, separator).LastIndexOf(':');
        int v6End = entry.LastIndexOf(']');
        return otherColon == -1 || (separator > v6End && otherColon < v6End) ? entry[..separator] : entry;
    }

    private static void File(string? path, string key)
    {
        if (path is not null && !System.IO.File.Exists(path))
        {
            throw new ServiceStartException($"The database connection string names a {key} file that does not exist: '{path}'.");
        }
    }
}
