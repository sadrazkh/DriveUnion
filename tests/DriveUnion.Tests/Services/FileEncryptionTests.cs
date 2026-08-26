using DriveUnion.Core.Application;
using DriveUnion.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// The storage side of client-side encryption: a header goes in with an upload and comes back out
/// with the file, and nothing on the way holds anything that opens it.
///
/// <para>The format itself is proven in <c>Scripts/crypto/format.test.ts</c>, where the primitives
/// are. What is under test here is the carrying: that a header survives an upload it was handed at
/// the first request and attached at the last, and that the paths which cannot decrypt say so
/// rather than serving ciphertext as though it were the file.</para>
/// </summary>
public class FileEncryptionTests
{
    private static EncryptionHeader Header(long plaintextLength = 4096) => new(
        Scheme: 1,
        SegmentSize: 1024 * 1024,
        NoncePrefix: "AAAAAAAAAAA=",
        PlaintextLength: plaintextLength,
        KdfSalt: "BBBBBBBBBBBBBBBBBBBBBB==",
        KdfIterations: 600_000,
        WrappedKey: "Q0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0M=");

    [Fact]
    public async Task A_header_handed_to_the_first_request_is_attached_to_the_finished_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var content = new byte[4112];
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id,
            Guid.NewGuid(),
            new BeginUploadRequest("secret.bin", "application/octet-stream", content.Length, Header()),
            default);

        // The file row is created by whichever request lands the last chunk — hours later for a
        // large file — so the header has to survive the whole session. That is the property.
        var progress = await harness.Uploads().WriteChunkAsync(
            tenant.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        progress.StoredFileId.Should().NotBeNull();

        var stored = await new FileEncryptionStore(harness.Db)
            .ForFileAsync(tenant.Id, progress.StoredFileId!.Value, default);

        stored.Should().BeEquivalentTo(Header());
    }

    [Fact]
    public async Task A_plain_upload_carries_no_header_at_all()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var content = new byte[64];
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, Guid.NewGuid(), new BeginUploadRequest("plain.txt", "text/plain", content.Length), default);

        var progress = await harness.Uploads().WriteChunkAsync(
            tenant.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        // Null and not a row of empty strings. «Is this encrypted» is the presence of a row, so an
        // empty one would be a file the panel offers to decrypt and cannot.
        (await new FileEncryptionStore(harness.Db).ForFileAsync(tenant.Id, progress.StoredFileId!.Value, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task The_stored_size_is_the_ciphertext_and_the_shown_size_is_the_file()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        // 4096 bytes of file becomes 4112 on the wire: one 16-byte tag on its single segment.
        var content = new byte[4112];
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id,
            Guid.NewGuid(),
            new BeginUploadRequest("secret.bin", "application/octet-stream", content.Length, Header(4096)),
            default);

        var progress = await harness.Uploads().WriteChunkAsync(
            tenant.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        var file = await harness.Db.StoredFiles.AsNoTracking()
            .FirstAsync(f => f.Id == progress.StoredFileId!.Value);

        // Both numbers are true and answer different questions: the quota is spent on what Drive
        // holds, and the number beside the name is the file the customer has.
        file.SizeBytes.Should().Be(4112);

        var header = await new FileEncryptionStore(harness.Db).ForFileAsync(tenant.Id, file.Id, default);
        header!.PlaintextLength.Should().Be(4096);
    }

    [Fact]
    public async Task One_workspace_cannot_read_anothers_header()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        harness.SeedAccount();

        var content = new byte[32];
        var begun = await harness.Uploads().BeginAsync(
            theirs.Id, Guid.NewGuid(), new BeginUploadRequest("theirs.bin", "application/octet-stream", content.Length, Header(16)), default);

        var progress = await harness.Uploads().WriteChunkAsync(
            theirs.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        var store = new FileEncryptionStore(harness.Db);
        var fileId = progress.StoredFileId!.Value;

        // The wrapped key is not the file, but it is the only thing standing between a stolen
        // database and somebody's passphrase — so reaching one through a guessed file id must not
        // work either. The tenant is on this table precisely for this.
        (await store.ForFileAsync(mine.Id, fileId, default)).Should().BeNull();
        (await store.PlaintextLengthsAsync(mine.Id, [fileId], default)).Should().BeEmpty();

        (await store.PlaintextLengthsAsync(theirs.Id, [fileId], default)).Should().ContainKey(fileId);
    }

    [Fact]
    public async Task A_listing_gets_the_padlock_and_the_real_size_and_nothing_else()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();

        var plain = harness.SeedFile(tenant.Id, account.Id, "plain.txt");

        var content = new byte[48];
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id, Guid.NewGuid(), new BeginUploadRequest("secret.bin", "application/octet-stream", content.Length, Header(32)), default);
        var progress = await harness.Uploads().WriteChunkAsync(
            tenant.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        var lengths = await new FileEncryptionStore(harness.Db)
            .PlaintextLengthsAsync(tenant.Id, [plain.Id, progress.StoredFileId!.Value], default);

        // Two columns and not the header. A row draws a padlock and a size; sending every wrapped
        // key to a screen that needed neither would be handing out material for no reason at all.
        lengths.Should().ContainSingle().Which.Key.Should().Be(progress.StoredFileId!.Value);

        // 48 bytes stored and 32 bytes of file, and it is the 32 the customer's list has to show —
        // the same number the public download page shows for the very same file.
        lengths[progress.StoredFileId!.Value].Should().Be(32);

        (await new FileEncryptionStore(harness.Db).PlaintextLengthsAsync(tenant.Id, [], default))
            .Should().BeEmpty("an empty page asks nothing rather than everything");
    }

    [Theory]
    [InlineData(0, 1024, 600_000, "AAA=", "BBB=", "CCC=")]        // scheme zero
    [InlineData(1, 0, 600_000, "AAA=", "BBB=", "CCC=")]           // no segment size
    [InlineData(1, 1024, 1, "AAA=", "BBB=", "CCC=")]              // a derivation worth nothing
    [InlineData(1, 1024, 600_000, "", "BBB=", "CCC=")]            // no nonce
    [InlineData(1, 1024, 600_000, "AAA=", "", "CCC=")]            // no salt
    [InlineData(1, 1024, 600_000, "AAA=", "BBB=", "")]            // no key
    public void A_header_that_is_not_shaped_like_one_is_refused(
        int scheme,
        int segmentSize,
        int iterations,
        string noncePrefix,
        string salt,
        string wrapped)
    {
        // Not a check that the header is correct — the server cannot know that and could not act on
        // it if it did. It is a check that the columns will hold it and the numbers are not
        // nonsense, so a malformed upload is refused at the door rather than stored and discovered
        // by whoever tries to open the file six months later.
        new EncryptionHeader(scheme, segmentSize, noncePrefix, 100, salt, iterations, wrapped)
            .IsWellFormed.Should().BeFalse();

        Header().IsWellFormed.Should().BeTrue();
    }

    [Fact]
    public async Task A_malformed_header_stops_the_upload_before_anything_is_reserved()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var refused = async () => await harness.Uploads().BeginAsync(
            tenant.Id,
            Guid.NewGuid(),
            new BeginUploadRequest("bad.bin", "application/octet-stream", 100, Header() with { Scheme = 0 }),
            default);

        await refused.Should().ThrowAsync<Infrastructure.Services.UploadRejectedException>();

        // Nothing reserved and nothing started: the check runs ahead of the storage reserve, so a
        // refused upload spends none of the customer's quota.
        (await harness.Db.UploadSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_share_link_to_a_locked_file_carries_what_opens_it()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        harness.SeedAccount();

        var content = new byte[4112];
        var begun = await harness.Uploads().BeginAsync(
            tenant.Id,
            Guid.NewGuid(),
            new BeginUploadRequest("holiday.mp4", "video/mp4", content.Length, Header(4096)),
            default);

        var progress = await harness.Uploads().WriteChunkAsync(
            tenant.Id, begun.SessionId, new MemoryStream(content), 0, content.Length, default);

        harness.SeedLink(tenant.Id, progress.StoredFileId!.Value, "kx91mzq4");

        var resolution = await harness.PublicLinks().ResolveAsync("kx91mzq4", default);

        // Public on an anonymous page, and deliberately: none of it is secret, and the visitor who
        // holds the link is who the owner meant to give the file to. Making them ask a second
        // endpoint for it would be a round trip protecting nothing.
        resolution.File!.Encryption.Should().BeEquivalentTo(Header(4096));

        // The size on the card is the file's, not the ciphertext's — the visitor is about to receive
        // the file, and the tags that make the stored figure larger are not part of it.
        resolution.File.Encryption!.PlaintextLength.Should().Be(4096);
        resolution.File.SizeBytes.Should().Be(4112);
    }

    [Fact]
    public async Task A_share_link_to_an_ordinary_file_carries_nothing_extra()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "notes.txt", 4096);

        harness.SeedLink(tenant.Id, file.Id, "kx91mzq4");

        // Null and not an empty header. The download page decides which of two entirely different
        // screens to draw from this one field, and an empty one would ask a visitor for the key to a
        // file that does not have one.
        (await harness.PublicLinks().ResolveAsync("kx91mzq4", default))
            .File!.Encryption.Should().BeNull();
    }

    [Fact]
    public void A_field_longer_than_its_column_is_refused_rather_than_truncated()
    {
        var tooLong = new string('A', Core.Storage.FileEncryption.MaxFieldLength + 1);

        // Truncating a wrapped key stores something that will never unwrap, and the customer finds
        // out when they try to open the file. It is a refusal at the door instead.
        (Header() with { WrappedKey = tooLong }).IsWellFormed.Should().BeFalse();
        (Header() with { KdfSalt = tooLong }).IsWellFormed.Should().BeFalse();
        (Header() with { NoncePrefix = tooLong }).IsWellFormed.Should().BeFalse();
    }
}
