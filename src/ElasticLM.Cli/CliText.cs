using System.Globalization;
using System.Text;

namespace ElasticLM.Cli;

/// <summary>
/// Text from outside <c>elm</c> made safe to write to a terminal (design §16, V1d27): an answer of a service, an identity
/// provider, or a server, and a file whose signature was not checked.
/// </summary>
internal static class CliText
{
    /// <summary>
    /// Replaces each control character (<see cref="char.IsControl(char)"/>: U+0000 to U+001F, U+007F, and U+0080 to
    /// U+009F) with <c>\uXXXX</c>, so that the text cannot move the cursor, clear the screen, or rewrite a line.
    /// </summary>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    /// <param name="keepLines"><see langword="true"/> to keep line feeds and tabs, for a body written as lines.</param>
    /// <returns>The text with its control characters replaced; an empty string for <see langword="null"/>.</returns>
    public static string Printable(string? text, bool keepLines = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int first = 0;
        while (first < text.Length && !Replaced(text[first], keepLines))
        {
            first++;
        }

        if (first == text.Length)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        result.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (Replaced(c, keepLines))
            {
                result.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static bool Replaced(char c, bool keepLines) => char.IsControl(c) && !(keepLines && c is '\n' or '\t');
}
