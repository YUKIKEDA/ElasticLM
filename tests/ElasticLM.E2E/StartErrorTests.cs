namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server with a setting that its libraries would refuse with an ArgumentException: it ends with exit code
/// 1 and one line on standard error that names the setting, not with an unhandled exception (design §13.1, §17.3, V1c10).
/// </summary>
public sealed class StartErrorTests
{
    /// <summary>
    /// The published elm-server whose dataDirectory, license, tls.certificate, or tls.key is white space only ends with exit
    /// code 1 and the line "error: {setting} must not be empty or only white space." last on standard error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with the vendor's root key; the case's variable (ELM_DATADIRECTORY,
    ///   ELM_LICENSE, ELM_TLS_CERTIFICATE, or ELM_TLS_KEY) set to three spaces (an empty variable counts as not set, design
    ///   §13.8). trust.rootKeys has no such case: its variable is a list whose blank entries are dropped.
    ///
    /// Steps:
    /// - Run the server to its end.
    ///
    /// Expected:
    /// - Exit code 1; the last line of standard error is "error: {setting} must not be empty or only white space." with the
    ///   case's setting, and standard error holds no "Unhandled exception".
    /// </remarks>
    /// <param name="variable">The variable of the setting.</param>
    /// <param name="setting">The setting's key (design §13.8).</param>
    [Theory]
    [InlineData("ELM_DATADIRECTORY", "dataDirectory")]
    [InlineData("ELM_LICENSE", "license")]
    [InlineData("ELM_TLS_CERTIFICATE", "tls.certificate")]
    [InlineData("ELM_TLS_KEY", "tls.key")]
    public async Task Server_PathSettingBlank_ExitsWithOneLine(string variable, string setting)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", vendor.File("server.elmlic"));
        server.SetEnvironment(variable, "   ");

        (int code, _, string error) = await server.RunToExitAsync();

        Assert.Equal(1, code);
        Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        Assert.Equal($"error: {setting} must not be empty or only white space.", error.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r'));
    }

    /// <summary>
    /// The published elm-server with admin.listen still set, here through ELM_ADMIN_LISTEN, ends with exit code 2, the code of
    /// a settings error, and the line that says the setting is gone (design §13.8, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with the vendor's root key, and ELM_ADMIN_LISTEN set to
    ///   http://127.0.0.1:7071, the default before the setting went away.
    ///
    /// Steps:
    /// - Run the server to its end.
    ///
    /// Expected:
    /// - Exit code 2; standard error is the one line "error: admin.listen is no longer a setting: the administration API
    ///   listens on the socket file admin.sock in the data folder. Remove admin.listen (ELM_ADMIN_LISTEN)."; the data folder
    ///   is not made.
    /// </remarks>
    [Fact]
    public async Task Server_AdminListenStillSet_ExitsWith2AndTheLine()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", vendor.File("server.elmlic"));
        server.SetEnvironment("ELM_ADMIN_LISTEN", "http://127.0.0.1:7071");

        (int code, _, string error) = await server.RunToExitAsync();

        Assert.Equal(2, code);
        Assert.Equal(
            "error: admin.listen is no longer a setting: the administration API listens on the socket file admin.sock in the data folder. "
                + "Remove admin.listen (ELM_ADMIN_LISTEN).",
            error.Trim()
        );
        Assert.False(Path.Exists(server.DataDirectory));
    }
}
