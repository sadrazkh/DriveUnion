using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// Every «کپی» button in the panel is wired to something that will actually copy.
///
/// <para><b>The failure this exists for had shipped.</b> <c>copyLink.ts</c> read <c>data-value</c>
/// off the element carrying <c>data-island="copy-link"</c> and returned when it found none. The API
/// keys screen mounts that island on the <c>.field</c> box around a readout and a button, and
/// declares the address as <c>data-copy-value</c> — so the guard clause hit on the first line, no
/// listener was ever attached, and both Copy buttons on that screen did nothing when pressed.</para>
///
/// <para>Nothing could see it. <c>IslandRegistrationTests</c> checks that every island a view mounts
/// is registered in <c>main.ts</c> and that every registration is mounted somewhere — and both were
/// true here. The island's <i>name</i> was right; it was the attributes underneath that disagreed,
/// and an attribute nobody reads throws no error in any language.</para>
///
/// <para>It mattered more than a dead button usually does: an API secret is shown exactly once and
/// the row keeps only its SHA-256. A customer who pressed Copy, saw a button behaving like every
/// other button, and navigated away held a key they could no longer read and could only revoke.</para>
/// </summary>
public class CopyButtonContractTests
{
    /// <summary>
    /// Mount points for the copy island, with whatever attributes the opening tag carries.
    ///
    /// <para>Matched to the end of the tag rather than to a fixed set of attributes, because the
    /// point is to read what the view actually declares rather than what it was expected to.</para>
    /// </summary>
    private static readonly Regex MountPoint = new(
        """<[a-zA-Z]+[^>]*?data-island\s*=\s*"copy-link"[^>]*>""",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_copy_island_declares_an_address_the_script_reads()
    {
        var script = Read("src/DriveUnion.Web/Scripts/copyLink.ts");

        // The two spellings the script resolves. Read out of the script rather than listed here, so
        // this test is about the two halves agreeing rather than about a third opinion.
        script.Should().Contain("el.dataset.value", "the bare-button shape");
        script.Should().Contain("el.dataset.copyValue", "the box-with-a-readout shape");

        var found = 0;

        foreach (var (file, text) in Views())
        {
            foreach (Match mount in MountPoint.Matches(text))
            {
                found++;

                var tag = mount.Value;

                // Either the mount point is the button and carries the address, or it is the box and
                // declares it — and in the second case there has to be a button in it to attach to.
                var isButton = tag.Contains("data-value=", StringComparison.Ordinal);
                var isBox = tag.Contains("data-copy-value=", StringComparison.Ordinal);

                (isButton || isBox).Should().BeTrue(
                    $"{file} mounts copy-link with neither data-value nor data-copy-value, "
                        + "so the script attaches no listener and the button silently does nothing");

                if (!isBox) continue;

                // The box shape needs the button the script looks for. Searched from the mount tag
                // to the end of the file rather than inside a parsed element — this is a scan, and
                // a false pass here is a button that does nothing.
                text[mount.Index..].Should().Contain(
                    "data-copy-button",
                    $"{file} declares data-copy-value but no [data-copy-button] for the script to bind");
            }
        }

        // A floor, because a regex that matched nothing would pass every assertion above in silence.
        // Two on the API keys screen — the newly minted secret and the id/secret pair — and one on
        // the file detail panel's share link.
        found.Should().BeGreaterThanOrEqualTo(
            3, "the two on the API keys screen and the one on the file detail panel");
    }

    private static IEnumerable<(string File, string Text)> Views()
    {
        var root = RepositoryRoot();

        foreach (var folder in new[] { "src/DriveUnion.Web/Views", "src/DriveUnion.Web/Areas" })
        {
            var directory = new DirectoryInfo(Path.Combine(root, folder));

            if (!directory.Exists) continue;

            foreach (var file in directory.EnumerateFiles("*.cshtml", SearchOption.AllDirectories))
            {
                yield return (Path.GetRelativePath(root, file.FullName).Replace('\\', '/'),
                    File.ReadAllText(file.FullName));
            }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("DriveUnion.slnx was not found above the test binaries.");
    }
}
