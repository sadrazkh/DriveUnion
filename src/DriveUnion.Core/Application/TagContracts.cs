namespace DriveUnion.Core.Application;

/// <param name="FileCount">Live files carrying it. A tag on nothing is one the customer can retire.</param>
public sealed record TagSummary(Guid Id, string Name, int FileCount);

public enum TagOutcome
{
    Done,
    NotFound,
    NameEmpty,

    /// <summary>Past <see cref="Storage.Tag.MaxPerTenant"/>. See there for why there is a ceiling.</summary>
    TooMany,
}

/// <param name="Affected">How many files the call actually changed — never how many were asked for.</param>
public sealed record TagResult(TagOutcome Outcome, Guid? TagId = null, int Affected = 0)
{
    public bool Succeeded => Outcome == TagOutcome.Done;
}

/// <summary>
/// The workspace's labels, and what carries them.
///
/// <para>Every call takes <c>tenantId</c> explicitly, like the rest of this product: there is no
/// global query filter, because <c>/d/{slug}</c> is anonymous and a filter fed by the signed-in user
/// resolves it to nobody.</para>
/// </summary>
public interface ITags
{
    /// <summary>Every tag in the workspace with its live count, by name.</summary>
    Task<IReadOnlyList<TagSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// The tag by that name, made if the workspace has not got one.
    ///
    /// <para>Find-or-create rather than create, because the screen's only way to make a tag is to
    /// type it onto a file. Two people typing «فوری» on two files must land on one tag or the filter
    /// is split in half without either of them being told.</para>
    /// </summary>
    Task<TagResult> EnsureAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    /// <summary>Puts a tag on files that do not already carry it. Returns how many changed.</summary>
    Task<TagResult> ApplyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        Guid tagId,
        CancellationToken cancellationToken);

    Task<TagResult> RemoveAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        Guid tagId,
        CancellationToken cancellationToken);

    /// <summary>Retires a tag and takes it off everything. The files are untouched.</summary>
    Task<TagResult> DeleteAsync(Guid tenantId, Guid tagId, CancellationToken cancellationToken);

    /// <summary>
    /// The tags on each of these files, for drawing a list of rows in one query rather than one per
    /// row. Files with no tags are absent from the result rather than present and empty.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TagSummary>>> ForFilesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);
}
