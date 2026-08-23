using System.Globalization;
using System.Text;

namespace DriveUnion.Core.Telegram;

/// <summary>
/// A QR code, as a square of modules. <c>this[x, y]</c> is true where the module is dark.
/// </summary>
public sealed class QrMatrix
{
    private readonly bool[] _modules;

    internal QrMatrix(int version, bool[] modules)
    {
        Version = version;
        Size = QrCode.SizeOfVersion(version);
        _modules = modules;
    }

    /// <summary>1 to 10. Larger versions are not built here — see <see cref="QrCode"/>.</summary>
    public int Version { get; }

    /// <summary>Modules per side, excluding the quiet zone.</summary>
    public int Size { get; }

    public bool this[int x, int y] => _modules[(y * Size) + x];
}

/// <summary>
/// A QR encoder, because the panel needs one and cannot have a dependency for it.
///
/// <para>The customer's linking card draws a QR code so a desktop panel and a phone's Telegram can
/// meet: nobody types a 43-character token. There is no package to do it — this solution takes none
/// beyond EF, Identity and Data Protection — and there is no CDN to fetch one from either, since the
/// panel is forbidden a foreign origin even for fonts. So it is here, in Core, where it is pure and
/// testable and reaches nothing.</para>
///
/// <para><b>Byte mode, error-correction level L, versions 1 to 10.</b> That is the smallest thing
/// that does the job: a Telegram deep link is at most about 95 characters
/// (<c>https://t.me/</c> + a 32-character @username + <c>?start=</c> + 43), and level L at version 10
/// carries 271 bytes. Level L rather than M because this code is read from a screen a hand's width
/// away, where the damage the extra parity buys does not happen. <see cref="Encode"/> throws rather
/// than truncating for anything longer, because a QR code that encodes half a URL scans perfectly and
/// goes nowhere.</para>
/// </summary>
public static class QrCode
{
    /// <summary>The longest string level L carries at version 10.</summary>
    public const int MaxBytes = 271;

    private const int MinVersion = 1;
    private const int MaxVersion = 10;

    /// <summary>Error-correction level L, as the two bits that go into the format information.</summary>
    private const int EccFormatBits = 1;

    /// <summary>Data codewords available per version at level L, indexed by version.</summary>
    private static readonly int[] DataCodewords =
        [0, 19, 34, 55, 80, 108, 136, 156, 194, 232, 274];

    /// <summary>Error-correction codewords in each block, at level L, indexed by version.</summary>
    private static readonly int[] EccPerBlock =
        [0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18];

    /// <summary>Blocks the data is split into, at level L, indexed by version.</summary>
    private static readonly int[] BlockCount =
        [0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4];

    /// <summary>Alignment-pattern centre coordinates, indexed by version.</summary>
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

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        // GF(256) with the QR specification's primitive polynomial x⁸+x⁴+x³+x²+1.
        var value = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)value;
            Log[value] = (byte)i;

            value <<= 1;
            if ((value & 0x100) != 0) value ^= 0x11D;
        }

        for (var i = 255; i < Exp.Length; i++) Exp[i] = Exp[i - 255];
    }

    public static int SizeOfVersion(int version) => (version * 4) + 17;

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8 in byte mode.
    ///
    /// The mask is chosen by the specification's four penalty rules rather than fixed, because a
    /// fixed mask on a URL of a particular length can produce large blank runs that make a code hard
    /// to read at an angle — and the choice costs eight cheap passes over a matrix of at most 57
    /// modules a side.
    /// </summary>
    /// <exception cref="ArgumentException">The text does not fit at version 10.</exception>
    public static QrMatrix Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > MaxBytes)
        {
            throw new ArgumentException(
                $"A QR code at level L carries at most {MaxBytes} bytes; this is {payload.Length}.",
                nameof(text));
        }

        var version = SmallestVersionFor(payload.Length);
        var codewords = BuildCodewords(payload, version);

        var size = SizeOfVersion(version);
        var best = Draw(version, codewords, 0);
        var bestPenalty = Penalty(best, size);

        for (var mask = 1; mask < 8; mask++)
        {
            var candidate = Draw(version, codewords, mask);
            var penalty = Penalty(candidate, size);

            if (penalty >= bestPenalty) continue;

            best = candidate;
            bestPenalty = penalty;
        }

        return new QrMatrix(version, best);
    }

    /// <summary>
    /// The code as a standalone SVG, sized in the module grid rather than in pixels so a stylesheet
    /// decides how big it is drawn.
    ///
    /// One <c>&lt;path&gt;</c> of dark modules over one background rectangle: several hundred
    /// <c>&lt;rect&gt;</c> elements render identically and cost several times the bytes. The colours
    /// are literal black and white on purpose — a QR code inverted by a dark theme does not scan.
    /// </summary>
    public static string ToSvg(string text, string? title = null)
    {
        var matrix = Encode(text);

        // The specification requires four clear modules on every side. Without them a scanner cannot
        // find the edge of the symbol against whatever the page puts next to it.
        const int Quiet = 4;
        var extent = matrix.Size + (Quiet * 2);

        // Horizontal runs rather than one subpath per module: a version-5 code has some fifteen
        // hundred dark modules and the merged form is a third of the bytes for identical output.
        var path = new StringBuilder();
        for (var y = 0; y < matrix.Size; y++)
        {
            var x = 0;
            while (x < matrix.Size)
            {
                if (!matrix[x, y])
                {
                    x++;
                    continue;
                }

                var run = 0;
                while (x + run < matrix.Size && matrix[x + run, y]) run++;

                path.Append(CultureInfo.InvariantCulture,
                    $"M{x + Quiet} {y + Quiet}h{run}v1h-{run}z");

                x += run;
            }
        }

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {extent} {extent}\" ");
        svg.Append("shape-rendering=\"crispEdges\" role=\"img\"");

        if (!string.IsNullOrEmpty(title))
        {
            svg.Append(CultureInfo.InvariantCulture, $" aria-label=\"{Escape(title)}\"");
        }
        else
        {
            svg.Append(" aria-hidden=\"true\"");
        }

        svg.Append('>');
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{extent}\" height=\"{extent}\" fill=\"#ffffff\"/>");
        svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"#000000\" d=\"{path}\"/>");
        svg.Append("</svg>");

        return svg.ToString();
    }

    /// <summary>
    /// The full codeword sequence — data and error correction, interleaved — that is laid into the
    /// matrix.
    ///
    /// Public only so the tests can check the Reed-Solomon remainder against the specification's own
    /// algebra: a codeword block must be divisible by its generator polynomial, which is a check
    /// this file's arithmetic cannot fake. Nothing in the product calls it.
    /// </summary>
    public static byte[] BuildCodewords(ReadOnlySpan<byte> payload, int version)
    {
        var capacity = DataCodewords[version];
        var data = BuildDataCodewords(payload, version, capacity);

        var blocks = BlockCount[version];
        var eccLength = EccPerBlock[version];

        // The short blocks come first and the long ones — one codeword longer — last. Only the
        // shortest layout has all blocks equal, and treating them as equal is how a version-10 code
        // silently becomes unreadable.
        var shortLength = capacity / blocks;
        var longBlocks = capacity % blocks;

        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];

        var offset = 0;
        for (var i = 0; i < blocks; i++)
        {
            var length = shortLength + (i >= blocks - longBlocks ? 1 : 0);
            dataBlocks[i] = data[offset..(offset + length)];
            eccBlocks[i] = ErrorCorrection(dataBlocks[i], eccLength);
            offset += length;
        }

        var result = new List<byte>(capacity + (blocks * eccLength));

        for (var i = 0; i <= shortLength; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length) result.Add(block[i]);
            }
        }

        for (var i = 0; i < eccLength; i++)
        {
            foreach (var block in eccBlocks) result.Add(block[i]);
        }

        return [.. result];
    }

    /// <summary>The smallest version at level L that carries this many bytes in byte mode.</summary>
    public static int SmallestVersionFor(int payloadLength)
    {
        for (var version = MinVersion; version <= MaxVersion; version++)
        {
            // Four bits of mode indicator plus the character count, which widens at version 10.
            var header = 4 + (version < 10 ? 8 : 16);
            if ((DataCodewords[version] * 8) - header >= payloadLength * 8) return version;
        }

        throw new ArgumentOutOfRangeException(
            nameof(payloadLength),
            payloadLength,
            "No supported QR version carries this many bytes.");
    }

    private static byte[] BuildDataCodewords(ReadOnlySpan<byte> payload, int version, int capacity)
    {
        var bits = new BitBuffer(capacity * 8);

        bits.Append(0b0100, 4);
        bits.Append(payload.Length, version < 10 ? 8 : 16);

        foreach (var b in payload) bits.Append(b, 8);

        // Terminator: up to four zero bits, then zeros to the next byte boundary.
        bits.Append(0, Math.Min(4, (capacity * 8) - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);

        // The specification's pad codewords, alternating, for whatever the message did not fill.
        var pad = true;
        while (bits.Length < capacity * 8)
        {
            bits.Append(pad ? 0xEC : 0x11, 8);
            pad = !pad;
        }

        return bits.ToArray();
    }

    private static byte[] ErrorCorrection(ReadOnlySpan<byte> data, int length)
    {
        var generator = GeneratorPolynomial(length);
        var remainder = new byte[length];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);

            Array.Copy(remainder, 1, remainder, 0, length - 1);
            remainder[length - 1] = 0;

            for (var i = 0; i < length; i++)
            {
                remainder[i] ^= Multiply(generator[i + 1], factor);
            }
        }

        return remainder;
    }

    private static byte[] GeneratorPolynomial(int degree)
    {
        var poly = new byte[] { 1 };

        for (var i = 0; i < degree; i++)
        {
            var next = new byte[poly.Length + 1];

            for (var j = 0; j < poly.Length; j++)
            {
                next[j] ^= poly[j];
                next[j + 1] ^= Multiply(poly[j], Exp[i]);
            }

            poly = next;
        }

        return poly;
    }

    /// <summary>Multiplication in GF(256), which the tests use to verify a block's remainder.</summary>
    public static byte Multiply(byte a, byte b) =>
        a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>How many codewords of error correction each block of a version carries at level L.</summary>
    public static int EccCodewordsPerBlock(int version) => EccPerBlock[version];

    /// <summary>How many blocks a version's data is split into at level L.</summary>
    public static int BlocksPerVersion(int version) => BlockCount[version];

    /// <summary>How many data codewords a version carries at level L.</summary>
    public static int DataCodewordsPerVersion(int version) => DataCodewords[version];

    private static bool[] Draw(int version, ReadOnlySpan<byte> codewords, int mask)
    {
        var size = SizeOfVersion(version);
        var modules = new bool[size * size];
        var reserved = new bool[size * size];

        DrawFunctionPatterns(version, size, modules, reserved);
        DrawFormatBits(size, modules, reserved, mask);
        DrawData(size, modules, reserved, codewords, mask);

        return modules;
    }

    private static void DrawFunctionPatterns(int version, int size, bool[] modules, bool[] reserved)
    {
        void Set(int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;

            modules[(y * size) + x] = dark;
            reserved[(y * size) + x] = true;
        }

        void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(cx + dx, cy + dy, ring != 2 && ring != 4);
                }
            }
        }

        Finder(3, 3);
        Finder(size - 4, 3);
        Finder(3, size - 4);

        for (var i = 8; i < size - 8; i++)
        {
            Set(i, 6, i % 2 == 0);
            Set(6, i, i % 2 == 0);
        }

        var centres = AlignmentCentres[version];
        for (var i = 0; i < centres.Length; i++)
        {
            for (var j = 0; j < centres.Length; j++)
            {
                // The three corners are already finder patterns.
                var corner = (i == 0 && j == 0)
                    || (i == 0 && j == centres.Length - 1)
                    || (i == centres.Length - 1 && j == 0);

                if (corner) continue;

                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        Set(centres[j] + dx, centres[i] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        // The dark module, which is always set and is the one module the format bits do not cover.
        Set(8, size - 8, true);

        // The format information's own two copies, reserved now and written once a mask is chosen.
        // Index 6 is skipped in both directions: those two modules belong to the timing patterns
        // drawn above, and reserving them here would overwrite the alternating run a scanner uses to
        // find the module grid at all.
        for (var i = 0; i <= 8; i++)
        {
            if (i == 6) continue;

            Set(8, i, false);
            Set(i, 8, false);
        }

        for (var i = 0; i < 8; i++)
        {
            Set(size - 1 - i, 8, false);
            Set(8, size - 1 - i, false);
        }

        if (version < 7) return;

        var remainder = version;
        for (var i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        var versionBits = (version << 12) | remainder;

        for (var i = 0; i < 18; i++)
        {
            var bit = ((versionBits >> i) & 1) != 0;
            var far = size - 11 + (i % 3);
            var near = i / 3;

            Set(far, near, bit);
            Set(near, far, bit);
        }
    }

    private static void DrawFormatBits(int size, bool[] modules, bool[] reserved, int mask)
    {
        var data = (EccFormatBits << 3) | mask;

        var remainder = data;
        for (var i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        // The specification's fixed mask, so an all-zero format never reads as a blank region.
        var bits = ((data << 10) | remainder) ^ 0x5412;

        void Set(int x, int y, bool dark)
        {
            modules[(y * size) + x] = dark;
            reserved[(y * size) + x] = true;
        }

        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));

        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));

        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));

        Set(8, size - 8, true);
    }

    private static void DrawData(
        int size,
        bool[] modules,
        bool[] reserved,
        ReadOnlySpan<byte> codewords,
        int mask)
    {
        var bit = 0;
        var total = codewords.Length * 8;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            // Column 6 is the vertical timing pattern; the two-wide strips step over it.
            if (right == 6) right = 5;

            for (var vertical = 0; vertical < size; vertical++)
            {
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vertical : vertical;

                    if (reserved[(y * size) + x]) continue;

                    var dark = bit < total
                        && ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;

                    // Past the last codeword the remainder bits stay light, and the mask still
                    // applies to them — they are data modules, just empty ones.
                    if (Masked(mask, x, y)) dark = !dark;

                    modules[(y * size) + x] = dark;

                    if (bit < total) bit++;
                }
            }
        }
    }

    private static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => (x * y % 2) + (x * y % 3) == 0,
        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
    };

    /// <summary>
    /// The specification's four penalty rules: long runs, 2×2 blocks, finder-like sequences, and an
    /// unbalanced ratio of dark to light. Lower is better; the number itself means nothing outside
    /// the comparison between the eight masks.
    /// </summary>
    private static int Penalty(bool[] modules, int size)
    {
        bool At(int x, int y) => modules[(y * size) + x];

        var score = 0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x + 1 < size && y + 1 < size
                    && At(x, y) == At(x + 1, y)
                    && At(x, y) == At(x, y + 1)
                    && At(x, y) == At(x + 1, y + 1))
                {
                    score += 3;
                }
            }
        }

        score += RunPenalty(size, At, horizontal: true);
        score += RunPenalty(size, At, horizontal: false);

        var dark = modules.Count(m => m);
        var percent = dark * 100 / modules.Length;
        score += Math.Abs(percent - 50) / 5 * 10;

        return score;
    }

    private static int RunPenalty(int size, Func<int, int, bool> at, bool horizontal)
    {
        var score = 0;

        for (var line = 0; line < size; line++)
        {
            var run = 0;
            var runColour = false;

            // Eleven modules of history, as a rolling bit pattern, so the finder-lookalike test is
            // one comparison rather than a search.
            var history = 0;

            for (var i = 0; i < size; i++)
            {
                var dark = horizontal ? at(i, line) : at(line, i);

                if (i > 0 && dark == runColour)
                {
                    run++;
                    if (run == 5) score += 3;
                    else if (run > 5) score++;
                }
                else
                {
                    run = 1;
                    runColour = dark;
                }

                history = ((history << 1) | (dark ? 1 : 0)) & 0x7FF;

                if (i < 10) continue;

                // 1011101 0000 and its mirror: the sequence a finder pattern makes, which must not
                // appear anywhere else or a scanner will look for the symbol in the wrong place.
                if (history is 0b10111010000 or 0b00001011101) score += 40;
            }
        }

        return score;
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed class BitBuffer(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity / 8);
        private int _pending;
        private int _pendingBits;

        public int Length { get; private set; }

        public void Append(int value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                _pending = (_pending << 1) | ((value >> i) & 1);
                _pendingBits++;
                Length++;

                if (_pendingBits != 8) continue;

                _bytes.Add((byte)_pending);
                _pending = 0;
                _pendingBits = 0;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
