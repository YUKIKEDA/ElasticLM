using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// The token-bucket limiter that elm-server and elm-service share (design §13.7, §14.7).
/// </summary>
public sealed class RateLimiterTests
{
    /// <summary>
    /// The rate limiter allows bursts up to the rate and refills at the rate per second; an IPv6 /64 is one source; when the
    /// table is full, new sources share one overflow bucket instead of being refused; idle sources are forgotten.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A limiter of 2 per second for at most 2 sources, on a fake clock.
    ///
    /// Steps:
    /// - Three requests from A; half a second later one more. One request from each of two addresses in one IPv6 /64 and a
    ///   third from it. Requests from C, D, and E (the table is full). One second later purge; C again.
    ///
    /// Expected:
    /// - Allowed, Allowed, refused; allowed after half a second. The /64 is one source: allowed, allowed, refused. C and D are
    ///   allowed and E refused, from the shared overflow bucket. After the purge no source is tracked and C is allowed.
    /// </remarks>
    [Fact]
    public void TryAcquire_BurstsSourcesAndOverflow_FollowTheRate()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter<IPAddress>(2, time, capacity: 2);
        bool Take(IPAddress address) => limiter.TryAcquire(RateLimitSource.Of(address));
        IPAddress a = IPAddress.Parse("10.0.0.1");

        Assert.True(Take(a));
        Assert.True(Take(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.False(Take(a));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(Take(a));

        Assert.True(Take(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.True(Take(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.False(Take(IPAddress.Parse("2001:db8:1:2::77")));

        Assert.True(Take(IPAddress.Parse("10.0.0.3")));
        Assert.True(Take(IPAddress.Parse("10.0.0.4")));
        Assert.False(Take(IPAddress.Parse("10.0.0.5")));
        Assert.Equal(2, limiter.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        limiter.Purge();
        Assert.Equal(0, limiter.Count);
        Assert.True(Take(IPAddress.Parse("10.0.0.3")));
    }
}
