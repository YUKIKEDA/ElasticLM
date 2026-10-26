namespace ElasticLM.Cli;

/// <summary>
/// What a command reads from and writes to outside itself, so tests can run commands in process.
/// </summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required TimeProvider Time { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>Gets the reader of a secret typed by a person, or <see langword="null"/> when nobody can type (redirected input).</summary>
    public Func<string, string?>? ReadSecret { get; init; }

    public static CliHost System() =>
        new()
        {
            Out = Console.Out,
            Error = Console.Error,
            Time = TimeProvider.System,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            ReadSecret = Console.IsInputRedirected ? null : ReadMasked,
        };

    private static string ReadMasked(string prompt)
    {
        Console.Error.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
