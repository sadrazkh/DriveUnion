using System.Buffers.Text;

namespace DriveUnion.Infrastructure.Push;

/// <summary>
/// base64url, read leniently and written strictly.
///
/// <para><b>Why leniently on the way in.</b> Everything decoded here was produced by somebody else's
/// JavaScript. <c>PushSubscription.toJSON()</c> gives unpadded base64url; hand-written code that
/// does <c>btoa(String.fromCharCode(...new Uint8Array(key)))</c> gives padded standard base64 with
/// <c>+</c> and <c>/</c> in it; a great deal of published sample code does the second. Both name the
/// same 65 bytes, and refusing one of them would be a subscribe button that works in some browsers
/// and reports nothing in others.</para>
///
/// <para><b>Why strictly on the way out.</b> What this writes goes into a JWT and into an
/// <c>Authorization</c> header, where padding is not merely untidy — RFC 7515 says the encoding
/// carries no <c>=</c>, and a push service comparing a signature over a padded segment computes a
/// different hash and answers 403 with no explanation.</para>
/// </summary>
internal static class Base64UrlText
{
    /// <summary>Unpadded base64url, which is the only form anything here emits.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>
    /// The bytes, or null when the text is not a base64 encoding of anything at all.
    ///
    /// <para>Null rather than an exception: every caller is reading a value that arrived over the
    /// wire, and «this is not a key» is an answer they have to give a 400 for rather than a state
    /// worth unwinding the stack over.</para>
    /// </summary>
    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // The two alphabets differ in exactly two characters, so one substitution makes standard
        // base64 readable by the base64url decoder. Padding is dropped rather than corrected:
        // Base64Url refuses '=' outright, and the padding carries no information — the length of the
        // remaining characters already says how many bytes the last group holds.
        var normalised = value.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('=');

        try
        {
            var buffer = new byte[Base64Url.GetMaxDecodedLength(normalised.Length)];

            return Base64Url.TryDecodeFromChars(normalised, buffer, out var written)
                ? buffer[..written]
                : null;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // Try-and-catch rather than try-and-return-false, because the framework's Try* does both:
            // it answers false for a destination that is too small and throws for text that is not
            // base64 at all. The values reaching here are an operator's pasted key and a browser's
            // POST body, so «not base64 at all» is the ordinary case rather than the exotic one — and
            // it would otherwise be an unhandled FormatException on the notifications screen and
            // inside the push worker.
            return null;
        }
    }
}
