namespace DriveUnion.Core.Application;

/// <summary>One file waiting in the trash.</summary>
/// <param name="PurgeAfter">
/// When the sweeper may take it. Null for a file deleted before the trash existed — the purge leaves
/// those alone rather than inventing a deadline for something somebody deleted under other rules.
/// </param>
public sealed record TrashItem(
    Guid Id,
    string Name,
    long SizeBytes,
    DateTimeOffset DeletedAt,
    DateTimeOffset? PurgeAfter);

/// <summary>
/// The customer's side of the trash: what is in it, putting something back, and emptying it.
///
/// <para><b>Emptying is the only thing here that frees space</b>, and that is deliberate. Deleting a
/// file moves it into a folder the operator still owns, so the bytes are still occupying the pool
/// and the customer's usage figure is still telling the truth. This is what Drive itself does, so it
/// is the model the customer already has — and it is the version where the number on the screen and
/// the bytes on the disk agree.</para>
/// </summary>
public interface ITrash
{
    Task<IReadOnlyList<TrashItem>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Puts a file back where it was. False when it is not this tenant's, not in the trash, or the
    /// purge reached it first.
    /// </summary>
    Task<bool> RestoreAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Purges everything in this tenant's trash now, whatever its deadline, and returns how many
    /// files went. This is the button that actually gives the customer their space back.
    /// </summary>
    Task<int> EmptyAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// What the trash is holding, in bytes. It belongs on the capacity card because it is exactly
    /// the difference between what a customer believes they freed and what they actually did.
    /// </summary>
    Task<long> SizeAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The sweeper's side, and it has no tenant.
///
/// <para>It runs with no request, no cookie and no principal, over rows that each carry their own
/// tenant — the shape M1 §8 exists to protect. A tenant-scoped read from here would be handed
/// <c>Guid.Empty</c> and would sweep nothing, for ever, silently.</para>
/// </summary>
public interface ITrashPurge
{
    /// <summary>
    /// Takes up to <paramref name="batchSize"/> files whose deadline has passed and returns how many
    /// were purged.
    ///
    /// <para>Bounded because a purge deletes in Drive one file at a time against a shared request
    /// budget, and an unbounded sweep after a large delete would spend the whole allowance on
    /// housekeeping while customers are trying to upload.</para>
    /// </summary>
    Task<int> PurgeDueAsync(int batchSize, CancellationToken cancellationToken);
}
