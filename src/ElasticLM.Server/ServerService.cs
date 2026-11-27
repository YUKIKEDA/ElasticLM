using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace ElasticLM.Server;

/// <summary>
/// elm-server as a Windows service (design §13.1, V1d29a): the service lifetime under the service control manager, the
/// event log as the operational log from Warning up (design §18.3), and the start errors in the event log.
/// </summary>
internal static class ServerService
{
    /// <summary>The name of the service and the source of its entries in the Application event log.</summary>
    public const string Name = "elm-server";

    /// <summary>The event ID of a start error in the event log (design §13.1).</summary>
    public const int StartErrorEventId = 50;

    /// <summary>Gets the full name of the event log's provider, which rules may also name by its alias <c>EventLog</c>.</summary>
    public static string EventLogProvider { get; } = typeof(EventLogLoggerProvider).FullName!;

    /// <summary>
    /// On Windows, lets the host run under the service control manager: <c>AddWindowsService</c> changes nothing unless the
    /// process was started by it, and then gives the host the service lifetime and the event log. The event log writes
    /// from Warning up unless the settings name its provider.
    /// </summary>
    public static void AddTo(IServiceCollection services, ILoggingBuilder logging)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        services.AddWindowsService(options => options.ServiceName = Name);
        KeepEventLogAtWarning(logging);
    }

    /// <summary>
    /// After the settings' rules, adds a rule of the event log's provider for every category at Warning, unless a rule names
    /// that provider: the default builder of .NET does the same (<c>HostingHostBuilderExtensions.AddDefaultServices</c>),
    /// and the slim builder does not. As a rule of the provider, it is not lowered by a setting for all providers.
    /// </summary>
    public static void KeepEventLogAtWarning(ILoggingBuilder logging) =>
        logging
            .Services.AddOptions<LoggerFilterOptions>()
            .PostConfigure(options =>
            {
                if (!options.Rules.Any(rule => HostRequestLog.ProviderOf(rule.ProviderName) == EventLogProvider))
                {
                    options.Rules.Add(new LoggerFilterRule(EventLogProvider, null, LogLevel.Warning, null));
                }
            });

    /// <summary>
    /// Writes the line that the program prints before it ends with exit code 1 or 2 to the Application event log too, as an
    /// error with <see cref="StartErrorEventId"/>, when the process runs as a Windows service: a service's standard error
    /// reaches nobody (design §13.1, V1d29a).
    /// </summary>
    /// <param name="line">The line as printed, from <c>error: </c> on.</param>
    public static void ReportStartError(string line)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
        {
            return;
        }

        try
        {
            // Registers the source when it is missing, which an administrator's account, LocalSystem among them, may do.
            EventLog.WriteEntry(Name, line, EventLogEntryType.Error, StartErrorEventId);
        }
        catch (Exception e) when (e is SecurityException or InvalidOperationException or Win32Exception or ArgumentException or IOException)
        {
            // Nothing else is left to tell: the line went to standard error already.
        }
    }
}
