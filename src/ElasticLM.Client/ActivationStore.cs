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

    /// <summary>Returns this activation with a <c>jti</c> added last, the oldest dropped past <see cref="MaxRetry"/>.</summary>
    public StoredActivation WithRetry(string jti) => this with { Retry = [.. Retry.Append(jti).TakeLast(MaxRetry)] };
}

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
                || retryValue.ValueKind != JsonValueKind.Array
                || retryValue.GetArrayLength() > StoredActivation.MaxRetry
            )
            {
                return null;
            }

            var retry = new List<string>();
            foreach (JsonElement item in retryValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= MaxJtiLength } jti)
                {
                    return null;
                }

                retry.Add(jti);
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
            return together && kind != "?" ? new StoredActivation(id, seq, kind, license, retry) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
