using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Requests at Kestrel's limits on the request line and the headers, sent over a raw connection, for the service and the
/// on-premises server (design §5.3 T16). The limits are Kestrel's defaults (KestrelServerLimits, aspnetcore v10.0.14),
/// which neither host changes.
/// </summary>
internal static class KestrelLimits
{
    /// <summary>Gets each kind of limit with the size at it and one past it, and the status each gets.</summary>
    public static TheoryData<string, int, int> AtAndPastEachLimit =>
        new()
        {
            { "line", 8192, 200 },
            { "line", 8193, 414 },
            { "count", 100, 200 },
            { "count", 101, 431 },
            { "size", (32 * 1024) + 2, 200 },
            { "size", (32 * 1024) + 3, 431 },
        };

    /// <summary>
    /// Returns a GET of the path whose request line (with its CRLF), header count (Host among them), or header section
    /// (Host, one padding header, and the empty line that ends it, with their CRLFs) is the size given.
    /// </summary>
    public static string Request(string path, string kind, int size)
    {
        const string Version = " HTTP/1.1\r\n";
        const string Host = "Host: x\r\n";
        string head = "GET " + path + "?";
        return kind switch
        {
            "line" => head + new string('q', size - head.Length - Version.Length) + Version + Host + "\r\n",
            "count" => "GET " + path + Version + Host + string.Concat(Enumerable.Range(1, size - 1).Select(i => $"X-{i}: v\r\n")) + "\r\n",
            _ => "GET " + path + Version + Host + "X-Pad: " + new string('p', size - Host.Length - "X-Pad: \r\n\r\n".Length) + "\r\n\r\n",
        };
    }

    /// <summary>Sends a request over a new connection, through <paramref name="wrap"/> when given, and returns the status code
    /// of the answer's status line.</summary>
    public static async Task<int> StatusAsync(Uri url, string request, Func<Stream, Task<Stream>>? wrap = null)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(url.Host, url.Port, cancel);
        Stream stream = wrap is null ? tcp.GetStream() : await wrap(tcp.GetStream());
        await using (stream)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancel);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string line = await reader.ReadLineAsync(cancel) ?? string.Empty;
            string[] parts = line.Split(' ');
            Assert.True(
                parts.Length >= 2 && parts[0] == "HTTP/1.1" && parts[1].Length == 3 && parts[1].All(char.IsAsciiDigit),
                $"The answer has no HTTP/1.1 status line; its first line is '{line}'."
            );
            return int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
    }
}
