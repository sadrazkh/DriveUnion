using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;

namespace DriveUnion.Infrastructure.Backup;

/// <summary>
/// What a catalogue snapshot looks like from the outside.
///
/// <para><b>JSON Lines, gzipped.</b> One record per line, one line per row, the whole thing through
/// <c>gzip</c>. The choice was between this and CSV, and every argument for it is about the day it
/// gets used — which is a day when this application is not running and possibly not recoverable, and
/// somebody is reading the file with whatever they have.</para>
///
/// <list type="number">
/// <item><b>A truncated line costs one row.</b> A single JSON array or object cannot be parsed at
/// all if the last byte is missing, and «the last byte is missing» is the ordinary way a backup goes
/// wrong. Here a reader gets everything up to the break, and the missing
/// <see cref="Footer"/> line is what tells it the break happened.</item>
/// <item><b>It streams in both directions.</b> A hundred thousand rows are written a line at a time
/// and read a line at a time — <c>zcat catalogue-….jsonl.gz | jq -c 'select(.type=="file")'</c>
/// never holds more than one record, on a laptop, with no code from this repository.</item>
/// <item><b>Named fields beat column order.</b> A CSV is a contract about position that lives
/// nowhere; the first person to add a column in the middle silently rewrites every reader anybody
/// wrote. And file names really do contain commas, quotes and newlines, which is a quoting bug
/// waiting for the worst possible moment.</item>
/// <item><b>Null survives.</b> <c>deletedAt</c> absent and <c>deletedAt</c> empty are the difference
/// between a live file and a deleted one; in CSV they are the same two characters.</item>
/// <item><b>Six shapes, one stream.</b> Tenants, accounts, folders, files and encryption headers do
/// not share a row shape. As CSV they are six files that can be separated from each other; here they
/// are one file with a <c>type</c> on every line.</item>
/// </list>
///
/// <para><b>gzip</b> because it is the one compressor every operating system, every language and
/// every shell already has, and because it streams — which the format above would otherwise
/// waste.</para>
///
/// <para><b>What is deliberately not in it</b> is in <see cref="Note"/>, written into the file
/// itself: no Google refresh tokens, no S3 secrets, no API token hashes, no password hashes, no Data
/// Protection keys, no share-link slugs. This file sits in a Drive account and is treated
/// throughout as something that could leak.</para>
/// </summary>
public static class CatalogueSnapshotFormat
{
    /// <summary>
    /// Written on the header line so a reader can tell what it is holding without parsing the rest,
    /// and so a future shape can be told apart from this one by something other than guesswork.
    /// </summary>
    public const string FormatId = "driveunion.catalogue.v1";

    /// <summary>
    /// What Drive is told the file is. <c>application/gzip</c> and not <c>application/json</c>: the
    /// bytes are compressed, and a browser told otherwise offers to display them.
    /// </summary>
    public const string MimeType = "application/gzip";

    /// <summary>
    /// The sentence on the first line of every snapshot.
    ///
    /// <para>It is in the file rather than only in this repository on purpose. The reader of this
    /// file may have no repository — that is the situation it exists for — and «what am I allowed to
    /// assume about this thing I just found in a Drive folder» is the first question they have.</para>
    /// </summary>
    public const string Note =
        "Every field needed to reconnect a stored file to the Drive object holding it. "
        + "No credentials of any kind: no Google tokens, no S3 keys, no API token hashes, "
        + "no password hashes, no Data Protection keys, no share-link slugs. "
        + "Encryption headers are included and cannot open anything without the customer's passphrase.";

    /// <summary>
    /// <c>catalogue-20260827-041500Z.jsonl.gz</c>.
    ///
    /// <para>UTC, and sortable as text. The person reading it is looking at a folder of these in
    /// Google's own web interface on the worst day of their year, and «the newest one» has to be
    /// obvious without converting a timezone in their head.</para>
    /// </summary>
    public static string NameFor(DateTimeOffset takenAt) => string.Create(
        CultureInfo.InvariantCulture,
        $"catalogue-{takenAt.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl.gz");
}

/// <summary>
/// What a snapshot knows about one pool account.
///
/// <para><b>A projection and not the entity, and that is the whole security of this file.</b>
/// <see cref="GoogleAccount"/> also carries <c>RefreshTokenProtected</c> and
/// <c>AccessTokenProtected</c>. Reading the entity and writing «the fields we want» leaves the
/// secret one property away from a snapshot for ever — one <c>JsonSerializer.Serialize(account)</c>
/// by somebody in a hurry. Selecting these five columns in the query means the token is not omitted
/// from the file, it is absent from the process.</para>
/// </summary>
/// <param name="RootFolderId">
/// The Drive id of <c>DriveUnion/</c> in this account. Not needed to reach a file — every file
/// record carries its own folder — but it is where a person opening the account by hand starts.
/// </param>
public sealed record SnapshotAccountRow(
    Guid Id,
    string Label,
    string Email,
    string? GoogleUserId,
    string? RootFolderId,
    GoogleAccountStatus Status);

/// <summary>
/// One snapshot's lines, written into a stream that is already compressing.
///
/// <para>Every line is built in one small reusable buffer and pushed straight out, so the memory
/// this costs is one record and not one catalogue. Nothing here is async: the stream underneath is
/// the gzip buffer in memory, and what talks to Google is the caller, a chunk at a time.</para>
/// </summary>
internal sealed class CatalogueSnapshotLines : IDisposable
{
    /// <summary>
    /// <para>Not indented — a line per record is the format, and pretty-printing it would be a
    /// different one.</para>
    ///
    /// <para>The encoder allows every Unicode range, so «گزارش سه‌ماهه.pdf» is written as itself
    /// rather than as forty <c>\u06xx</c> escapes. The default encoder is built for putting JSON
    /// inside HTML; this file is read by a human and by <c>jq</c>, and the escaping that helps there
    /// makes it unreadable here. The HTML-sensitive ASCII characters are still escaped, because
    /// «somebody opens the snapshot in a browser» is not a thing to have to think about.</para>
    /// </summary>
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly Stream _destination;
    private readonly ArrayBufferWriter<byte> _line = new(4096);
    private readonly Utf8JsonWriter _json;

    public CatalogueSnapshotLines(Stream destination)
    {
        _destination = destination;
        _json = new Utf8JsonWriter(_line, Options);
    }

    /// <summary>
    /// The first line: what this file is, when it was taken, and what it is not.
    ///
    /// <para>First so that <c>zcat … | head -1</c> answers every question a person has before they
    /// start writing a restore script.</para>
    /// </summary>
    public void Header(DateTimeOffset takenAt)
    {
        _json.WriteStartObject();
        _json.WriteString("type", "header");
        _json.WriteString("format", CatalogueSnapshotFormat.FormatId);
        _json.WriteString("takenAt", takenAt);
        _json.WriteString("note", CatalogueSnapshotFormat.Note);
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// A workspace. The slug matters more than the name: it is the folder each account holds this
    /// tenant's files under, so it is how a person with no database still finds them in Drive.
    /// </summary>
    public void Tenant(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        _json.WriteStartObject();
        _json.WriteString("type", "tenant");
        _json.WriteString("id", tenant.Id);
        _json.WriteString("name", tenant.Name);
        _json.WriteString("slug", tenant.Slug);
        _json.WriteString("createdAt", tenant.CreatedAt);
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// A pool account: which Google address the opaque <c>accountId</c> on every file record means.
    ///
    /// <para>The address is the operator's own and is the only thing that turns «this file is on
    /// account 6f2a…» into an account somebody can sign into. Nothing that authenticates as it is
    /// here — see <see cref="SnapshotAccountRow"/>.</para>
    /// </summary>
    public void Account(SnapshotAccountRow account)
    {
        ArgumentNullException.ThrowIfNull(account);

        _json.WriteStartObject();
        _json.WriteString("type", "account");
        _json.WriteString("id", account.Id);
        _json.WriteString("label", account.Label);
        _json.WriteString("email", account.Email);
        WriteNullable("googleUserId", account.GoogleUserId);
        WriteNullable("rootFolderId", account.RootFolderId);
        _json.WriteString("status", account.Status.ToString());
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// A folder in the tree the customer built — not a Drive folder. Without these a restored
    /// catalogue is every file in one flat list, which is a workspace nobody recognises as theirs.
    /// </summary>
    public void Folder(Folder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _json.WriteStartObject();
        _json.WriteString("type", "folder");
        _json.WriteString("id", folder.Id);
        _json.WriteString("tenantId", folder.TenantId);
        _json.WriteString("ownerUserId", folder.OwnerUserId);
        WriteNullable("parentFolderId", folder.ParentFolderId);
        _json.WriteString("name", folder.Name);
        _json.WriteString("createdAt", folder.CreatedAt);
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// The record the whole file exists for: one customer's file, and the Drive object holding it.
    /// </summary>
    /// <param name="tenantSlug">
    /// Copied onto every file line rather than looked up through <c>tenantId</c>. It costs a few
    /// bytes a row after compression and it means one <c>grep</c> for a file name answers «whose is
    /// it and where is it» from the single line it printed — which is what somebody actually does at
    /// three in the morning.
    /// </param>
    /// <param name="accountLabel">The same, for the same reason: <c>A2</c> is what the operator reads.</param>
    public void File(StoredFile file, string tenantSlug, string accountLabel)
    {
        ArgumentNullException.ThrowIfNull(file);

        _json.WriteStartObject();
        _json.WriteString("type", "file");
        _json.WriteString("id", file.Id);
        _json.WriteString("tenantId", file.TenantId);
        _json.WriteString("tenantSlug", tenantSlug);
        WriteNullable("ownerUserId", file.OwnerUserId);
        _json.WriteString("accountId", file.GoogleAccountId);
        _json.WriteString("accountLabel", accountLabel);
        _json.WriteString("driveFileId", file.DriveFileId);
        WriteNullable("driveFolderId", file.DriveFolderId);
        WriteNullable("restoreFolderId", file.RestoreFolderId);
        WriteNullable("folderId", file.FolderId);
        _json.WriteString("name", file.Name);
        _json.WriteString("mimeType", file.MimeType);
        _json.WriteNumber("sizeBytes", file.SizeBytes);
        _json.WriteString("createdAt", file.CreatedAt);
        _json.WriteString("modifiedAt", file.ModifiedAt);
        WriteNullable("deletedAt", file.DeletedAt);
        WriteNullable("purgeAfter", file.PurgeAfter);
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// How to open an encrypted file — and nothing that opens one.
    ///
    /// <para><b>Why these are in a backup at all.</b> The bytes in Drive are ciphertext. Lose this
    /// row and they are unopenable for ever, by anyone, including the person who has the passphrase:
    /// the wrapped content key, the salt, the iteration count and the nonce prefix are the only
    /// route from what they typed to what the browser needs. A snapshot that restored the file and
    /// not its header would hand somebody back a file they can see, own, download — and never
    /// read.</para>
    ///
    /// <para><b>Why it is safe to write down.</b> The content key here is sealed under a key derived
    /// from the customer's passphrase at 600,000 PBKDF2 rounds, and this product has never held that
    /// passphrase. Whoever reads this file gets what the server already has, which is nothing
    /// usable — see <c>FileEncryption</c>, which makes the same point about the database row. A
    /// share link's slug, by contrast, <i>is</i> a bearer capability and is deliberately not in this
    /// file.</para>
    /// </summary>
    public void Encryption(FileEncryption header)
    {
        ArgumentNullException.ThrowIfNull(header);

        _json.WriteStartObject();
        _json.WriteString("type", "encryption");
        _json.WriteString("storedFileId", header.StoredFileId);
        _json.WriteString("tenantId", header.TenantId);
        _json.WriteString("sealedBy", header.SealedBy.ToString());
        _json.WriteNumber("scheme", header.Scheme);
        _json.WriteNumber("segmentSize", header.SegmentSize);
        _json.WriteString("noncePrefix", header.NoncePrefix);
        _json.WriteNumber("plaintextLength", header.PlaintextLength);
        _json.WriteString("kdfSalt", header.KdfSalt);
        _json.WriteNumber("kdfIterations", header.KdfIterations);
        _json.WriteString("wrappedKey", header.WrappedKey);
        _json.WriteString("createdAt", header.CreatedAt);
        _json.WriteEndObject();

        Flush();
    }

    /// <summary>
    /// The last line, and the only proof the file is whole.
    ///
    /// <para>A snapshot that stopped halfway — a killed process, a full account, a token that
    /// expired mid-write — looks exactly like a complete one for as long as nobody counts. This is
    /// how a reader tells: no footer, no trust. The counts beside it let the same reader check that
    /// what they parsed is what was written rather than what survived.</para>
    /// </summary>
    public void Footer(
        int tenants,
        int accounts,
        int folders,
        int files,
        int encryptions)
    {
        _json.WriteStartObject();
        _json.WriteString("type", "footer");
        _json.WriteBoolean("complete", true);
        _json.WriteStartObject("counts");
        _json.WriteNumber("tenants", tenants);
        _json.WriteNumber("accounts", accounts);
        _json.WriteNumber("folders", folders);
        _json.WriteNumber("files", files);
        _json.WriteNumber("encryptions", encryptions);
        _json.WriteEndObject();
        _json.WriteEndObject();

        Flush();
    }

    public void Dispose() => _json.Dispose();

    private void WriteNullable(string name, string? value)
    {
        if (value is null) _json.WriteNull(name);
        else _json.WriteString(name, value);
    }

    private void WriteNullable(string name, Guid? value)
    {
        if (value is { } id) _json.WriteString(name, id);
        else _json.WriteNull(name);
    }

    private void WriteNullable(string name, DateTimeOffset? value)
    {
        if (value is { } moment) _json.WriteString(name, moment);
        else _json.WriteNull(name);
    }

    /// <summary>
    /// Pushes the record just built out as one line and starts the next one in the same buffer.
    ///
    /// <para>The newline is what makes this JSON Lines rather than a stream of concatenated objects:
    /// it is the boundary a reader recovers on, and the reason a torn file costs one row.</para>
    /// </summary>
    private void Flush()
    {
        _json.Flush();

        _destination.Write(_line.WrittenSpan);
        _destination.WriteByte((byte)'\n');

        _json.Reset();
        _line.ResetWrittenCount();
    }
}
