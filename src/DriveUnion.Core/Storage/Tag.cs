namespace DriveUnion.Core.Storage;

/// <summary>
/// A label a customer puts on files, across the tree rather than inside it.
///
/// <para><b>Why this is not a folder.</b> A file is in one folder and the folder answers «where do I
/// keep this». A tag answers «what is this», and a file has as many of those as it has aspects: an
/// invoice that is also from 2026 and also unpaid. The tree could express one of those three by
/// nesting and would then have to pick which one, which is the choice this exists to avoid.</para>
///
/// <para>Named per workspace and reused, not free text per file. Two files tagged «فوری» and
/// «فوری‌!» are two tags that look alike, and the list of them is a control the customer picks from
/// rather than a field they retype — which is also what makes «show me everything tagged X» a query
/// on an id instead of a string comparison.</para>
/// </summary>
public sealed class Tag
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public const int MaxNameLength = 64;

    /// <summary>
    /// How many a workspace may have.
    ///
    /// <para>Not a storage limit — the rows are tiny. It is a limit on the control: the tag list is
    /// drawn in full on the files screen so somebody can filter by pressing one, and a hundred of
    /// them is a wall rather than a list. Past this the answer is folders.</para>
    /// </summary>
    public const int MaxPerTenant = 60;
}

/// <summary>
/// One tag on one file.
///
/// <para><c>TenantId</c> is carried here too, and it is redundant with both of its parents. That is
/// deliberate: this model has no global query filter, so isolation is whatever each query's WHERE
/// clause says — and a join table without the tenant on it forces every query over it to reach
/// through a parent to find one, which is exactly the shape a scope goes missing in.</para>
/// </summary>
public sealed class FileTag
{
    public Guid StoredFileId { get; set; }

    public Guid TagId { get; set; }

    public Guid TenantId { get; set; }
}
