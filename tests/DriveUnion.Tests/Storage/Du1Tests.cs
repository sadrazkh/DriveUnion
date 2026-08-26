using System.Text;
using System.Text.Json;
using DriveUnion.Core.Storage;
using FluentAssertions;

namespace DriveUnion.Tests.Storage;

/// <summary>
/// The <c>du1</c> format in C#, and — the part that matters — that it is the same format the browser
/// implements.
///
/// <para>Two implementations of one thing is a standing invitation to drift, and drift here means a
/// file somebody cannot open. Nothing in a C# test can catch that on its own: it would only be this
/// implementation agreeing with itself. So a fixture produced by each side is committed and each
/// side opens the other's, and <c>Scripts/crypto/interop.test.ts</c> is the other half of this
/// file.</para>
/// </summary>
public class Du1Tests
{
    /// <summary>Where the two fixtures live, beside the TypeScript that reads and writes them.</summary>
    private static string FixturePath(string name) =>
        Path.Combine(RepositoryRoot(), "src", "DriveUnion.Web", "Scripts", "crypto", "fixtures", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DriveUnion.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found from the test binary.");
    }

    /// <summary>The shape both sides write. Base64 everywhere, so it is a plain JSON file.</summary>
    private sealed record Fixture(
        string Secret,
        int Scheme,
        int SegmentSize,
        string NoncePrefix,
        long PlaintextLength,
        string KdfSalt,
        int KdfIterations,
        string WrappedKey,
        string Ciphertext,
        string Plaintext);

    private static Fixture Read(string name) =>
        JsonSerializer.Deserialize<Fixture>(
            File.ReadAllText(FixturePath(name)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException($"{name} did not parse.");

    [Fact]
    public void A_file_the_browser_sealed_opens_here()
    {
        // The direction that matters most: everything a customer uploads from a browser is written
        // by that implementation, and this one now has to read it — to move it, to serve it, and one
        // day to re-encrypt it. A drift in this direction is unopenable customer data.
        var fixture = Read("browser-sealed.json");

        var wrapping = Du1.DeriveWrappingKey(
            fixture.Secret, Convert.FromBase64String(fixture.KdfSalt), fixture.KdfIterations);

        var contentKey = Du1.UnwrapKey(Convert.FromBase64String(fixture.WrappedKey), wrapping);
        contentKey.Should().NotBeNull("the passphrase in the fixture is the one that sealed it");

        var opened = OpenAll(fixture, contentKey!);

        opened.Should().Equal(Convert.FromBase64String(fixture.Plaintext));
    }

    [Fact]
    public void What_this_seals_is_what_the_browser_fixture_says_it_should_be()
    {
        // The other direction is asserted in Scripts/crypto/interop.test.ts, which opens the fixture
        // written below. This half only checks the fixture is still the shape that file expects —
        // a rename here and a green suite there would be two tests passing about nothing.
        var fixture = Read("server-sealed.json");

        fixture.Scheme.Should().Be(Du1.Scheme);
        fixture.KdfIterations.Should().Be(Du1.KdfIterations);

        // Deliberately not compared against Du1.SegmentSize. The fixtures use 64 bytes so that a
        // file spanning three segments is a few hundred bytes of base64 rather than three megabytes
        // — which is legitimate precisely because the segment size is a field in the header and not
        // a constant either side may assume.
        fixture.SegmentSize.Should().BeGreaterThan(0);

        var wrapping = Du1.DeriveWrappingKey(
            fixture.Secret, Convert.FromBase64String(fixture.KdfSalt), fixture.KdfIterations);

        var contentKey = Du1.UnwrapKey(Convert.FromBase64String(fixture.WrappedKey), wrapping);

        OpenAll(fixture, contentKey!).Should().Equal(Convert.FromBase64String(fixture.Plaintext));
    }

    /// <summary>Every segment of a fixture, decrypted and joined — what a reader on either side does.</summary>
    private static byte[] OpenAll(Fixture fixture, byte[] contentKey)
    {
        var cipher = Convert.FromBase64String(fixture.Ciphertext);
        var prefix = Convert.FromBase64String(fixture.NoncePrefix);

        var segments = (int)((fixture.PlaintextLength + fixture.SegmentSize - 1) / fixture.SegmentSize);
        var stride = fixture.SegmentSize + Du1.TagBytes;

        var plain = new List<byte>();

        for (var index = 0; index < segments; index++)
        {
            var from = index * stride;
            var length = (int)Math.Min(
                fixture.SegmentSize, fixture.PlaintextLength - ((long)index * fixture.SegmentSize))
                + Du1.TagBytes;

            var opened = Du1.DecryptSegment(
                contentKey, prefix, index, index == segments - 1, cipher.AsSpan(from, length));

            opened.Should().NotBeNull($"segment {index} must verify");
            plain.AddRange(opened!);
        }

        return [.. plain];
    }

    [Fact]
    public void A_segment_round_trips()
    {
        var key = new byte[Du1.KeyBytes];
        var prefix = new byte[Du1.NoncePrefixBytes];
        var plain = Encoding.UTF8.GetBytes("the quarterly numbers");

        var sealedBytes = Du1.EncryptSegment(key, prefix, 0, true, plain);

        sealedBytes.Length.Should().Be(plain.Length + Du1.TagBytes, "the tag is appended");
        Du1.DecryptSegment(key, prefix, 0, true, sealedBytes).Should().Equal(plain);
    }

    [Fact]
    public void A_segment_moved_to_another_index_does_not_verify()
    {
        var key = new byte[Du1.KeyBytes];
        var prefix = new byte[Du1.NoncePrefixBytes];

        var sealedBytes = Du1.EncryptSegment(key, prefix, 3, false, [1, 2, 3, 4]);

        // Both the nonce and the AAD carry the index, so a genuine segment of a genuine file read at
        // the wrong offset fails rather than decrypting into the wrong minute of a video.
        Du1.DecryptSegment(key, prefix, 4, false, sealedBytes).Should().BeNull();
    }

    [Fact]
    public void Lying_about_which_segment_is_last_does_not_verify()
    {
        var key = new byte[Du1.KeyBytes];
        var prefix = new byte[Du1.NoncePrefixBytes];

        var sealedBytes = Du1.EncryptSegment(key, prefix, 0, true, [9, 9, 9]);

        // Without the final flag in the AAD a truncated file decrypts cleanly and simply ends early.
        Du1.DecryptSegment(key, prefix, 0, false, sealedBytes).Should().BeNull();
    }

    [Fact]
    public void A_single_flipped_bit_does_not_verify()
    {
        var key = new byte[Du1.KeyBytes];
        var prefix = new byte[Du1.NoncePrefixBytes];

        var sealedBytes = Du1.EncryptSegment(key, prefix, 0, true, [4, 5, 6, 7]);
        sealedBytes[1] ^= 0x01;

        Du1.DecryptSegment(key, prefix, 0, true, sealedBytes).Should().BeNull();
    }

    [Fact]
    public void A_wrong_secret_unwraps_to_nothing_rather_than_to_rubbish()
    {
        var salt = new byte[Du1.SaltBytes];
        var contentKey = new byte[Du1.KeyBytes];

        var envelope = Du1.WrapKey(contentKey, Du1.DeriveWrappingKey("right", salt, 100_000));

        Du1.UnwrapKey(envelope, Du1.DeriveWrappingKey("wrong", salt, 100_000)).Should().BeNull();
        Du1.UnwrapKey(envelope, Du1.DeriveWrappingKey("right", salt, 100_000)).Should().Equal(contentKey);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 17)]
    [InlineData(Du1.SegmentSize, Du1.SegmentSize + 16)]
    [InlineData(Du1.SegmentSize + 1, Du1.SegmentSize + 1 + 32)]
    [InlineData(Du1.SegmentSize * 3, (Du1.SegmentSize * 3) + 48)]
    public void The_stored_length_is_the_file_plus_one_tag_per_segment(long plain, long stored)
    {
        // The number the quota is spent on and the number the plan is checked against. It has to
        // agree with cipherLength in format.ts to the byte, or an upload declares one length to
        // storage and sends another.
        Du1.CipherLength(plain).Should().Be(stored);
    }

    [Fact]
    public void The_nonce_is_the_prefix_then_the_index_big_endian()
    {
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Du1.NonceFor(prefix, 0x01020304).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void The_aad_is_du1_then_the_index_then_the_final_flag()
    {
        Du1.AadFor(1, false).Should().Equal((byte)'d', (byte)'u', (byte)'1', 0, 0, 0, 1, 0);
        Du1.AadFor(1, true).Should().Equal((byte)'d', (byte)'u', (byte)'1', 0, 0, 0, 1, 1);
    }
}
