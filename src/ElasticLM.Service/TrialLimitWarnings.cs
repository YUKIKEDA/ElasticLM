using System.Collections.Concurrent;

namespace ElasticLM.Service;

/// <summary>
/// When this node warns that a product reached its limit of trials in 24 hours (design §12.9, §18.2, V1d17): once a product,
/// and again only 24 hours after its last warning. A node keeps the times in memory, one a product that reached its limit.
/// </summary>
internal sealed class TrialLimitWarnings
{
    private readonly ConcurrentDictionary<Guid, long> _warned = new();

    /// <summary>
    /// Returns whether a refusal at a time is the one that warns: the product has no warning yet, or its last one is 24 hours
    /// old or more. When two refusals ask at once, one of them warns.
    /// </summary>
    /// <param name="product">The product's ID.</param>
    /// <param name="at">The time of the refusal, in Unix seconds.</param>
    public bool Begin(Guid product, long at)
    {
        while (true)
        {
            if (!_warned.TryGetValue(product, out long last))
            {
                if (_warned.TryAdd(product, at))
                {
                    return true;
                }

                continue;
            }

            if (at < last + Policies.Day)
            {
                return false;
            }

            if (_warned.TryUpdate(product, at, last))
            {
                return true;
            }
        }
    }
}
