using System.Globalization;
using System.Text.Json;
using DriveUnion.Core.Telegram;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The Bot API's JSON, read by hand.
///
/// <para><b>Why by hand rather than with a client library.</b> The solution's project files are not
/// this slice's to edit, so a package reference is not available to it — but the choice would be the
/// same with one. What this product asks of Telegram is fifteen calls with small, stable shapes; a
/// library would bring several hundred generated types, its own <c>HttpClient</c> lifetime and its own
/// exception hierarchy, and the seam every test in the suite runs against is
/// <c>ITelegramBotGateway</c>, not the library's client. The library's genuine value — keeping up with
/// a changing API — buys little for calls that have not changed since Bot API 7.0.</para>
///
/// <para>Everything here is deliberately tolerant of fields it does not know and intolerant of ones it
/// needs: an update whose <c>update_id</c> is missing is not an update, and treating it as one would
/// put a null through the dedup table.</para>
/// </summary>
internal static class TelegramWire
{
    /// <summary>
    /// The four update kinds this bot asks for. Everything else Telegram would send is bandwidth and
    /// a handler that does not exist, and an explicit list means a future API addition cannot start
    /// arriving unannounced.
    /// </summary>
    public static readonly string[] AllowedUpdates = ["message", "edited_message", "callback_query"];

    public static bool ReadOk(JsonElement root) =>
        root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Telegram's error shape: a code, a description, and — on flood control — a
    /// <c>parameters.retry_after</c> in seconds, which is obeyed rather than argued with.
    /// </summary>
    public static TelegramFailure ReadFailure(JsonElement root, int httpStatus)
    {
        var code = root.TryGetProperty("error_code", out var errorCode)
                   && errorCode.TryGetInt32(out var parsed)
            ? parsed
            : httpStatus;

        var description = root.TryGetProperty("description", out var text)
                          && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "Telegram refused the call without saying why."
            : $"Telegram answered HTTP {httpStatus.ToString(CultureInfo.InvariantCulture)}.";

        TimeSpan? retryAfter = null;
        if (root.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("retry_after", out var seconds)
            && seconds.TryGetInt32(out var wait)
            && wait >= 0)
        {
            retryAfter = TimeSpan.FromSeconds(wait);
        }

        return new TelegramFailure(code, description, retryAfter);
    }

    public static TelegramUpdate? ReadUpdate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("update_id", out var id) || !id.TryGetInt64(out var updateId)) return null;

        // An edited message is handled as the message it became: a customer who corrects a typo in a
        // command is asking for the corrected command, and treating the edit as nothing is a chat
        // that stops answering for no visible reason.
        var message = ReadMessage(Property(element, "message") ?? Property(element, "edited_message"));
        var callback = ReadCallbackQuery(Property(element, "callback_query"));

        return new TelegramUpdate(updateId, message, callback);
    }

    public static TelegramSentMessage? ReadSentMessage(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (!result.TryGetProperty("message_id", out var id) || !id.TryGetInt64(out var messageId)) return null;

        var chatId = Property(result, "chat") is { } chat
                     && chat.TryGetProperty("id", out var raw)
                     && raw.TryGetInt64(out var value)
            ? value
            : 0;

        // The file handle Telegram minted, which is the whole point of caching: the response to the
        // one expensive send carries the argument that makes every later one free.
        var document = Property(result, "document")
                       ?? Property(result, "video")
                       ?? Property(result, "audio");

        return new TelegramSentMessage(
            chatId,
            messageId,
            document is { } file ? String(file, "file_id") : null,
            document is { } unique ? String(unique, "file_unique_id") : null);
    }

    public static TelegramBotProfile? ReadProfile(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        if (!result.TryGetProperty("id", out var id) || !id.TryGetInt64(out var botUserId)) return null;

        return new TelegramBotProfile(botUserId, String(result, "username") ?? string.Empty);
    }

    public static TelegramWebhookInfo ReadWebhookInfo(JsonElement result) => new(
        String(result, "url"),
        Int(result, "pending_update_count") ?? 0,
        String(result, "last_error_message"),
        UnixSeconds(result, "last_error_date"),
        String(result, "ip_address"),
        Int(result, "max_connections"));

    /// <summary>
    /// The <c>getFile</c> answer, which is not the same kind of thing on the two servers.
    ///
    /// Against our own server <c>file_path</c> is an absolute path on this box; against the cloud API
    /// it is a relative path that becomes a URL carrying the bot token. The caller has to know which,
    /// so the shape is decided by configuration rather than guessed from the string — a path that
    /// happens to look relative is not evidence about which server answered.
    /// </summary>
    public static TelegramFileHandle? ReadFile(JsonElement result, bool localServer, Uri apiBase, string token)
    {
        if (String(result, "file_path") is not { Length: > 0 } path) return null;

        var size = result.TryGetProperty("file_size", out var bytes) && bytes.TryGetInt64(out var value)
            ? value
            : (long?)null;

        if (localServer) return new TelegramFileHandle(new TelegramFileLocation.OnDisk(path), size);

        // One absolute string rather than a relative combine: a bot token carries a colon, and the
        // Uri constructor reads a colon before the first slash as a scheme.
        return new TelegramFileHandle(
            new TelegramFileLocation.AtUrl(new Uri(
                $"{apiBase.ToString().TrimEnd('/')}/file/bot{token}/{path}",
                UriKind.Absolute)),
            size);
    }

    private static TelegramIncomingMessage? ReadMessage(JsonElement? element)
    {
        if (element is not { } message) return null;
        if (!message.TryGetProperty("message_id", out var id) || !id.TryGetInt64(out var messageId)) return null;

        if (Property(message, "chat") is not { } chatElement) return null;
        if (!chatElement.TryGetProperty("id", out var chatIdRaw) || !chatIdRaw.TryGetInt64(out var chatId))
        {
            return null;
        }

        var chat = new TelegramChat(chatId, String(chatElement, "type") ?? string.Empty);

        return new TelegramIncomingMessage(
            messageId,
            chat,
            ReadSender(Property(message, "from")),
            String(message, "text") ?? String(message, "caption"),
            ReadFileFrom(message));
    }

    /// <summary>
    /// A document, a video, an audio file or the largest of a photo's sizes. They are separate fields
    /// in the API and one concept here, and a bot that only understood <c>document</c> would silently
    /// ignore every video anybody sent it.
    /// </summary>
    private static TelegramIncomingFile? ReadFileFrom(JsonElement message)
    {
        foreach (var name in (string[])["document", "video", "audio", "voice", "animation"])
        {
            if (Property(message, name) is { } file && ReadIncomingFile(file, name) is { } read) return read;
        }

        if (Property(message, "photo") is { ValueKind: JsonValueKind.Array } photo)
        {
            // Telegram sends a photo as an array of sizes, smallest first. The last is the original,
            // and anything else silently downgrades the customer's own picture.
            JsonElement? largest = null;
            foreach (var size in photo.EnumerateArray()) largest = size;

            if (largest is { } best) return ReadIncomingFile(best, "photo");
        }

        return null;
    }

    private static TelegramIncomingFile? ReadIncomingFile(JsonElement file, string kind)
    {
        if (String(file, "file_id") is not { Length: > 0 } fileId) return null;

        var size = file.TryGetProperty("file_size", out var bytes) && bytes.TryGetInt64(out var value)
            ? value
            : (long?)null;

        return new TelegramIncomingFile(
            fileId,
            String(file, "file_unique_id") ?? fileId,
            String(file, "file_name") ?? DefaultName(kind),
            String(file, "mime_type"),
            size);
    }

    /// <summary>A photo or a voice note arrives with no name, and a file has to have one.</summary>
    private static string DefaultName(string kind) => kind switch
    {
        "photo" => "photo.jpg",
        "voice" => "voice.ogg",
        "video" => "video.mp4",
        "audio" => "audio.mp3",
        "animation" => "animation.mp4",
        _ => "file",
    };

    private static TelegramCallbackQuery? ReadCallbackQuery(JsonElement? element)
    {
        if (element is not { } query) return null;
        if (String(query, "id") is not { Length: > 0 } id) return null;
        if (ReadSender(Property(query, "from")) is not { } from) return null;

        var message = Property(query, "message");
        TelegramChat? chat = null;
        long? messageId = null;

        if (message is { } present)
        {
            if (present.TryGetProperty("message_id", out var raw) && raw.TryGetInt64(out var value))
            {
                messageId = value;
            }

            if (Property(present, "chat") is { } chatElement
                && chatElement.TryGetProperty("id", out var chatIdRaw)
                && chatIdRaw.TryGetInt64(out var chatId))
            {
                chat = new TelegramChat(chatId, String(chatElement, "type") ?? string.Empty);
            }
        }

        return new TelegramCallbackQuery(id, from, chat, messageId, String(query, "data"));
    }

    private static TelegramSender? ReadSender(JsonElement? element)
    {
        if (element is not { } from) return null;
        if (!from.TryGetProperty("id", out var id) || !id.TryGetInt64(out var senderId)) return null;

        var first = String(from, "first_name");
        var last = String(from, "last_name");
        var display = string.Join(' ', new[] { first, last }.Where(p => !string.IsNullOrWhiteSpace(p)));

        return new TelegramSender(
            senderId,
            String(from, "username"),
            display.Length == 0 ? null : display,
            String(from, "language_code"));
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind is not JsonValueKind.Null
            ? value
            : null;

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        Property(element, name) is { } value && value.TryGetInt32(out var parsed) ? parsed : null;

    private static DateTimeOffset? UnixSeconds(JsonElement element, string name) =>
        Property(element, name) is { } value && value.TryGetInt64(out var parsed) && parsed > 0
            ? DateTimeOffset.FromUnixTimeSeconds(parsed)
            : null;
}
