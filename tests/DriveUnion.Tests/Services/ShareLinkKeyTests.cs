using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// A link that carries its own copy of a file's key.
///
/// <para>The cryptography is proven in <c>Scripts/crypto/rewrap.test.ts</c>, where it happens. What
/// is under test here is the carrying: that the link's three custody fields replace the file's and
/// nothing else does, that the two rows land together or not at all, and that revoking the link is
/// what takes the recipient's copy away.</para>
/// </summary>
public class ShareLinkKeyTests
{
    private const string FileWrapped = "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=";
    private const string LinkWrapped = "RERERERERERERERERERERERERERERERERERERERERERE=";

    private static EncryptionHeader Header() => new(
        Scheme: 1,
        SegmentSize: 1024 * 1024,
        NoncePrefix: "AAAAAAAAAAA=",
        PlaintextLength: 4096,
        KdfSalt: "BBBBBBBBBBBBBBBBBBBBBB==",
        KdfIterations: 600_000,
        WrappedKey: FileWrapped);

    private static LinkKeyMaterial Material() =>
        new("Q0NDQ0NDQ0NDQ0NDQ0NDQ0M=", 600_000, LinkWrapped);

    /// <summary>An uploaded, encrypted file — the thing an owner is about to share.</summary>
    private static async Task<Guid> LockedFileAsync(ServiceTestHarness harness, Guid tenantId)
    {
        var content = new byte[4112];
        var begun = await harness.Uploads().BeginAsync(
            tenantId,
            Guid.NewGuid(),
            new BeginUploadRequest("contract.pdf", "application/pdf", content.Length, Header()),
            default);

        var progress = await harness.Uploads().WriteChunkAsync(
            tenantId, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        return progress.StoredFileId!.Value;
    }

    [Fact]
    public async Task The_visitor_is_handed_the_links_key_and_the_files_format()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(fileId, null, null, null, Material()),
            default);

        var header = (await harness.PublicLinks().ResolveAsync(link.Slug, default)).File!.Encryption!;

        // Three fields replaced: the ones that decide who may open it.
        header.WrappedKey.Should().Be(LinkWrapped);
        header.KdfSalt.Should().Be(Material().KdfSalt);
        header.KdfIterations.Should().Be(Material().KdfIterations);

        // Four left alone: the ones that describe the ciphertext actually on disk. Swapping any of
        // these for a link's would be describing a different file — the segments would be read at
        // the wrong offsets and every nonce would be wrong.
        header.Scheme.Should().Be(1);
        header.SegmentSize.Should().Be(1024 * 1024);
        header.NoncePrefix.Should().Be("AAAAAAAAAAA=");
        header.PlaintextLength.Should().Be(4096);
    }

    [Fact]
    public async Task A_link_without_one_still_serves_the_files_own_key()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null), default);

        // What shipped with the format, and still correct: the recipient needs the owner's own
        // passphrase. The panel says which of the two kinds a link is, precisely because they are
        // indistinguishable from the outside.
        (await harness.PublicLinks().ResolveAsync(link.Slug, default))
            .File!.Encryption!.WrappedKey.Should().Be(FileWrapped);

        link.HasOwnKey.Should().BeFalse();
    }

    [Fact]
    public async Task Two_links_to_one_file_carry_two_different_keys()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);

        var first = await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null, null, Material()), default);

        var other = new LinkKeyMaterial("RUVFRUVFRUVFRUVFRUVFRUU=", 600_000, "RkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkY=");

        var second = await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null, null, other), default);

        // One file, one set of bytes, two people who may open it and cannot open each other's link.
        // Nothing was re-encrypted to get there — which is why sharing a 40 GB film costs a row.
        var a = (await harness.PublicLinks().ResolveAsync(first.Slug, default)).File!.Encryption!;
        var b = (await harness.PublicLinks().ResolveAsync(second.Slug, default)).File!.Encryption!;

        a.WrappedKey.Should().Be(LinkWrapped);
        b.WrappedKey.Should().Be(other.WrappedKey);
        a.NoncePrefix.Should().Be(b.NoncePrefix, "it is the same ciphertext behind both");
    }

    [Fact]
    public async Task Revoking_the_link_takes_the_recipients_key_with_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);
        var link = await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null, null, Material()), default);

        (await harness.Links().RevokeAsync(tenant.Id, link.Id, default)).Should().BeTrue();

        // The link resolves to nothing, so the header goes nowhere. Whoever already downloaded the
        // ciphertext still has it and still has their secret — that cannot be taken back and this
        // does not pretend to — but the product stops handing the pair out.
        (await harness.PublicLinks().ResolveAsync(link.Slug, default)).IsAvailable.Should().BeFalse();

        // The file itself is untouched: its own wrapped key is still there and the owner still opens
        // it with the passphrase they always used.
        (await harness.Db.FileEncryptions.AsNoTracking().SingleAsync(e => e.StoredFileId == fileId))
            .WrappedKey.Should().Be(FileWrapped);
    }

    [Fact]
    public async Task A_malformed_key_makes_no_link_at_all()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);

        var refused = async () => await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(fileId, null, null, null, Material() with { WrappedKey = "" }),
            default);

        await refused.Should().ThrowAsync<ArgumentException>();

        // Not a link with the file's key quietly substituted, and not a link with an empty one:
        // no link. The first would widen what the recipient can open without saying so, and the
        // second would resolve, render, ask for a secret and never open.
        (await harness.Db.ShareLinks.CountAsync()).Should().Be(0);
        (await harness.Db.ShareLinkKeys.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("", 600_000, "QUFB")]           // no salt
    [InlineData("QUFB", 600_000, "")]           // no key
    [InlineData("QUFB", 1, "QUFB")]             // a derivation worth nothing
    [InlineData("QUFB", 50_000_000, "QUFB")]    // a number nobody would choose
    public void A_re_wrap_that_is_not_shaped_like_one_is_refused(string salt, int iterations, string wrapped)
    {
        // The same judgement the file's own header gets, for the same reason: the server cannot tell
        // whether these bytes unwrap to anything, but it can tell that a field will not fit its
        // column or that a count is nonsense — and it refuses before a link exists rather than after
        // somebody has sent it to a person who cannot open it.
        new LinkKeyMaterial(salt, iterations, wrapped).IsWellFormed.Should().BeFalse();

        Material().IsWellFormed.Should().BeTrue();
    }

    [Fact]
    public void A_field_longer_than_its_column_is_refused_rather_than_truncated()
    {
        var tooLong = new string('A', Core.Storage.FileEncryption.MaxFieldLength + 1);

        (Material() with { WrappedKey = tooLong }).IsWellFormed.Should().BeFalse();
        (Material() with { KdfSalt = tooLong }).IsWellFormed.Should().BeFalse();
    }

    [Fact]
    public async Task The_panel_is_told_which_kind_of_link_each_one_is()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var fileId = await LockedFileAsync(harness, tenant.Id);

        await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null, null, Material()), default);
        await harness.Links().CreateAsync(
            tenant.Id, new CreateShareLinkRequest(fileId, null, null), default);

        var links = await harness.Links().ListForFileAsync(tenant.Id, fileId, default);

        // A bit, never the key. The owner has to be able to tell «I gave them a key for this file»
        // from «I gave them my passphrase» afterwards, and nothing else on the row says so.
        links.Should().HaveCount(2);
        links.Count(l => l.HasOwnKey).Should().Be(1);
    }
}
