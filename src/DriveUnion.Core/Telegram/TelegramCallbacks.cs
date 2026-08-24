using System.Buffers.Text;

namespace DriveUnion.Core.Telegram;

/// <summary>What a button press asks for.</summary>
public enum TelegramCallbackVerb
{
    /// <summary>Send this file into the chat.</summary>
    SendFile,

    /// <summary>Mint a share link for this file.</summary>
    CreateLink,

    /// <summary>Show the file card again.</summary>
    ShowFile,

    /// <summary>The file list.</summary>
    ListFiles,

    /// <summary>«دریافت کردم، پاک کن» on a delivered document.</summary>
    AcknowledgeDelivery,

    /// <summary>Confirm <c>/unlink</c>.</summary>
    ConfirmUnlink,

    /// <summary>Back out of a confirmation.</summary>
    Cancel,
}

/// <summary>
/// One button press, decoded.
/// </summary>
public sealed record TelegramCallback(TelegramCallbackVerb Verb, Guid? Id, long? Number);

/// <summary>
/// <c>callback_data</c> is documented as 1–64 bytes, which does not fit a GUID as text — 36
/// characters each, and a callback sometimes needs two values.
///
/// <para>So a GUID travels as the 22-character base64url of its 16 bytes and the verb is one
/// character: <c>s.{22}</c> is 24 bytes for "send file X", leaving room for a second value. That is
/// arithmetic rather than taste — a button whose data is 65 bytes is refused by Telegram at send time,
/// so the card would simply fail to render, in production, on the longest file name.</para>
///
/// <para>And, always: <b>this data is never an authorization.</b> It arrives from a client we do not
/// control, so a crafted callback naming another tenant's file must produce exactly what a random GUID
/// produces. The ids below are re-resolved through a tenant-scoped repository by every handler; this
/// class only says what the bytes mean.</para>
/// </summary>
public static class TelegramCallbackData
{
    /// <summary>Telegram's own limit, and the reason for the encoding.</summary>
    public const int MaxBytes = 64;

    private const int GuidChars = 22;

    public static string Encode(TelegramCallbackVerb verb, Guid? id = null, long? number = null)
    {
        var tag = Tag(verb);

        if (id is not { } value)
        {
            return number is { } bare
                ? $"{tag}..{bare.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : tag;
        }

        var encoded = Base64Url.EncodeToString(value.ToByteArray());

        return number is { } extra
            ? $"{tag}.{encoded}.{extra.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{tag}.{encoded}";
    }

    /// <summary>Null for anything this bot did not mint. A stale button is not an error, it is nothing.</summary>
    public static TelegramCallback? Decode(string? data)
    {
        if (data is not { Length: > 0 } || data.Length > MaxBytes) return null;

        var parts = data.Split('.');
        if (parts.Length is < 1 or > 3) return null;

        if (Verb(parts[0]) is not { } verb) return null;

        Guid? id = null;
        if (parts.Length >= 2 && parts[1].Length > 0)
        {
            if (parts[1].Length != GuidChars) return null;

            Span<byte> bytes = stackalloc byte[16];
            if (Base64Url.DecodeFromChars(parts[1], bytes, out _, out var written) != System.Buffers.OperationStatus.Done
                || written != 16)
            {
                return null;
            }

            id = new Guid(bytes);
        }

        long? number = null;
        if (parts.Length == 3)
        {
            if (!long.TryParse(
                    parts[2],
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return null;
            }

            number = parsed;
        }

        return new TelegramCallback(verb, id, number);
    }

    private static string Tag(TelegramCallbackVerb verb) => verb switch
    {
        TelegramCallbackVerb.SendFile => "s",
        TelegramCallbackVerb.CreateLink => "l",
        TelegramCallbackVerb.ShowFile => "f",
        TelegramCallbackVerb.ListFiles => "n",
        TelegramCallbackVerb.AcknowledgeDelivery => "a",
        TelegramCallbackVerb.ConfirmUnlink => "u",
        TelegramCallbackVerb.Cancel => "x",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static TelegramCallbackVerb? Verb(string tag) => tag switch
    {
        "s" => TelegramCallbackVerb.SendFile,
        "l" => TelegramCallbackVerb.CreateLink,
        "f" => TelegramCallbackVerb.ShowFile,
        "n" => TelegramCallbackVerb.ListFiles,
        "a" => TelegramCallbackVerb.AcknowledgeDelivery,
        "u" => TelegramCallbackVerb.ConfirmUnlink,
        "x" => TelegramCallbackVerb.Cancel,
        _ => null,
    };
}
