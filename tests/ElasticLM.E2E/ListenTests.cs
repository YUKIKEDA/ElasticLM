using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server and elm-service on a port another socket holds: each ends with exit code 1 and one line on
/// standard error that names the address, after the host's own "Hosting failed to start" on standard output (design §13.1,
/// §14.8, V1c9).
/// </summary>
public sealed class ListenTests
{
    /// <summary>
    /// The published elm-server whose listen port another socket holds ends with exit code 1 and the line
    /// "error: elm-server cannot listen on …" last on standard error, not with an unhandled exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with a server license issued by elm, bound to the key the server made on its
    ///   first run; a TcpListener of the test started on 127.0.0.1 at a port the OS picked, which the server is given as
    ///   listen, before that first run.
    ///
    /// Steps:
    /// - Run the server to its end.
    ///
    /// Expected:
    /// - Exit code 1; standard output holds "Hosting failed to start"; the last line of standard error begins
    ///   "error: elm-server cannot listen on https://127.0.0.1:{port}/ and ".
    /// </remarks>
    [Fact]
    public async Task Server_ListenPortHeld_ExitsWithOneLine()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");

        // The test holds a port the system chose and gives it to the server, so no other program can take it between the
        // two (Issue #399). The run without a license below ends before the server listens.
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", license, port: port);
        string serverKey = await server.InstallationKeyAsync();
        string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        await vendor.ElmOkAsync(
            "license",
            "issue",
            "--signing-key",
            vendor.SigningKey,
            "--certificate",
            vendor.Certificate,
            "--product",
            "acme-cad",
            "--license-id",
            "e2e-server",
            "--kind",
            "server",
            "--feature",
            "pro;seats=3",
            "--expires",
            expires,
            "--bind-key",
            serverKey,
            "--trust",
            vendor.RootPublicKey,
            "--out",
            license
        );

        (int code, string output, string error) = await server.RunToExitAsync();

        Assert.Equal(1, code);
        Assert.Contains("Hosting failed to start", output, StringComparison.Ordinal);
        Assert.StartsWith($"error: elm-server cannot listen on https://127.0.0.1:{port}/ and ", LastLine(error), StringComparison.Ordinal);
    }

    /// <summary>
    /// The published elm-service whose listen port another socket holds ends with exit code 1 and the line
    /// "elm-service cannot listen on …" last on standard error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service set up on SQLite with the vendor's keys (a certificate for license, lease, and log); a
    ///   TcpListener of the test started on 127.0.0.1 at a port the OS picked, which the service is given as listen.
    ///
    /// Steps:
    /// - Run the service to its end.
    ///
    /// Expected:
    /// - Exit code 1; standard output holds "Hosting failed to start"; the last line of standard error begins
    ///   "elm-service cannot listen on http://127.0.0.1:{port}/: ".
    /// </remarks>
    [Fact]
    public async Task Service_ListenPortHeld_ExitsWithOneLine()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        (int code, string output, string error) = await ServiceProcess.RunToExitAsync(vendor, port);

        Assert.Equal(1, code);
        Assert.Contains("Hosting failed to start", output, StringComparison.Ordinal);
        Assert.StartsWith($"elm-service cannot listen on http://127.0.0.1:{port}/: ", LastLine(error), StringComparison.Ordinal);
    }

    private static string LastLine(string text) => text.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r');
}
