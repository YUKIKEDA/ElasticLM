using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>The body of a cloud lease's acquisition (design §12.7.6).</summary>
internal sealed record AcquireRequest(
    string LicenseKey,
    string Product,
    string Feature,
    string? Version,
    long Count,
    long? Borrow,
    IReadOnlyList<string> Fingerprints,
    ClientInfo? Client,
    string? App
);

/// <summary>
/// The rules of the bodies of cloud lease requests (design §8.6, §12.7.1, §12.7.6): the acquisition reuses the members and
/// limits of an activation, and a renewal is the <c>{seq, retry?}</c> of a refresh.
/// </summary>
internal static class LeaseRequests
{
    public const string Malformed = "The request body is not a valid lease request.";

    /// <summary>The longest version sent (design §8.6).</summary>
    public const int MaxVersionLength = 32;

    /// <summary>The longest borrow period: 100 years of 366 days (design §8.6).</summary>
    public const long MaxBorrowSeconds = 3_162_240_000;

    /// <summary>The length of a lease ID: 128 bits in base64url (design §14.2).</summary>
    public const int LeaseIdLength = 22;

    /// <summary>Reads the body of an acquisition.</summary>
    /// <exception cref="BodyException">A member is missing, of the wrong type, or past its limit (<c>ELM_MALFORMED</c>).</exception>
    public static AcquireRequest ReadAcquire(RequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string key = body.RequiredText("licenseKey");
        string product = Activations.ProductCode(body, "product", Malformed);
        string feature = Activations.ProductCode(body, "feature", Malformed);
        string? version = body.TryGet("version", out JsonElement versionValue) ? body.Text(versionValue) : null;
        if (version is { Length: > MaxVersionLength })
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        long count =
            body.TryGet("count", out JsonElement countValue) && body.Integer(countValue) is long n
                ? n
                : throw new BodyException(ErrorCode.Malformed, Malformed);
        long? borrow = body.TryGet("borrow", out JsonElement borrowValue) ? body.Integer(borrowValue) : null;
        if (borrow is < 0 or > MaxBorrowSeconds)
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        IReadOnlyList<string> fingerprints =
            body.TryGet("fingerprints", out JsonElement printsValue) && printsValue.ValueKind != JsonValueKind.Null
                ? Activations.Fingerprints(body, "fingerprints", Malformed)
                : [];
        return new AcquireRequest(
            key,
            product,
            feature,
            version,
            count,
            borrow,
            fingerprints,
            Activations.Client(body, Malformed),
            Activations.App(body, Malformed)
        );
    }

    /// <summary>Returns whether a text has the form of a lease ID: 22 characters of base64url.</summary>
    public static bool IsLeaseId(string? id) =>
        id is { Length: LeaseIdLength } && id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
