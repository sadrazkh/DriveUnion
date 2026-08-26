using DriveUnion.Core.Sharing;

namespace DriveUnion.Core.Application;

public sealed record ShareLinkSummary(
    Guid Id,
    string Slug,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads,
    int DownloadCount,
    bool IsActive);

public sealed record CreateShareLinkRequest(
    Guid StoredFileId,
    DateTimeOffset? ExpiresAt,
    int? MaxDownloads);

/// <summary>The owner's side of a link. Tenant-scoped, like everything else in the panel.</summary>
public interface IShareLinkService
{
    Task<ShareLinkSummary> CreateAsync(
        Guid tenantId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ShareLinkSummary>> ListForFileAsync(
        Guid tenantId,
        Guid fileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every link the tenant owns, newest first, each with the file it points at.
    ///
    /// The panel's «لینک‌های اشتراک» table is file · address · downloads · expiry · status, and a
    /// <see cref="ShareLinkSummary"/> on its own can name none of the first column and offer nowhere
    /// to go — so the row travels as a triple rather than as a fourth summary record that this one
    /// table would be the only reader of.
    ///
    /// <c>tenantId</c> is an argument and not an ambient filter, for the reason in §8 of the M1
    /// design: this product has no global query filter, because one would hand <c>Guid.Empty</c> to
    /// every anonymous /d/{slug} and refuse every live link in the product.
    /// </summary>
    Task<IReadOnlyList<(ShareLinkSummary Link, Guid StoredFileId, string FileName)>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid tenantId, Guid linkId, CancellationToken cancellationToken);
}

/// <summary>What the public landing page shows. No account, no file id, no tenant.</summary>
/// <param name="SizeBytes">
/// What is stored, which for an encrypted file is the ciphertext and is longer than the file by one
/// tag per segment. <c>Encryption.PlaintextLength</c> is the number to show when there is one.
/// </param>
/// <param name="Encryption">
/// How to open it, for a file the uploader locked before sending — and null for every other file.
///
/// <para>Public, on an anonymous page, and that is not an oversight. None of it is secret: the salt
/// and the nonce prefix are public by construction, and the wrapped key is a key only to somebody
/// who also has the passphrase it is wrapped with. Whoever holds the link is who the owner meant to
/// give the file to; making them ask a second endpoint for the header would be one more round trip
/// protecting nothing.</para>
/// </param>
public sealed record PublicFileView(
    string Slug,
    string FileName,
    string MimeType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    int DownloadCount,
    int? MaxDownloads,
    DateTimeOffset? ExpiresAt,
    EncryptionHeader? Encryption = null);

/// <summary>
/// Everything the streaming route needs. <see cref="GoogleAccountId"/> and
/// <see cref="DriveFileId"/> stay server-side — they must never reach a response body, header or URL.
/// </summary>
/// <param name="TenantId">
/// Whose egress this transfer is, and the only reason a tenant appears on an anonymous path.
///
/// <para>The interface's own summary says this reader has no tenant and must not acquire one, and
/// that is still true of the <i>lookup</i>: nothing about resolving a slug is scoped by workspace,
/// because the visitor has none. This is the answer, not the question — the file that was found
/// belongs to somebody, and the bytes about to be sent are billed to them. It stays server-side
/// like the two below.</para>
/// </param>
public sealed record PublicDownloadTicket(
    Guid ShareLinkId,
    Guid TenantId,
    Guid GoogleAccountId,
    string DriveFileId,
    string FileName,
    string MimeType,
    long SizeBytes);

/// <summary>
/// A slug lookup. <c>Reason</c> is null when no such slug exists, and set when a real link is
/// refusing — a distinction for the logs and the owner's panel only. The visitor gets one identical
/// card either way, because telling "expired" apart from "never existed" is enough to enumerate the
/// slug space.
/// </summary>
public sealed record PublicLinkResolution(
    bool IsAvailable,
    ShareLinkAvailability? Reason,
    PublicFileView? File)
{
    public static readonly PublicLinkResolution NotFound = new(false, null, null);
}

/// <summary>
/// The public path, and the reason this interface exists separately from <see cref="IFileCatalog"/>.
///
/// /d/{slug} is anonymous. It has no tenant and must not acquire one: a reader that took a tenantId
/// would be handed <c>Guid.Empty</c> by an anonymous request and would 404 every live link in the
/// product while the rows sat plainly in the table. So this type has no tenant concept at all, and
/// that absence is load-bearing.
/// </summary>
public interface IPublicLinkReader
{
    Task<PublicLinkResolution> ResolveAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Null when the slug is unknown or the link is refusing. The caller renders one card.</summary>
    Task<PublicDownloadTicket?> ResolveForDownloadAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Takes one of the link's remaining downloads and says whether there was one left to take.
    ///
    /// This is the gate. The count <see cref="ResolveForDownloadAsync"/> reads is advisory — true
    /// when it was read and possibly false a moment later — and a transfer runs for as long as the
    /// file is big, so a link at 499 of 500 with several downloads in flight would hand out a 501st.
    /// The slot is therefore spent here, before Google is contacted, and the answer is the database's
    /// rather than this process's.
    ///
    /// False means no slot was left. The caller must answer with the same card it gives a revoked,
    /// expired or never-existed slug: a refusal that looked different — a 429, a 409, anything — is
    /// a fourth oracle for telling live slugs from dead ones.
    ///
    /// Every true must be finished by exactly one <see cref="RecordDownloadAsync"/> when the visitor
    /// took the bytes, or one <see cref="ReleaseDownloadAsync"/> when they did not.
    /// </summary>
    Task<bool> TryReserveDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes down a reservation the visitor consumed — the audit row behind the number, and
    /// nothing else. The counter already moved when the slot was reserved, so moving it again here
    /// would bill one download twice.
    ///
    /// The caller decides whether a request counts at all — see <see cref="DownloadCounting"/> —
    /// because that decision is about the Range header, not the row. A request that does not count
    /// reserves nothing and records nothing.
    /// </summary>
    Task RecordDownloadAsync(
        Guid shareLinkId,
        string ipHash,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gives a reservation back, for a download that never happened: Drive refused before the first
    /// byte, or the stream died mid-response. No audit row, because nothing was served.
    ///
    /// It cannot drive the count below zero. A caller that releases twice, or releases a slot it
    /// never reserved, must not be able to mint downloads out of a negative counter.
    /// </summary>
    Task ReleaseDownloadAsync(Guid shareLinkId, CancellationToken cancellationToken);
}
