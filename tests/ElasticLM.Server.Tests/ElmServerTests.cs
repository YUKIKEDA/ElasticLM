using System.Security.Cryptography;

namespace ElasticLM.Server.Tests;

/// <summary>
/// Start errors of <see cref="ElmServer"/> from its settings: a <see cref="ServerStartException"/>, which elm-server prints
/// as one "error:" line before it exits with 1, with no .NET parameter name in it (design §13.1, §17.3, V1c10).
/// </summary>
public sealed class ElmServerTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// Trust settings that the trust store refuses stop the start with the trust store's sentence, and an empty revoked key
    /// ID with a sentence that names the setting.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; a trust.rootKeys file with a P-384 public key.
    ///
    /// Steps:
    /// - Start with that file as trust.rootKeys; start with trust.revokedKeyIds of the root key's ID and "".
    ///
    /// Expected:
    /// - ServerStartException "The trust settings are not valid: A root key is not a P-256 public key in PEM."; and
    ///   ServerStartException "trust.revokedKeyIds must not hold an empty or null entry.".
    /// </remarks>
    [Fact]
    public async Task Start_TrustSettingsNotValid_ThrowsWithoutParameterName()
    {
        string data = _deployment.NewFolder("server");
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string p384File = _deployment.FilePath("p384.pub.pem");
        File.WriteAllText(p384File, p384.ExportSubjectPublicKeyInfoPem() + "\n");

        var notP256 = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { TrustRootKeyFiles = [p384File] })
        );
        var emptyId = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { TrustRevokedKeyIds = [_deployment.Trust.RootKeyIds.Single(), string.Empty] })
        );

        Assert.Equal("The trust settings are not valid: A root key is not a P-256 public key in PEM.", notP256.Message);
        Assert.Equal("trust.revokedKeyIds must not hold an empty or null entry.", emptyId.Message);
    }

    /// <summary>
    /// An empty dataDirectory, or one of white space only, stops the start with a sentence that names the setting, not with
    /// the key store's ArgumentException (design §17.3, V1c10 R5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, whose path the settings then replace.
    ///
    /// Steps:
    /// - Start with dataDirectory ""; start with dataDirectory of three spaces.
    ///
    /// Expected:
    /// - ServerStartException "dataDirectory must not be empty or only white space." both times.
    /// </remarks>
    [Fact]
    public async Task Start_DataDirectoryBlank_ThrowsNamingTheSetting()
    {
        string data = _deployment.NewFolder("server");
        foreach (string blank in new[] { string.Empty, "   " })
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(data, s => s with { DataDirectory = blank })
            );
            Assert.Equal("dataDirectory must not be empty or only white space.", refused.Message);
        }
    }

    /// <summary>
    /// An empty license setting, or one of white space only, stops the start with a sentence that names the setting, not with
    /// the ArgumentException of the file system (design §17.3, V1c10 D3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, whose license path the settings then replace.
    ///
    /// Steps:
    /// - Start with license ""; start with license of three spaces.
    ///
    /// Expected:
    /// - ServerStartException "license must not be empty or only white space." both times.
    /// </remarks>
    [Fact]
    public async Task Start_LicenseBlank_ThrowsNamingTheSetting()
    {
        string data = _deployment.NewFolder("server");
        foreach (string blank in new[] { string.Empty, "   " })
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(data, s => s with { LicensePath = blank })
            );
            Assert.Equal("license must not be empty or only white space.", refused.Message);
        }
    }
}
