using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ElasticLM.Service.Tests;

/// <summary>
/// License keys (design §9.5): the form, the bits they carry, and the hash that ignores case and '-'.
/// </summary>
public sealed partial class LicenseKeysTests
{
    /// <summary>
    /// A key is written from the first 100 bits of its random bytes in Crockford's base32, five characters a group.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Byte strings whose keys were worked out outside the product (a short Python script: the first 100 bits as one
    ///   number, 5 bits a character).
    ///
    /// Steps:
    /// - Format 01 02 … 0D, thirteen FF, and thirteen 00; make 1,000 new keys.
    ///
    /// Expected:
    /// - ELM-04106-1050R-3GG28-A1C60; ELM-ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ; ELM-00000-00000-00000-00000; every new key in the form
    ///   ^ELM(-[0-9A-HJKMNP-TV-Z]{5}){4}$ and all 1,000 different.
    /// </remarks>
    [Fact]
    public void Format_RandomBytes_GiveTheReferenceKeys()
    {
        Assert.Equal("ELM-04106-1050R-3GG28-A1C60", LicenseKeys.Format([.. Enumerable.Range(1, 13).Select(i => (byte)i)]));
        Assert.Equal("ELM-ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ", LicenseKeys.Format([.. Enumerable.Repeat((byte)0xFF, 13)]));
        Assert.Equal("ELM-00000-00000-00000-00000", LicenseKeys.Format(new byte[13]));

        string[] keys = [.. Enumerable.Range(0, 1_000).Select(_ => LicenseKeys.NewKey())];
        Assert.All(keys, k => Assert.Matches(KeyForm(), k));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The stored hash is the SHA-256 of the key in upper case without '-', so a key typed in lower case or without dashes
    /// finds the same license; text that is not a key has no normal form.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The key ELM-04106-1050R-3GG28-A1C60.
    ///
    /// Steps:
    /// - Hash it as written, in lower case, without dashes, and with dashes elsewhere; normalize a key with I, L, O, or U; with
    ///   19 and 21 characters; with another prefix.
    ///
    /// Expected:
    /// - Four equal hashes, each the SHA-256 of "ELM041061050R3GG28A1C60" in ASCII; null for every other text.
    /// </remarks>
    [Fact]
    public void HashOf_CaseAndDashes_AreIgnored()
    {
        byte[] expected = SHA256.HashData(Encoding.ASCII.GetBytes("ELM041061050R3GG28A1C60"));
        foreach (
            string typed in new[]
            {
                "ELM-04106-1050R-3GG28-A1C60",
                "elm-04106-1050r-3gg28-a1c60",
                "ELM041061050R3GG28A1C60",
                "E-LM04-1061050R3GG28A1C60",
            }
        )
        {
            Assert.Equal(expected, LicenseKeys.HashOf(typed));
        }

        foreach (
            string other in new[]
            {
                "ELM-04106-1050R-3GG28-A1C6I",
                "ELM-04106-1050R-3GG28-A1C6L",
                "ELM-04106-1050R-3GG28-A1C6O",
                "ELM-04106-1050R-3GG28-A1C6U",
                "ELM-04106-1050R-3GG28-A1C6",
                "ELM-04106-1050R-3GG28-A1C600",
                "ELN-04106-1050R-3GG28-A1C60",
            }
        )
        {
            Assert.Null(LicenseKeys.Normalize(other));
        }
    }

    [GeneratedRegex("^ELM(-[0-9A-HJKMNP-TV-Z]{5}){4}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyForm();
}
