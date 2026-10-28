using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// An activation as kept on the disk (design §12.6.2): its ID, last sequence number, and kind (<c>standard</c>,
/// <c>trial</c>, <c>server</c>), the license file it got, and the <c>jti</c> of requests sent and not yet answered (design
/// §10.4 step 2). A request sent before the first activation has no ID, kind, or license, and sequence number 0.
/// </summary>
internal sealed record StoredActivation(string? ActivationId, long Seq, string? Kind, byte[]? License, IReadOnlyList<string> Retry)
{
    /// <summary>Gets the most <c>jti</c> kept (design §8.6).</summary>
    public const int MaxRetry = 16;

    /// <summary>
    /// Gets the <c>jti</c> of activations and trials sent and not yet answered, kept apart from <see cref="Retry"/> (the kept
    /// activation's refreshes and deactivations), so the success of one does not drop the other's (design §12.6.2).
    /// </summary>
    public IReadOnlyList<string> Pending { get; init; } = [];

    /// <summary>
    /// Gets the hash (SHA-256, base64url) of the last file the service answered and this installation did not keep, or null.
    /// The same file answered again means the service took the request for a resend, so its jti is not kept (design §12.6.2).
    /// </summary>
    public string? Unkept { get; init; }

    /// <summary>Gets the offline request waiting for its answer, or <see langword="null"/> (design §12.8.5).</summary>
    public OfflineRequest? Offline { get; init; }

    /// <summary>Gets the offline deactivation proof not yet passed to the service, or <see langword="null"/> (design §12.8.5).</summary>
    public string? DeactivationProof { get; init; }

    /// <summary>Returns the list a kind of request keeps its jti in.</summary>
    public IReadOnlyList<string> ListFor(bool pending) => pending ? Pending : Retry;

    /// <summary>Returns this activation with a <c>jti</c> added last to one list, the oldest dropped past <see cref="MaxRetry"/>.</summary>
    public StoredActivation WithRetry(string jti, bool pending = false) =>
        pending
            ? this with
            {
                Pending = [.. Pending.Append(jti).TakeLast(MaxRetry)],
            }
            : this with
            {
                Retry = [.. Retry.Append(jti).TakeLast(MaxRetry)],
            };
}

/// <summary>
/// An offline request waiting for its answer (design §12.8.5), kept without the license key: its claims, from which the same
/// signing input is made again, and the SHA-256 (base64url) of the license key an activation request holds.
/// </summary>
internal sealed record OfflineRequest(
    bool Refresh,
    string Nonce,
    long IssuedAt,
    long ExpiresAt,
    long Seq,
    string? ActivationId,
    string? KeyHash,
    IReadOnlyList<string> Retry,
    IReadOnlyList<string> Fingerprints
);

/// <summary>
/// The file <c>activation.json</c> beside the installation key, and the lock file <c>activation.lock</c> that makes the
/// processes of one device send their activation requests one at a time (design §12.5, §12.6.2).
/// </summary>
internal sealed class ActivationStore(string directory, TimeProvider time)
{
    /// <summary>The largest activation file read (design §12.6.2).</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>How long a request waits for another process's lock.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private const string FileName = "activation.json";
    private const string LockName = "activation.lock";
    private const int MaxJtiLength = 64;
    private const int UnkeptLength = 43;
    private const int MaxFileLength = 16 * 1024;
    private const int NonceLength = 22;
    private const int MaxFingerprints = 8;
    private const int FingerprintLength = 32;
    private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(50);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    private string FilePath => Path.Combine(directory, FileName);

    /// <summary>
    /// Reads the activation.
    /// </summary>
    /// <param name="damaged"><see langword="true"/> when a file is there but is too large or not in the form written.</param>
    /// <returns>The activation, or <see langword="null"/> when there is none or it is damaged.</returns>
    public StoredActivation? Read(out bool damaged)
    {
        damaged = false;
        byte[] bytes;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxLength)
            {
                damaged = true;
                return null;
            }

            // A reader lets a writer replace or remove the file, so another process's Check never blocks a save on Windows.
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            damaged = true;
            return null;
        }

        StoredActivation? read = Parse(bytes);
        damaged = read is null;
        return read;
    }

    /// <summary>Writes the activation to a temporary file and moves it over the old one, so a reader never sees half of it.</summary>
    public void Write(StoredActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (activation.ActivationId is string id)
            {
                w.WriteString("activationId", id);
            }
            else
            {
                w.WriteNull("activationId");
            }

            w.WriteNumber("seq", activation.Seq);
            if (activation.Kind is string kind)
            {
                w.WriteString("kind", kind);
            }
            else
            {
                w.WriteNull("kind");
            }

            w.WritePropertyName("license");
            if (activation.License is byte[] license)
            {
                w.WriteRawValue(license);
            }
            else
            {
                w.WriteNullValue();
            }

            w.WriteStartArray("retry");
            foreach (string jti in activation.Retry)
            {
                w.WriteStringValue(jti);
            }

            w.WriteEndArray();
            w.WriteStartArray("pending");
            foreach (string jti in activation.Pending)
            {
                w.WriteStringValue(jti);
            }

            w.WriteEndArray();
            w.WriteString("unkept", activation.Unkept);
            if (activation.Offline is OfflineRequest offline)
            {
                w.WriteStartObject("offline");
                w.WriteString("op", offline.Refresh ? "refresh" : "activate");
                w.WriteString("nonce", offline.Nonce);
                w.WriteNumber("iat", offline.IssuedAt);
                w.WriteNumber("exp", offline.ExpiresAt);
                w.WriteNumber("seq", offline.Seq);
                w.WriteString("act", offline.ActivationId);
                w.WriteString("key", offline.KeyHash);
                WriteList(w, "retry", offline.Retry);
                WriteList(w, "fp", offline.Fingerprints);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("offline");
            }

            w.WriteString("deactivation", activation.DeactivationProof);
            w.WriteEndObject();
        }

        string temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>Removes the activation file.</summary>
    public void Delete() => File.Delete(FilePath);

    /// <summary>
    /// Takes the lock file, waiting up to <see cref="LockWait"/> on the client's clock for another process to let it go.
    /// </summary>
    /// <returns>The lock, released when disposed; or why there is none: <c>ELM_INVALID_STATE</c> when the wait ran out,
    /// <c>ELM_INTERNAL_ERROR</c> when the lock file cannot be opened.</returns>
    public async Task<(IDisposable? Lock, LicenseError? Error)> LockAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, LockName);
        long start = time.GetTimestamp();
        while (true)
        {
            try
            {
                // FileShare.None is a share mode on Windows and an flock on Linux and macOS: either way one holder at a time.
                return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), null);
            }
            catch (UnauthorizedAccessException e)
            {
                return (null, new LicenseError(ErrorCode.InternalError, "The activation lock cannot be opened: " + e.Message));
            }
            catch (DirectoryNotFoundException e)
            {
                return (null, new LicenseError(ErrorCode.InternalError, "The activation lock cannot be opened: " + e.Message));
            }
            catch (IOException)
            {
                if (time.GetElapsedTime(start) >= LockWait)
                {
                    return (null, new LicenseError(ErrorCode.InvalidState, "Another process of this device holds the activation lock."));
                }
            }

            await Task.Delay(LockPoll, time, cancellationToken).ConfigureAwait(false);
        }
    }

    // A list of jti: an array of at most 16 strings of 1 to 64 characters; null when it is not.
    private static List<string>? Jtis(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > StoredActivation.MaxRetry)
        {
            return null;
        }

        var jtis = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= MaxJtiLength } jti)
            {
                return null;
            }

            jtis.Add(jti);
        }

        return jtis;
    }

    // The form Write makes; anything else is damaged. The license is kept as the bytes of its JSON, checked when used.
    private static StoredActivation? Parse(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, Options);
            JsonElement root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("activationId", out JsonElement idValue)
                || !root.TryGetProperty("seq", out JsonElement seqValue)
                || !root.TryGetProperty("kind", out JsonElement kindValue)
                || !root.TryGetProperty("license", out JsonElement licenseValue)
                || !root.TryGetProperty("retry", out JsonElement retryValue)
                || idValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !seqValue.TryGetInt64(out long seq)
                || seq < 0
                || licenseValue.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
            )
            {
                return null;
            }

            List<string>? retry = Jtis(retryValue);
            List<string>? pending = root.TryGetProperty("pending", out JsonElement pendingValue) ? Jtis(pendingValue) : [];
            if (retry is null || pending is null)
            {
                return null;
            }

            string? unkept = null;
            if (root.TryGetProperty("unkept", out JsonElement unkeptValue) && unkeptValue.ValueKind != JsonValueKind.Null)
            {
                if (unkeptValue.ValueKind != JsonValueKind.String || unkeptValue.GetString() is not { Length: UnkeptLength } hash)
                {
                    return null;
                }

                unkept = hash;
            }

            if (!TryReadOffline(root, out OfflineRequest? offline) || !TryReadText(root, "deactivation", MaxFileLength, out string? proof))
            {
                return null;
            }

            string? id = idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            if (id is not null && !Guid.TryParseExact(id, "D", out _))
            {
                return null;
            }

            string? kind = kindValue.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String when kindValue.GetString() is "standard" or "trial" or "server" => kindValue.GetString(),
                _ => "?",
            };
            byte[]? license = licenseValue.ValueKind == JsonValueKind.Object ? Encoding.UTF8.GetBytes(licenseValue.GetRawText()) : null;
            bool together = (id is null) == (license is null) && (id is null) == (kind is null);
            return together && kind != "?"
                ? new StoredActivation(id, seq, kind, license, retry)
                {
                    Pending = pending,
                    Unkept = unkept,
                    Offline = offline,
                    DeactivationProof = proof,
                }
                : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // The waiting offline request or null (design §12.6.2, §12.8.5); a member left out is null.
    private static bool TryReadOffline(JsonElement root, out OfflineRequest? offline)
    {
        offline = null;
        if (!root.TryGetProperty("offline", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (
            value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("op", out JsonElement op)
            || op.GetString() is not ("activate" or "refresh")
            || !TryReadText(value, "nonce", NonceLength, out string? nonce)
            || nonce?.Length != NonceLength
            || !TryReadNumber(value, "iat", out long issued)
            || !TryReadNumber(value, "exp", out long expires)
            || !TryReadNumber(value, "seq", out long seq)
            || !TryReadText(value, "act", 36, out string? act)
            || (act is not null && !Guid.TryParseExact(act, "D", out _))
            || !TryReadText(value, "key", UnkeptLength, out string? key)
            || (key is not null && key.Length != UnkeptLength)
            || !value.TryGetProperty("retry", out JsonElement retryValue)
            || Jtis(retryValue) is not List<string> retry
            || !value.TryGetProperty("fp", out JsonElement fpValue)
            || Fingerprints(fpValue) is not List<string> fingerprints
        )
        {
            return false;
        }

        bool refresh = op.GetString() == "refresh";
        if (refresh ? act is null || key is not null : act is not null || key is null)
        {
            return false;
        }

        offline = new OfflineRequest(refresh, nonce, issued, expires, seq, act, key, retry, fingerprints);
        return true;
    }

    private static bool TryReadNumber(JsonElement parent, string name, out long number)
    {
        number = 0;
        return parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out number) && number >= 0;
    }

    // At most 8, each 32 lower-case hexadecimal characters (design §10.3).
    private static List<string>? Fingerprints(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxFingerprints)
        {
            return null;
        }

        var fingerprints = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (
                item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: FingerprintLength } fingerprint
                || !fingerprint.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            )
            {
                return null;
            }

            fingerprints.Add(fingerprint);
        }

        return fingerprints;
    }

    private static void WriteList(Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (string item in items)
        {
            w.WriteStringValue(item);
        }

        w.WriteEndArray();
    }

    // A string of 1 to the most characters, or null or left out.
    private static bool TryReadText(JsonElement parent, string name, int most, out string? text)
    {
        text = null;
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } read || read.Length > most)
        {
            return false;
        }

        text = read;
        return true;
    }
}
