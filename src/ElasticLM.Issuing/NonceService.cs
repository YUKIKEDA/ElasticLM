using System.Buffers.Binary;
using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// Issues and checks the nonces that request proofs carry (design §12.2). A nonce holds its issue time and 128 random bits,
/// protected by HMAC-SHA256 with the server's secret, so the server stores nothing to check it.
/// </summary>
public sealed class NonceService
{
    /// <summary>The shortest secret accepted.</summary>
    public const int MinSecretLength = 32;

    private const int TimeLength = 8;
    private const int RandomLength = 16;
    private const int MacLength = 32;
    private const int NonceLength = TimeLength + RandomLength + MacLength;

    /// <summary>How long a nonce is accepted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(300);

    private readonly byte[] _secret;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="NonceService"/> class.
    /// </summary>
    /// <param name="secret">The HMAC secret, at least <see cref="MinSecretLength"/> bytes. An on-premises server creates a new
    /// one at each start (design §13.7); every node of the hosted service shares one (design §14.8). It is copied.</param>
    /// <param name="time">The time provider.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is shorter than <see cref="MinSecretLength"/>.</exception>
    public NonceService(ReadOnlySpan<byte> secret, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (secret.Length < MinSecretLength)
        {
            throw new ArgumentException($"The nonce secret must have at least {MinSecretLength} bytes.", nameof(secret));
        }

        _secret = secret.ToArray();
        _time = time;
    }

    /// <summary>
    /// Issues a new nonce.
    /// </summary>
    /// <returns>The nonce, base64url.</returns>
    public string Issue()
    {
        Span<byte> nonce = stackalloc byte[NonceLength];
        BinaryPrimitives.WriteInt64BigEndian(nonce, _time.GetUtcNow().ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(nonce.Slice(TimeLength, RandomLength));
        HMACSHA256.HashData(_secret, nonce[..(TimeLength + RandomLength)], nonce[(TimeLength + RandomLength)..]);
        return Base64UrlCodec.Encode(nonce);
    }

    /// <summary>
    /// Checks a nonce: issued by this secret, not in the future, and not older than <see cref="Lifetime"/>.
    /// </summary>
    /// <param name="nonce">The nonce text from a proof.</param>
    /// <param name="expiresAt">The time the nonce stops being accepted, when valid.</param>
    /// <returns><see langword="true"/> when the nonce is valid now.</returns>
    public bool IsValid(string? nonce, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (nonce is null || nonce.Length > 128 || !Base64UrlCodec.TryDecode(nonce, out byte[] bytes) || bytes.Length != NonceLength)
        {
            return false;
        }

        Span<byte> mac = stackalloc byte[MacLength];
        HMACSHA256.HashData(_secret, bytes.AsSpan(0, TimeLength + RandomLength), mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, bytes.AsSpan(TimeLength + RandomLength)))
        {
            return false;
        }

        long issued = BinaryPrimitives.ReadInt64BigEndian(bytes);
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (issued > now || now - issued > (long)Lifetime.TotalSeconds)
        {
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(issued) + Lifetime;
        return true;
    }
}
