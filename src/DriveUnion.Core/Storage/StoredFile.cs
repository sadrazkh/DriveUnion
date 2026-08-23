namespace DriveUnion.Core.Storage;

/// <summary>
/// A file a tenant owns, and the Drive object that physically holds it.
///
/// The panel reads these rows rather than listing Drive live: it is faster, it is already scoped to
/// a tenant, and it keeps the 12,000-queries-per-60-seconds budget for work that actually needs
/// Google. Reconciling drift against Drive is M2's problem, not M1's.
/// </summary>
public sealed class StoredFile
{
    public Guid Id { get; set; }

    /// <summary>Who owns the file.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Where the bytes physically sit. Operator-facing only — never serialised to a tenant.</summary>
    public Guid GoogleAccountId { get; set; }

    public required string DriveFileId { get; set; }

    public required string Name { get; set; }

    public required string MimeType { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>Soft delete: a revoked link must not resurrect a file the tenant removed.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
