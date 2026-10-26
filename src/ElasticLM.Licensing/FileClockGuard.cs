using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// A clock guard that keeps the last time seen in a file (design §11.4). With an installation key the state is signed, and a
/// state whose signature fails is treated as missing. A missing, edited, or unreadable state falls back to the token's
/// <c>iat</c>, so the clock can never go below a token's issue time.
/// </summary>
public sealed class FileClockGuard : IClockGuard
{
    private const int MaxStateLength = 4096;
    private static readonly TimeSpan WriteThreshold = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly ECDsa? _installationKey;
    private readonly Lock _lock = new();
    private long? _lastSeen;
    private long? _lastWritten;
    private long? _lastFailedWrite;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileClockGuard"/> class.
    /// </summary>
    /// <param name="path">The state file path. The directory must exist.</param>
    /// <param name="installationKey">The installation key with its private part to sign the state, or <see langword="null"/> for unbound licenses.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public FileClockGuard(string path, ECDsa? installationKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = Path.GetFullPath(path);
        _installationKey = installationKey;
    }

    /// <inheritdoc />
    public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew)
    {
        long nowSeconds = now.ToUnixTimeSeconds();
        long floor = tokenIssuedAt.ToUnixTimeSeconds();
        lock (_lock)
        {
            long? stored = ReadState();
            long lastSeen = Math.Max(floor, Math.Max(stored ?? long.MinValue, _lastSeen ?? long.MinValue));
            if (nowSeconds < lastSeen - (long)allowedSkew.TotalSeconds)
            {
                return false;
            }

            long next = Math.Max(lastSeen, nowSeconds);
            _lastSeen = next;
            long written = Math.Max(stored ?? long.MinValue, _lastWritten ?? long.MinValue);

            // After a failed write (no directory, no permission, disk full) the guard keeps going on the in-memory value and the
            // token's iat, and does not try again for the same interval, so a read-only location costs no I/O per verification.
            bool retryAllowed = _lastFailedWrite is not long failed || next - failed >= (long)WriteThreshold.TotalSeconds;
            if (retryAllowed && (stored is null || next - written >= (long)WriteThreshold.TotalSeconds))
            {
                WriteState(next);
            }

            return true;
        }
    }

    private static byte[] SignedBytes(long lastSeen) => Encoding.UTF8.GetBytes("elm-clock-state:" + lastSeen.ToString(CultureInfo.InvariantCulture));

    private long? ReadState()
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length > MaxStateLength)
            {
                return null;
            }

            bytes = File.ReadAllBytes(_path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        ClockState? state;
        try
        {
            state = JsonSerializer.Deserialize(bytes, LicensingJsonContext.Default.ClockState);
        }
        catch (JsonException)
        {
            return null;
        }

        if (state is null || state.V != 1 || !Limits.IsTime(state.LastSeen))
        {
            return null;
        }

        if (_installationKey is not null)
        {
            if (state.Sig is null || !Base64UrlCodec.TryDecode(state.Sig, out byte[] signature) || signature.Length != 64)
            {
                return null;
            }

            bool valid = _installationKey.VerifyData(
                SignedBytes(state.LastSeen),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            );
            if (!valid)
            {
                return null;
            }
        }

        return state.LastSeen;
    }

    private void WriteState(long lastSeen)
    {
        var state = new ClockState { V = 1, LastSeen = lastSeen };
        if (_installationKey is not null)
        {
            byte[] signature = _installationKey.SignData(
                SignedBytes(lastSeen),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            );
            state.Sig = Base64UrlCodec.Encode(signature);
        }

        string temp = _path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, LicensingJsonContext.Default.ClockState));
            File.Move(temp, _path, overwrite: true);
            _lastWritten = lastSeen;
            _lastFailedWrite = null;
        }
        catch (IOException)
        {
            _lastFailedWrite = lastSeen;
            TryDelete(temp);
        }
        catch (UnauthorizedAccessException)
        {
            _lastFailedWrite = lastSeen;
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
