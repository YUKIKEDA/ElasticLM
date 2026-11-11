namespace ElasticLM.E2E;

/// <summary>
/// The help of the published elm-server: its two lines say how the server is started and where its settings come from, in
/// words a user can act on (design §13.8, §20.7).
/// </summary>
public sealed class ServerHelpTests
{
    /// <summary>
    /// The published elm-server run with --help or -h prints the usage line and the settings line, and ends with exit code 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server; no settings file and no trust, which the help does not read. The cases are --help and -h.
    ///
    /// Steps:
    /// - Run the server with the case's argument to its end.
    ///
    /// Expected:
    /// - Exit code 0, nothing on standard error, and standard output is the two lines
    ///   "Usage: elm-server [--config &lt;settings.json&gt;] [--set-aside-broken-records]" and
    ///   "Settings: the JSON file given with --config; each key can also be set as ELM_&lt;KEY&gt;, for example ELM_PRODUCT.".
    /// </remarks>
    /// <param name="argument">The argument that asks for the help.</param>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Server_Help_PrintsUsageAndSettings(string argument)
    {
        (int code, string output, string error) = await Binaries.RunAsync(Binaries.Server, [argument], Path.GetTempPath());

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            [
                "Usage: elm-server [--config <settings.json>] [--set-aside-broken-records]",
                "Settings: the JSON file given with --config; each key can also be set as ELM_<KEY>, for example ELM_PRODUCT.",
            ],
            output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')
        );
    }
}
