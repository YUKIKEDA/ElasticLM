using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace ElasticLM.Server.Tests;

/// <summary>
/// As a Windows service, elm-server writes its operational log to the event log from Warning up unless the settings name
/// the event log's provider (design §18.3, V1d29a).
/// </summary>
public sealed class ServerServiceTests
{
    /// <summary>
    /// The event log writes elm-server's own category from Warning up by default; a setting for all providers does not
    /// change that, a setting of the event log's provider, by its alias or its full name, does for what it names, and a
    /// setting of that provider for another category leaves this one at Warning.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, where the event log's provider runs.
    /// - A logging builder as the host of a Windows service makes it: the settings' rules, the minimum level Information,
    ///   the event log, and the event log's default rule (ServerService.KeepEventLogAtWarning).
    ///
    /// Steps:
    /// - For each case of settings, ask a logger of the category elm-server whether it writes Debug, Information, and
    ///   Warning. The cases: no setting; Default Trace for all providers; Default Information for EventLog; Default
    ///   Information for the full name; the category at Debug for EventLog; another category at Debug for EventLog; Default
    ///   Error for EventLog.
    ///
    /// Expected:
    /// - Warning only, with no setting and with Default Trace for all providers; Information and Warning for Default
    ///   Information under either name; all three for the category at Debug; Warning only when EventLog names another
    ///   category, which leaves the rest at Warning; none for Default Error.
    /// </remarks>
    [Theory]
    [InlineData("", false, false, true)]
    [InlineData("LogLevel:Default=Trace", false, false, true)]
    [InlineData("EventLog:LogLevel:Default=Information", false, true, true)]
    [InlineData("{eventlog}:LogLevel:Default=Information", false, true, true)]
    [InlineData("EventLog:LogLevel:elm-server=Debug", true, true, true)]
    [InlineData("EventLog:LogLevel:Other.Category=Debug", false, false, true)]
    [InlineData("EventLog:LogLevel:Default=Error", false, false, false)]
    public void KeepEventLogAtWarning_EachSetting_WritesFromWarningUnlessTheEventLogIsNamed(
        string settings,
        bool debug,
        bool information,
        bool warning
    )
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The event log's provider runs on Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            // CA1416 does not read SkipUnless as a platform check.
            return;
        }

        Assert.Equal((debug, information, warning), Levels(settings));
    }

    /// <summary>
    /// The name that the rules give the event log's provider is the provider's full type name.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Read ServerService.EventLogProvider, and the alias of EventLog through HostRequestLog.ProviderOf.
    ///
    /// Expected:
    /// - Both are Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider.
    /// </remarks>
    [Fact]
    public void EventLogProvider_IsTheFullTypeName()
    {
        Assert.Equal("Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider", ServerService.EventLogProvider);
        Assert.Equal("Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider", HostRequestLog.ProviderOf("EventLog"));
    }

    /// <summary>Adds the event log and its default rule, as the host of a Windows service does.</summary>
    [SupportedOSPlatform("windows")]
    internal static void AddEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog();
        ServerService.KeepEventLogAtWarning(logging);
    }

    // Whether a logger of the category elm-server writes Debug, Information, and Warning under settings of the form
    // "key=value;…" below Logging, where {eventlog} stands for the full type name of the event log's provider.
    [SupportedOSPlatform("windows")]
    private static (bool Debug, bool Information, bool Warning) Levels(string settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string pair in settings.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=');
            values["Logging:" + parts[0].Replace("{eventlog}", typeof(EventLogLoggerProvider).FullName, StringComparison.Ordinal)] = parts[1];
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.SetMinimumLevel(LogLevel.Information);
            AddEventLog(logging);
        });
        using ServiceProvider built = services.BuildServiceProvider();
        ILogger logger = built.GetRequiredService<ILoggerFactory>().CreateLogger("elm-server");
        return (logger.IsEnabled(LogLevel.Debug), logger.IsEnabled(LogLevel.Information), logger.IsEnabled(LogLevel.Warning));
    }
}
