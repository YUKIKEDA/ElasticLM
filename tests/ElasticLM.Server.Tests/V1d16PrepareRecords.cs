using System.Security.Cryptography;
using ElasticLM.Leasing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// TEMPORARY (V1d16 review K2, removed in the next commit): writes data folders with a full borrow record for the NativeAOT
/// start measurement of the temporary workflow. Skipped unless V1D16_PREPARE names an output folder.
/// </summary>
public sealed class V1d16PrepareRecords
{
    /// <summary>
    /// Writes case-0, case-999, and case-1999: a server data folder whose newest borrow record holds 10,000 early returns
    /// (8 fingerprints each) carried forward, then that many returns written one at a time.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - V1D16_PREPARE names an empty folder.
    ///
    /// Steps:
    /// - Write the folders.
    ///
    /// Expected:
    /// - Each folder holds settings.json, root.pub.pem, and data.
    /// </remarks>
    [Fact]
    public async Task V1d16Prepare_WritesTheRecords()
    {
        string? output = Environment.GetEnvironmentVariable("V1D16_PREPARE");
        Assert.SkipWhen(output is null, "Set V1D16_PREPARE to write the measurement folders.");
#pragma warning disable RS0030 // The records must be valid at the measurement's real time.
        DateTimeOffset now = DateTimeOffset.UtcNow;
#pragma warning restore RS0030
        await using var deployment = new Deployment();
        deployment.Time.SetUtcNow(now);
        string folder = deployment.NewFolder();
        deployment.WriteServerLicense(folder, s => s with { ExpiresAt = now.AddDays(365), BorrowReturnLimit = 1_000 });
        await using (ElmServer server = await deployment.StartServerAsync(folder))
        {
            Write(server, 10_000, now);
        }

        await (await deployment.StartServerAsync(folder)).DisposeAsync();
        foreach (int k in new[] { 0, 999, 1_999 })
        {
            string master = deployment.NewFolder();
            Copy(folder, master);
            if (k > 0)
            {
                await using ElmServer server = await deployment.StartServerAsync(master);
                Write(server, k, now.AddDays(-31));
            }

            foreach (int run in Enumerable.Range(1, 5))
            {
                string target = Path.Combine(output!, $"case-{k}", $"run-{run}");
                Directory.CreateDirectory(target);
                Copy(master, Path.Combine(target, "data"));
                File.Copy(deployment.RootKeyFile, Path.Combine(target, "root.pub.pem"));
                File.WriteAllText(
                    Path.Combine(target, "settings.json"),
                    "{\"product\":\"acme-cad\",\"listen\":\"https://127.0.0.1:7272\",\"license\":\"data/server.elmlic\",\"dataDirectory\":\"data\",\"trust\":{\"rootKeys\":[\"root.pub.pem\"]}}\n"
                );
            }
        }
    }

    private static void Write(ElmServer server, int count, DateTimeOffset at)
    {
        for (int i = 0; i < count; i++)
        {
            string[] prints = [.. Enumerable.Range(0, 8).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)))];
            server.Record.Returned(new BorrowReturn(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(11)), "k" + i, prints, at));
        }
    }

    private static void Copy(string from, string to)
    {
        if (!Directory.Exists(to))
        {
            ElasticLM.Client.ProtectedFiles.CreateDirectory(to, ElasticLM.Client.KeyScope.Machine);
        }

        foreach (string file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file);
            if (name is not ("server.lock" or "admin.sock"))
            {
                File.Copy(file, Path.Combine(to, name));
            }
        }

        foreach (string sub in Directory.GetDirectories(from))
        {
            Copy(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
