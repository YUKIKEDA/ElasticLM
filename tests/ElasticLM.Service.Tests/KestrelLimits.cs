using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Requests at Kestrel's limits on the request line and the headers, over HTTP/1.1 on a raw connection and over HTTP/2 as
/// frames on a TLS connection (ALPN h2), and at Kestrel's other HTTP/2 limits (concurrent streams, frame size, HPACK table,
/// receive windows), for the service and the on-premises server (design §5.3 T16). The limits are Kestrel's defaults
/// (KestrelServerLimits and Http2Limits, aspnetcore v10.0.14), which neither host changes.
/// </summary>
internal static class KestrelLimits
{
    private const byte FrameData = 0x0;
    private const byte FrameHeaders = 0x1;
    private const byte FrameRstStream = 0x3;
    private const byte FrameSettings = 0x4;
    private const byte FramePing = 0x6;
    private const byte FrameGoAway = 0x7;
    private const byte FrameWindowUpdate = 0x8;
    private const byte FrameContinuation = 0x9;

    // SETTINGS_INITIAL_WINDOW_SIZE (RFC 9113 §6.5.2) and SETTINGS_ENABLE_CONNECT_PROTOCOL (RFC 8441 §3).
    private const ushort SettingInitialWindowSize = 0x4;
    private const ushort SettingEnableConnectProtocol = 0x8;

    // The default SETTINGS_MAX_FRAME_SIZE (RFC 9113 §4.2), the largest frame the test sends unless it tests that limit.
    private const int FrameLimit = 16 * 1024;

    // How long a read of the server's answer waits on the real clock; the answers came within 1 second in every run.
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(10);

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
    /// Gets each of Kestrel's other HTTP/2 limits with the size at it and one past it, and the outcome each gets: the status
    /// code of the last stream, "reset" and the error code of its RST_STREAM, "ping" for the acknowledgement of a PING sent
    /// after the request bodies, or "goaway" and the error code of a GOAWAY.
    /// </summary>
    public static TheoryData<string, int, string> Http2AtAndPastOtherLimits =>
        new()
        {
            { "streams", 100, "200" },
            { "streams", 101, "reset REFUSED_STREAM" },
            { "frame", 16 * 1024, "200" },
            { "frame", (16 * 1024) + 1, "goaway FRAME_SIZE_ERROR" },
            { "table", 4096, "200" },
            { "table", 4097, "goaway COMPRESSION_ERROR" },
            { "stream window", 768 * 1024, "ping" },
            { "stream window", (768 * 1024) + 1, "goaway FLOW_CONTROL_ERROR" },
            { "connection window", 1024 * 1024, "ping" },
            { "connection window", (1024 * 1024) + 1, "goaway FLOW_CONTROL_ERROR" },
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
        string authority = url.Authority;
        string target =
            kind == "line" ? path + "?" + new string('q', size - "GET".Length - "https".Length - authority.Length - path.Length - 1) : path;
        List<(string Name, string Value)> fields = Get(url, target);
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
            case "field":
                fields.Add(("x-pad", new string('p', size)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind of limit is not known.");
        }

        List<byte> block = HeaderBlock(fields);
        bool cut = kind == "field" && size > 32 * 1024;
        if (cut)
        {
            // Up to the length of the last value, which is the last field.
            block.RemoveRange(block.Count - size, size);
        }

        await using Http2Connection connection = await Http2Connection.OpenAsync(url);
        await connection.WriteAsync([HeaderFrames(1, block, endHeaders: !cut, endStream: true)]);
        string outcome = await connection.ReadOutcomeAsync(1);
        if (!outcome.StartsWith("goaway", StringComparison.Ordinal) && outcome != "no answer")
        {
            await connection.WriteAsync([HeaderFrames(3, HeaderBlock(Get(url, path)), endHeaders: true, endStream: true)]);
            Assert.Equal("200", await connection.ReadOutcomeAsync(3));
        }

        return outcome;
    }

    /// <summary>
    /// Sends GETs of <paramref name="path"/> as HTTP/2 frames on a new TLS connection to <paramref name="url"/> (ALPN h2) up
    /// to or one past one of Kestrel's other HTTP/2 limits, and returns the outcome: the status code of the last stream,
    /// "reset" and the error code of its RST_STREAM, "ping" for the acknowledgement of a PING sent after the request bodies,
    /// "goaway" and the error code of a GOAWAY, or "no answer". It asserts the answers on the way: the 200 of every earlier
    /// stream, and after a 200 or a reset of the last stream, the 200 of a plain GET on the same connection.
    /// </summary>
    /// <remarks>
    /// "streams" opens that many streams at once, each a GET with END_STREAM, on a connection whose client SETTINGS sets
    /// SETTINGS_INITIAL_WINDOW_SIZE to 0: the server can send no DATA of an answer, so each stream stays open after its 200
    /// (Http2Connection.cs StartStream refuses a stream past MaxStreamsPerConnection with REFUSED_STREAM). After a reset of
    /// the last stream, the client resets the first stream with CANCEL, which ends its count at once (Http2Stream.cs
    /// AbortRstStreamReceived), and opens one more. "frame" is the length of one HEADERS frame, filled with a padding field;
    /// past the limit only the frame's header goes, since Kestrel refuses the length before it waits for the payload
    /// (Http2FrameReader.cs TryReadFrame), and a payload it no longer reads can make the connection reset before the GOAWAY
    /// arrives. "table" is the value of a dynamic table size update at the start of the header block (RFC 7541 §6.3), which
    /// Kestrel's decoder takes up to Http2Limits.HeaderTableSize (HPackDecoder.cs SetDynamicHeaderTableSize). "stream window"
    /// and "connection window" are the bytes of DATA on one stream, or split over two streams, sent after the 200 of a GET
    /// without END_STREAM on a connection whose client window is 0, so the answers stay open and the handler never reads the
    /// body: Kestrel gives back window only for what a handler reads (Http2Stream.cs OnDataRead), and a DATA frame past
    /// either window ends the connection (InputFlowControl.cs TryAdvance). The DATA and the PING after it go in one write.
    /// </remarks>
    public static async Task<string> Http2OtherOutcomeAsync(Uri url, string path, string kind, int size)
    {
        byte[] get = [.. HeaderBlock(Get(url, path))];
        switch (kind)
        {
            case "streams":
            {
                await using Http2Connection connection = await Http2Connection.OpenAsync(url, (SettingInitialWindowSize, 0));
                int last = (2 * size) - 1;
                await connection.WriteAsync(Enumerable.Range(0, size).Select(i => HeaderFrames(1 + (2 * i), get, endHeaders: true, endStream: true)));
                List<(int Stream, string Outcome)> answers = await connection.ReadAsync(size);
                Assert.Equal(
                    Enumerable.Range(0, size - 1).Select(i => (1 + (2 * i), "200")),
                    answers.Where(a => a.Stream != last).OrderBy(a => a.Stream)
                );
                string outcome = answers.Single(a => a.Stream == last).Outcome;
                if (outcome == "200" || outcome.StartsWith("reset", StringComparison.Ordinal))
                {
                    // RST_STREAM with CANCEL (RFC 9113 §7).
                    byte[] cancel = Frame(FrameRstStream, 0, 1, [0, 0, 0, 0x8]);
                    await connection.WriteAsync([cancel, HeaderFrames(last + 2, get, endHeaders: true, endStream: true)]);
                    Assert.Equal(new[] { (last + 2, "200") }, await connection.ReadAsync(1));
                }

                return outcome;
            }

            case "frame":
            case "table":
            {
                await using Http2Connection connection = await Http2Connection.OpenAsync(url);
                byte[] frame;
                if (kind == "frame")
                {
                    List<(string Name, string Value)> fields = [.. Get(url, path), ("x-pad", string.Empty)];

                    // The value's length takes 3 bytes in place of 1 (RFC 7541 §5.1, a 7-bit prefix: 127 and two more).
                    int pad = size - HeaderBlock(fields).Count - 2;
                    fields[^1] = ("x-pad", new string('p', pad));
                    List<byte> block = HeaderBlock(fields);
                    Assert.Equal(size, block.Count);
                    frame = Frame(FrameHeaders, 0x5, 1, [.. block]);
                    if (size > FrameLimit)
                    {
                        frame = frame[..9];
                    }
                }
                else
                {
                    // 001 and the size as an integer with a 5-bit prefix (RFC 7541 §6.3).
                    var block = new List<byte>();
                    Integer(block, 0x20, 5, size);
                    block.AddRange(get);
                    frame = Frame(FrameHeaders, 0x5, 1, [.. block]);
                }

                await connection.WriteAsync([frame]);
                string outcome = await connection.ReadOutcomeAsync(1);
                if (outcome == "200")
                {
                    await connection.WriteAsync([HeaderFrames(3, get, endHeaders: true, endStream: true)]);
                    Assert.Equal("200", await connection.ReadOutcomeAsync(3));
                }

                return outcome;
            }

            case "stream window":
            case "connection window":
            {
                await using Http2Connection connection = await Http2Connection.OpenAsync(url, (SettingInitialWindowSize, 0));
                int[] streams = kind == "stream window" ? [1] : [1, 3];
                await connection.WriteAsync(streams.Select(id => HeaderFrames(id, get, endHeaders: true, endStream: false)));
                Assert.Equal(streams.Select(id => (id, "200")), (await connection.ReadAsync(streams.Length)).OrderBy(a => a.Stream));
                var frames = new List<byte[]>();
                for (int i = 0; i < streams.Length; i++)
                {
                    frames.Add(DataFrames(streams[i], (size / streams.Length) + (i == streams.Length - 1 ? size % streams.Length : 0)));
                }

                frames.Add(Frame(FramePing, 0, 0, new byte[8]));
                await connection.WriteAsync(frames);
                (int stream, string outcome) = (await connection.ReadAsync(1))[0];
                return stream == 0 ? outcome : stream.ToString(CultureInfo.InvariantCulture) + " " + outcome;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind of limit is not known.");
        }
    }

    /// <summary>
    /// Returns the SETTINGS that the server sends first on a new HTTP/2 connection, without SETTINGS_ENABLE_CONNECT_PROTOCOL,
    /// as "identifier=value" in the order of the identifiers, and the sum of the WINDOW_UPDATE increments of the connection
    /// that come before the acknowledgement of a PING.
    /// </summary>
    public static async Task<(string Settings, long ConnectionWindowIncrement)> Http2ServerSettingsAsync(Uri url)
    {
        await using Http2Connection connection = await Http2Connection.OpenAsync(url);
        await connection.WriteAsync([Frame(FramePing, 0, 0, new byte[8])]);
        Assert.Equal(new[] { (0, "ping") }, await connection.ReadAsync(1));
        string settings = string.Join(
            ", ",
            connection
                .ServerSettings.Where(s => s.Key != SettingEnableConnectProtocol)
                .OrderBy(s => s.Key)
                .Select(s => s.Key.ToString(CultureInfo.InvariantCulture) + "=" + s.Value.ToString(CultureInfo.InvariantCulture))
        );
        return (settings, connection.ConnectionWindowIncrement);
    }

    // The pseudo-header fields of a GET of the path.
    private static List<(string Name, string Value)> Get(Uri url, string path) =>
        [(":method", "GET"), (":scheme", "https"), (":authority", url.Authority), (":path", path)];

    // Each field as a literal without indexing with a new name (RFC 7541 §6.2.2), its strings without Huffman coding.
    private static List<byte> HeaderBlock(List<(string Name, string Value)> fields)
    {
        var block = new List<byte>();
        foreach ((string name, string value) in fields)
        {
            block.Add(0x00);
            foreach (string text in new[] { name, value })
            {
                Integer(block, 0x00, 7, text.Length);
                block.AddRange(Encoding.ASCII.GetBytes(text));
            }
        }

        return block;
    }

    // An integer with a prefix of the given bits after the first bits of its first byte (RFC 7541 §5.1).
    private static void Integer(List<byte> block, byte first, int bits, int value)
    {
        int max = (1 << bits) - 1;
        if (value < max)
        {
            block.Add((byte)(first | value));
            return;
        }

        block.Add((byte)(first | max));
        for (value -= max; value >= 128; value /= 128)
        {
            block.Add((byte)((value % 128) + 128));
        }

        block.Add((byte)value);
    }

    // A HEADERS frame, END_STREAM when asked, and CONTINUATION frames, of 16 KiB at most each (RFC 9113 §4.2, the default
    // SETTINGS_MAX_FRAME_SIZE); END_HEADERS on the last when asked.
    private static byte[] HeaderFrames(int id, IReadOnlyList<byte> block, bool endHeaders, bool endStream)
    {
        byte[] bytes = [.. block];
        using var frames = new MemoryStream();
        for (int start = 0; start < bytes.Length || start == 0; start += FrameLimit)
        {
            int length = Math.Min(FrameLimit, bytes.Length - start);
            bool last = start + length == bytes.Length;
            byte type = start == 0 ? FrameHeaders : FrameContinuation;
            byte flags = (byte)((start == 0 && endStream ? 0x1 : 0) | (last && endHeaders ? 0x4 : 0));
            frames.Write(Frame(type, flags, id, bytes.AsSpan(start, length)));
        }

        return frames.ToArray();
    }

    // DATA frames of 16 KiB at most that carry the given number of zero bytes, without END_STREAM.
    private static byte[] DataFrames(int id, int length)
    {
        using var frames = new MemoryStream();
        byte[] zeros = new byte[FrameLimit];
        for (int left = length; left > 0; left -= FrameLimit)
        {
            frames.Write(Frame(FrameData, 0, id, zeros.AsSpan(0, Math.Min(FrameLimit, left))));
        }

        return frames.ToArray();
    }

    private static byte[] Frame(byte type, byte flags, int id, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), id);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }

    // The :status, the first field of the answer (RFC 9113 §8.3), after any dynamic table size update at the start of the
    // block (RFC 7541 §4.2, §6.3): indexed from the static table (RFC 7541 Appendix A, entries 8 to 14), or a literal whose
    // name is entry 8 to 14 and whose value is not Huffman-coded.
    private static string Status(byte[] payload, byte flags)
    {
        Assert.Equal(0, flags & 0x28); // no PADDED, no PRIORITY
        int at = 0;
        while ((payload[at] & 0xe0) == 0x20)
        {
            bool more = (payload[at++] & 0x1f) == 0x1f;
            while (more)
            {
                more = (payload[at++] & 0x80) != 0;
            }
        }

        byte first = payload[at];
        int[] statuses = [200, 204, 206, 304, 400, 404, 500];
        if ((first & 0x80) != 0)
        {
            int index = first & 0x7f;
            Assert.InRange(index, 8, 14);
            return statuses[index - 8].ToString(CultureInfo.InvariantCulture);
        }

        int name = (first & 0x40) != 0 ? first & 0x3f : first & 0x0f;
        Assert.InRange(name, 8, 14);
        Assert.Equal(0, payload[at + 1] & 0x80);
        return Encoding.ASCII.GetString(payload, at + 2, payload[at + 1]);
    }

    private static string ErrorName(uint code) =>
        code switch
        {
            0x1 => "PROTOCOL_ERROR",
            0x3 => "FLOW_CONTROL_ERROR",
            0x6 => "FRAME_SIZE_ERROR",
            0x7 => "REFUSED_STREAM",
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

    /// <summary>
    /// An HTTP/2 connection on TLS (ALPN h2) that writes the frames the test builds and reads the answers the server sends.
    /// The client does not check the server's certificate, only that there is one: what the tests check is the limits.
    /// </summary>
    public sealed class Http2Connection : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _tls;
        private readonly CancellationToken _cancel;

        private Http2Connection(TcpClient tcp, SslStream tls, CancellationToken cancel)
        {
            _tcp = tcp;
            _tls = tls;
            _cancel = cancel;
        }

        /// <summary>Gets the values of the first SETTINGS frame of the server that a read has passed, by identifier.</summary>
        public Dictionary<ushort, uint> ServerSettings { get; } = [];

        /// <summary>Gets the sum of the WINDOW_UPDATE increments of the connection (stream 0) that reads have passed.</summary>
        public long ConnectionWindowIncrement { get; private set; }

        /// <summary>
        /// Opens a TLS connection to <paramref name="url"/> with ALPN h2, asserts that the server chose h2, and writes the
        /// connection preface and a SETTINGS frame with the values given, none by default.
        /// </summary>
        public static async Task<Http2Connection> OpenAsync(Uri url, params (ushort Id, uint Value)[] settings)
        {
            CancellationToken cancel = TestContext.Current.CancellationToken;
            var tcp = new TcpClient();
            SslStream? tls = null;
            try
            {
                await tcp.ConnectAsync(url.Host, url.Port, cancel);
                tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = url.Host, ApplicationProtocols = [SslApplicationProtocol.Http2] },
                    cancel
                );
                Assert.Equal(SslApplicationProtocol.Http2, tls.NegotiatedApplicationProtocol);
                byte[] values = new byte[6 * settings.Length];
                for (int i = 0; i < settings.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(values.AsSpan(6 * i), settings[i].Id);
                    BinaryPrimitives.WriteUInt32BigEndian(values.AsSpan((6 * i) + 2), settings[i].Value);
                }

                await tls.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), cancel);
                await tls.WriteAsync(Frame(FrameSettings, 0, 0, values), cancel);
                return new Http2Connection(tcp, tls, cancel);
            }
            catch
            {
                if (tls is not null)
                {
                    await tls.DisposeAsync();
                }

                tcp.Dispose();
                throw;
            }
        }

        /// <summary>Writes the frames given in one write.</summary>
        public async Task WriteAsync(IEnumerable<byte[]> frames)
        {
            await _tls.WriteAsync(frames.SelectMany(f => f).ToArray(), _cancel);
        }

        /// <summary>
        /// Reads frames until <paramref name="count"/> answers have come or a GOAWAY, and returns the answers in the order
        /// they came: the status code of the HEADERS of a stream, "reset" and the error code of a RST_STREAM, "ping" (stream
        /// 0) for the acknowledgement of a PING, "goaway" and its error code (stream 0), or "no answer" (stream 0) when
        /// AnswerLimit of the real clock passes first. Frames of streams that <paramref name="streams"/> does not take are
        /// passed over, and so are the server's SETTINGS and WINDOW_UPDATE frames, whose values the properties keep.
        /// </summary>
        /// <remarks>
        /// A read writes nothing: Kestrel does not wait for the acknowledgement of its SETTINGS (Http2Connection.cs
        /// ProcessSettingsFrameAsync), and a byte written after a request that ends the connection, which Kestrel no longer
        /// reads, can make the connection reset before the GOAWAY arrives. An acknowledgement written here made the client
        /// read such a reset in 1 of 30 runs of the server's tests (docs/rows/V1d29f.md). The bound keeps a server that
        /// waits for more of the request (a limit raised past what the test sends) from holding the test until the server
        /// gives up on the request.
        /// </remarks>
        public async Task<List<(int Stream, string Outcome)>> ReadAsync(int count, Func<int, bool>? streams = null)
        {
            var answers = new List<(int Stream, string Outcome)>();
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(_cancel);
            bound.CancelAfter(AnswerLimit);
            byte[] head = new byte[9];
            try
            {
                while (answers.Count < count)
                {
                    await _tls.ReadExactlyAsync(head, bound.Token);
                    int length = (head[0] << 16) | (head[1] << 8) | head[2];
                    byte type = head[3];
                    byte flags = head[4];
                    int id = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(5)) & int.MaxValue;
                    byte[] payload = new byte[length];
                    await _tls.ReadExactlyAsync(payload, bound.Token);
                    bool taken = streams is null || streams(id);
                    switch (type)
                    {
                        case FrameGoAway:
                            answers.Add((0, "goaway " + ErrorName(BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)))));
                            return answers;
                        case FramePing when (flags & 0x1) != 0:
                            answers.Add((0, "ping"));
                            break;
                        case FrameSettings when (flags & 0x1) == 0 && ServerSettings.Count == 0:
                            for (int at = 0; at + 6 <= payload.Length; at += 6)
                            {
                                ServerSettings[BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(at))] = BinaryPrimitives.ReadUInt32BigEndian(
                                    payload.AsSpan(at + 2)
                                );
                            }

                            break;
                        case FrameWindowUpdate when id == 0:
                            ConnectionWindowIncrement += BinaryPrimitives.ReadInt32BigEndian(payload) & int.MaxValue;
                            break;
                        case FrameRstStream when id != 0 && taken:
                            answers.Add((id, "reset " + ErrorName(BinaryPrimitives.ReadUInt32BigEndian(payload))));
                            break;
                        case FrameHeaders when id != 0 && taken:
                            answers.Add((id, Status(payload, flags)));
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
            {
                answers.Add((0, "no answer"));
            }

            return answers;
        }

        /// <summary>
        /// Reads until the answer of stream <paramref name="id"/> and returns its outcome as <see cref="ReadAsync"/> gives it,
        /// passing over the frames of other streams.
        /// </summary>
        public async Task<string> ReadOutcomeAsync(int id) => (await ReadAsync(1, s => s == id))[0].Outcome;

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await _tls.DisposeAsync();
            _tcp.Dispose();
        }
    }
}
