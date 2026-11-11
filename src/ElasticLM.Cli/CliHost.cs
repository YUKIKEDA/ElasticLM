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

    /// <summary>
    /// Gets how a command waits, as <c>elm admin login</c> between polls (design §16); <see langword="null"/> waits on a timer of
    /// <see cref="Time"/>.
    /// </summary>
    public Action<TimeSpan>? Wait { get; init; }

    /// <summary>
    /// Gets how <c>elm server</c> connects to a server's administration socket (design §13.7); <see langword="null"/> connects a
    /// Unix domain socket. Tests replace it to raise a connection error that the OS gives only to another user.
    /// </summary>
    public Func<System.Net.Sockets.UnixDomainSocketEndPoint, CancellationToken, ValueTask<Stream>>? ConnectAdminSocket { get; init; }

    /// <summary>Waits for a time: with <see cref="Wait"/> when set, otherwise on a timer of <see cref="Time"/>.</summary>
    public void Pause(TimeSpan time)
    {
        if (Wait is not null)
        {
            Wait(time);
            return;
        }

        Task.Delay(time, Time).GetAwaiter().GetResult();
    }

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
