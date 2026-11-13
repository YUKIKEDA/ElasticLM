using Microsoft.Extensions.Logging;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A logger provider that keeps each entry's category, level, and message, and whether the host disposed it.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, LogLevel Level, string Message)> _entries = [];

    /// <summary>Gets a value indicating whether the host disposed the provider.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Gets the entries written so far.</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Gets the port of the host's one "Now listening on" entry for the scheme: for a listen on port 0, the port the system
    /// chose. The entry is written when the host has started to listen, before the program's own checks after that.
    /// </summary>
    /// <param name="scheme"><c>https</c> or <c>http</c>.</param>
    /// <returns>The port.</returns>
    public int ListeningPort(string scheme)
    {
        const string Listening = "Now listening on: ";
        (_, _, string message) = Assert.Single(
            Entries,
            e => e.Category == "Microsoft.Hosting.Lifetime" && e.Message.StartsWith($"{Listening}{scheme}://", StringComparison.Ordinal)
        );
        return new Uri(message[Listening.Length..]).Port;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() => Disposed = true;

    private sealed class Logger(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
            {
                owner._entries.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }
}
