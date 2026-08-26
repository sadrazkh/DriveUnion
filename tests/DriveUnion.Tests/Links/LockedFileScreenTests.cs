using DriveUnion.Core.Storage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Links;

/// <summary>
/// What the two screens say about a file this product cannot read.
///
/// <para><c>FileEncryptionTests</c> holds the storage and <c>Scripts/crypto/*.test.ts</c> holds the
/// format. This holds the half neither can see: that the panel marks the row and shows the file's
/// own size, and that the public page stops offering a download button which would hand somebody
/// ciphertext under the right file name — the failure that looks exactly like success.</para>
/// </summary>
public class LockedFileScreenTests
{
    /// <summary>2,048 bytes stored, 1,600 of file. Deliberately far enough apart to read.</summary>
    private const long Plaintext = 1600;

    private static StoredFile Lock(PanelPageHarness harness, StoredFile file)
    {
        using var db = harness.NewDbContext();

        db.FileEncryptions.Add(new FileEncryption
        {
            StoredFileId = file.Id,
            TenantId = file.TenantId,
            Scheme = 1,
            SegmentSize = 1024 * 1024,
            NoncePrefix = "AAAAAAAAAAA=",
            PlaintextLength = Plaintext,
            KdfSalt = "BBBBBBBBBBBBBBBBBBBBBB==",
            KdfIterations = 600_000,
            WrappedKey = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.SaveChanges();

        return file;
    }

    [Fact]
    public async Task The_row_carries_a_padlock_with_a_word_behind_it()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "notes.txt", "kx91mzq4");

        Lock(harness, harness.SeedFile(tenant.Id, "passport.pdf"));

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files");

        markup.Should().Contain("🔒");

        // The glyph is what the eye reads and the label is what a screen reader gets: an unlabelled
        // ideogram is a row that says nothing at all to somebody listening to it.
        markup.Should().MatchRegex(@"aria-hidden=""true"">🔒</span>\s*<span class=""visually-hidden"">");
    }

    [Fact]
    public async Task The_size_beside_the_name_is_the_file_and_not_the_ciphertext()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "notes.txt", "kx91mzq4");

        var locked = Lock(harness, harness.SeedFile(tenant.Id, "passport.pdf"));

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync($"/files?selected={locked.Id}");

        // 2,048 is what Drive holds and what the quota was charged; 1,600 is the file the customer
        // has and will get back. Both are true and the list is the place for the second one — the
        // public download page shows the same figure for the same file, and the two disagreeing
        // would be a defect nobody could explain from either screen alone.
        markup.Should().Contain("1.6 KB");
        markup.Should().NotContain("2.0 KB");
    }

    [Fact]
    public async Task An_ordinary_file_is_marked_with_nothing_at_all()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "notes.txt", "kx91mzq4");

        using var client = harness.NewClient(tenant.Id);
        var markup = await client.GetStringAsync("/files");

        // The padlock has to mean something, which means the screen it does not apply to must not
        // carry it. A marker on every row is a marker on none.
        markup.Should().NotContain("🔒");
    }

    [Fact]
    public async Task The_public_page_asks_for_the_key_instead_of_offering_the_bytes()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "passport.pdf", "kx91mzq4");

        using var db = harness.NewDbContext();
        var file = db.StoredFiles.AsNoTracking().First(f => f.TenantId == tenant.Id);
        Lock(harness, file);

        using var client = harness.NewClient(null);
        var markup = await client.GetStringAsync("/d/kx91mzq4");

        // The bytes behind this URL are ciphertext, so a plain link would hand the visitor a file of
        // the right name and the right length that nothing on their machine can open.
        markup.Should().NotContain(@"href=""/d/kx91mzq4/file""");

        // What replaces it: the mount point, and the header it needs — which is public because none
        // of it opens anything without what the visitor is about to type.
        markup.Should().Contain(@"data-island=""unlock-download""");
        markup.Should().Contain("data-header=");
        markup.Should().Contain("&quot;wrappedKey&quot;");

        // And the size on the card is the file's, not the ciphertext's.
        markup.Should().Contain("1.6 KB");
    }

    [Fact]
    public async Task The_public_page_for_an_ordinary_file_still_just_offers_it()
    {
        using var harness = new PanelPageHarness();
        harness.SeedTenant("Acme", "holiday.mp4", "kx91mzq4");

        using var client = harness.NewClient(null);
        var markup = await client.GetStringAsync("/d/kx91mzq4");

        markup.Should().Contain(@"href=""/d/kx91mzq4/file""");
        markup.Should().NotContain(@"data-island=""unlock-download""");
    }
}
