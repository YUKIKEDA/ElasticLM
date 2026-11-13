using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ElasticLM.Server;

/// <summary>
/// Keeps the web host's request lines out of the operational log (design §18.3, V1d4a). ASP.NET Core writes, in the
/// category <see cref="Category"/> at Information, the path, the query, and the Host header of each request; that category
/// is never written below Warning, whatever the settings say.
/// </summary>
internal static class HostRequestLog
{
    /// <summary>The category of the web host's "Request starting" and "Request finished" lines.</summary>
    public const string Category = "Microsoft.AspNetCore.Hosting.Diagnostics";

    // The name of the added rules: the longest name that can cover the category. A covering name has at most one '*', and
    // the category must start with the part before it and end with the part after it, so no covering name is longer than
    // the category twice with the '*' between. The selector takes the longer name, the '*' counted, and the later rule
    // of two names of one length, so the added rules, which come last, always apply.
    private const string RuleName = Category + "*" + Category;

    // The alias that the ProviderAlias attribute of ConsoleLoggerProvider gives it. The selector takes a rule named by a
    // provider's alias as one named by its full type name, so both names are one provider here. The console is the only
    // provider with an alias that the programs register.
    private const string ConsoleAlias = "Console";

    private static readonly string ConsoleProvider = typeof(ConsoleLoggerProvider).FullName!;

    /// <summary>
    /// After the settings' rules, adds a rule for <see cref="Category"/> for every provider and for each provider that a rule
    /// names: the level the rules before it give the category, and at least Warning. So a higher level of the settings, such
    /// as <c>None</c>, still holds for the category, and no setting brings its request lines back.
    /// </summary>
    public static void RaiseToWarning(ILoggingBuilder logging) => logging.Services.AddOptions<LoggerFilterOptions>().PostConfigure(Raise);

    // The rule that applies to a category and a provider is picked as LoggerRuleSelector.Select picks it (dotnet/runtime
    // release/10.0, src/libraries/Microsoft.Extensions.Logging/src/LoggerRuleSelector.cs): a rule of the provider before a
    // rule of all providers, then the longest name that covers the category, the '*' counted, then the last; with none,
    // the minimum level.
    private static void Raise(LoggerFilterOptions options)
    {
        string?[] providers = [null, .. options.Rules.Select(r => ProviderOf(r.ProviderName)).OfType<string>().Distinct(StringComparer.Ordinal)];
        var added = new List<LoggerFilterRule>();
        foreach (string? provider in providers)
        {
            LoggerFilterRule? applies = null;
            foreach (LoggerFilterRule rule in options.Rules)
            {
                if (
                    ProviderOf(rule.ProviderName) == provider
                    && Covers(rule.CategoryName)
                    && (applies?.CategoryName is null || (rule.CategoryName is not null && rule.CategoryName.Length >= applies.CategoryName.Length))
                )
                {
                    applies = rule;
                }
            }

            if (applies is null && provider is not null)
            {
                // No rule of this provider covers the category, so the rules of all providers decide it.
                continue;
            }

            LogLevel level = applies is null ? options.MinLevel : applies.LogLevel ?? LogLevel.Trace;
            added.Add(new LoggerFilterRule(provider, RuleName, level > LogLevel.Warning ? level : LogLevel.Warning, null));
        }

        foreach (LoggerFilterRule rule in added)
        {
            options.Rules.Add(rule);
        }
    }

    private static string? ProviderOf(string? name) => name == ConsoleAlias ? ConsoleProvider : name;

    // A rule's name covers the host's category when it is absent, or when the category starts with the part before its one
    // '*' and ends with the part after it, ignoring case.
    private static bool Covers(string? name)
    {
        if (name is null)
        {
            return true;
        }

        int wildcard = name.IndexOf('*', StringComparison.Ordinal);
        string prefix = wildcard < 0 ? name : name[..wildcard];
        string suffix = wildcard < 0 ? string.Empty : name[(wildcard + 1)..];
        return Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Category.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
