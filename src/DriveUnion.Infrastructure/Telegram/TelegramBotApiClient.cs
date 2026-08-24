using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The one thing in this product that reaches Telegram.
///
/// <para><b>Nothing in the test suite constructs this class</b>, exactly as nothing constructs the
/// Google Drive client: there is no bot token on this machine and no Bot API server to answer it. Its
/// job is to be small enough that everything worth arguing about lives on the other side of
/// <see cref="ITelegramBotGateway"/>, where a hand-written in-memory Telegram can prove it.</para>
///
/// <para>Three rules that are not obvious from the code and are worth stating before somebody
/// "tidies" one of them away:</para>
/// <list type="number">
/// <item><b>The bot token is in the URL path.</b> <c>POST /bot&lt;token&gt;/sendMessage</c> is the only
/// authentication the Bot API has, so no request URI may ever reach a log, an exception message or a
/// response. That is why every failure below is built from the status and the body and never from
/// <c>HttpRequestMessage.RequestUri</c>.</item>
/// <item><b>The client's own timeout is infinite and every call carries its own deadline.</b>
/// <c>HttpClient.Timeout</c> covers the whole request including the body, so a two-gigabyte upload
/// dies at a hundred seconds regardless of progress — a one-line bug that produces a feature which
/// works in every test and fails on every real file.</item>
/// <item><b>A document body is streamed and never buffered.</b> The stream arrives from the storage
/// pool and is forwarded straight into the multipart body, which also means the request cannot be
/// replayed and is marked as such for the retry handler.</item>
/// </list>
/// </summary>
public sealed class TelegramBotApiClient(
    HttpClient http,
    ITelegramBotSettingsStore settings,
    IOptions<TelegramOptions> options,
    ILogger<TelegramBotApiClient> logger) : ITelegramBotGateway
{
    /// <summary>The name the registration gives the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "DriveUnion.Telegram";

    /// <summary>A chat reply is tens of milliseconds of work. Anything longer is a fault, not slowness.</summary>
    private static readonly TimeSpan ShortCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Floor and rate for a transfer's deadline. Two megabytes a second is a pessimistic uplink and
    /// deliberately so: the cost of being generous is a stuck worker for a few extra minutes, and the
    /// cost of being tight is a two-gigabyte upload cancelled at ninety per cent.
    /// </summary>
    private static readonly TimeSpan TransferFloor = TimeSpan.FromMinutes(5);

    private const long TransferBytesPerSecond = 2_000_000;

    private readonly TelegramOptions _options = options.Value;

    public Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chat_id"] = Number(message.ChatId),
            ["text"] = message.Text,

            // No parse mode. Customer file names arrive in these strings and Markdown or HTML would
            // make an underscore or an angle bracket in somebody's file name a send that Telegram
            // refuses — or worse, renders as markup.
            ["disable_web_page_preview"] = "true",
        };

        AddKeyboard(form, message.Keyboard);

        return CallAsync("sendMessage", form, ShortCallTimeout, TelegramWire.ReadSentMessage, cancellationToken);
    }

    public Task<TelegramCall<TelegramSentMessage>> EditMessageAsync(
        TelegramMessageEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chat_id"] = Number(edit.ChatId),
            ["message_id"] = Number(edit.MessageId),
            ["text"] = edit.Text,
            ["disable_web_page_preview"] = "true",
        };

        AddKeyboard(form, edit.Keyboard);

        return CallAsync("editMessageText", form, ShortCallTimeout, TelegramWire.ReadSentMessage, cancellationToken);
    }

    public Task<TelegramCall<TelegramAck>> DeleteMessageAsync(
        long chatId,
        long messageId,
        CancellationToken cancellationToken) =>
        AckAsync(
            "deleteMessage",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat_id"] = Number(chatId),
                ["message_id"] = Number(messageId),
            },
            cancellationToken);

    public Task<TelegramCall<TelegramAck>> SendChatActionAsync(
        long chatId,
        string action,
        CancellationToken cancellationToken) =>
        AckAsync(
            "sendChatAction",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat_id"] = Number(chatId),
                ["action"] = action,
            },
            cancellationToken);

    public Task<TelegramCall<TelegramAck>> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["callback_query_id"] = callbackQueryId,
        };

        if (text is { Length: > 0 }) form["text"] = text;

        return AckAsync("answerCallbackQuery", form, cancellationToken);
    }

    public async Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramDocumentSend send,
        Stream? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        if (send.CachedFileId is { Length: > 0 } cached)
        {
            // The free lunch: no bytes leave this box, nothing is read out of the storage pool, and
            // the working directory is not touched at all.
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat_id"] = Number(send.ChatId),
                ["document"] = cached,
            };

            if (send.Caption is { Length: > 0 } caption) form["caption"] = caption;
            AddKeyboard(form, send.Keyboard);

            return await CallAsync(
                "sendDocument",
                form,
                ShortCallTimeout,
                TelegramWire.ReadSentMessage,
                cancellationToken).ConfigureAwait(false);
        }

        if (content is null)
        {
            return TelegramCall<TelegramSentMessage>.Failed(
                null,
                "A document send needs either a cached file handle or a stream, and it was given neither.");
        }

        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(Number(send.ChatId)), "chat_id");

        if (send.Caption is { Length: > 0 } text) body.Add(new StringContent(text), "caption");
        if (send.Keyboard is { } keyboard) body.Add(new StringContent(Keyboard(keyboard)), "reply_markup");

        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(send.MimeType) ? "application/octet-stream" : send.MimeType);

        // A known length keeps the request out of chunked transfer encoding, which some proxies
        // handle badly and which loses the only progress signal a stuck upload has.
        if (send.SizeBytes > 0) file.Headers.ContentLength = send.SizeBytes;

        body.Add(file, "document", send.FileName);

        return await CallAsync(
            "sendDocument",
            body,
            TransferTimeout(send.SizeBytes),
            nonRewindable: true,
            TelegramWire.ReadSentMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramCall<TelegramFileHandle>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        var token = await settings.ReadBotTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return TelegramCall<TelegramFileHandle>.Failed(null, NoToken);

        return await CallAsync(
            "getFile",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["file_id"] = fileId },
            ShortCallTimeout,
            result => TelegramWire.ReadFile(result, _options.LocalBotServer, BaseUri, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramCall<Stream>> OpenRemoteFileAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransferTimeout(_options.MaxReceiveBytes));

        try
        {
            var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();

                // Never the URL. It carries the bot token in its path.
                return TelegramCall<Stream>.Failed(
                    status,
                    $"Telegram refused a file download with HTTP {Number(status)}.");
            }

            return TelegramCall<Stream>.Success(
                await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return TelegramCall<Stream>.Failed(null, ex.Message);
        }
    }

    public Task<TelegramCall<TelegramBotProfile>> GetMeAsync(CancellationToken cancellationToken) =>
        CallAsync("getMe", [], ShortCallTimeout, TelegramWire.ReadProfile, cancellationToken);

    public Task<TelegramCall<TelegramAck>> SetWebhookAsync(
        string url,
        string secretToken,
        int maxConnections,
        CancellationToken cancellationToken) =>
        AckAsync(
            "setWebhook",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["url"] = url,
                ["secret_token"] = secretToken,
                ["max_connections"] = Number(maxConnections),
                ["allowed_updates"] = JsonSerializer.Serialize(TelegramWire.AllowedUpdates),

                // Never true. Updates waiting at Telegram are held for 24 hours, and dropping them
                // is the one way to turn a registration change into lost customer files.
                ["drop_pending_updates"] = "false",
            },
            cancellationToken);

    public Task<TelegramCall<TelegramAck>> DeleteWebhookAsync(CancellationToken cancellationToken) =>
        AckAsync(
            "deleteWebhook",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["drop_pending_updates"] = "false" },
            cancellationToken);

    public Task<TelegramCall<TelegramWebhookInfo>> GetWebhookInfoAsync(CancellationToken cancellationToken) =>
        CallAsync<TelegramWebhookInfo>(
            "getWebhookInfo",
            [],
            ShortCallTimeout,
            result => TelegramWire.ReadWebhookInfo(result),
            cancellationToken);

    public Task<TelegramCall<IReadOnlyList<TelegramUpdate>>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["offset"] = Number(offset),
            ["timeout"] = Number(timeoutSeconds),
            ["allowed_updates"] = JsonSerializer.Serialize(TelegramWire.AllowedUpdates),
        };

        // The long poll holds the connection for `timeout` seconds by design, so its deadline is that
        // plus room for the round trip rather than the short-call one.
        return CallAsync<IReadOnlyList<TelegramUpdate>>(
            "getUpdates",
            form,
            TimeSpan.FromSeconds(timeoutSeconds) + ShortCallTimeout,
            ReadUpdates,
            cancellationToken);
    }

    public Task<TelegramCall<TelegramAck>> SetMyCommandsAsync(
        IReadOnlyList<TelegramBotCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var payload = commands.Select(c => new { command = c.Command, description = c.Description });

        return AckAsync(
            "setMyCommands",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commands"] = JsonSerializer.Serialize(payload),
            },
            cancellationToken);
    }

    private const string NoToken = "No Telegram bot token is configured.";

    private Uri BaseUri => new(
        _options.ApiBaseUrl.EndsWith('/') ? _options.ApiBaseUrl : _options.ApiBaseUrl + "/");

    /// <summary>
    /// The method's URL, built as one absolute string rather than by combining a relative one.
    ///
    /// <para>A @BotFather token is «&lt;digits&gt;:&lt;secret&gt;», so the relative form
    /// <c>bot123456789:AA…/sendMessage</c> has a colon before its first slash — which is a URI
    /// <em>scheme</em>. <c>new Uri(base, relative)</c> reads it as one and throws
    /// «the 'bot123456789' scheme is not supported», on the first call, against every real token.
    /// Concatenating first and parsing once removes the ambiguity.</para>
    /// </summary>
    private Uri MethodUri(string token, string method) => new(
        $"{_options.ApiBaseUrl.TrimEnd('/')}/bot{token}/{method}",
        UriKind.Absolute);

    private static IReadOnlyList<TelegramUpdate>? ReadUpdates(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return null;

        var updates = new List<TelegramUpdate>();
        foreach (var element in result.EnumerateArray())
        {
            if (TelegramWire.ReadUpdate(element) is { } update) updates.Add(update);
        }

        return updates;
    }

    private Task<TelegramCall<TelegramAck>> AckAsync(
        string method,
        Dictionary<string, string> form,
        CancellationToken cancellationToken) =>
        CallAsync(method, form, ShortCallTimeout, _ => TelegramAck.Instance, cancellationToken);

    private Task<TelegramCall<T>> CallAsync<T>(
        string method,
        Dictionary<string, string> form,
        TimeSpan timeout,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken)
        where T : class =>
        CallAsync(method, new FormUrlEncodedContent(form), timeout, nonRewindable: false, read, cancellationToken);

    private async Task<TelegramCall<T>> CallAsync<T>(
        string method,
        HttpContent content,
        TimeSpan timeout,
        bool nonRewindable,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken)
        where T : class
    {
        var token = await settings.ReadBotTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return TelegramCall<T>.Failed(null, NoToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUri(token, method))
        {
            Content = content,
        };

        if (nonRewindable) request.Options.Set(TelegramRetryHandler.NonRewindableBody, true);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var root = document.RootElement;

            if (!TelegramWire.ReadOk(root))
            {
                var failure = TelegramWire.ReadFailure(root, (int)response.StatusCode);

                // The method name and Telegram's own words. Not the URI, not the form, not the token.
                logger.LogWarning(
                    "Telegram refused {Method}: {ErrorCode} {Description}",
                    method,
                    failure.ErrorCode,
                    failure.Description);

                return TelegramCall<T>.Failed(failure);
            }

            if (!root.TryGetProperty("result", out var result))
            {
                return TelegramCall<T>.Failed(null, $"Telegram answered {method} with no result.");
            }

            return read(result) is { } value
                ? TelegramCall<T>.Success(value)
                : TelegramCall<T>.Failed(null, $"Telegram's answer to {method} was not the expected shape.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TelegramCall<T>.Failed(null, $"Telegram did not answer {method} within {timeout}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return TelegramCall<T>.Failed(null, ex.Message);
        }
    }

    /// <summary>
    /// A deadline derived from the size, because a fixed one is either useless for a small file or
    /// fatal for a large one.
    /// </summary>
    private static TimeSpan TransferTimeout(long sizeBytes)
    {
        if (sizeBytes <= 0) return TransferFloor;

        var derived = TimeSpan.FromSeconds((double)sizeBytes / TransferBytesPerSecond);

        return derived < TransferFloor ? TransferFloor : derived;
    }

    private static void AddKeyboard(Dictionary<string, string> form, TelegramKeyboard? keyboard)
    {
        if (keyboard is { } present) form["reply_markup"] = Keyboard(present);
    }

    private static string Keyboard(TelegramKeyboard keyboard)
    {
        var rows = keyboard.Rows
            .Select(row => row.Select(b => new { text = b.Text, callback_data = b.CallbackData }));

        return JsonSerializer.Serialize(new { inline_keyboard = rows });
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
