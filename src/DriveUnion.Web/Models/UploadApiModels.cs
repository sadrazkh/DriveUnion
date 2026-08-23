using System.ComponentModel.DataAnnotations;
using DriveUnion.Core.Application;

namespace DriveUnion.Web.Models;

/// <summary>
/// Body of <c>POST /api/uploads</c>. The lengths are the storage columns' own, checked here so an
/// oversized name is a 400 on the first call instead of a database error thirty gigabytes later.
/// </summary>
public sealed class BeginUploadPayload
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(512, MinimumLength = 1)]
    public string FileName { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [StringLength(255, MinimumLength = 1)]
    public string MimeType { get; init; } = string.Empty;

    [Range(0, long.MaxValue)]
    public long SizeBytes { get; init; }
}

public sealed record BeginUploadResponse(Guid Id, int ChunkSize);

public sealed record UploadProgressResponse(
    Guid Id,
    long BytesConfirmed,
    long SizeBytes,
    string Status,
    Guid? StoredFileId,
    string? FailureReason)
{
    /// <summary>
    /// The status crosses the wire as its name rather than its number: an enum reordered in Core
    /// would otherwise silently retype every client's state machine.
    /// </summary>
    public static UploadProgressResponse From(UploadProgress progress) => new(
        progress.SessionId,
        progress.BytesConfirmed,
        progress.SizeBytes,
        progress.Status.ToString(),
        progress.StoredFileId,
        progress.FailureReason);
}
