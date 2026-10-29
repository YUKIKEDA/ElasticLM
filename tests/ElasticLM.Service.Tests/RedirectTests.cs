using System.Net;
using System.Net.Sockets;
using System.Text;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The clients that send a license key do not follow redirects (design §12.1): a 3xx is an error, so a misconfigured
/// proxy cannot send the key on to another place.
/// </summary>
public sealed class RedirectTests
{
    /// <summary>
    /// A <see cref="LicenseClient"/> with a license key and an <see cref="ActivationClient"/> stop at a 307 and send nothing
    /// to the place it names.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A loopback listener that answers every request with 307 and a Location of /moved on itself.
    ///
    /// Steps:
    /// - Acquire a lease with a license key; activate with the same key; read the request lines the listener got.
    ///
    /// Expected:
    /// - Both fail; no request reached /moved, and no request line or body carried the key to it.
    /// </remarks>
    [Fact]
    public async Task Clients_Redirect_AreNotFollowed()
    {
        await using TestService test = TestService.Open("sqlite");
        using var listener = new RedirectingListener();
        TrustStore trust = TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []);
        const string Key = "ELM-AAAA-BBBB-CCCC-DDDD";
        CancellationToken cancel = TestContext.Current.CancellationToken;

        var leaseOptions = new LicenseClientOptions
        {
            ProductId = "app",
            ServerUrl = listener.Url,
            LicenseKey = Key,
            Trust = trust,
            DataDirectory = test.FilePath("lease"),
            TimeProvider = test.Time,
        };
        Assert.True(LicenseClient.TryCreate(leaseOptions, out LicenseClient? leases, out LicenseError? error), error?.Message);
        Assert.IsType<LeaseResult.Failed>(await leases!.AcquireLeaseAsync("core", cancellationToken: cancel));
        await leases.DisposeAsync();

        var activationOptions = new ActivationClientOptions
        {
            ProductId = "app",
            ServiceUrl = listener.Url,
            Trust = trust,
            DataDirectory = test.FilePath("activation"),
            TimeProvider = test.Time,
        };
        Assert.True(ActivationClient.TryCreate(activationOptions, out ActivationClient? activations, out error), error?.Message);
        Assert.IsType<ActivationResult.Failed>(await activations!.ActivateAsync(Key, cancel));
        await activations.DisposeAsync();

        string[] requests = listener.Requests();
        Assert.NotEmpty(requests);
        Assert.DoesNotContain(requests, r => r.Contains("/moved", StringComparison.Ordinal));
    }

    /// <summary>Answers every request on a loopback port with 307 to /moved on itself, and records each request's head.</summary>
    private sealed class RedirectingListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<string> _requests = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public RedirectingListener()
        {
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(LoopAsync);
        }

        public Uri Url { get; }

        public string[] Requests()
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The loop ends with the listener stopped.
            }

            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    var head = new StringBuilder();
                    byte[] one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                    {
                        head.Append((char)one[0]);
                    }

                    lock (_requests)
                    {
                        _requests.Add(head.ToString());
                    }

                    string answer = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {Url}moved\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
                }
            }
        }
    }
}
