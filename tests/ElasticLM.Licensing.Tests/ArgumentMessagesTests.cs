namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="ArgumentMessages"/> (design §17.3, V1c10).
/// </summary>
public sealed class ArgumentMessagesTests
{
    /// <summary>
    /// The part .NET appends for the parameter is removed from the message of each kind of argument exception, and the
    /// "Actual value was" line of an out-of-range exception with it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An ArgumentException with a message and a parameter name; the exceptions of ArgumentException.ThrowIfNullOrEmpty(""),
    ///   ArgumentNullException.ThrowIfNull(null), and ArgumentOutOfRangeException.ThrowIfLessThan(0, 1), each with a
    ///   parameter name; an ArgumentOutOfRangeException with an actual value and one without; a type derived from
    ///   ArgumentException that does not override Message.
    ///
    /// Steps:
    /// - Read the message of each through WithoutParameterName.
    ///
    /// Expected:
    /// - The message without " (Parameter '…')" and without the line after it: "A root key is not a P-256 public key in
    ///   PEM.", "The value cannot be an empty string.", "Value cannot be null.", "count ('0') must be greater than or equal
    ///   to '1'.", "The clock skew must be between zero and one hour." twice, and "The lease was made by another client.".
    /// </remarks>
    [Fact]
    public void WithoutParameterName_ParameterNamed_RemovesTheAppendedPart()
    {
        Assert.Equal(
            "A root key is not a P-256 public key in PEM.",
            ArgumentMessages.WithoutParameterName(new ArgumentException("A root key is not a P-256 public key in PEM.", "rootPublicKeysPem"))
        );
        Assert.Equal(
            "The value cannot be an empty string.",
            ArgumentMessages.WithoutParameterName(Catch(() => ArgumentException.ThrowIfNullOrEmpty(string.Empty, "featureId")))
        );
        Assert.Equal(
            "Value cannot be null.",
            ArgumentMessages.WithoutParameterName(Catch(() => ArgumentNullException.ThrowIfNull((object?)null, "key")))
        );
        Assert.Equal(
            "count ('0') must be greater than or equal to '1'.",
            ArgumentMessages.WithoutParameterName(Catch(() => ArgumentOutOfRangeException.ThrowIfLessThan(0, 1, "count")))
        );
        Assert.Equal(
            "The clock skew must be between zero and one hour.",
            ArgumentMessages.WithoutParameterName(
                new ArgumentOutOfRangeException("options", TimeSpan.FromHours(2), "The clock skew must be between zero and one hour.")
            )
        );
        Assert.Equal(
            "The clock skew must be between zero and one hour.",
            ArgumentMessages.WithoutParameterName(new ArgumentOutOfRangeException("options", "The clock skew must be between zero and one hour."))
        );
        Assert.Equal(
            "The lease was made by another client.",
            ArgumentMessages.WithoutParameterName(new Derived("The lease was made by another client.", "lease"))
        );
    }

    /// <summary>
    /// An exception without a parameter name keeps its message.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An ArgumentException without a parameter name, one with an empty parameter name, and an ArgumentOutOfRangeException
    ///   without one.
    ///
    /// Steps:
    /// - Read the message of each through WithoutParameterName.
    ///
    /// Expected:
    /// - Each message unchanged: "The product ID must be lowercase letters." twice and "The index is out of range.".
    /// </remarks>
    [Fact]
    public void WithoutParameterName_NoParameterName_KeepsTheMessage()
    {
        Assert.Equal(
            "The product ID must be lowercase letters.",
            ArgumentMessages.WithoutParameterName(new ArgumentException("The product ID must be lowercase letters."))
        );
        Assert.Equal(
            "The product ID must be lowercase letters.",
            ArgumentMessages.WithoutParameterName(new ArgumentException("The product ID must be lowercase letters.", string.Empty))
        );
        Assert.Equal(
            "The index is out of range.",
            ArgumentMessages.WithoutParameterName(new ArgumentOutOfRangeException(null, "The index is out of range."))
        );
    }

    /// <summary>
    /// A message that does not end with exactly the part .NET appends is kept as it is, such as the message of a derived type
    /// that overrides Message.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A derived type with the parameter name "path" whose Message is its own text, without the appended part; and one whose
    ///   Message adds a sentence after the appended part.
    ///
    /// Steps:
    /// - Read the message of each through WithoutParameterName.
    ///
    /// Expected:
    /// - "The path is not valid." and "The path is not valid. (Parameter 'path') Use another folder.", unchanged.
    /// </remarks>
    [Fact]
    public void WithoutParameterName_MessageDoesNotEndWithThePart_KeepsTheMessage()
    {
        Assert.Equal("The path is not valid.", ArgumentMessages.WithoutParameterName(new Overridden("The path is not valid.", "path", null)));
        Assert.Equal(
            "The path is not valid. (Parameter 'path') Use another folder.",
            ArgumentMessages.WithoutParameterName(new Overridden("The path is not valid.", "path", " Use another folder."))
        );
    }

    private static ArgumentException Catch(Action action) => Assert.ThrowsAny<ArgumentException>(action);

    private sealed class Derived(string message, string paramName) : ArgumentException(message, paramName);

    // Its own text alone, or the base message with a sentence after it.
    private sealed class Overridden(string text, string paramName, string? after) : ArgumentException(text, paramName)
    {
        public override string Message => after is null ? Text : base.Message + after;

        private string Text { get; } = text;
    }
}
