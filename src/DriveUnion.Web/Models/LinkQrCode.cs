using System.Globalization;
using System.Net;
using System.Text;

namespace DriveUnion.Web.Models;

/// <summary>
/// A QR code for a share address, encoded in this process and drawn as SVG.
///
/// <para><b>Why this is here rather than a package or a service.</b> Every hosted QR generator —
/// <c>api.qrserver.com</c>, Google's old chart API, the dozen clones of them — is asked for a
/// picture by putting the payload in the query string. The payload here is the one secret a share
/// link has: whoever holds <c>/d/{slug}</c> holds the file. Handing every slug the panel draws to a
/// third party, in a URL that lands in their logs and in every proxy between here and them, would
/// give away the product's only access control to buy a 200×200 PNG. So the code is drawn where the
/// slug already is, and the slug goes nowhere.</para>
///
/// <para>Hand-written rather than taken from NuGet because there was nothing to take: the machine's
/// private feed is unreachable and no QR package is referenced anywhere in the solution, so adding
/// one would have been a new dependency that the build could not restore. What is here is the
/// smallest correct subset of ISO/IEC 18004 that a share address needs — byte mode, error
/// correction level M, versions 1 to 10 — and nothing else. A URL that does not fit in 213 bytes is
/// refused loudly rather than silently truncated into a code that scans to the wrong address.</para>
///
/// <para><b>SVG rather than PNG</b> so there is no image pipeline, no raster size to pick, and no
/// blur when somebody prints the page or scans it off a high-density screen. The document
/// references nothing: no <c>&lt;image&gt;</c>, no <c>href</c>, no font — a reader that is offline
/// draws the same code as one that is not.</para>
///
/// <para>Deterministic by construction. The same text always produces the same modules and the same
/// markup: mask selection is the standard penalty score, which has no ties broken by anything but
/// the mask number, and nothing in here reads a clock or a random source. A QR that changed shape
/// between two renders of the same page would be a code somebody photographed and could not
/// reproduce.</para>
/// </summary>
public static class LinkQrCode
{
    /// <summary>
    /// Four modules of white on every side, which the specification requires and scanners rely on.
    ///
    /// It is inside the <c>viewBox</c> rather than left to the page's own padding: this drawing gets
    /// copied out of the panel and pasted onto a poster, and a quiet zone that lived in a CSS rule
    /// would not travel with it.
    /// </summary>
    public const int QuietZone = 4;

    /// <summary>The XML namespace, which is a name and not an address — nothing fetches it.</summary>
    public const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Error correction level M: about 15% of the code may be damaged and still read.</summary>
    /// <remarks>
    /// L would make the code smaller and M is what almost every printed QR uses. The thing being
    /// encoded is an address with no redundancy in it — one wrong character is a 404, not a typo a
    /// reader can see through — and this code will be photographed off a screen at an angle by
    /// somebody's phone. The extra 10 modules a side are worth it.
    /// </remarks>
    private const int FormatEcBits = 0b00;

    /// <summary>Total codewords per version, data and error correction together. Index is the version.</summary>
    private static readonly int[] TotalCodewords =
        [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346];

    /// <summary>Error-correction codewords in each block, at level M.</summary>
    private static readonly int[] EcCodewordsPerBlock =
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];

    /// <summary>How many blocks the data is split into, at level M.</summary>
    private static readonly int[] BlockCount =
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    /// <summary>
    /// Where the alignment patterns' centres sit, per version.
    ///
    /// Version 1 has none; from 7 upwards there are three coordinates and so nine candidate centres,
    /// three of which fall on the finder patterns and are skipped.
    /// </summary>
    private static readonly int[][] AlignmentCentres =
    [
        [],
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
    ];

    private const int LargestVersion = 10;

    /// <summary>
    /// The code for <paramref name="text"/> as a complete SVG document.
    /// </summary>
    /// <param name="title">
    /// What a screen reader is told the picture is, or null for a picture that names itself
    /// somewhere else. <b>Never the address:</b> a title element is text in the markup, and the
    /// point of the exercise is that the slug is only ever in the modules.
    /// </param>
    public static string Svg(string text, string? title = null)
    {
        var modules = Modules(text);
        var size = modules.GetLength(0);
        var extent = size + (QuietZone * 2);

        var svg = new StringBuilder(1024);

        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"{SvgNamespace}\" viewBox=\"0 0 {extent} {extent}\"");

        // shape-rendering, because a module is one user unit and the default antialiasing draws a
        // grey seam between two black modules — which a scanner reads as a lighter run and, at small
        // sizes, as no module at all.
        svg.Append(" shape-rendering=\"crispEdges\"");

        if (title is null)
        {
            // Presentational: the caller has already put the name on the box around it, and two
            // labels on one picture is the picture announced twice.
            svg.Append(" aria-hidden=\"true\"");
        }
        else
        {
            svg.Append(" role=\"img\"");
        }

        svg.Append('>');

        if (title is not null)
        {
            svg.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        }

        // Explicit white rather than a transparent background. A QR is read as dark-on-light and
        // nothing else; on this panel's dark theme a transparent code would be black modules on a
        // near-black card, which no scanner will resolve. The two colours are written as attributes
        // and not as tokens for the same reason: a themed QR is an unreadable QR.
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{extent}\" height=\"{extent}\" fill=\"#ffffff\"/>");
        svg.Append("<path fill=\"#000000\" d=\"");

        // One path of horizontal runs rather than one rect per module: a version-3 code is 841
        // modules and about half of them are dark, so the difference is a few hundred bytes against
        // several thousand — on a table that draws one of these per row.
        for (var y = 0; y < size; y++)
        {
            var x = 0;

            while (x < size)
            {
                if (!modules[y, x])
                {
                    x++;
                    continue;
                }

                var run = 1;
                while (x + run < size && modules[y, x + run]) run++;

                svg.Append(CultureInfo.InvariantCulture, $"M{x + QuietZone} {y + QuietZone}h{run}v1h-{run}z");

                x += run;
            }
        }

        svg.Append("\"/></svg>");

        return svg.ToString();
    }

    /// <summary>
    /// The finished module matrix, indexed <c>[row, column]</c>, true where a module is dark.
    ///
    /// <para>Public because it is the seam the tests read the code back through: a decoder that
    /// walks this matrix, undoes the mask and the interleaving and recovers the bytes is the only
    /// way to assert that what was drawn says what it was asked to say. The SVG above is a picture
    /// of exactly this and nothing more.</para>
    /// </summary>
    public static bool[,] Modules(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // UTF-8 because that is what byte mode means in practice and what every scanner assumes. A
        // share address is ASCII, so this is the identity for everything this product encodes — but
        // encoding it as anything else would be a silent difference nobody would find until a
        // non-ASCII base URL appeared.
        var payload = Encoding.UTF8.GetBytes(text);
        var version = SmallestVersionFor(payload.Length);

        var codewords = WithErrorCorrection(BitStream(payload, version), version);

        return Draw(version, codewords);
    }

    private static int SmallestVersionFor(int byteCount)
    {
        for (var version = 1; version <= LargestVersion; version++)
        {
            if (byteCount <= ByteCapacity(version)) return version;
        }

        // Loud, not truncated. A code that fits by dropping the end of the address is a code that
        // scans to a working page for a different file, or to nothing at all — and it looks correct.
        throw new ArgumentOutOfRangeException(
            nameof(byteCount),
            byteCount,
            $"A QR code at error correction M holds {ByteCapacity(LargestVersion)} bytes at the "
            + "largest version this encoder draws. A share address longer than that is a base URL "
            + "that wants shortening rather than a bigger code.");
    }

    /// <summary>How many bytes fit, after the mode indicator and the character count.</summary>
    private static int ByteCapacity(int version) =>
        ((DataCodewords(version) * 8) - 4 - CharacterCountBits(version)) / 8;

    private static int DataCodewords(int version) =>
        TotalCodewords[version] - (EcCodewordsPerBlock[version] * BlockCount[version]);

    /// <summary>Eight bits up to version 9 and sixteen from version 10 — the specification's break.</summary>
    private static int CharacterCountBits(int version) => version < 10 ? 8 : 16;

    /// <summary>Mode indicator, length, payload, terminator and the alternating pad bytes.</summary>
    private static byte[] BitStream(byte[] payload, int version)
    {
        var capacityBits = DataCodewords(version) * 8;
        var bits = new BitBuffer(capacityBits);

        bits.Append(0b0100, 4);
        bits.Append(payload.Length, CharacterCountBits(version));

        foreach (var b in payload) bits.Append(b, 8);

        // Up to four zero bits saying the message has ended, then whatever it takes to finish the
        // byte, then 0xEC 0x11 for ever. The pad pair is the specification's and is not arbitrary:
        // it is two bytes that share no run of five, so padding cannot itself create the pattern the
        // mask penalty is looking for.
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);

        for (var pad = 0; bits.Length < capacityBits; pad++)
        {
            bits.Append(pad % 2 == 0 ? 0xEC : 0x11, 8);
        }

        return bits.ToBytes();
    }

    /// <summary>
    /// Splits the data into the version's blocks, gives each its own remainder, and interleaves
    /// them.
    ///
    /// The interleaving is the whole point of blocks: a coffee ring over one corner of a printed
    /// code takes a few codewords from every block rather than all of one, and each block's
    /// error correction can then cover its own share of the damage.
    /// </summary>
    private static byte[] WithErrorCorrection(byte[] data, int version)
    {
        var blocks = BlockCount[version];
        var ecLength = EcCodewordsPerBlock[version];

        // Short blocks first, then the ones carrying one extra codeword — the order the standard
        // fixes and the order a reader will put them back in.
        var shortLength = data.Length / blocks;
        var shortBlocks = blocks - (data.Length % blocks);

        var dataBlocks = new byte[blocks][];
        var ecBlocks = new byte[blocks][];

        var read = 0;

        for (var i = 0; i < blocks; i++)
        {
            var length = shortLength + (i < shortBlocks ? 0 : 1);

            dataBlocks[i] = data[read..(read + length)];
            ecBlocks[i] = Remainder(dataBlocks[i], ecLength);

            read += length;
        }

        var interleaved = new byte[TotalCodewords[version]];
        var write = 0;

        for (var i = 0; i <= shortLength; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length) interleaved[write++] = block[i];
            }
        }

        for (var i = 0; i < ecLength; i++)
        {
            foreach (var block in ecBlocks) interleaved[write++] = block[i];
        }

        return interleaved;
    }

    // ------------------------------------------------------------------ GF(256), the Reed–Solomon field

    /// <summary>
    /// Multiplication in GF(256) with the QR code's primitive polynomial, 0x11D.
    ///
    /// Written as the shift-and-reduce loop rather than as log/antilog tables because it is called a
    /// few thousand times per code and the tables would be two more arrays to get right — and
    /// because a log table has a hole at zero that every user of it has to remember.
    /// </summary>
    private static byte Multiply(byte x, byte y)
    {
        var product = 0;

        for (var bit = 7; bit >= 0; bit--)
        {
            product = (product << 1) ^ ((product >> 7) * 0x11D);
            product ^= ((y >> bit) & 1) * x;
        }

        return (byte)product;
    }

    /// <summary>The generator polynomial's coefficients, highest power first, leading 1 omitted.</summary>
    private static byte[] GeneratorPolynomial(int degree)
    {
        var coefficients = new byte[degree];
        coefficients[degree - 1] = 1;

        byte root = 1;

        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                coefficients[j] = Multiply(coefficients[j], root);

                if (j + 1 < degree) coefficients[j] ^= coefficients[j + 1];
            }

            root = Multiply(root, 2);
        }

        return coefficients;
    }

    /// <summary>The error-correction codewords: the data polynomial's remainder modulo the generator.</summary>
    private static byte[] Remainder(byte[] data, int ecLength)
    {
        var generator = GeneratorPolynomial(ecLength);
        var remainder = new byte[ecLength];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);

            Array.Copy(remainder, 1, remainder, 0, ecLength - 1);
            remainder[ecLength - 1] = 0;

            for (var i = 0; i < ecLength; i++)
            {
                remainder[i] ^= Multiply(generator[i], factor);
            }
        }

        return remainder;
    }

    // ------------------------------------------------------------------ the drawing

    private static bool[,] Draw(int version, byte[] codewords)
    {
        var size = (version * 4) + 17;
        var modules = new bool[size, size];

        // Which cells belong to the code's own furniture. Data is laid into everything else, and
        // the mask is applied to everything else — a mask that flipped a finder pattern would make
        // the code unfindable.
        var reserved = new bool[size, size];

        DrawFunctionPatterns(version, modules, reserved);
        DrawCodewords(codewords, modules, reserved);

        var mask = BestMask(modules, reserved);

        ApplyMask(mask, modules, reserved);
        DrawFormat(mask, modules);

        return modules;
    }

    private static void DrawFunctionPatterns(int version, bool[,] modules, bool[,] reserved)
    {
        var size = modules.GetLength(0);

        // The three corners a scanner looks for, each with its one-module white separator.
        DrawFinder(modules, reserved, 0, 0);
        DrawFinder(modules, reserved, size - 7, 0);
        DrawFinder(modules, reserved, 0, size - 7);

        // Timing: the alternating row and column that tell a reader how wide a module is.
        for (var i = 8; i < size - 8; i++)
        {
            Set(modules, reserved, i, 6, i % 2 == 0);
            Set(modules, reserved, 6, i, i % 2 == 0);
        }

        var centres = AlignmentCentres[version];

        foreach (var cx in centres)
        {
            foreach (var cy in centres)
            {
                // The three that would sit on a finder pattern. Written as a corner test rather than
                // as an index test so it stays true whatever the version's coordinate list is.
                var onFinder =
                    (cx == 6 && cy == 6)
                    || (cx == 6 && cy == size - 7)
                    || (cx == size - 7 && cy == 6);

                if (onFinder) continue;

                DrawAlignment(modules, reserved, cx, cy);
            }
        }

        // The one module that is always dark, and the fifteen either side of it that the format
        // information is written into once a mask has been chosen.
        Set(modules, reserved, 8, size - 8, true);

        for (var i = 0; i < 9; i++)
        {
            if (i != 6) Reserve(reserved, i, 8);
            if (i != 6) Reserve(reserved, 8, i);
        }

        Reserve(reserved, 8, 8);

        for (var i = 0; i < 8; i++)
        {
            Reserve(reserved, size - 1 - i, 8);
            Reserve(reserved, 8, size - 1 - i);
        }

        if (version >= 7) DrawVersion(version, modules, reserved);
    }

    private static void DrawFinder(bool[,] modules, bool[,] reserved, int left, int top)
    {
        var size = modules.GetLength(0);

        // Eight by eight, not seven: the extra row and column are the separator, and drawing them
        // here is what keeps the pattern from touching the data around it.
        for (var dy = -1; dy <= 7; dy++)
        {
            for (var dx = -1; dx <= 7; dx++)
            {
                var x = left + dx;
                var y = top + dy;

                if (x < 0 || y < 0 || x >= size || y >= size) continue;

                var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));

                Set(modules, reserved, x, y, ring != 2 && ring <= 3);
            }
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] reserved, int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                Set(modules, reserved, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    /// <summary>
    /// The version block, present from version 7 up: six bits of version and twelve of BCH, drawn
    /// twice so a reader that has one corner can still work out how big the code is.
    /// </summary>
    private static void DrawVersion(int version, bool[,] modules, bool[,] reserved)
    {
        var size = modules.GetLength(0);
        var remainder = version;

        for (var i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        var bits = (version << 12) | remainder;

        for (var i = 0; i < 18; i++)
        {
            var bit = ((bits >> i) & 1) == 1;
            var a = size - 11 + (i % 3);
            var b = i / 3;

            Set(modules, reserved, a, b, bit);
            Set(modules, reserved, b, a, bit);
        }
    }

    /// <summary>
    /// The zigzag: two-module columns walked from the bottom right leftwards, alternating direction,
    /// stepping over the vertical timing column.
    /// </summary>
    private static void DrawCodewords(byte[] codewords, bool[,] modules, bool[,] reserved)
    {
        var size = modules.GetLength(0);
        var bit = 0;
        var totalBits = codewords.Length * 8;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            // Column 6 is the timing pattern and is not part of any pair; the walk closes over it.
            if (right == 6) right = 5;

            for (var step = 0; step < size; step++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - step : step;

                    if (reserved[y, x]) continue;

                    // Past the end of the codewords are the remainder bits, which are zero and which
                    // exist only because some versions have a few cells left over.
                    modules[y, x] = bit < totalBits && ((codewords[bit / 8] >> (7 - (bit % 8))) & 1) == 1;

                    bit++;
                }
            }
        }
    }

    private static bool MaskAt(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => (x * y % 2) + (x * y % 3) == 0,
        6 => ((x * y % 2) + (x * y % 3)) % 2 == 0,
        _ => (((x + y) % 2) + (x * y % 3)) % 2 == 0,
    };

    private static void ApplyMask(int mask, bool[,] modules, bool[,] reserved)
    {
        var size = modules.GetLength(0);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (reserved[y, x]) continue;

                modules[y, x] ^= MaskAt(mask, x, y);
            }
        }
    }

    /// <summary>
    /// The mask with the lowest penalty, which is the specification's own answer and is what makes
    /// this encoder deterministic: eight candidates, one score each, lowest wins and ties go to the
    /// lower mask number because that is the one the loop reached first.
    /// </summary>
    private static int BestMask(bool[,] modules, bool[,] reserved)
    {
        var best = 0;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask, modules, reserved);
            DrawFormat(mask, modules);

            var penalty = Penalty(modules);

            // Exclusive-or is its own inverse, so the same call puts the matrix back. The format
            // bits are overwritten by the next candidate and by the real one at the end.
            ApplyMask(mask, modules, reserved);

            if (penalty >= bestPenalty) continue;

            bestPenalty = penalty;
            best = mask;
        }

        return best;
    }

    /// <summary>
    /// The four penalty rules, which between them push the chosen mask away from anything that looks
    /// like a finder pattern or like a photograph of a fence.
    /// </summary>
    private static int Penalty(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var penalty = 0;
        var dark = 0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (modules[y, x]) dark++;
            }
        }

        // N1 — runs of five or more of one colour, in both directions.
        for (var i = 0; i < size; i++)
        {
            penalty += RunPenalty(modules, i, horizontal: true);
            penalty += RunPenalty(modules, i, horizontal: false);
        }

        // N2 — every two-by-two block of one colour.
        for (var y = 0; y < size - 1; y++)
        {
            for (var x = 0; x < size - 1; x++)
            {
                var colour = modules[y, x];

                if (modules[y, x + 1] == colour && modules[y + 1, x] == colour && modules[y + 1, x + 1] == colour)
                {
                    penalty += 3;
                }
            }
        }

        // N3 — the 1:1:3:1:1 finder-like run with four white either side of it.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                penalty += 40 * FinderLookalikes(modules, x, y);
            }
        }

        // N4 — how far the proportion of dark modules is from half, in steps of five per cent.
        var total = size * size;
        var deviation = Math.Abs((dark * 20) - (total * 10)) / total;

        penalty += 10 * deviation;

        return penalty;
    }

    private static int RunPenalty(bool[,] modules, int line, bool horizontal)
    {
        var size = modules.GetLength(0);
        var penalty = 0;
        var run = 1;

        for (var i = 1; i < size; i++)
        {
            var current = horizontal ? modules[line, i] : modules[i, line];
            var previous = horizontal ? modules[line, i - 1] : modules[i - 1, line];

            if (current == previous)
            {
                run++;

                if (run == 5) penalty += 3;
                else if (run > 5) penalty++;
            }
            else
            {
                run = 1;
            }
        }

        return penalty;
    }

    /// <summary>How many of the two finder-lookalike patterns start at this cell, across and down.</summary>
    private static int FinderLookalikes(bool[,] modules, int x, int y)
    {
        // 1011101 with four light modules on one side or the other, which is what a scanner uses to
        // find the real corners — so it must not appear anywhere else.
        ReadOnlySpan<bool> pattern =
            [true, false, true, true, true, false, true];

        var found = 0;

        if (Matches(modules, x, y, pattern, horizontal: true)) found++;
        if (Matches(modules, x, y, pattern, horizontal: false)) found++;

        return found;
    }

    private static bool Matches(bool[,] modules, int x, int y, ReadOnlySpan<bool> pattern, bool horizontal)
    {
        var size = modules.GetLength(0);
        var length = horizontal ? x : y;
        var start = horizontal ? x : y;

        if (start + pattern.Length > size) return false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var value = horizontal ? modules[y, x + i] : modules[y + i, x];

            if (value != pattern[i]) return false;
        }

        // Four light modules before or after. "Before" runs off the edge of the code into the quiet
        // zone, which counts as light — that is why a pattern hard against the border still scores.
        return Light(modules, x, y, start - 4, start - 1, horizontal)
            || Light(modules, x, y, start + pattern.Length, start + pattern.Length + 3, horizontal);

        static bool Light(bool[,] modules, int x, int y, int from, int to, bool horizontal)
        {
            var size = modules.GetLength(0);

            for (var i = from; i <= to; i++)
            {
                if (i < 0 || i >= size) continue;

                var value = horizontal ? modules[y, i] : modules[i, x];

                if (value) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The fifteen format bits — two of error correction level, three of mask, ten of BCH — written
    /// twice, once around the top-left corner and once split between the other two.
    /// </summary>
    private static void DrawFormat(int mask, bool[,] modules)
    {
        var size = modules.GetLength(0);
        var data = (FormatEcBits << 3) | mask;
        var remainder = data;

        for (var i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        // The final mask is the specification's, and it is what stops a format of all zeroes — which
        // is a legal one — from being a blank corner a reader cannot lock on to.
        var bits = ((data << 10) | remainder) ^ 0x5412;

        for (var i = 0; i <= 5; i++) modules[8, i] = Bit(bits, i);

        modules[8, 7] = Bit(bits, 6);
        modules[8, 8] = Bit(bits, 7);
        modules[7, 8] = Bit(bits, 8);

        for (var i = 9; i < 15; i++) modules[14 - i, 8] = Bit(bits, i);

        for (var i = 0; i < 8; i++) modules[size - 1 - i, 8] = Bit(bits, i);

        for (var i = 8; i < 15; i++) modules[8, size - 15 + i] = Bit(bits, i);

        static bool Bit(int value, int index) => ((value >> index) & 1) == 1;
    }

    private static void Set(bool[,] modules, bool[,] reserved, int x, int y, bool dark)
    {
        modules[y, x] = dark;
        reserved[y, x] = true;
    }

    private static void Reserve(bool[,] reserved, int x, int y) => reserved[y, x] = true;

    /// <summary>Bits in, most significant first — the order every field of a QR message is written in.</summary>
    private sealed class BitBuffer(int capacityBits)
    {
        private readonly List<bool> bits = new(capacityBits);

        public int Length => bits.Count;

        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) == 1);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[bits.Count / 8];

            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i]) bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }

            return bytes;
        }
    }
}
