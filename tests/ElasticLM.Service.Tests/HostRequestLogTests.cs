using System.Net;
using System.Net.Http.Headers;
using ElasticLM.Server.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The operational log of elm-service holds no text of a request's path, query, or Host header: the web host's request
/// lines are written from Warning up only, whatever the settings say (design §18.1, V1d4a).
/// </summary>
public sealed class HostRequestLogTests
{
    // As in the elm-server tests: a path with an encoded line feed and a forged entry after it, a long query, and a Host
    // header of 15 labels of 60 letters, which Kestrel takes.
    private const string ForgedLine = "info: elm-forged-line";
    private static readonly string LongPath =
        "/elm-path-" + new string('p', 3_000) + "%0A" + ForgedLine.Replace(" ", "%20", StringComparison.Ordinal);

    private static readonly string LongQuery = "?elm-query=" + new string('q', 2_000);
    private static readonly string LongHost = "elm-host." + string.Join('.', Enumerable.Repeat(new string('h', 60), 15));
    private static readonly string[] RequestTexts =
    [
        "elm-path-",
        "elm-forged-line",
        "elm-query",
        "elm-host",
        new string('p', 100),
        new string('q', 100),
    ];

    /// <summary>
    /// Requests with a long path, query, and Host header on the listener of elm-service leave none of that text in the
    /// operational log, while the service's own entries and the web host's other entries of the level Information are still
    /// written.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup, listening on http, whose host writes the operational log to a provider of the test from
    ///   the level Information on, the default level.
    /// - A path of 3,000 letters after "/elm-path-", then %0A and "info: elm-forged-line"; a query "elm-query=" with 2,000
    ///   letters; a Host header "elm-host." followed by 15 labels of 60 letters.
    ///
    /// Steps:
    /// - With the query and the Host header: GET the long path; GET /v1/nonce; GET /v1/admin/products with setup's API key;
    ///   the same without a key.
    /// - Stop the service and read the log.
    ///
    /// Expected:
    /// - 404, 200, 200, 401.
    /// - No entry of any category holds "elm-path-", "elm-forged-line", "elm-query", "elm-host", or 100 letters p or q in a
    ///   row, and no entry has the category Microsoft.AspNetCore.Hosting.Diagnostics.
    /// - The log has the service's own entry "service.started: …" at the level Information, and the web host's entries
    ///   "Executing endpoint 'HTTP: GET /v1/nonce'" and "Executing endpoint 'HTTP: GET /v1/admin/products'" at the level
    ///   Information.
    /// </remarks>
    [Fact]
    public async Task Requests_LongPathQueryAndHost_LeaveNoRequestTextInTheLog()
    {
        await using TestService test = new();
        string key = await test.SetupAsync();
        using var log = new RecordingLoggerProvider();

        await using (ElmService service = await test.StartAsync(log: log))
        {
            using var http = new HttpClient { BaseAddress = service.Url };
            Assert.Equal(HttpStatusCode.NotFound, await SendAsync(http, LongPath + LongQuery, null));
            Assert.Equal(HttpStatusCode.OK, await SendAsync(http, "/v1/nonce" + LongQuery, null));
            Assert.Equal(HttpStatusCode.OK, await SendAsync(http, "/v1/admin/products" + LongQuery, key));
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(http, "/v1/admin/products" + LongQuery, null));
        }

        IReadOnlyList<(string Category, LogLevel Level, string Message)> entries = log.Entries;
        foreach (string text in RequestTexts)
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(text, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(entries, e => e.Category == HostRequestLog.Category);
        Assert.Contains(
            entries,
            e => (e.Category, e.Level) == ("elm-service", LogLevel.Information) && e.Message.StartsWith("service.started: ", StringComparison.Ordinal)
        );
        foreach (string path in new[] { "/v1/nonce", "/v1/admin/products" })
        {
            Assert.Contains(
                entries,
                e => e == ("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Information, $"Executing endpoint 'HTTP: GET {path}'")
            );
        }
    }

    /// <summary>
    /// The rule for the web host's request lines keeps them at Warning or above against every setting of the log level, and
    /// keeps a higher level that the settings give.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A logging builder as the host makes it: the settings' rules, the minimum level Information, a provider of the test,
    ///   then the rule.
    ///
    /// Steps:
    /// - For each case of settings, ask a logger of the category Microsoft.AspNetCore.Hosting.Diagnostics whether it writes
    ///   Information, Warning, and Error. The cases: no setting; Default None; Default Error; Default Debug with the
    ///   category itself at Trace; Default Trace for the test's provider alone; the category at Trace for the test's
    ///   provider alone; "Microsoft.AspNetCore.Hosting.*" at Trace; "Microsoft.*" at None; Default None with another
    ///   category at Trace for the test's provider; Default Critical with the category at Information.
    /// - Names with a '*' that cover the category and are longer than it, each at Trace, for all providers and for the
    ///   test's provider alone: the category with '*' after it (41 characters), before it, and in its middle, and the
    ///   category, '*', and the category again (81 characters, the longest name that can cover it).
    ///
    /// Expected:
    /// - Information is never written. Warning and Error are written as the settings give the category, but never below
    ///   Warning: Warning and Error for no setting and for every setting that asks for less, the longer names included;
    ///   nothing for None; Error only for Default Error; nothing for "Microsoft.*" at None, and for Default None beside a
    ///   rule of the provider that does not cover the category; Warning and Error for the category at Information under
    ///   Default Critical, since the category's own setting is the one that applies to it.
    /// </remarks>
    [Theory]
    [InlineData("", false, true, true)]
    [InlineData("LogLevel:Default=None", false, false, false)]
    [InlineData("LogLevel:Default=Error", false, false, true)]
    [InlineData("LogLevel:Default=Debug;LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:Default=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("LogLevel:Microsoft.AspNetCore.Hosting.*=Trace", false, true, true)]
    [InlineData("LogLevel:Microsoft.*=None", false, false, false)]
    [InlineData("LogLevel:Default=None;{provider}:LogLevel:Other.Category=Trace", false, false, false)]
    [InlineData("LogLevel:Default=Critical;LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Information", false, true, true)]
    [InlineData("LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*=Trace", false, true, true)]
    [InlineData("LogLevel:*Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("LogLevel:Microsoft.AspNetCore.Hosting.Diagnost*ics=Trace", false, true, true)]
    [InlineData("LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:*Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnost*ics=Trace", false, true, true)]
    [InlineData("{provider}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    public void RaiseToWarning_EachSetting_KeepsTheRequestLinesOut(string settings, bool information, bool warning, bool error)
    {
        using var provider = new RecordingLoggerProvider();
        Assert.Equal((information, warning, error), Levels(settings, logging => logging.AddProvider(provider)));
    }

    /// <summary>
    /// For the console, the provider the programs write to, a rule named by the provider's alias Console and one named by
    /// its full type name are one provider's rules: the rule for the web host's request lines keeps them at Warning or
    /// above, and keeps a higher level that those rules give together.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A logging builder as the host makes it, with the console provider: the settings' rules, the minimum level
    ///   Information, the console, then the rule.
    ///
    /// Steps:
    /// - For each case of settings, ask a logger of the category whether it writes Information, Warning, and Error. The
    ///   cases, where Console is the alias and the full name is Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider:
    ///   Default Trace for Console; Default None for Console; the category with '*' after it at Trace for Console; the
    ///   category, '*', and the category at Trace for the full name; the category at None for Console beside Default Trace
    ///   for the full name; the category at None for the full name beside Default Trace for Console; Default Trace for
    ///   Console beside the category at Error for the full name.
    ///
    /// Expected:
    /// - Information is never written. Warning and Error for the Trace settings; nothing for Default None and for the
    ///   category at None under either name, since the longer name of the two names' rules applies; Error only for the
    ///   category at Error.
    /// </remarks>
    [Theory]
    [InlineData("Console:LogLevel:Default=Trace", false, true, true)]
    [InlineData("Console:LogLevel:Default=None", false, false, false)]
    [InlineData("Console:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*=Trace", false, true, true)]
    [InlineData("{console}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics*Microsoft.AspNetCore.Hosting.Diagnostics=Trace", false, true, true)]
    [InlineData("Console:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=None;{console}:LogLevel:Default=Trace", false, false, false)]
    [InlineData("{console}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=None;Console:LogLevel:Default=Trace", false, false, false)]
    [InlineData("Console:LogLevel:Default=Trace;{console}:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics=Error", false, false, true)]
    public void RaiseToWarning_ConsoleSettings_KeepsTheRequestLinesOut(string settings, bool information, bool warning, bool error) =>
        Assert.Equal((information, warning, error), Levels(settings, logging => logging.AddConsole()));

    // Whether a logger of the web host's category writes Information, Warning, and Error, under settings of the form
    // "key=value;…" below Logging, where {provider} and {console} stand for the full type names of the test's provider and
    // the console's.
    private static (bool Information, bool Warning, bool Error) Levels(string settings, Action<ILoggingBuilder> addProvider)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string pair in settings.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=');
            string key = parts[0]
                .Replace("{provider}", typeof(RecordingLoggerProvider).FullName, StringComparison.Ordinal)
                .Replace("{console}", typeof(ConsoleLoggerProvider).FullName, StringComparison.Ordinal);
            values["Logging:" + key] = parts[1];
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.SetMinimumLevel(LogLevel.Information);
            addProvider(logging);
            HostRequestLog.RaiseToWarning(logging);
        });
        using ServiceProvider built = services.BuildServiceProvider();
        ILogger logger = built.GetRequiredService<ILoggerFactory>().CreateLogger(HostRequestLog.Category);
        return (logger.IsEnabled(LogLevel.Information), logger.IsEnabled(LogLevel.Warning), logger.IsEnabled(LogLevel.Error));
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient http, string pathAndQuery, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(http.BaseAddress!, pathAndQuery));

        // Without the client's own check of the value, so that the request carries the header as the test wrote it.
        Assert.True(request.Headers.TryAddWithoutValidation("Host", LongHost));
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using HttpResponseMessage answer = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return answer.StatusCode;
    }
}
