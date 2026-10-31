using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Licensing;
using ElasticLM.Measure;

// design §20.7 (V1b): the load of the §4.2 targets against a published elm-server or elm-service that run.ps1 started.
//   leases       --url <https> --trust <root.pub.pem> --keys <n> --per-key <n> --acquire-rate <n/s> --renew-seconds <s> --cycles <n> --work <folder> --out <json>
//   activations  --url <http> --license-key-file <file> --count <n> --rate <n/s> --work <folder> --out <json>
//   admin-writes --url <http> --api-key-file <file> --count <n> --runs <n> --out <json>
if (args.Length == 0 || args[0] is not ("leases" or "activations" or "admin-writes"))
{
    Console.Error.WriteLine("usage: ElasticLM.Measure leases|activations|admin-writes --name value ...");
    return 2;
}

Dictionary<string, string> options = [];
for (int i = 1; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

using var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1024,

    // elm-server listens on https with its own self-signed certificate; an app checks its key against the server license
    // (design §12.7.5). The measurement keeps the certificate of its first connection to the loopback server and refuses
    // any other, the way an app keeps its pin.
    SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => Pin.Matches(certificate) },
};
using var http = new HttpClient(handler) { BaseAddress = new Uri(options["url"]), Timeout = TimeSpan.FromMinutes(2) };
await using FileStream output = File.Create(options["out"]);
await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
writer.WriteStartObject();
int result = args[0] switch
{
    "leases" => await Leases.RunAsync(http, options, writer),
    "activations" => await Activations.RunAsync(http, options, writer),
    _ => await AdminWrites.RunAsync(http, options, writer),
};
writer.WriteEndObject();
return result;

/// <summary>Reads the options of the tool.</summary>
internal static class Option
{
    public static int Int(Dictionary<string, string> options, string name) => int.Parse(options[name], CultureInfo.InvariantCulture);

    public static double Double(Dictionary<string, string> options, string name) => double.Parse(options[name], CultureInfo.InvariantCulture);
}

/// <summary>The latest time by which a scheduled send started later than its time, across tasks.</summary>
internal sealed class Lag
{
    private long _ticks;

    public TimeSpan Max => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Waits until <paramref name="at"/> after <paramref name="start"/>, then records how late the send starts.</summary>
    public async Task UntilAsync(long start, TimeSpan at)
    {
        TimeSpan wait = at - TimeProvider.System.GetElapsedTime(start);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, TimeProvider.System);
        }

        long late = (TimeProvider.System.GetElapsedTime(start) - at).Ticks;
        long seen;
        while (late > (seen = Interlocked.Read(ref _ticks)) && Interlocked.CompareExchange(ref _ticks, late, seen) != seen) { }
    }
}

/// <summary>
/// 10,000 concurrent leases of elm-server (design §4.2, §20.7): keys that each hold the same number of leases, taken at a
/// steady rate once the reclaim time after the start has passed, then renewed at a fixed interval from their acquisition.
/// Every lease token is checked against the server license after the timed part.
/// </summary>
internal static class Leases
{
    private const string Acquire = """{"feature":"pro","count":1}""";

    public static async Task<int> RunAsync(HttpClient http, Dictionary<string, string> options, Utf8JsonWriter writer)
    {
        int keys = Option.Int(options, "keys");
        int perKey = Option.Int(options, "per-key");
        double rate = Option.Double(options, "acquire-rate");
        var interval = TimeSpan.FromSeconds(Option.Double(options, "renew-seconds"));
        int cycles = Option.Int(options, "cycles");
        string work = options["work"];
        Device[] devices = [.. Enumerable.Range(0, keys).Select(i => new Device(http, "acme-cad", Path.Combine(work, "key-" + i)))];
        try
        {
            // design §13.4: the server refuses new leases for a lease's length after it starts, and says how long.
            long waitStart = TimeProvider.System.GetTimestamp();
            while (true)
            {
                Answer probe = await devices[0].PostAsync("v1/leases", Acquire, includeKey: true);
                if (probe.Code is null)
                {
                    await devices[0].DeleteAsync($"v1/leases/{Device.Lease(probe.Body).Id}");
                    break;
                }

                if (probe.Code != "ELM_SERVER_RECOVERING")
                {
                    throw new InvalidOperationException($"The first acquisition was refused with {probe.Code} ({(int)probe.Status}).");
                }

                await Task.Delay(probe.RetryAfter ?? TimeSpan.FromSeconds(5), TimeProvider.System);

                // The reclaim time (a lease's length, 10 minutes) outlasts a nonce (300 seconds, design §12.2): get a new one.
                await devices[0].PrepareAsync();
            }

            double recoveryWait = TimeProvider.System.GetElapsedTime(waitStart).TotalSeconds;
            var acquired = new Latencies();
            var renewed = new Latencies();
            var lag = new Lag();
            using LeaseChecker checker = await LeaseChecker.CreateAsync(http, options["trust"]);
            long start = TimeProvider.System.GetTimestamp();

            // Lease j of key i is taken at (j × keys + i) / rate seconds and renewed every interval after that, cycles times.
            await Task.WhenAll(
                devices.Select(
                    (device, i) =>
                        Task.Run(async () =>
                        {
                            var held = new List<(TimeSpan At, string Id, long Seq)>();
                            for (int j = 0; j < perKey; j++)
                            {
                                var at = TimeSpan.FromSeconds(((j * keys) + i) / rate);
                                await lag.UntilAsync(start, at);
                                Answer answer = await device.PostAsync("v1/leases", Acquire, includeKey: true);
                                if (answer.Code is string refused)
                                {
                                    acquired.Failed(refused);
                                    continue;
                                }

                                acquired.Succeeded(answer.Milliseconds);
                                (string id, long seq) = Device.Lease(answer.Body);
                                held.Add((at, id, seq));
                                checker.Check(answer.Body, device.Thumbprint);
                            }

                            for (int c = 1; c <= cycles; c++)
                            {
                                for (int k = 0; k < held.Count; k++)
                                {
                                    (TimeSpan at, string id, long seq) = held[k];
                                    await lag.UntilAsync(start, at + (interval * c));
                                    Answer answer = await device.PostAsync($"v1/leases/{id}/renew", $$"""{"seq":{{seq}}}""", includeKey: false);
                                    if (answer.Code is string refused)
                                    {
                                        renewed.Failed(refused);
                                        continue;
                                    }

                                    renewed.Succeeded(answer.Milliseconds);
                                    held[k] = (at, id, Device.Lease(answer.Body).Seq);
                                    checker.Check(answer.Body, device.Thumbprint);
                                }
                            }
                        })
                )
            );

            writer.WriteNumber("keys", keys);
            writer.WriteNumber("leases", keys * perKey);
            writer.WriteNumber("recoveryWaitSeconds", Math.Round(recoveryWait, 1));
            writer.WriteNumber("maxSendLagMs", Math.Round(lag.Max.TotalMilliseconds, 1));
            acquired.Write(writer, "acquire");
            renewed.Write(writer, "renew");
            writer.WriteNumber("tokensVerified", checker.Verified);
            writer.WriteNumber("tokensRefused", checker.Refused);
            return 0;
        }
        finally
        {
            foreach (Device device in devices)
            {
                device.Dispose();
            }
        }
    }
}

/// <summary>
/// Checks each lease as an app checks it (design §12.7.5), against the server license and the key the server serves, outside
/// the timed part of the request and right after its answer, while the token is current.
/// </summary>
internal sealed class LeaseChecker : IDisposable
{
    private readonly LeaseVerifier _leases;
    private readonly ServerIdentity _server;
    private int _verified;
    private int _refused;

    private LeaseChecker(LeaseVerifier leases, ServerIdentity server)
    {
        _leases = leases;
        _server = server;
    }

    public int Verified => Volatile.Read(ref _verified);

    public int Refused => Volatile.Read(ref _refused);

    public static async Task<LeaseChecker> CreateAsync(HttpClient http, string trustFile)
    {
        using JsonDocument served = JsonDocument.Parse(await http.GetStringAsync("v1/server/license"));
        byte[] license = Encoding.UTF8.GetBytes(served.RootElement.GetProperty("license").GetRawText());
        byte[] jwk = Encoding.UTF8.GetBytes(served.RootElement.GetProperty("key").GetRawText());
        TrustStore trust = TrustStore.Create([await File.ReadAllTextAsync(trustFile)], []);
        var licenses = new LicenseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance);
        if (!ServerIdentity.TryVerify(licenses, license, jwk, "acme-cad", out ServerIdentity? identity, out LicenseError? error))
        {
            throw new InvalidOperationException("The server license does not verify: " + error?.Message);
        }

        return new LeaseChecker(new LeaseVerifier(trust, TimeProvider.System, StatelessClockGuard.Instance), identity!);
    }

    /// <summary>Checks the lease of an answer for the installation with the thumbprint given.</summary>
    public void Check(string body, string thumbprint)
    {
        LeaseVerificationResult result = _leases.VerifyOnPremises(Device.Token(body), _server, new LeaseExpectation("acme-cad", thumbprint));
        Interlocked.Increment(ref result is LeaseVerificationResult.Valid ? ref _verified : ref _refused);
    }

    public void Dispose() => _server.Dispose();
}

/// <summary>
/// Online activations of elm-service at a steady rate (design §4.2, §12.6.1, §20.7), measured as in M4b1: the keys and then
/// the nonces are made before the clock starts, and each request is timed from building its proof to its answer.
/// </summary>
internal static class Activations
{
    // The requests and the flushes listed one by one: those slower than these.
    private const double SlowMilliseconds = 50;
    private const double SlowFlushMilliseconds = 20;

    // design §12.2: a nonce lives 300 seconds; every one must still be alive when it is used.
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromSeconds(300);

    // Writes 4 KiB and flushes it to the disk every 50 ms until stopped, and returns when each flush began and how long it took.
    private static async Task<List<(double At, double Milliseconds)>> SampleFlushes(string path, long start, CancellationToken stop)
    {
        var times = new List<(double, double)>();
        byte[] block = new byte[4096];
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
        while (!stop.IsCancellationRequested)
        {
            double at = TimeProvider.System.GetElapsedTime(start).TotalSeconds;
            long begin = TimeProvider.System.GetTimestamp();
            file.Position = 0;
            file.Write(block);
            file.Flush(flushToDisk: true);
            times.Add((at, TimeProvider.System.GetElapsedTime(begin).TotalMilliseconds));
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return times;
    }

    public static async Task<int> RunAsync(HttpClient http, Dictionary<string, string> options, Utf8JsonWriter writer)
    {
        int count = Option.Int(options, "count");
        double rate = Option.Double(options, "rate");
        string key = (await File.ReadAllTextAsync(options["license-key-file"])).Trim();
        string work = options["work"];

        long keysStart = TimeProvider.System.GetTimestamp();
        Device[] devices = [.. Enumerable.Range(0, count).Select(i => new Device(http, "acme-cad", Path.Combine(work, "device-" + i)))];
        try
        {
            double keysSeconds = TimeProvider.System.GetElapsedTime(keysStart).TotalSeconds;
            long noncesStart = TimeProvider.System.GetTimestamp();
            foreach (Device device in devices)
            {
                await device.PrepareAsync();
            }

            var activated = new Latencies();
            var lag = new Lag();
            var slow = new List<(double At, double Milliseconds, int InFlight)>();
            int inFlight = 0;
            using var stop = new CancellationTokenSource();
            long start = TimeProvider.System.GetTimestamp();

            // Issue #182 (the p99 that missed its target): a disk-flush sampler beside the load, on the same disk as the
            // service's database, to tell a slow flush from a wait for SQLite's write lock.
            Task<List<(double At, double Milliseconds)>> flushes = Task.Run(() =>
                SampleFlushes(Path.Combine(work, "flush.probe"), start, stop.Token)
            );

            // Request k is sent at k / rate seconds, whether or not the ones before it have been answered.
            await Task.WhenAll(
                devices.Select(
                    (device, k) =>
                        Task.Run(async () =>
                        {
                            var at = TimeSpan.FromSeconds(k / rate);
                            await lag.UntilAsync(start, at);
                            string fingerprint = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                            Interlocked.Increment(ref inFlight);
                            Answer answer = await device.PostAsync(
                                "v1/activations",
                                $$"""{"licenseKey":"{{key}}","product":"acme-cad","fingerprints":["{{fingerprint}}"]}""",
                                includeKey: true
                            );
                            int others = Interlocked.Decrement(ref inFlight);
                            if (answer.Milliseconds > SlowMilliseconds)
                            {
                                lock (slow)
                                {
                                    slow.Add((at.TotalSeconds, answer.Milliseconds, others));
                                }
                            }

                            if (answer.Code is string refused)
                            {
                                activated.Failed(refused);
                            }
                            else
                            {
                                activated.Succeeded(answer.Milliseconds);
                            }
                        })
                )
            );
            await stop.CancelAsync();
            List<(double At, double Milliseconds)> flushTimes = await flushes;

            TimeSpan nonceWindow = TimeProvider.System.GetElapsedTime(noncesStart);
            writer.WriteNumber("keysSeconds", Math.Round(keysSeconds, 1));
            writer.WriteNumber("nonceWindowSeconds", Math.Round(nonceWindow.TotalSeconds, 1));
            writer.WriteNumber("runSeconds", Math.Round(TimeProvider.System.GetElapsedTime(start).TotalSeconds, 1));
            writer.WriteNumber("maxSendLagMs", Math.Round(lag.Max.TotalMilliseconds, 1));
            activated.Write(writer, "activate");

            // The slow requests: when each was sent, how long it took, and how many others were in flight as it ended.
            writer.WriteStartArray("slow");
            foreach ((double sentAt, double milliseconds, int others) in slow.OrderBy(s => s.At))
            {
                writer.WriteStartObject();
                writer.WriteNumber("atS", Math.Round(sentAt, 2));
                writer.WriteNumber("ms", Math.Round(milliseconds, 1));
                writer.WriteNumber("inFlight", others);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            double[] sortedFlushes = [.. flushTimes.Select(f => f.Milliseconds).Order()];
            writer.WriteStartObject("flush");
            writer.WriteNumber("count", sortedFlushes.Length);
            if (sortedFlushes.Length > 0)
            {
                writer.WriteNumber("p50Ms", Math.Round(Latencies.Percentile(sortedFlushes, 0.50), 2));
                writer.WriteNumber("p99Ms", Math.Round(Latencies.Percentile(sortedFlushes, 0.99), 2));
                writer.WriteNumber("maxMs", Math.Round(sortedFlushes[^1], 2));
            }

            writer.WriteStartArray("slow");
            foreach ((double flushedAt, double milliseconds) in flushTimes.Where(f => f.Milliseconds > SlowFlushMilliseconds))
            {
                writer.WriteStartObject();
                writer.WriteNumber("atS", Math.Round(flushedAt, 2));
                writer.WriteNumber("ms", Math.Round(milliseconds, 1));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            return nonceWindow < NonceLifetime ? 0 : 1;
        }
        finally
        {
            foreach (Device device in devices)
            {
                device.Dispose();
            }
        }
    }
}

/// <summary>
/// The admin API's audited write of M4a1 (design §4.2): API keys made one after another, each an authentication, a key row
/// and an audit row in one transaction, as the average of a batch, several batches.
/// </summary>
internal static class AdminWrites
{
    public static async Task<int> RunAsync(HttpClient http, Dictionary<string, string> options, Utf8JsonWriter writer)
    {
        int count = Option.Int(options, "count");
        int runs = Option.Int(options, "runs");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            (await File.ReadAllTextAsync(options["api-key-file"])).Trim()
        );

        writer.WriteStartArray("averageMs");
        for (int run = 0; run < runs; run++)
        {
            long start = TimeProvider.System.GetTimestamp();
            for (int n = 0; n < count; n++)
            {
                using var body = new StringContent(
                    $$"""{"name":"m-{{run}}-{{n}}","permissions":["audit.read"]}""",
                    Encoding.UTF8,
                    "application/json"
                );
                using HttpResponseMessage response = await http.PostAsync("v1/admin/api-keys", body);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"POST v1/admin/api-keys answered {(int)response.StatusCode}.");
                }
            }

            writer.WriteNumberValue(Math.Round(TimeProvider.System.GetElapsedTime(start).TotalMilliseconds / count, 3));
        }

        writer.WriteEndArray();
        return 0;
    }
}

/// <summary>The certificate of the first connection, which every later connection must present.</summary>
internal static class Pin
{
    private static readonly Lock Gate = new();
    private static byte[]? _hash;

    public static bool Matches(System.Security.Cryptography.X509Certificates.X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(certificate.GetRawCertData());
        lock (Gate)
        {
            _hash ??= hash;
            return CryptographicOperations.FixedTimeEquals(_hash, hash);
        }
    }
}
