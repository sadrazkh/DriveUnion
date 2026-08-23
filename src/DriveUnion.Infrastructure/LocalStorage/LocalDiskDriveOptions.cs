namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// What the local-disk Drive substitute needs to run, and the switch that keeps it off.
///
/// Every value here is deliberately absent by default. A misread configuration must produce a
/// backend that does nothing, not one that quietly starts keeping customers' files on one box.
/// </summary>
public sealed class LocalDiskDriveOptions
{
    public const string SectionName = "DriveUnion:LocalDisk";

    /// <summary>
    /// Off unless a deployment says otherwise. Nothing in <see cref="LocalDiskDriveClient"/> is
    /// reachable while this is false: the registration does not run and no <c>IDriveClient</c> is
    /// replaced.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where the bytes go. Relative paths resolve against the process's working directory, which for
    /// <c>dotnet run</c> is the web project — pick an absolute path if that is not what you want.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// How long a resumable session stays usable. Drive's own window is about a week, and matching it
    /// is the point: an upload that resumes here on day six must resume against Google too.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);
}
