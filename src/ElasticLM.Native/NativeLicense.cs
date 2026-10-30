using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary>
/// <c>elm_license</c>: a verified license with every string already in native memory (design §17.4). Nothing is made later,
/// so readers on several threads see the same values; <see cref="Dispose"/> frees them.
/// </summary>
internal sealed class NativeLicense : IDisposable
{
    private readonly List<nint> _allocations = [];

    public NativeLicense(VerifiedLicense license, bool inGracePeriod)
    {
        Id = Keep(license.LicenseId).Pointer;
        Product = Keep(license.ProductId).Pointer;
        Kind = license.Kind;
        IssuedAt = license.IssuedAt.ToUnixTimeSeconds();
        ExpiresAt = license.ExpiresAt?.ToUnixTimeSeconds();
        GracePeriodSeconds = (long)license.GracePeriod.TotalSeconds;
        InGracePeriod = inGracePeriod;
        Features =
        [
            .. license.Features.Select(f => new FeatureEntry(
                Keep(f.Id).Pointer,
                f.VersionRange is null ? 0 : Keep(f.VersionRange).Pointer,
                f.Seats,
                f.ExpiresAt?.ToUnixTimeSeconds()
            )),
        ];
        Meta =
        [
            .. license.Metadata.Select(m =>
            {
                (nint key, nuint keyLength) = Keep(m.Key);
                (nint value, nuint valueLength) = Keep(m.Value);
                return new MetaEntry(m.Key, key, keyLength, value, valueLength);
            }),
        ];
    }

    public nint Id { get; }

    public nint Product { get; }

    public LicenseKind Kind { get; }

    public long IssuedAt { get; }

    public long? ExpiresAt { get; }

    public long GracePeriodSeconds { get; }

    public bool InGracePeriod { get; }

    public IReadOnlyList<FeatureEntry> Features { get; }

    public IReadOnlyList<MetaEntry> Meta { get; }

    public void Dispose()
    {
        foreach (nint pointer in _allocations)
        {
            NativeStrings.Free(pointer);
        }

        _allocations.Clear();
    }

    private (nint Pointer, nuint Length) Keep(string text)
    {
        (nint pointer, nuint length) = NativeStrings.Allocate(text);
        _allocations.Add(pointer);
        return (pointer, length);
    }

    /// <summary>A feature: its code and version range in native memory, and its optional seats and expiry.</summary>
    public sealed record FeatureEntry(nint Id, nint VersionRange, int? Seats, long? ExpiresAt);

    /// <summary>A meta pair: the key as text for lookups, and both in native memory with their lengths.</summary>
    public sealed record MetaEntry(string Key, nint KeyPointer, nuint KeyLength, nint ValuePointer, nuint ValueLength);
}
