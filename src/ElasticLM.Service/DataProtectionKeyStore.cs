using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElasticLM.Service;

/// <summary>
/// The Data Protection keys of the admin UI's sign-in, in the table <c>dataprotection_keys</c> (design §15.3, decision 0.99):
/// each key's XML encrypted with the key encryption key by AES-256-GCM, with the key's name as associated data, so that a
/// ciphertext moved to another row does not decrypt. The service makes the keys itself under a database lock; Data
/// Protection's automatic key generation is off, so two nodes never make keys at once.
/// </summary>
internal sealed class DataProtectionKeyStore : IXmlRepository
{
    /// <summary>A key's lifetime (design §15.3).</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(90);

    /// <summary>How long before the last key ends the next one is made (design §15.3).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(7);

    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly Database _database;
    private readonly byte[] _kek;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private List<DataProtectionKeyRow>? _made;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataProtectionKeyStore"/> class.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="kek">The key encryption key: 32 bytes.</param>
    /// <param name="time">The clock that decides when a key is made.</param>
    public DataProtectionKeyStore(Database database, byte[] kek, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(time);
        _database = database;
        _kek = kek;
        _time = time;
    }

    /// <summary>Gets or sets the log that names a row that cannot be decrypted.</summary>
    public ILogger Log { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Returns the keys that decrypt; a row that does not is skipped and named in the service log (design §15.3).
    /// </summary>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // Data Protection reads its key ring synchronously; the read runs on the thread pool, outside any caller's context.
        IReadOnlyList<DataProtectionKeyRow> rows = Task.Run(() => _database.ListDataProtectionKeysAsync(CancellationToken.None))
            .GetAwaiter()
            .GetResult();
        return [.. Decrypt(rows).Select(k => k.Element)];
    }

    /// <summary>
    /// Takes the key Data Protection made inside <see cref="EnsureKeyAsync"/>; the caller writes it under its lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">Data Protection stores a key outside <see cref="EnsureKeyAsync"/>.</exception>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(friendlyName);
        lock (_gate)
        {
            if (_made is null)
            {
                throw new InvalidOperationException("Data Protection keys are made by the service under its database lock.");
            }

            _made.Add(Encrypt(friendlyName, element, _time.GetUtcNow().ToUnixTimeSeconds()));
        }
    }

    /// <summary>
    /// Makes the key that is due, under the database lock (design §15.3): at the start, a key used at once when no key is
    /// in force; then, when the last key ends within 7 days, the key that starts at its end.
    /// </summary>
    /// <param name="keys">The host's key manager, which makes the key's XML.</param>
    /// <param name="atStart">Whether this is the start, where rows that all fail to decrypt stop the service.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The keys made.</returns>
    /// <exception cref="ServiceStartException">At the start, there are rows and none decrypts with the key encryption key.</exception>
    public Task<int> EnsureKeyAsync(IKeyManager keys, bool atStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _database.AddDataProtectionKeysAsync(
            rows =>
            {
                IReadOnlyList<StoredKey> stored = Decrypt(rows);
                if (atStart && rows.Count > 0 && stored.Count == 0)
                {
                    throw new ServiceStartException(
                        "No Data Protection key decrypts with the key encryption key; every node needs the same key encryption key."
                    );
                }

                DateTimeOffset now = _time.GetUtcNow();
                DateTimeOffset[] ends = [.. stored.Select(k => k.Expiration).OfType<DateTimeOffset>()];
                (DateTimeOffset Start, DateTimeOffset End)? due =
                    !ends.Any(end => end > now) ? (now, now + KeyLifetime)
                    : ends.Max() - now <= RenewBefore ? (ends.Max(), ends.Max() + KeyLifetime)
                    : null;
                return due is (DateTimeOffset start, DateTimeOffset end) ? Make(keys, start, end) : [];
            },
            cancellationToken
        );
    }

    // Lets Data Protection write the key's XML, which StoreElement takes instead of writing it.
    private List<DataProtectionKeyRow> Make(IKeyManager keys, DateTimeOffset start, DateTimeOffset end)
    {
        lock (_gate)
        {
            _made = [];
            try
            {
                _ = keys.CreateNewKey(start, end);
                return _made;
            }
            finally
            {
                _made = null;
            }
        }
    }

    private DataProtectionKeyRow Encrypt(string name, XElement element, long now)
    {
        byte[] plain = Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagLength];
        using (var aes = new AesGcm(_kek, TagLength))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        }

        return new DataProtectionKeyRow(name, nonce, cipher, tag, now);
    }

    private List<StoredKey> Decrypt(IReadOnlyList<DataProtectionKeyRow> rows)
    {
        var keys = new List<StoredKey>(rows.Count);
        using var aes = new AesGcm(_kek, TagLength);
        foreach (DataProtectionKeyRow row in rows)
        {
            try
            {
                byte[] plain = new byte[row.Ciphertext.Length];
                aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plain, Encoding.UTF8.GetBytes(row.Name));
                XElement element = XElement.Parse(Encoding.UTF8.GetString(plain));
                keys.Add(new StoredKey(element, Date(element, "expirationDate")));
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException or System.Xml.XmlException)
            {
                ServiceLog.DataProtectionKeySkipped(Log, row.Name);
            }
        }

        return keys;
    }

    // A key element's date (Data Protection writes ISO 8601); null for an element that is not a key, such as a revocation.
    private static DateTimeOffset? Date(XElement element, string name) =>
        element.Name.LocalName == "key"
        && element.Element(name)?.Value is string text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset date)
            ? date
            : null;

    private sealed record StoredKey(XElement Element, DateTimeOffset? Expiration);
}
