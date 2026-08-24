namespace DriveUnion.Core.Application;

/// <summary>
/// Where a person's files live inside one Google account, and where they go when deleted.
///
/// <code>
/// DriveUnion/{tenant}/{user}/           home
/// DriveUnion/{tenant}/{user}/.trash/    deleted, awaiting purge
/// </code>
///
/// <para>Two callers need the same answer and must not each derive it: the upload path asks for a
/// home before it opens a session, and the delete path asks for a trash before it moves a file. A
/// second derivation of the same layout is how two folders called the same thing end up existing
/// side by side, each holding half of somebody's files.</para>
///
/// <para><b>Every answer is cached.</b> Resolving a folder against Drive is a list and possibly a
/// create — two requests against a 12,000-per-minute budget shared with every upload in the
/// product — to learn a fact that never changes. The account's row already caches the
/// <c>DriveUnion/</c> root for exactly this reason; this is that idea for the two levels below it.
/// </para>
///
/// <para>Nothing here takes a folder from a caller, and that is deliberate: a folder id arriving
/// from outside is a folder id somebody can be persuaded to send, and it addresses another
/// customer's files just as well as their own.</para>
/// </summary>
public interface IDriveFolders
{
    /// <summary>
    /// The folder new uploads go into, created on first use.
    ///
    /// <para><paramref name="ownerUserId"/> is null only where the caller genuinely has no user —
    /// which no upload path does. It resolves to the tenant folder, which is where files sat before
    /// uploads were separated per person.</para>
    /// </summary>
    Task<string> HomeAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The trash beside that home, created on first use.
    ///
    /// <para>A folder rather than Drive's own <c>trashed</c> flag, because Google empties its trash
    /// on a schedule it neither publishes to us nor asks about. Retention here is the operator's
    /// setting, the sweeper is ours, and what is waiting is something the operator can look at.</para>
    /// </summary>
    Task<string> TrashAsync(
        Guid accountId,
        Guid tenantId,
        Guid? ownerUserId,
        CancellationToken cancellationToken);
}
