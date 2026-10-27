using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Service;

/// <summary>
/// One administration operation to record (design §18.2).
/// </summary>
/// <param name="ActorType"><c>apikey</c>, <c>oidc</c> (M5), or <c>system</c>.</param>
/// <param name="ActorId">The API key ID, the OIDC issuer and subject, or for <c>system</c> the operation's name (<c>setup</c>, <c>auth</c>).</param>
/// <param name="Action">The operation, such as <c>apikey.create</c>.</param>
/// <param name="Target">What it acted on, such as an API key ID, or <see langword="null"/>.</param>
/// <param name="Result">The result code, such as <c>ELM_OK</c>.</param>
/// <param name="Ip">The source address, or <see langword="null"/>.</param>
/// <param name="Detail">A compact JSON object with the operation's details, never a secret, or <see langword="null"/>.</param>
internal sealed record AuditEntry(string ActorType, string? ActorId, string Action, string? Target, string Result, string? Ip, string? Detail);

/// <summary>
/// A stored audit row.
/// </summary>
internal sealed record AuditRow(long N, long Ts, AuditEntry Entry, byte[] Prev, byte[] Hash);

/// <summary>
/// The result of checking the chain.
/// </summary>
/// <param name="Checked">The rows read.</param>
/// <param name="FirstBroken">The number of the first row that does not follow, or <see langword="null"/>.</param>
internal sealed record AuditCheck(long Checked, long? FirstBroken);

/// <summary>
/// The audit log (design §18.2): an append-only table whose rows are chained by SHA-256. A row's hash covers ten columns in a
/// fixed order, each written as a 4-byte big-endian length and its UTF-8 bytes (numbers in decimal, <c>prev</c> as its 32
/// bytes, <c>NULL</c> as the length <c>0xFFFFFFFF</c>), so another tool can compute the same hash from the design alone
/// (decision 0.77). Appends lock the one row of <c>audit_state</c>, so the chain does not fork.
/// </summary>
internal static class AuditLog
{
    /// <summary>The page size when none is given, and the largest (design §14.5).</summary>
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private const int HashLength = 32;

    /// <summary>
    /// Returns the bytes a row's hash covers.
    /// </summary>
    public static byte[] RowBytes(long n, long ts, AuditEntry entry, ReadOnlySpan<byte> prev)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (prev.Length != HashLength)
        {
            throw new ArgumentException("The previous hash is 32 bytes.", nameof(prev));
        }

        using var buffer = new MemoryStream();
        void Write(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        void WriteText(string? text)
        {
            if (text is null)
            {
                Span<byte> none = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(none, uint.MaxValue);
                buffer.Write(none);
                return;
            }

            Write(Encoding.UTF8.GetBytes(text));
        }

        WriteText(n.ToString(CultureInfo.InvariantCulture));
        WriteText(ts.ToString(CultureInfo.InvariantCulture));
        WriteText(entry.ActorType);
        WriteText(entry.ActorId);
        WriteText(entry.Action);
        WriteText(entry.Target);
        WriteText(entry.Result);
        WriteText(entry.Ip);
        WriteText(entry.Detail);
        Write(prev);
        return buffer.ToArray();
    }

    /// <summary>Returns a row's hash.</summary>
    public static byte[] Hash(long n, long ts, AuditEntry entry, ReadOnlySpan<byte> prev) => SHA256.HashData(RowBytes(n, ts, entry, prev));

    /// <summary>
    /// Writes a compact JSON object for <see cref="AuditEntry.Detail"/>.
    /// </summary>
    public static string Detail(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Returns the detail of an <c>apikey.create</c> row: the name, permissions, and expiry, and for setup the owner.
    /// </summary>
    public static string KeyCreatedDetail(ApiKeyRecord record, AdminIdentity? owner)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Detail(w =>
        {
            w.WriteString("name", record.Name);
            w.WriteStartArray("permissions");
            foreach (string permission in record.Permissions)
            {
                w.WriteStringValue(permission);
            }

            w.WriteEndArray();
            w.WriteString("expiresAt", AdminApi.Time(record.ExpiresAt));
            if (owner is not null)
            {
                w.WriteString("owner", owner.Issuer + " " + owner.Subject);
            }
        });
    }

    /// <summary>Reads a stored row.</summary>
    public static AuditRow FromRow(AuditLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entry = new AuditEntry(row.ActorType, row.ActorId, row.Action, row.Target, row.Result, row.Ip, row.Detail);
        return new AuditRow(row.N, row.Ts, entry, row.Prev, row.Hash);
    }
}

/// <summary>
/// The actions written in the audit log (design §18.2). They name operations; the permissions of <see cref="Permissions"/>
/// name what a key may do, and some share their text.
/// </summary>
internal static class AuditActions
{
    public const string ApiKeyCreate = "apikey.create";
    public const string ApiKeyList = "apikey.list";
    public const string ApiKeyRevoke = "apikey.revoke";
    public const string AuditList = "audit.list";
    public const string AuditVerify = "audit.verify";
    public const string AuthFailed = "auth.failed";
}
