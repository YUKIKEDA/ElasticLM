using System.Net;

namespace ElasticLM.Server;

/// <summary>
/// The <c>allow</c> and <c>deny</c> settings (design §13.8). An entry is an IP address, an IP range in CIDR form, or
/// <c>host:</c> / <c>user:</c> followed by a host name or OS user name.
/// </summary>
/// <remarks>
/// IP entries apply to every lease request. Host and user entries apply to new leases only, because only the acquire request
/// carries them, and only when the client sends them (<c>ClientInfoStrategy: HostAndUser</c>); the client states them, so
/// they cannot be checked (design §5.4). Within each kind a request is refused when a deny entry matches it, or when allow
/// entries of that kind exist and none matches.
/// </remarks>
internal sealed class AccessRules
{
    public static readonly AccessRules Empty = new([], [], [], [], [], []);

    private const int MaxEntries = 1_000;
    private const int MaxNameLength = 255;

    private readonly IPNetwork[] _allowNetworks;
    private readonly IPNetwork[] _denyNetworks;
    private readonly string[] _allowHosts;
    private readonly string[] _denyHosts;
    private readonly string[] _allowUsers;
    private readonly string[] _denyUsers;

    private AccessRules(
        IPNetwork[] allowNetworks,
        IPNetwork[] denyNetworks,
        string[] allowHosts,
        string[] denyHosts,
        string[] allowUsers,
        string[] denyUsers
    )
    {
        _allowNetworks = allowNetworks;
        _denyNetworks = denyNetworks;
        _allowHosts = allowHosts;
        _denyHosts = denyHosts;
        _allowUsers = allowUsers;
        _denyUsers = denyUsers;
    }

    /// <summary>
    /// Reads the entries.
    /// </summary>
    /// <exception cref="ServerStartException">An entry is not valid, or there are more than 1,000.</exception>
    public static AccessRules Parse(IReadOnlyList<string> allow, IReadOnlyList<string> deny)
    {
        if (allow.Count + deny.Count > MaxEntries)
        {
            throw new ServerStartException($"allow and deny hold at most {MaxEntries} entries together.");
        }

        (IPNetwork[] allowNetworks, string[] allowHosts, string[] allowUsers) = Split(allow, "allow");
        (IPNetwork[] denyNetworks, string[] denyHosts, string[] denyUsers) = Split(deny, "deny");
        return new AccessRules(allowNetworks, denyNetworks, allowHosts, denyHosts, allowUsers, denyUsers);
    }

    /// <summary>
    /// Returns whether a request from an address passes the IP entries.
    /// </summary>
    public bool AllowsAddress(IPAddress? address)
    {
        if (_allowNetworks.Length == 0 && _denyNetworks.Length == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        // IPNetwork.Contains matches an IPv4-mapped IPv6 address against an IPv4 range itself.
        return !Array.Exists(_denyNetworks, n => n.Contains(address))
            && (_allowNetworks.Length == 0 || Array.Exists(_allowNetworks, n => n.Contains(address)));
    }

    /// <summary>
    /// Returns whether a new lease for the host and user the client sent passes the host and user entries.
    /// </summary>
    public bool AllowsClient(string? host, string? user) =>
        Allows(_allowHosts, _denyHosts, host, StringComparer.OrdinalIgnoreCase) && Allows(_allowUsers, _denyUsers, user, StringComparer.Ordinal);

    private static bool Allows(string[] allow, string[] deny, string? value, StringComparer comparer)
    {
        bool matchesDeny = value is not null && deny.Contains(value, comparer);
        bool matchesAllow = value is not null && allow.Contains(value, comparer);
        return !matchesDeny && (allow.Length == 0 || matchesAllow);
    }

    private static (IPNetwork[] Networks, string[] Hosts, string[] Users) Split(IReadOnlyList<string> entries, string key)
    {
        var networks = new List<IPNetwork>();
        var hosts = new List<string>();
        var users = new List<string>();
        foreach (string entry in entries)
        {
            if (entry.StartsWith("host:", StringComparison.Ordinal))
            {
                hosts.Add(Name(entry["host:".Length..], entry, key));
            }
            else if (entry.StartsWith("user:", StringComparison.Ordinal))
            {
                users.Add(Name(entry["user:".Length..], entry, key));
            }
            else if (IPNetwork.TryParse(entry, out IPNetwork network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out IPAddress? address))
            {
                networks.Add(new IPNetwork(address, address.GetAddressBytes().Length * 8));
            }
            else
            {
                throw new ServerStartException($"{key} entry '{entry}' is not an IP address, a CIDR range, host:NAME, or user:NAME.");
            }
        }

        return ([.. networks], [.. hosts], [.. users]);
    }

    private static string Name(string name, string entry, string key) =>
        name.Length is > 0 and <= MaxNameLength ? name : throw new ServerStartException($"{key} entry '{entry}' has an empty or too long name.");
}

/// <summary>
/// A token bucket per source (design §13.8 <c>rateLimit.perIp</c>): each source may send its rate per second, with bursts
/// up to the same number. A source is an IPv4 address or an IPv6 /64 network, because one IPv6 host may use any address of
/// its /64. When the table is full, new sources share one overflow bucket rather than being refused, so addresses an
/// attacker cycles through cannot lock out the clients. It reads time from <see cref="TimeProvider"/>, which the BCL rate
/// limiters do not accept (dotnet/runtime#106447), so tests drive it with a fake clock.
/// </summary>
internal sealed class IpRateLimiter(int perSecond, TimeProvider time, int capacity = 100_000)
{
    private readonly Dictionary<IPAddress, Bucket> _buckets = [];
    private readonly Bucket _overflow = new() { Tokens = perSecond, Timestamp = time.GetTimestamp() };
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>
    /// Takes one request from the source's bucket.
    /// </summary>
    /// <returns><see langword="false"/> when the source sent more than its rate (<c>429 ELM_RATE_LIMITED</c>).</returns>
    public bool TryAcquire(IPAddress address)
    {
        IPAddress source = SourceOf(address);
        long now = time.GetTimestamp();
        lock (_lock)
        {
            if (!_buckets.TryGetValue(source, out Bucket? bucket))
            {
                if (_buckets.Count >= capacity)
                {
                    bucket = _overflow;
                }
                else
                {
                    _buckets[source] = new Bucket { Tokens = perSecond - 1, Timestamp = now };
                    return true;
                }
            }

            Refill(bucket, now);
            if (bucket.Tokens < 1)
            {
                return false;
            }

            bucket.Tokens -= 1;
            return true;
        }
    }

    // An IPv4 address (also when it arrives IPv4-mapped), or the /64 network of an IPv6 address.
    private static IPAddress SourceOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address;
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Forgets addresses whose bucket is full again, so the table holds only addresses active in the last second.
    /// </summary>
    public void Purge()
    {
        long now = time.GetTimestamp();
        lock (_lock)
        {
            foreach ((IPAddress address, Bucket bucket) in _buckets.ToArray())
            {
                Refill(bucket, now);
                if (bucket.Tokens >= perSecond)
                {
                    _buckets.Remove(address);
                }
            }
        }
    }

    private void Refill(Bucket bucket, long now)
    {
        double elapsed = time.GetElapsedTime(bucket.Timestamp, now).TotalSeconds;
        bucket.Tokens = Math.Min(perSecond, bucket.Tokens + (elapsed * perSecond));
        bucket.Timestamp = now;
    }

    private sealed class Bucket
    {
        public double Tokens { get; set; }

        public long Timestamp { get; set; }
    }
}
