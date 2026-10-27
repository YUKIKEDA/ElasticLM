using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>
/// A request body that cannot be used: the code and message of the answer (design §14.5).
/// </summary>
internal sealed class BodyException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

/// <summary>
/// A JSON object read from a request body, where a member that was sent, a member sent as <c>null</c>, and a member left out
/// are told apart (design §14.5: <c>PATCH</c> replaces the members sent, and a create uses the defaults for those left out).
/// The same limits as the source-generated readers: depth 8, no member twice; unknown members are ignored.
/// </summary>
internal sealed class RequestBody : IDisposable
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    private readonly JsonDocument _document;
    private readonly string _malformed;

    private RequestBody(JsonDocument document, string malformed)
    {
        _document = document;
        _malformed = malformed;
    }

    /// <summary>
    /// Reads a body that must be one JSON object.
    /// </summary>
    /// <param name="utf8">The body.</param>
    /// <param name="malformed">The message of every <c>ELM_MALFORMED</c> answer for this body.</param>
    /// <exception cref="BodyException">The body is not a JSON object within the limits.</exception>
    public static RequestBody Parse(byte[] utf8, string malformed)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, Options);
        }
        catch (JsonException)
        {
            throw new BodyException(ErrorCode.Malformed, malformed);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new BodyException(ErrorCode.Malformed, malformed);
        }

        return new RequestBody(document, malformed);
    }

    /// <summary>Returns a member if it was sent (its value may be <c>null</c>).</summary>
    public bool TryGet(string name, out JsonElement value) => _document.RootElement.TryGetProperty(name, out value);

    /// <summary>Returns whether a member was sent.</summary>
    public bool Has(string name) => _document.RootElement.TryGetProperty(name, out _);

    /// <summary>Reads a member that must be sent as a string.</summary>
    /// <exception cref="BodyException">It is missing, <c>null</c>, or not a string.</exception>
    public string RequiredText(string name) =>
        TryGet(name, out JsonElement value) && Text(value) is string text ? text : throw new BodyException(ErrorCode.Malformed, _malformed);

    /// <summary>Reads a string or <c>null</c>.</summary>
    /// <exception cref="BodyException">The value is another type, or text that is not valid UTF-16.</exception>
    public string? Text(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new BodyException(ErrorCode.Malformed, _malformed);
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape: valid JSON syntax, but not text (design §14.5).
            throw new BodyException(ErrorCode.Malformed, _malformed);
        }
    }

    /// <summary>Reads a whole number or <c>null</c>.</summary>
    /// <exception cref="BodyException">The value is another type, has a fraction, or does not fit 64 bits.</exception>
    public long? Integer(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            _ => throw new BodyException(ErrorCode.Malformed, _malformed),
        };

    public void Dispose() => _document.Dispose();
}
