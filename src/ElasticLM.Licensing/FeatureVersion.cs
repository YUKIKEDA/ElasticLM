using System.Globalization;

namespace ElasticLM.Licensing;

/// <summary>
/// A feature version <c>major.minor[.patch]</c> with numbers only (design §9.2). A missing patch is 0.
/// </summary>
internal readonly record struct FeatureVersion(int Major, int Minor, int Patch) : IComparable<FeatureVersion>
{
    public static bool TryParse(ReadOnlySpan<char> text, out FeatureVersion version)
    {
        version = default;
        Span<Range> parts = stackalloc Range[4];
        int count = text.Split(parts, '.');
        if (count is < 2 or > 3)
        {
            return false;
        }

        if (!TryParsePart(text[parts[0]], out int major) || !TryParsePart(text[parts[1]], out int minor))
        {
            return false;
        }

        int patch = 0;
        if (count == 3 && !TryParsePart(text[parts[2]], out patch))
        {
            return false;
        }

        version = new FeatureVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(FeatureVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    private static bool TryParsePart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        foreach (char c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return part.Length is > 0 and <= 9 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// A version range such as <c>&gt;=2.0 &lt;3.0</c>: comparators separated by spaces, all of which must hold (design §9.2).
/// </summary>
internal sealed class FeatureVersionRange
{
    private readonly (string Op, FeatureVersion Version)[] _comparators;

    private FeatureVersionRange((string Op, FeatureVersion Version)[] comparators) => _comparators = comparators;

    public static bool TryParse(string text, out FeatureVersionRange? range)
    {
        range = null;
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 4)
        {
            return false;
        }

        var comparators = new (string, FeatureVersion)[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string op =
                token.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : token.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : token.StartsWith('>') ? ">"
                : token.StartsWith('<') ? "<"
                : token.StartsWith('=') ? "="
                : string.Empty;
            if (op.Length == 0 || !FeatureVersion.TryParse(token.AsSpan(op.Length), out FeatureVersion version))
            {
                return false;
            }

            comparators[i] = (op, version);
        }

        range = new FeatureVersionRange(comparators);
        return true;
    }

    public bool Contains(FeatureVersion version)
    {
        foreach ((string op, FeatureVersion bound) in _comparators)
        {
            int c = version.CompareTo(bound);
            bool holds = op switch
            {
                ">=" => c >= 0,
                "<=" => c <= 0,
                ">" => c > 0,
                "<" => c < 0,
                _ => c == 0,
            };
            if (!holds)
            {
                return false;
            }
        }

        return true;
    }
}
