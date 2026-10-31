# ElasticLM.Client

The ElasticLM client: installation keys, request proofs, activations with the hosted service, and leases from on-premises
license servers and the hosted service, with automatic renewal and borrowing. It works with NativeAOT and builds on
`ElasticLM.Licensing`.

```csharp
using ElasticLM.Client;
using ElasticLM.Licensing;

var options = new LicenseClientOptions
{
    ProductId = "acme-cad",
    ServerUrl = new Uri("https://licenses.example.com:8443/"),
    Trust = TrustStore.Create([File.ReadAllText("root.pub.pem")], []),
};

if (!LicenseClient.TryCreate(options, out LicenseClient? client, out LicenseError? error))
{
    Console.WriteLine($"{error!.WireName}: {error.Message}");
    return;
}

await using (client)
{
    LeaseResult result = await client!.AcquireLeaseAsync("pro");
    if (result is LeaseResult.Acquired acquired)
    {
        Console.WriteLine($"Lease {acquired.Lease.Id} until {acquired.Lease.ExpiresAt}.");
        await client.ReleaseLeaseAsync(acquired.Lease);
    }
}
```

Source, design, and issues: https://github.com/YUKIKEDA/ElasticLM
