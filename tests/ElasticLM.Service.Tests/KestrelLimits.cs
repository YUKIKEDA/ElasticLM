using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Requests at Kestrel's limits on the request line and the headers, over HTTP/1.1 on a raw connection and over HTTP/2
/// with HttpClient, for the service and the on-premises server (design §5.3 T16). The limits are Kestrel's defaults
/// (KestrelServerLimits and Http2Limits, aspnetcore v10.0.14), which neither host changes.
/// </summary>
internal static class KestrelLimits
{
    private const byte FrameHeaders = 0x1;
    private const byte FrameRstStream = 0x3;
    private const byte FrameSettings = 0x4;
    private const byte FrameGoAway = 0x7;
    private const byte FrameContinuation = 0x9;

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

    /// <summary>
    /// Gets each kind of HTTP/2 limit with the size at it and one past it, and for the header fields at and past twice the
    /// limit, where Kestrel ends the connection instead of answering, and the outcome each gets: a status code (after which
    /// the connection answers another request), "reset" and the error code of a RST_STREAM (after which it answers too), or
    /// "goaway" and the error code of a GOAWAY.
    /// </summary>
    public static TheoryData<string, int, string> Http2AtAndPastEachLimit =>
        new()
        {
            { "line", 8192, "200" },
            { "line", 8193, "reset PROTOCOL_ERROR" },
            { "count", 100, "200" },
            { "count", 101, "431" },
            { "count", 200, "431" },
            { "count", 201, "goaway PROTOCOL_ERROR" },
            { "size", 32 * 1024, "200" },
            { "size", (32 * 1024) + 1, "431" },
            { "size", 64 * 1024, "431" },
            { "size", (64 * 1024) + 1, "goaway PROTOCOL_ERROR" },
            { "field", 32 * 1024, "431" },
            { "field", (32 * 1024) + 1, "goaway COMPRESSION_ERROR" },
        };

    /// <summary>
    /// Sends a GET of <paramref name="path"/> as HTTP/2 frames on a new TLS connection to <paramref name="url"/> (ALPN h2),
    /// made the size given for the kind of limit, and returns the outcome: the status code of the answer, "reset" and the
    /// error code of a RST_STREAM of the request, or "goaway" and the error code of a GOAWAY. After an answer or a reset, it
    /// sends a plain GET of the path on the same connection and asserts its 200.
    /// </summary>
    /// <remarks>
    /// HttpClient is not used: once the server's SETTINGS has come, it refuses itself a header block over the server's
    /// SETTINGS_MAX_HEADER_LIST_SIZE (dotnet/runtime v10.0.15, SocketsHttpHandler/Http2Connection.cs), so whether a request
    /// past the size reaches Kestrel depends on a race. Every field goes as an HPACK literal without indexing and without
    /// Huffman coding, in frames of 16 KiB at most. "line" is what Kestrel counts in place of the request line, the lengths of
    /// the values of :method, :scheme, :authority, and :path together (Http2Stream.cs TryValidatePseudoHeaders), made long
    /// with a query. For the header fields Kestrel counts every field, the four pseudo-header fields among them, and adds the
    /// bytes of each name and value without the 32 octets of RFC 9113's header list size (Http2Connection.cs OnHeaderCore).
    /// "count" is the number of fields; "size" is that sum, spread over fields of at most 16 KiB; "field" is the length of
    /// one value. A value past the field limit is sent up to its length alone, without END_HEADERS: Kestrel refuses the
    /// length itself (HPackDecoder.cs OnStringLength). Sent whole with HttpClient, such a value made the client read a reset
    /// connection in place of the GOAWAY in 1 of 8 runs on Windows (docs/rows/V1d29f.md).
    /// </remarks>
    public static async Task<string> Http2OutcomeAsync(Uri url, string path, string kind, int size)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string authority = url.Authority;
        string target =
            kind == "line" ? path + "?" + new string('q', size - "GET".Length - "https".Length - authority.Length - path.Length - 1) : path;
        var fields = new List<(string Name, string Value)> { (":method", "GET"), (":scheme", "https"), (":authority", authority), (":path", target) };
        int sum = fields.Sum(f => f.Name.Length + f.Value.Length);
        switch (kind)
        {
            case "line":
                Assert.Equal(size, "GET".Length + "https".Length + authority.Length + target.Length);
                break;
            case "count":
                fields.AddRange(Enumerable.Range(1, size - 4).Select(i => ("x-" + i.ToString(CultureInfo.InvariantCulture), "v")));
                break;
            case "size":
                for (int i = 1, left = size - sum; left > 0; i++)
                {
                    string name = "x-p" + i.ToString(CultureInfo.InvariantCulture);
                    int length = Math.Min(16 * 1024, left - name.Length);
                    Assert.True(length > 0, "The size leaves no room for a value.");
                    fields.Add((name, new string('p', length)));
                    left -= name.Length + length;
                }

                break;
            default:
                fields.Add(("x-pad", new string('p', size)));
                break;
        }

        List<byte> block = HeaderBlock(fields);
        bool cut = kind == "field" && size > 32 * 1024;
        if (cut)
        {
            // Up to the length of the last value, which is the last field.
            block.RemoveRange(block.Count - size, size);
        }

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(url.Host, url.Port, cancel);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await tls.AuthenticateAsClientAsync(
            new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = url.Host,
                ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http2],
            },
            cancel
        );
        Assert.Equal(System.Net.Security.SslApplicationProtocol.Http2, tls.NegotiatedApplicationProtocol);
        await tls.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), cancel);
        await tls.WriteAsync(Frame(FrameSettings, 0, 0, []), cancel);
        await WriteHeadersAsync(tls, 1, block, endHeaders: !cut, cancel);
        string outcome = await ReadOutcomeAsync(tls, 1, cancel);
        if (!outcome.StartsWith("goaway", StringComparison.Ordinal))
        {
            await WriteHeadersAsync(tls, 3, HeaderBlock(fields.Take(3).Append((":path", path)).ToList()), endHeaders: true, cancel);
            Assert.Equal("200", await ReadOutcomeAsync(tls, 3, cancel));
        }

        return outcome;
    }

    // Each field as a literal without indexing with a new name (RFC 7541 §6.2.2), its strings without Huffman coding.
    private static List<byte> HeaderBlock(List<(string Name, string Value)> fields)
    {
        var block = new List<byte>();
        foreach ((string name, string value) in fields)
        {
            block.Add(0x00);
            foreach (string text in new[] { name, value })
            {
                // RFC 7541 §5.1, a 7-bit prefix.
                int length = text.Length;
                if (length < 127)
                {
                    block.Add((byte)length);
                }
                else
                {
                    block.Add(127);
                    for (length -= 127; length >= 128; length /= 128)
                    {
                        block.Add((byte)((length % 128) + 128));
                    }

                    block.Add((byte)length);
                }

                block.AddRange(Encoding.ASCII.GetBytes(text));
            }
        }

        return block;
    }

    // A HEADERS frame with END_STREAM, and CONTINUATION frames, of 16 KiB at most each (RFC 9113 §4.2, the default
    // SETTINGS_MAX_FRAME_SIZE); END_HEADERS on the last when asked.
    private static async Task WriteHeadersAsync(Stream stream, int id, List<byte> block, bool endHeaders, CancellationToken cancel)
    {
        byte[] bytes = [.. block];
        using var frames = new MemoryStream();
        for (int start = 0; start < bytes.Length || start == 0; start += 16 * 1024)
        {
            int length = Math.Min(16 * 1024, bytes.Length - start);
            bool last = start + length == bytes.Length;
            byte type = start == 0 ? FrameHeaders : FrameContinuation;
            byte flags = (byte)((start == 0 ? 0x1 : 0) | (last && endHeaders ? 0x4 : 0));
            frames.Write(Frame(type, flags, id, bytes.AsSpan(start, length)));
        }

        await stream.WriteAsync(frames.ToArray(), cancel);
    }

    private static byte[] Frame(byte type, byte flags, int id, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), id);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }

    // Reads frames until the answer's HEADERS, a RST_STREAM of the stream, or a GOAWAY, and writes nothing: Kestrel does not
    // wait for the acknowledgement of its SETTINGS (Http2Connection.cs ProcessSettingsFrameAsync), and a byte written after
    // the request that it no longer reads can make the connection reset before the GOAWAY arrives. An acknowledgement
    // written here made the client read such a reset in 1 of 30 runs of the server's tests (docs/rows/V1d29f.md).
    private static async Task<string> ReadOutcomeAsync(Stream stream, int id, CancellationToken cancel)
    {
        byte[] head = new byte[9];
        while (true)
        {
            await stream.ReadExactlyAsync(head, cancel);
            int length = (head[0] << 16) | (head[1] << 8) | head[2];
            byte type = head[3];
            byte flags = head[4];
            int streamId = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(5)) & int.MaxValue;
            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancel);
            if (type == FrameGoAway)
            {
                return "goaway " + ErrorName(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)));
            }
            else if (type == FrameRstStream && streamId == id)
            {
                return "reset " + ErrorName(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload));
            }
            else if (type == FrameHeaders && streamId == id)
            {
                return Status(payload, flags);
            }
        }
    }

    // The :status, the first field of the answer (RFC 9113 §8.3): indexed from the static table (RFC 7541 Appendix A,
    // entries 8 to 14), or a literal whose name is entry 8 to 14 and whose value is not Huffman-coded.
    private static string Status(byte[] payload, byte flags)
    {
        Assert.Equal(0, flags & 0x28); // no PADDED, no PRIORITY
        byte first = payload[0];
        int[] statuses = [200, 204, 206, 304, 400, 404, 500];
        if ((first & 0x80) != 0)
        {
            int index = first & 0x7f;
            Assert.InRange(index, 8, 14);
            return statuses[index - 8].ToString(CultureInfo.InvariantCulture);
        }

        int name = (first & 0x40) != 0 ? first & 0x3f : first & 0x0f;
        Assert.InRange(name, 8, 14);
        Assert.Equal(0, payload[1] & 0x80);
        return Encoding.ASCII.GetString(payload, 2, payload[1]);
    }

    private static string ErrorName(uint code) =>
        code switch
        {
            0x1 => "PROTOCOL_ERROR",
            0x9 => "COMPRESSION_ERROR",
            _ => "0x" + code.ToString("x", CultureInfo.InvariantCulture),
        };

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
