namespace DriveUnion.Infrastructure.Tenancy;

/// <summary>
/// What a workspace slug may be.
///
/// <para>The slug is not decoration. M1 §5 makes it the per-tenant folder inside <b>every</b> Google
/// account in the operator's pool — <c>DriveUnion/{slug}/</c> — so it is at once a directory name on
/// a remote filesystem and a path segment in this product's own URLs. That is two sets of rules and
/// this class is their intersection, which is why it is stricter than either alone.</para>
///
/// <para><b>It is chosen once and never changed.</b> Renaming it does not move the files: they stay
/// in the old folder, and every future upload goes to a new one, so a workspace ends up with its
/// library split across two directories that nothing in the product knows are related. There is
/// deliberately no rename command anywhere for that reason, and the create form says so before the
/// operator commits to a spelling.</para>
///
/// <para>Lowercase only, so that two workspaces cannot differ by case: <c>Acme</c> and <c>acme</c>
/// are one folder on a case-insensitive filesystem and two rows in a case-sensitive index, and the
/// product would then be certain that two customers' files are separate while the disk holding them
/// disagrees.</para>
/// </summary>
public static class TenantSlug
{
    /// <summary>
    /// Long enough that a slug is not a collision waiting to happen and short enough to read in a
    /// path. Three is the shortest a human would pick deliberately.
    /// </summary>
    public const int MinimumLength = 3;

    /// <summary>
    /// Well inside the column's 64, and well inside every filesystem's per-component ceiling once
    /// the <c>DriveUnion/</c> prefix and a file name are added to it.
    /// </summary>
    public const int MaximumLength = 40;

    /// <summary>
    /// Names that are not files on Windows — they are devices, and a folder cannot be called one.
    ///
    /// <para>This is not hypothetical here: <c>LocalDiskDriveClient</c> is the development substitute
    /// for Drive and writes these folders onto a real disk, and the machine this product is built on
    /// is Windows. A workspace slugged <c>con</c> would create fine, take uploads, and fail on the
    /// first byte written — with an error naming a path rather than the workspace.</para>
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    /// <summary>
    /// Trims and lower-cases, and does nothing else. It deliberately does not <i>repair</i> a slug —
    /// no transliteration, no space-to-hyphen, no stripping of what it does not like. A form that
    /// silently turns «شرکت آلفا» into <c>shrkt-alfa</c> has chosen a permanent folder name on the
    /// operator's behalf; refusing and showing the rule lets them choose it themselves.
    /// </summary>
    public static string Normalise(string? slug) => slug?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Whether <paramref name="slug"/> is exactly what this product would put in a path and in a
    /// Drive folder name. Expects an already-normalised value; an upper-case letter is refused
    /// rather than folded, so the caller cannot skip <see cref="Normalise"/> by accident.
    /// </summary>
    public static bool IsWellFormed(string? slug)
    {
        if (slug is null || slug.Length < MinimumLength || slug.Length > MaximumLength) return false;

        // Ordinal, and not char.IsLetterOrDigit: the latter is true of «ا» and of '٣', which are
        // letters and digits and are not URL- or filename-safe in the sense this needs.
        for (var i = 0; i < slug.Length; i++)
        {
            var c = slug[i];
            var isAlphanumeric = c is >= 'a' and <= 'z' or >= '0' and <= '9';

            if (isAlphanumeric) continue;

            // A hyphen may join, never lead, trail or double. Leading and trailing hyphens are the
            // ones that get lost when a path is trimmed by something downstream; a doubled one makes
            // two visually indistinguishable slugs.
            if (c != '-' || i == 0 || i == slug.Length - 1 || slug[i - 1] == '-') return false;
        }

        return !ReservedNames.Contains(slug, StringComparer.Ordinal);
    }
}
