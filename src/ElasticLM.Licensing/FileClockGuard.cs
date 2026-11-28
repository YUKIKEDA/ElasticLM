using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Licensing;

// design §11.4.

/// <summary>
/// A clock guard that keeps the last time seen in a file. With an installation key the state is signed, and a
/// state whose signature fails is treated as missing. A missing, edited, or unreadable state falls back to the token's
/// <c>iat</c>, so the clock can never go below a token's issue time.
/// </summary>
public sealed class FileClockGuard : IClockGuard
{
    private const int MaxStateLength = 4096;
    private const int RandomLength = 16;
    private const int MaxLeftovers = 1000;
    private const string TemporaryExtension = ".tmp";
    private static readonly SearchValues<char> UppercaseHex = SearchValues.Create("0123456789ABCDEF");
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
    /// <param name="path">The state file path. The directory must exist. On Linux and macOS only the user who writes the state
    /// can read it (mode 0600).</param>
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

    /// <inheritdoc />
    /// <remarks>The state is replaced, also by an earlier time, and written at once.</remarks>
    public void AcceptServerTime(DateTimeOffset serverTime)
    {
        long seconds = serverTime.ToUnixTimeSeconds();
        lock (_lock)
        {
            _lastSeen = seconds;
            _lastFailedWrite = null;
            WriteState(seconds);
        }
    }

    private static byte[] SignedBytes(long lastSeen) => Encoding.UTF8.GetBytes("elm-clock-state:" + lastSeen.ToString(CultureInfo.InvariantCulture));

    private long? ReadState()
    {
        byte[] bytes;
        try
        {
            if (!new FileInfo(_path).Exists)
            {
                return null;
            }

            // design §11.4 step 9 (V1d24d): FileShare.Read, as File.ReadAllBytes. design §8.6 (V1d24c): the limit is checked
            // while the file is read.
            if (BoundedFile.Read(_path, MaxStateLength, FileShare.Read) is not byte[] read)
            {
                return null;
            }

            bytes = read;
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
            if (state.Sig is null || !Base64UrlCodec.TryDecode(state.Sig, out byte[] signature))
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

        RemoveLeftovers();

        // A random name, so that processes writing the same state at once never touch each other's temporary file.
        string temp = _path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomLength / 2)) + TemporaryExtension;
        try
        {
            using FileStream held = OpenNew(temp);
            try
            {
                held.Write(JsonSerializer.SerializeToUtf8Bytes(state, LicensingJsonContext.Default.ClockState));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                throw;
            }

            _lastWritten = lastSeen;
            _lastFailedWrite = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _lastFailedWrite = lastSeen;
        }
    }

    // design §11.4 step 7 (V1d24d), the way FileReplacement in ElasticLM.Client creates its files (§12.6.2): File.WriteAllBytes
    // opens what is at the path and on Linux and macOS takes the mode from the umask (0644 under umask 022, measured in V1d24d);
    // CreateNew refuses anything at the path (O_EXCL), so nothing is deleted first, and the mode is 0600, which the state file
    // keeps after the move. The caller holds the file until it is in place: Delete lets this process move it on Windows, where
    // RemoveLeftovers cannot open it with FileShare.None (both measured on Windows 11 with .NET 10.0.14 in V1d24d); on Linux and
    // macOS the shared flock that .NET takes for this handle refuses RemoveLeftovers' exclusive one. No buffer, so a write that
    // fails throws at once and the dispose writes nothing.
    private static FileStream OpenNew(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,
            BufferSize = 0,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    // design §11.4 step 8 (V1d24d): deletes the temporary files of this state that no writer holds, those left by a write
    // whose delete was refused or by a process that ended between creating and moving. A writer holds its file until it is in
    // place. On Windows an open with FileShare.None fails while one is at work, by the share mode. On Linux and macOS
    // UnixLeftover takes an exclusive flock, which fails while the writer's FileStream holds its shared flock; it opens without
    // waiting and leaves anything that is not a regular file (a FIFO, whose open would wait for a writer; a socket; a device):
    // the owner's 24d-6 and 24d-6a. A link is deleted itself, never followed. Nothing is thrown.
    private void RemoveLeftovers()
    {
        string name = Path.GetFileName(_path);
        try
        {
            int seen = 0;
            foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, name + ".*" + TemporaryExtension))
            {
                if (!IsTemporaryOf(Path.GetFileName(candidate), name))
                {
                    continue;
                }

                if (++seen > MaxLeftovers)
                {
                    return;
                }

                if (!OperatingSystem.IsWindows())
                {
                    UnixLeftover.TryRemove(candidate);
                    continue;
                }

                try
                {
                    if (new FileInfo(candidate).LinkTarget is null)
                    {
                        using var probe = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None);
                    }

                    File.Delete(candidate);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Held by a writer, unreadable, or refused: it stays for a later write.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed; the write that follows fails the same way.
        }
    }

    // <name>.<16 uppercase hexadecimal digits>.tmp, compared case-sensitively: the listing's pattern is not, on Windows.
    private static bool IsTemporaryOf(string candidate, string name) =>
        candidate.Length == name.Length + 1 + RandomLength + TemporaryExtension.Length
        && candidate.StartsWith(name + ".", StringComparison.Ordinal)
        && candidate.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && !candidate.AsSpan(name.Length + 1, RandomLength).ContainsAnyExcept(UppercaseHex);

    // A file that cannot be deleted stays until a later write of this state removes it (design §11.4 step 8, V1d24d).
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays.
        }
    }
}
