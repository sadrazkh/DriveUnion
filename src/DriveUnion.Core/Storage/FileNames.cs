namespace DriveUnion.Core.Storage;

/// <summary>
/// What a file may be called, once.
///
/// <para>Two places decide this and they must decide it identically: a name taken off a remote
/// server's <c>Content-Disposition</c>, and a name a customer types into the rename box. The first
/// had the rule and the second was about to copy it, which is how two spellings of one rule start —
/// and the interesting half of this rule is a security boundary, so the copy would have been a
/// second place for a traversal to get through.</para>
///
/// <para><b>Stripped rather than refused.</b> A name with a slash in it is almost always a path the
/// far end sent rather than an attack, and refusing the download over it would be refusing the file
/// somebody asked for. What is left after stripping is a name; if nothing is left, there was no name.</para>
/// </summary>
public static class FileNames
{
    /// <summary>The usable name in <paramref name="name"/>, or null when there is none.</summary>
    public static string? Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Quotes are how a header carries it and are not part of the name.
        var trimmed = name.Trim().Trim('"');

        // Directory separators and the traversal they enable, then anything a filesystem refuses.
        var cleaned = new string([.. trimmed
            .Where(c => c is not ('/' or '\\' or ':'))
            .Where(c => !char.IsControl(c))]);

        cleaned = cleaned.Replace("..", string.Empty, StringComparison.Ordinal).Trim();

        return cleaned.Length > 0 ? cleaned : null;
    }
}
