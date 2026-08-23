using DriveUnion.Core.Telegram;
using FluentAssertions;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The encoder has no library behind it and nothing on this machine can scan its output, so it is
/// checked against the specification's own algebra and geometry instead of against a decoder.
///
/// The Reed-Solomon test is the one that matters: a codeword block is by definition divisible by its
/// generator polynomial, so every syndrome must be zero. That is a property this file's arithmetic
/// cannot fake — a wrong table or a wrong remainder shows up immediately — and it is what makes the
/// difference between a QR code and a square of noise that renders perfectly.
/// </summary>
public class QrCodeTests
{
    private const string DeepLink =
        "https://t.me/DriveUnionBot?start=7Xb2mQ9tZk4pL0aVc8NfR1jEwYs6HdGu3iOx5TnBqMA";

    [Theory]
    [InlineData("A")]
    [InlineData(DeepLink)]
    [InlineData("https://t.me/aVeryLongBotUsernameHere0123456789?start="
        + "7Xb2mQ9tZk4pL0aVc8NfR1jEwYs6HdGu3iOx5TnBqMA")]
    public void Every_block_is_divisible_by_its_generator_polynomial(string text)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(text);
        var version = QrCode.SmallestVersionFor(payload.Length);

        var interleaved = QrCode.BuildCodewords(payload, version);

        var blocks = QrCode.BlocksPerVersion(version);
        var ecc = QrCode.EccCodewordsPerBlock(version);
        var capacity = QrCode.DataCodewordsPerVersion(version);

        interleaved.Should().HaveCount(capacity + (blocks * ecc));

        foreach (var block in Deinterleave(interleaved, version))
        {
            // Syndromes: evaluate the codeword polynomial at α⁰ … α^(ecc-1). All zero means the
            // block is a multiple of the generator, which is the definition of a valid RS codeword.
            for (var i = 0; i < ecc; i++)
            {
                byte syndrome = 0;
                byte power = 1;

                for (var j = block.Length - 1; j >= 0; j--)
                {
                    syndrome ^= QrCode.Multiply(block[j], power);
                    power = QrCode.Multiply(power, AlphaToThe(i));
                }

                syndrome.Should().Be(0, $"block syndrome {i} must vanish at version {version}");
            }
        }
    }

    [Fact]
    public void The_finder_and_timing_patterns_are_where_a_scanner_looks_for_them()
    {
        var matrix = QrCode.Encode(DeepLink);

        matrix.Size.Should().Be((matrix.Version * 4) + 17);

        // Three finder patterns: a 7×7 ring with a 3×3 core, at three corners. The fourth corner is
        // deliberately bare — that is how a scanner works out the symbol's rotation.
        foreach (var (ox, oy) in new[] { (0, 0), (matrix.Size - 7, 0), (0, matrix.Size - 7) })
        {
            for (var y = 0; y < 7; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                    matrix[ox + x, oy + y].Should().Be(ring != 2, $"finder module ({x},{y}) at ({ox},{oy})");
                }
            }
        }

        // The timing patterns alternate along row 6 and column 6, between the finders. These are the
        // two modules the format-information reservation used to overwrite.
        for (var i = 8; i < matrix.Size - 8; i++)
        {
            matrix[i, 6].Should().Be(i % 2 == 0, $"horizontal timing module at x={i}");
            matrix[6, i].Should().Be(i % 2 == 0, $"vertical timing module at y={i}");
        }

        // The dark module, which is set in every symbol ever produced.
        matrix[8, matrix.Size - 8].Should().BeTrue();
    }

    [Fact]
    public void A_deep_link_fits_in_a_small_symbol()
    {
        var matrix = QrCode.Encode(DeepLink);

        // A phone reads this off a laptop screen at arm's length; a version much past this would be
        // drawn too small to resolve inside the card's width.
        matrix.Version.Should().BeLessThanOrEqualTo(6);
    }

    [Fact]
    public void The_svg_is_self_contained_and_carries_no_text_from_the_link()
    {
        var svg = QrCode.ToSvg(DeepLink, "کد QR برای باز کردن ربات تلگرام");

        svg.Should().StartWith("<svg").And.EndWith("</svg>");
        svg.Should().Contain("viewBox=");
        svg.Should().Contain("<path");

        // Literal black on white, never a theme colour: a QR code inverted by a dark theme does not
        // scan, and one drawn on a transparent background scans whatever is behind it.
        svg.Should().Contain("#000000").And.Contain("#ffffff");

        // The token travels in the modules and nowhere else. A URL sitting in the markup would be a
        // linking token in the page source, which is the thing the second leg exists to devalue.
        svg.Should().NotContain("t.me");
        svg.Should().NotContain(DeepLink[^43..]);
    }

    [Fact]
    public void Text_that_will_not_fit_is_refused_rather_than_truncated()
    {
        var tooLong = new string('x', QrCode.MaxBytes + 1);

        var encode = () => QrCode.Encode(tooLong);

        // A QR code that encodes half a URL scans perfectly and goes nowhere, which is worse than
        // no QR code at all.
        encode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_version_chosen_is_the_smallest_that_fits()
    {
        // Level L, byte mode: 17 bytes at version 1, 32 at version 2.
        QrCode.SmallestVersionFor(17).Should().Be(1);
        QrCode.SmallestVersionFor(18).Should().Be(2);
        QrCode.SmallestVersionFor(32).Should().Be(2);
        QrCode.SmallestVersionFor(33).Should().Be(3);
        QrCode.SmallestVersionFor(QrCode.MaxBytes).Should().Be(10);
    }

    /// <summary>α^i, built from repeated multiplication so it uses only the published operation.</summary>
    private static byte AlphaToThe(int power)
    {
        byte value = 1;
        for (var i = 0; i < power; i++) value = QrCode.Multiply(value, 2);
        return value;
    }

    /// <summary>Undoes the codeword interleaving, so each block can be checked on its own.</summary>
    private static List<byte[]> Deinterleave(byte[] interleaved, int version)
    {
        var blocks = QrCode.BlocksPerVersion(version);
        var ecc = QrCode.EccCodewordsPerBlock(version);
        var capacity = QrCode.DataCodewordsPerVersion(version);

        var shortLength = capacity / blocks;
        var longBlocks = capacity % blocks;

        var lengths = new int[blocks];
        for (var i = 0; i < blocks; i++)
        {
            lengths[i] = shortLength + (i >= blocks - longBlocks ? 1 : 0);
        }

        var data = new List<byte>[blocks];
        var parity = new List<byte>[blocks];
        for (var i = 0; i < blocks; i++)
        {
            data[i] = [];
            parity[i] = [];
        }

        var cursor = 0;
        for (var i = 0; i <= shortLength; i++)
        {
            for (var b = 0; b < blocks; b++)
            {
                if (i < lengths[b]) data[b].Add(interleaved[cursor++]);
            }
        }

        for (var i = 0; i < ecc; i++)
        {
            for (var b = 0; b < blocks; b++) parity[b].Add(interleaved[cursor++]);
        }

        cursor.Should().Be(interleaved.Length, "every codeword belongs to exactly one block");

        return [.. Enumerable.Range(0, blocks).Select(b => data[b].Concat(parity[b]).ToArray())];
    }
}
