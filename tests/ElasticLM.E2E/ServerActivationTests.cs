using System.Text.Json;

namespace ElasticLM.E2E;

/// <summary>
/// The on-premises server gets its license from the hosted service with the published elm (design §9.6, §13.7, §20.7).
/// </summary>
public sealed class ServerActivationTests
{
    /// <summary>
    /// elm server activate turns the service's license key into the server's license, and the started server serves it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service with product acme-cad (feature pro), a server policy, and a license of 3 seats; the
    ///   published elm-server, run once without its license so that it makes its installation key.
    ///
    /// Steps:
    /// - elm server activate the key --product acme-cad --data the server's folder --service the service --license the file --trust the root key;
    ///   start the server with the service's address; elm server status --json.
    ///
    /// Expected:
    /// - activate exits 0; status exits 0 and names the service's license. No lease is taken: the service's server licenses
    ///   have leases of at least 120 seconds, and a server just started takes none for a lease's length (design §13.4).
    /// </remarks>
    /// <param name="database">The service's database.</param>
    [Theory]
    [MemberData(nameof(ServiceProcess.Databases), MemberType = typeof(ServiceProcess))]
    public async Task Activate_FromTheService_ServerServesTheLicense(string database)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, database);
        string product = await service.ProductAsync("acme-cad");
        (string licenseId, string key) = await service.LicenseAsync(
            await service.PolicyAsync(product, "\"licenseKind\":\"server\",\"leaseDuration\":120"),
            seats: 3
        );

        string license = vendor.File("activated.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "activated", "acme-cad", license, service.Url);
        await server.InstallationKeyAsync();
        await vendor.ElmOkAsync(
            "server",
            "activate",
            key,
            "--product",
            "acme-cad",
            "--data",
            server.DataDirectory,
            "--service",
            service.Url.ToString(),
            "--license",
            license,
            "--trust",
            vendor.RootPublicKey
        );
        await server.StartAsync();

        (int code, string status, string error) = await server.ElmServerAsync(vendor, "status", "--json");
        Assert.True(code == 0, error);
        using JsonDocument json = JsonDocument.Parse(status);
        Assert.Equal(licenseId, json.RootElement.GetProperty("license").GetString());
    }
}
