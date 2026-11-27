using System.Text;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// <see cref="CliText.Printable"/>: the control characters of text from outside elm, replaced before it reaches a terminal
/// (design §16, V1d27).
/// </summary>
public sealed class CliTextTests
{
    /// <summary>
    /// Every control character of C0, DEL, and C1 becomes \uXXXX with four upper-case hexadecimal digits, and every other
    /// character stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Each of the 65 control characters (U+0000 to U+001F, U+007F, U+0080 to U+009F) between "a" and "b"; the
    ///   characters next to them (space, "~", U+00A0, U+00FF), U+FFFD, a surrogate pair (U+1F600), and a backslash.
    ///
    /// Steps:
    /// - Call Printable on each, without and with keepLines.
    ///
    /// Expected:
    /// - Each control character: "a\u0000b" ... "a\u009Fb", written out here from the code point; with keepLines the same,
    ///   but for line feed and tab, which stay.
    /// - The others come back unchanged; null and the empty string give the empty string.
    /// </remarks>
    [Fact]
    public void Printable_EachCharacter_ReplacesOnlyControlCharacters()
    {
        int[] controls = [.. Enumerable.Range(0, 0x20), 0x7F, .. Enumerable.Range(0x80, 0x20)];
        foreach (int code in controls)
        {
            string text = "a" + (char)code + "b";
            string escaped = "a\\u" + code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + "b";
            Assert.Equal(escaped, CliText.Printable(text));
            Assert.Equal(code is 0x0A or 0x09 ? text : escaped, CliText.Printable(text, keepLines: true));
        }

        foreach (string kept in new[] { "a b", "a~b", "a b", "aÿb", "a�b", "a\U0001F600b", "a\\u001Bb", "日本語" })
        {
            Assert.Equal(kept, CliText.Printable(kept));
            Assert.Equal(kept, CliText.Printable(kept, keepLines: true));
        }

        Assert.Equal(string.Empty, CliText.Printable(null));
        Assert.Equal(string.Empty, CliText.Printable(string.Empty));
    }

    /// <summary>
    /// A long text with terminal sequences throughout is replaced to its end, with nothing cut.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100,000 times "x" followed by ESC "[2J" CSI (U+009B) "1m" and CR, and a lone high surrogate at the end.
    ///
    /// Steps:
    /// - Call Printable.
    ///
    /// Expected:
    /// - The same text built with "\u001B", "\u009B", and "\u000D" in their places; the lone surrogate stays (it is not a
    ///   control character, and the readers of elm never hand one on).
    /// </remarks>
    [Fact]
    public void Printable_LongText_IsReplacedToItsEnd()
    {
        var text = new StringBuilder();
        var expected = new StringBuilder();
        for (int i = 0; i < 100_000; i++)
        {
            text.Append("x\u001B[2J\u009B1m\r");
            expected.Append("x\\u001B[2J\\u009B1m\\u000D");
        }

        text.Append('\uD800');
        expected.Append('\uD800');

        Assert.Equal(expected.ToString(), CliText.Printable(text.ToString()));
    }
}
