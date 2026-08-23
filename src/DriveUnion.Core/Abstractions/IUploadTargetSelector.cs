namespace DriveUnion.Core.Abstractions;

/// <summary>
/// Chooses which Google account in the pool receives an upload.
///
/// M1 has one account, so the only implementation returns it. The seam exists now because M2's
/// three policies — most free space, round robin, manual priority — change this decision and
/// nothing else, and because a call site that already asks the question is far cheaper to extend
/// than one that assumes the answer.
/// </summary>
public interface IUploadTargetSelector
{
    /// <summary>
    /// Returns the account that should hold a file of <paramref name="sizeBytes"/>, or null when no
    /// account in the pool can take it.
    /// </summary>
    Task<Guid?> SelectAsync(long sizeBytes, CancellationToken cancellationToken);
}
