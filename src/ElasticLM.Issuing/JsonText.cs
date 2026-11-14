using System.Text.Json;

namespace ElasticLM.Issuing;

/// <summary>
/// Checks that untrusted JSON read with <see cref="JsonDocument"/> holds only text that can be read (design §8.3, V1d23).
/// </summary>
public static class JsonText
{
    /// <summary>
    /// Returns whether every member name and every string of a value can be read as text. A lone surrogate escape
    /// (<c>"\ud800"</c>), a pair in the wrong order, and bytes that are not UTF-8 pass <c>JsonDocument.Parse</c>, and
    /// <see cref="JsonElement.GetString"/> and <see cref="JsonProperty.Name"/> throw <see cref="InvalidOperationException"/>
    /// on them later.
    /// </summary>
    /// <param name="value">The value, nested no deeper than the parse that made it allowed.</param>
    /// <returns><see langword="true"/> when all of its text can be read.</returns>
    public static bool IsReadable(JsonElement value)
    {
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    _ = value.GetString();
                    return true;
                case JsonValueKind.Array:
                    return value.EnumerateArray().All(IsReadable);
                case JsonValueKind.Object:
                    foreach (JsonProperty member in value.EnumerateObject())
                    {
                        _ = member.Name;
                        if (!IsReadable(member.Value))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
