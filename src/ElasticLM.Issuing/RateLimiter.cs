using System.Net;

namespace ElasticLM.Issuing;

/// <summary>
/// A token bucket per key (design §13.8 and §14.7): each key may make its rate of requests per second, with bursts up to the
/// same number. When the table is full, new keys share one overflow bucket rather than being refused, so keys an attacker
/// cycles through cannot lock out the others. It reads time from <see cref="TimeProvider"/>, which the BCL rate limiters do
/// not accept (dotnet/runtime#106447, design decision 0.46), so tests drive it with a fake clock.
/// </summary>
/// <typeparam name="TKey">What a bucket belongs to: a source address (<see cref="RateLimitSource.Of"/>) or an API key ID.</typeparam>
/// <param name="perSecond">The requests each key may make per second, and the burst.</param>
/// <param name="time">The time provider.</param>
/// <param name="capacity">The most keys kept; beyond it, new keys share the overflow bucket.</param>
public sealed class RateLimiter<TKey>(int perSecond, TimeProvider time, int capacity = 100_000)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Bucket> _buckets = [];
    private readonly Bucket _overflow = new() { Tokens = perSecond, Timestamp = time.GetTimestamp() };
    private readonly Lock _lock = new();

    /// <summary>Gets the number of keys that have their own bucket.</summary>
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
    /// Takes one request from the key's bucket.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="false"/> when the key made more than its rate (<c>429 ELM_RATE_LIMITED</c>).</returns>
    public bool TryAcquire(TKey key)
    {
        long now = time.GetTimestamp();
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                if (_buckets.Count >= capacity)
                {
                    bucket = _overflow;
                }
                else
                {
                    _buckets[key] = new Bucket { Tokens = perSecond - 1, Timestamp = now };
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

    /// <summary>
    /// Forgets keys whose bucket is full again, so the table holds only keys active in the last second.
    /// </summary>
    public void Purge()
    {
        long now = time.GetTimestamp();
        lock (_lock)
        {
            foreach ((TKey key, Bucket bucket) in _buckets.ToArray())
            {
                Refill(bucket, now);
                if (bucket.Tokens >= perSecond)
                {
                    _buckets.Remove(key);
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

/// <summary>
/// The source a rate limit counts a request against (design §13.8).
/// </summary>
public static class RateLimitSource
{
    /// <summary>
    /// Returns the source of an address: an IPv4 address (also when it arrives IPv4-mapped), or the /64 network of an IPv6
    /// address, because one IPv6 host may use any address of its /64.
    /// </summary>
    /// <param name="address">The remote address.</param>
    /// <returns>The source.</returns>
    public static IPAddress Of(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
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
}
