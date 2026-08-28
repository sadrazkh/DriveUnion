using System.Text.RegularExpressions;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Every place in the product that opens a stored file for reading also writes down what left.
///
/// <para><b>Why this is a source scan and not a behaviour test.</b> The bug it exists for is not a
/// wrong answer, it is a missing call — and a missing call has no behaviour to assert against. Three
/// download paths were written over about a year; the first metered, and the two after it did not,
/// each for a reason that was plausible in isolation. Nothing failed. The operator's own «what has
/// this product served» chart simply drew a third of the truth, and a workspace over its monthly
/// allowance could pull terabytes through the two routes best suited to pulling terabytes.</para>
///
/// <para>So the guard is about the shape of the source: a file that reaches storage for bytes must
/// also reach the meter. It cannot prove the figure is right — <c>TrafficMeterTests</c> and the two
/// cap tests do that — but it is the only thing that can fail on the day somebody adds the fourth
/// path, which is the day it needs to.</para>
/// </summary>
public class EveryEgressPathIsMeteredTests
{
    /// <summary>
    /// The one call that opens a read stream against the pool. Anything holding bytes to send holds
    /// them because of this.
    /// </summary>
    private const string OpensStorage = "OpenDownloadAsync";

    /// <summary>
    /// The one call that writes egress down. <c>ITrafficMeter</c> deliberately has a single write —
    /// see its doc — so there is exactly one spelling to look for.
    /// </summary>
    private const string Meters = "RecordAsync";

    /// <summary>
    /// Files that open a download and are right not to meter it, each with the argument written out.
    ///
    /// <para>An allow-list rather than a rule the scanner infers, because every entry is a judgement
    /// somebody has to make deliberately. Adding a file here is a line in a diff a reviewer sees;
    /// forgetting a metering call is not.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // The operator moving a file from one of their own Google accounts to another. It is real
        // egress and the operator really pays for it, but it belongs to no customer's allowance: a
        // workspace must not spend its month because the operator chose to retire an account. There
        // is nowhere to put it either — TenantUsageDay is keyed by tenant, and an operator-owned
        // bucket is a different table and a different feature.
        ["src/DriveUnion.Infrastructure/Services/AccountMigrator.cs"] =
            "the operator's own housekeeping between two pool accounts, chargeable to no workspace",

        // Locking a file that is already stored. It reads the readable copy out of Drive and writes
        // a sealed one back, so it is real egress and the operator really pays Google for it — but
        // nothing is served. The bytes go from the pool account to this process and back to the same
        // pool account, and no reader anywhere receives one.
        //
        // Charging it to the customer would be charging them for the privilege of taking a copy the
        // operator can read *away* from the operator, at the rate the customer's own traffic
        // allowance is sold: "bytes served out of this workspace". Nothing was served out. The
        // allowance would also make the feature unusable on exactly the files it is most wanted for,
        // since locking a 10 GB film would spend 10 GB of a month nobody watched anything in.
        ["src/DriveUnion.Infrastructure/Uploads/FileLocker.cs"] =
            "an internal re-write within one pool account; real cost to the operator, nothing served "
                + "to anybody, and chargeable to no allowance that is sold as bytes delivered",
    };

    [Fact]
    public void Nothing_opens_storage_for_reading_without_recording_what_left()
    {
        var offenders = new List<string>();
        var opened = 0;

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(Path.Combine(Root(), file));

            if (!text.Contains(OpensStorage, StringComparison.Ordinal)) continue;

            // The interface and the two implementations declare the method; they do not call it on
            // somebody else's behalf, so they are not egress paths.
            if (Declares(text)) continue;

            opened++;

            if (Exempt.ContainsKey(file)) continue;
            if (text.Contains(Meters, StringComparison.Ordinal)) continue;

            offenders.Add(file);
        }

        // A floor, because a scanner that matched nothing would pass this test most loudly of all.
        // Three call sites meter and one is exempt; the number only goes up.
        opened.Should().BeGreaterThanOrEqualTo(
            4, "the public download path, the JSON API, the S3 gateway and the account migrator");

        offenders.Should().BeEmpty(
            "each of these opens a stored file for reading and never tells ITrafficMeter what left, "
                + "so Google bills the operator for bytes no screen in this product can account for");
    }

    /// <summary>
    /// The exemptions still exist, still open a download, and still do not meter.
    ///
    /// <para>Without this, an entry that stopped being true — a file renamed, deleted, or since
    /// taught to meter — would sit in the list forever, silently excusing a path nobody is looking
    /// at any more.</para>
    /// </summary>
    [Fact]
    public void Every_exemption_is_still_about_a_real_unmetered_path()
    {
        foreach (var (file, reason) in Exempt)
        {
            reason.Should().NotBeNullOrWhiteSpace("an exemption without an argument is an oversight");

            var full = Path.Combine(Root(), file);

            File.Exists(full).Should().BeTrue($"{file} is exempted from egress metering but is not there");

            var text = File.ReadAllText(full);

            text.Should().Contain(
                OpensStorage,
                $"{file} no longer opens a download, so its exemption is stale and should be deleted");

            text.Should().NotContain(
                Meters,
                $"{file} meters now, so its exemption is wrong rather than merely unnecessary");
        }
    }

    /// <summary>
    /// True for the interface and the drivers that implement the call rather than make it.
    ///
    /// <para>Matched on the declaration's own shape — a return type and the name followed by an open
    /// bracket — instead of on a list of file names, so a second Drive backend is covered on the day
    /// it is written.</para>
    /// </summary>
    private static bool Declares(string text) =>
        Regex.IsMatch(
            text,
            $@"(Task<DriveDownload>|ValueTask<DriveDownload>)\s+{OpensStorage}\s*\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

    private static IEnumerable<string> SourceFiles()
    {
        foreach (var project in new[] { "src/DriveUnion.Core", "src/DriveUnion.Infrastructure", "src/DriveUnion.Web" })
        {
            var directory = new DirectoryInfo(Path.Combine(Root(), project));

            Assert.True(directory.Exists, $"{project} does not exist; this test reads the product's source.");

            foreach (var file in directory.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Root(), file.FullName).Replace('\\', '/');

                if (relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (relative.Contains("/bin/", StringComparison.Ordinal)) continue;
                if (relative.Contains("/node_modules/", StringComparison.Ordinal)) continue;

                yield return relative;
            }
        }
    }

    private static string Root()
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
