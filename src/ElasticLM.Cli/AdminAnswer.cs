using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ElasticLM.Client;

namespace ElasticLM.Cli;

/// <summary>
/// An answer of a server's administration API, read without a document object model (design §13.7, V1d6). The answer may
/// be up to <see cref="ServerAdminApi.MaxAnswerLength"/> long, and a document model of a body dense in tokens takes many
/// times the body's length; so the body is checked and read with <see cref="Utf8JsonReader"/>, which keeps nothing of
/// it, and the parts a command prints are read into types of their own.
/// </summary>
internal sealed class AdminAnswer
{
    private const int FlushBytes = 16 * 1024;

    private readonly ReadOnlyMemory<byte> _body;

    private AdminAnswer(HttpStatusCode status, ReadOnlyMemory<byte> body, bool isJson)
    {
        Status = status;
        _body = body;
        IsJson = isJson;
    }

    /// <summary>Gets the answer's status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Gets a value indicating whether the status is a success (2xx).</summary>
    public bool IsSuccess => (int)Status is >= 200 and <= 299;

    /// <summary>Gets a value indicating whether the body is empty.</summary>
    public bool IsEmpty => _body.IsEmpty;

    /// <summary>Gets a value indicating whether the body is one JSON value, or empty.</summary>
    public bool IsJson { get; }

    /// <summary>Checks the body of a whole answer: whether it is one JSON value, nested at most 64 levels.</summary>
    /// <param name="whole">The whole answer the HTTP client read.</param>
    /// <returns>The answer, which tells whether its body is JSON.</returns>
    public static AdminAnswer Of(BoundedAnswer.Whole whole)
    {
        ArgumentNullException.ThrowIfNull(whole);
        bool json = true;
        if (!whole.Body.IsEmpty)
        {
            try
            {
                var reader = new Utf8JsonReader(whole.Body.Span);
                while (reader.Read())
                {
                    // Each token is checked as it is read; nothing of it is kept.
                }
            }
            catch (JsonException)
            {
                json = false;
            }
        }

        return new AdminAnswer(whole.Status, whole.Body, json);
    }

    /// <summary>Returns the string value of a member of the body's top-level object, or <see langword="null"/>. Of a member
    /// given more than once, the last counts.</summary>
    /// <param name="name">The member's name.</param>
    public string? Text(string name)
    {
        string? found = null;
        Members(name, (ref Utf8JsonReader reader) => found = reader.TokenType == JsonTokenType.String ? reader.GetString() : null);
        return found;
    }

    /// <summary>Returns the Boolean value of a member of the body's top-level object, or <see langword="null"/>. Of a member
    /// given more than once, the last counts.</summary>
    /// <param name="name">The member's name.</param>
    public bool? Boolean(string name)
    {
        bool? found = null;
        Members(
            name,
            (ref Utf8JsonReader reader) =>
                found = reader.TokenType switch
                {
                    JsonTokenType.True => true,
                    JsonTokenType.False => false,
                    _ => null,
                }
        );
        return found;
    }

    /// <summary>Reads the body as one type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="type">The type's source-generated metadata.</param>
    /// <exception cref="CliException">The body is not of that type (exit code 3).</exception>
    public T Read<T>(JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(_body.Span, type) ?? throw new JsonException("The body is null.");
        }
        catch (JsonException)
        {
            throw NotTheAnswer();
        }
    }

    /// <summary>
    /// Reads the items of an array that is a member of the body's top-level object, one at a time: each item is read,
    /// handed to <paramref name="each"/>, and dropped, so a long array is never held whole. Of a member given more than
    /// once, each is read.
    /// </summary>
    /// <typeparam name="T">The type of an item.</typeparam>
    /// <param name="name">The member's name.</param>
    /// <param name="type">The item type's source-generated metadata.</param>
    /// <param name="each">What to do with an item.</param>
    /// <exception cref="CliException">The body has no such array, or an item is not of that type (exit code 3).</exception>
    public void ReadEach<T>(string name, JsonTypeInfo<T> type, Action<T> each)
    {
        ArgumentNullException.ThrowIfNull(each);
        bool found = false;
        try
        {
            var reader = new Utf8JsonReader(_body.Span);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool match = reader.ValueTextEquals(name);
                    reader.Read();
                    if (!match || reader.TokenType != JsonTokenType.StartArray)
                    {
                        reader.Skip();
                        continue;
                    }

                    found = true;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        each(JsonSerializer.Deserialize(ref reader, type) ?? throw new JsonException("An item is null."));
                    }
                }
            }
        }
        catch (JsonException)
        {
            found = false;
        }

        if (!found)
        {
            throw NotTheAnswer();
        }
    }

    /// <summary>
    /// Writes the body as indented JSON, as <see cref="JsonSerializer"/> writes a <see cref="JsonElement"/>, a part at a
    /// time, so that what is written is not held whole.
    /// </summary>
    /// <param name="output">Where to write.</param>
    public void WriteIndented(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var buffer = new ArrayBufferWriter<byte>(FlushBytes * 2);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            var reader = new Utf8JsonReader(_body.Span);
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        WithText(ref reader, writer, static (w, text) => w.WritePropertyName(text));
                        break;
                    case JsonTokenType.String:
                        WithText(ref reader, writer, static (w, text) => w.WriteStringValue(text));
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(true);
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(false);
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                }

                if (writer.BytesPending + buffer.WrittenCount >= FlushBytes)
                {
                    writer.Flush();
                    Drain(buffer, output);
                }
            }

            writer.Flush();
        }

        Drain(buffer, output);
        output.WriteLine();
    }

    // A flush ends between two tokens, so the bytes written so far are whole UTF-8. They are decoded into a pooled buffer,
    // not into a string for each part.
    private static void Drain(ArrayBufferWriter<byte> buffer, TextWriter output)
    {
        char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(buffer.WrittenCount));
        try
        {
            int count = Encoding.UTF8.GetChars(buffer.WrittenSpan, chars);
            output.Write(chars, 0, count);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        buffer.ResetWrittenCount();
    }

    private CliException NotTheAnswer() =>
        new($"The server answered {(int)Status} with a body that is not the answer asked for.", ExitCodes.NetworkError);

    // Hands a string or a property name, unescaped, to the writer, which escapes it again as the serializer does.
    private static void WithText(ref Utf8JsonReader reader, Utf8JsonWriter writer, Action<Utf8JsonWriter, ReadOnlySpan<byte>> write)
    {
        if (!reader.ValueIsEscaped)
        {
            write(writer, reader.ValueSpan);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
        try
        {
            int length = reader.CopyString(rented);
            write(writer, rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Calls the action on the value of each top-level member of that name, when the body is a JSON object.
    private void Members(string name, MemberAction action)
    {
        if (!IsJson || _body.IsEmpty)
        {
            return;
        }

        var reader = new Utf8JsonReader(_body.Span);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool match = reader.ValueTextEquals(name);
            reader.Read();
            if (match)
            {
                action(ref reader);
            }

            reader.Skip();
        }
    }

    private delegate void MemberAction(ref Utf8JsonReader reader);
}

/// <summary><c>GET /admin/v1/status</c>, as <c>elm server status</c> prints it; its <c>features</c> are read one at a time
/// (<see cref="AdminFeature"/>).</summary>
internal sealed record AdminStatus(
    [property: JsonPropertyName("product"), JsonRequired] string Product,
    [property: JsonPropertyName("license"), JsonRequired] string License,
    [property: JsonPropertyName("licenseToken"), JsonRequired] string LicenseToken,
    [property: JsonPropertyName("licenseExpires")] long? LicenseExpires,
    [property: JsonPropertyName("key"), JsonRequired] string Key,
    [property: JsonPropertyName("recovering"), JsonRequired] bool Recovering,
    [property: JsonPropertyName("recoveryEnds"), JsonRequired] long RecoveryEnds,
    [property: JsonPropertyName("leases"), JsonRequired] int Leases
);

/// <summary>The use of one feature in <see cref="AdminStatus"/>.</summary>
internal sealed record AdminFeature(
    [property: JsonPropertyName("feature"), JsonRequired] string Feature,
    [property: JsonPropertyName("seats"), JsonRequired] int Seats,
    [property: JsonPropertyName("allowed"), JsonRequired] long Allowed,
    [property: JsonPropertyName("inUse"), JsonRequired] long InUse,
    [property: JsonPropertyName("borrowed"), JsonRequired] long Borrowed
);

/// <summary>One lease of <c>GET /admin/v1/leases</c>, as <c>elm server leases</c> prints it.</summary>
internal sealed record AdminLease(
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("feature"), JsonRequired] string Feature,
    [property: JsonPropertyName("seats"), JsonRequired] int Seats,
    [property: JsonPropertyName("key"), JsonRequired] string Key,
    [property: JsonPropertyName("expires"), JsonRequired] long Expires,
    [property: JsonPropertyName("borrow"), JsonRequired] bool Borrow,
    [property: JsonPropertyName("stopped"), JsonRequired] bool Stopped
);

/// <summary><c>POST /admin/v1/license/reload</c>, as <c>elm server reload</c> prints it.</summary>
internal sealed record AdminReload(
    [property: JsonPropertyName("license"), JsonRequired] string License,
    [property: JsonPropertyName("licenseToken"), JsonRequired] string LicenseToken
);

/// <summary>
/// Source-generated JSON for the answers of the administration API that <c>elm server</c> prints (NativeAOT). The answers
/// come from whatever listens on the socket file, so they are read as untrusted input.
/// </summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AdminStatus))]
[JsonSerializable(typeof(AdminFeature))]
[JsonSerializable(typeof(AdminLease))]
[JsonSerializable(typeof(AdminReload))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
