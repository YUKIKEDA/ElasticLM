namespace ElasticLM.Licensing;

// design §17.3, V1c10.

/// <summary>
/// The message of an argument exception without the part .NET appends for the parameter, for the places that show it to
/// someone who does not call the .NET API: <c>elm_last_error_message</c>, the <c>error:</c> line of <c>elm</c>, and the start
/// errors of <c>elm-server</c> and <c>elm-service</c>.
/// </summary>
internal static class ArgumentMessages
{
    /// <summary>
    /// Returns the message of <paramref name="exception"/> without the " (Parameter '…')" part that .NET appends, and for an
    /// <see cref="ArgumentOutOfRangeException"/> without the "Actual value was …" line after it. The message is returned
    /// unchanged when the exception has no parameter name or when its message does not end with exactly that part.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The message.</returns>
    public static string WithoutParameterName(ArgumentException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = exception.Message;
        if (string.IsNullOrEmpty(exception.ParamName))
        {
            return message;
        }

        // design §17.3 (V1c10, R6): an exception of the base type with an empty message holds only the appended part, with
        // the same line break (measured on .NET 10.0.14). NativeAOT cannot create an exception of any derived type, so a type
        // that overrides Message does not match and keeps its message.
        string appended = exception is ArgumentOutOfRangeException range
            ? new ArgumentOutOfRangeException(range.ParamName, range.ActualValue, string.Empty).Message
            : new ArgumentException(string.Empty, exception.ParamName).Message;
        return message.EndsWith(appended, StringComparison.Ordinal) ? message[..^appended.Length] : message;
    }
}
