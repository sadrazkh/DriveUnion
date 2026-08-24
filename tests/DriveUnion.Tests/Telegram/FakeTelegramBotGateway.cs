using System.Text;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;

namespace DriveUnion.Tests.Telegram;

/// <summary>The calls a test can ask about, in the order they happened.</summary>
public enum FakeTelegramOperation
{
    SendMessage,
    EditMessage,
    DeleteMessage,
    SendChatAction,
    AnswerCallbackQuery,
    SendDocument,
    GetFile,
    OpenRemoteFile,
    GetMe,
    SetWebhook,
    DeleteWebhook,
    GetWebhookInfo,
    GetUpdates,
    SetMyCommands,
}

/// <summary>
/// One call. <see cref="Text"/> is the message body, the caption or the file name, depending on the
/// operation, and it is what the "nothing about the pool leaks" test reads.
/// </summary>
public sealed record FakeTelegramCall(
    FakeTelegramOperation Operation,
    long ChatId,
    string? Text,
    string? CallbackData,
    long? MessageId,
    long UploadedBytes,
    string? CachedFileId,
    IReadOnlyList<string> ButtonLabels);

/// <summary>
/// An in-memory Telegram.
///
/// <para>It exists for the same reason the in-memory Drive does: this product has to be provable
/// without a network, and here there is not even a credential to try one with — no bot token, no
/// <c>api_id</c>, and no Bot API server on this machine. What is worth testing is which tenant a chat
/// may read, whether an oversized file ever reaches an upload, what a 429 does to a queued item, and
/// whether the local copy is deleted when the send throws. None of those needs a socket.</para>
///
/// <para><b>Its <c>getFile</c> answers with an absolute local path by default</b>, because that is the
/// branch production runs. A test opts in to the URL shape with <see cref="AnswerFilesWithUrls"/>. The
/// reverse arrangement — the development branch as the default — is exactly how a production-only bug
/// gets written and then passes every test.</para>
///
/// <para>The local branch writes a <b>real file</b> into <see cref="WorkDirectory"/>, so
/// "the local copy is deleted, on both outcomes" is asserted against the filesystem rather than
/// against a mock. A mock cannot tell a <c>finally</c> from an <c>if</c>.</para>
///
/// <para>How to drive it:</para>
/// <list type="bullet">
/// <item><c>FailNext(op, failure)</c> — the next call to that operation fails, once.</item>
/// <item><c>FailAlways(op, failure)</c> / <c>ClearFailure(op)</c> — until cleared.</item>
/// <item><c>ThrottleNext(op, retryAfter)</c> — the same, with flood control already built.</item>
/// <item><c>Forbid(op)</c> — a call that must not happen. It throws rather than recording, because in
/// a test a silent tolerance is a bug that ships.</item>
/// </list>
///
/// One instance per test. It is not thread-safe.
/// </summary>
public sealed class FakeTelegramBotGateway : ITelegramBotGateway, IDisposable
{
    private readonly List<FakeTelegramCall> _calls = [];
    private readonly Dictionary<FakeTelegramOperation, Queue<TelegramFailure>> _failOnce = [];
    private readonly Dictionary<FakeTelegramOperation, TelegramFailure> _failAlways = [];
    private readonly HashSet<FakeTelegramOperation> _forbidden = [];
    private readonly Dictionary<string, byte[]> _files = [];

    private long _nextMessageId = 1000;
    private int _nextFileId;

    public FakeTelegramBotGateway()
    {
        WorkDirectory = Path.Combine(
            Path.GetTempPath(),
            "drive-union-fake-telegram",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(WorkDirectory);
    }

    /// <summary>Where the local-path branch materialises bytes, exactly as the real server would.</summary>
    public string WorkDirectory { get; }

    public IReadOnlyList<FakeTelegramCall> Calls => _calls;

    /// <summary>Opt in to the cloud API's <c>getFile</c> shape. Off by default; see the class remarks.</summary>
    public bool AnswerFilesWithUrls { get; set; }

    /// <summary>What <see cref="GetMeAsync"/> reports.</summary>
    public TelegramBotProfile Profile { get; set; } = new(123456789, "DriveUnionBot");

    public TelegramWebhookInfo WebhookInfo { get; set; } = new(null, 0, null, null, null, 40);

    /// <summary>What the next <see cref="GetUpdatesAsync"/> hands back, then clears.</summary>
    public List<TelegramUpdate> PendingUpdates { get; } = [];

    /// <summary>Every message body the bot has sent, for assertions on the raw string.</summary>
    public IEnumerable<string> SentTexts =>
        _calls.Where(c => c.Operation is FakeTelegramOperation.SendMessage
                or FakeTelegramOperation.EditMessage
                or FakeTelegramOperation.AnswerCallbackQuery
                or FakeTelegramOperation.SendDocument)
            .Select(c => c.Text)
            .Where(t => t is not null)
            .Select(t => t!);

    /// <summary>Every button label the bot has drawn, which is also outbound text.</summary>
    public IEnumerable<string> ButtonLabels => _calls.SelectMany(c => c.ButtonLabels);

    public void FailNext(FakeTelegramOperation operation, TelegramFailure failure)
    {
        if (!_failOnce.TryGetValue(operation, out var queue))
        {
            queue = new Queue<TelegramFailure>();
            _failOnce[operation] = queue;
        }

        queue.Enqueue(failure);
    }

    public void FailAlways(FakeTelegramOperation operation, TelegramFailure failure) =>
        _failAlways[operation] = failure;

    public void ClearFailure(FakeTelegramOperation operation)
    {
        _failAlways.Remove(operation);
        _failOnce.Remove(operation);
    }

    /// <summary>Flood control, which is obeyed rather than retried.</summary>
    public void ThrottleNext(FakeTelegramOperation operation, TimeSpan retryAfter) =>
        FailNext(operation, new TelegramFailure(429, "Too Many Requests: retry later", retryAfter));

    /// <summary>A call that must not happen. Loud rather than forgiving.</summary>
    public void Forbid(FakeTelegramOperation operation) => _forbidden.Add(operation);

    /// <summary>Puts a file where the local branch will find it, and returns its Telegram handle.</summary>
    public string SeedIncomingFile(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var fileId = $"tg-file-{++_nextFileId}";
        _files[fileId] = content;

        return fileId;
    }

    public int FilesLeftInWorkDirectory() =>
        Directory.Exists(WorkDirectory)
            ? Directory.GetFiles(WorkDirectory, "*", SearchOption.AllDirectories).Length
            : 0;

    public Task<TelegramCall<TelegramSentMessage>> SendMessageAsync(
        TelegramOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Refuse<TelegramSentMessage>(
                FakeTelegramOperation.SendMessage,
                message.ChatId,
                message.Text,
                null,
                null,
                0,
                null,
                Labels(message.Keyboard)) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramSentMessage>.Success(
            new TelegramSentMessage(message.ChatId, ++_nextMessageId)));
    }

    public Task<TelegramCall<TelegramSentMessage>> EditMessageAsync(
        TelegramMessageEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (Refuse<TelegramSentMessage>(
                FakeTelegramOperation.EditMessage,
                edit.ChatId,
                edit.Text,
                null,
                edit.MessageId,
                0,
                null,
                Labels(edit.Keyboard)) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramSentMessage>.Success(
            new TelegramSentMessage(edit.ChatId, edit.MessageId)));
    }

    public Task<TelegramCall<TelegramAck>> DeleteMessageAsync(
        long chatId,
        long messageId,
        CancellationToken cancellationToken)
    {
        if (Refuse<TelegramAck>(
                FakeTelegramOperation.DeleteMessage,
                chatId,
                null,
                null,
                messageId,
                0,
                null,
                []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public Task<TelegramCall<TelegramAck>> SendChatActionAsync(
        long chatId,
        string action,
        CancellationToken cancellationToken)
    {
        if (Refuse<TelegramAck>(
                FakeTelegramOperation.SendChatAction,
                chatId,
                action,
                null,
                null,
                0,
                null,
                []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public Task<TelegramCall<TelegramAck>> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        CancellationToken cancellationToken)
    {
        if (Refuse<TelegramAck>(
                FakeTelegramOperation.AnswerCallbackQuery,
                0,
                text,
                callbackQueryId,
                null,
                0,
                null,
                []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public async Task<TelegramCall<TelegramSentMessage>> SendDocumentAsync(
        TelegramDocumentSend send,
        Stream? content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        // Drained rather than ignored, so a caller that handed over a stream it had already read is
        // caught by the byte count rather than by a production incident.
        var uploaded = 0L;
        if (content is not null)
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0) uploaded += read;
        }

        if (Refuse<TelegramSentMessage>(
                FakeTelegramOperation.SendDocument,
                send.ChatId,
                send.FileName,
                null,
                null,
                uploaded,
                send.CachedFileId,
                Labels(send.Keyboard)) is { } refused)
        {
            return refused;
        }

        var minted = send.CachedFileId ?? $"tg-sent-{++_nextFileId}";

        return TelegramCall<TelegramSentMessage>.Success(new TelegramSentMessage(
            send.ChatId,
            ++_nextMessageId,
            minted,
            $"unique-{minted}"));
    }

    public async Task<TelegramCall<TelegramFileHandle>> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        if (Refuse<TelegramFileHandle>(
                FakeTelegramOperation.GetFile,
                0,
                fileId,
                null,
                null,
                0,
                null,
                []) is { } refused)
        {
            return refused;
        }

        if (!_files.TryGetValue(fileId, out var content))
        {
            return TelegramCall<TelegramFileHandle>.Failed(400, "Bad Request: file not found");
        }

        if (AnswerFilesWithUrls)
        {
            return TelegramCall<TelegramFileHandle>.Success(new TelegramFileHandle(
                new TelegramFileLocation.AtUrl(new Uri($"https://api.telegram.invalid/file/{fileId}")),
                content.LongLength));
        }

        // The local server has already written the bytes into its working directory by the time it
        // answers, which is why the obligation is to delete them rather than to fetch them.
        var path = Path.Combine(WorkDirectory, fileId);
        await File.WriteAllBytesAsync(path, content, cancellationToken);

        return TelegramCall<TelegramFileHandle>.Success(new TelegramFileHandle(
            new TelegramFileLocation.OnDisk(path),
            content.LongLength));
    }

    public Task<TelegramCall<Stream>> OpenRemoteFileAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (Refuse<Stream>(
                FakeTelegramOperation.OpenRemoteFile,
                0,
                null,
                null,
                null,
                0,
                null,
                []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        var fileId = url.Segments[^1];

        return Task.FromResult(_files.TryGetValue(fileId, out var content)
            ? TelegramCall<Stream>.Success(new MemoryStream(content, writable: false))
            : TelegramCall<Stream>.Failed(404, "Not Found"));
    }

    public Task<TelegramCall<TelegramBotProfile>> GetMeAsync(CancellationToken cancellationToken)
    {
        if (Refuse<TelegramBotProfile>(
                FakeTelegramOperation.GetMe, 0, null, null, null, 0, null, []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramBotProfile>.Success(Profile));
    }

    public Task<TelegramCall<TelegramAck>> SetWebhookAsync(
        string url,
        string secretToken,
        int maxConnections,
        CancellationToken cancellationToken)
    {
        if (Refuse<TelegramAck>(
                FakeTelegramOperation.SetWebhook, 0, url, null, null, 0, null, []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        WebhookInfo = WebhookInfo with { Url = url, MaxConnections = maxConnections };

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public Task<TelegramCall<TelegramAck>> DeleteWebhookAsync(CancellationToken cancellationToken)
    {
        if (Refuse<TelegramAck>(
                FakeTelegramOperation.DeleteWebhook, 0, null, null, null, 0, null, []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        WebhookInfo = WebhookInfo with { Url = null };

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public Task<TelegramCall<TelegramWebhookInfo>> GetWebhookInfoAsync(CancellationToken cancellationToken)
    {
        if (Refuse<TelegramWebhookInfo>(
                FakeTelegramOperation.GetWebhookInfo, 0, null, null, null, 0, null, []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramWebhookInfo>.Success(WebhookInfo));
    }

    public Task<TelegramCall<IReadOnlyList<TelegramUpdate>>> GetUpdatesAsync(
        long offset,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (Refuse<IReadOnlyList<TelegramUpdate>>(
                FakeTelegramOperation.GetUpdates, 0, null, null, null, 0, null, []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        var batch = PendingUpdates.Where(u => u.UpdateId >= offset).ToList();
        PendingUpdates.Clear();

        return Task.FromResult(
            TelegramCall<IReadOnlyList<TelegramUpdate>>.Success(batch));
    }

    public Task<TelegramCall<TelegramAck>> SetMyCommandsAsync(
        IReadOnlyList<TelegramBotCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (Refuse<TelegramAck>(
                FakeTelegramOperation.SetMyCommands,
                0,
                string.Join(',', commands.Select(c => c.Command)),
                null,
                null,
                0,
                null,
                []) is { } refused)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(TelegramCall<TelegramAck>.Success(TelegramAck.Instance));
    }

    public void Dispose()
    {
        if (!Directory.Exists(WorkDirectory)) return;

        try
        {
            Directory.Delete(WorkDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives one test run is not a test failure.
        }
    }

    /// <summary>
    /// Records the call, refuses a forbidden one loudly, and returns a scripted failure when there is
    /// one. Null means "carry on".
    /// </summary>
    private TelegramCall<T>? Refuse<T>(
        FakeTelegramOperation operation,
        long chatId,
        string? text,
        string? callbackData,
        long? messageId,
        long uploadedBytes,
        string? cachedFileId,
        IReadOnlyList<string> buttons)
        where T : class
    {
        if (_forbidden.Contains(operation))
        {
            throw new InvalidOperationException(
                $"The fake Telegram was asked to {operation}, and this test forbade it. "
                + "That call is the failure under test, not an incidental detail.");
        }

        _calls.Add(new FakeTelegramCall(
            operation,
            chatId,
            text,
            callbackData,
            messageId,
            uploadedBytes,
            cachedFileId,
            buttons));

        if (_failAlways.TryGetValue(operation, out var always)) return TelegramCall<T>.Failed(always);

        if (_failOnce.TryGetValue(operation, out var queue) && queue.Count > 0)
        {
            return TelegramCall<T>.Failed(queue.Dequeue());
        }

        return null;
    }

    private static IReadOnlyList<string> Labels(TelegramKeyboard? keyboard) =>
        keyboard is null ? [] : [.. keyboard.Rows.SelectMany(r => r).Select(b => b.Text)];

    /// <summary>Deterministic bytes, so a mangled body is visible at the first differing index.</summary>
    public static byte[] TestBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);

        return bytes;
    }

    /// <summary>Text as bytes, for the tests that want to read the stored file back.</summary>
    public static byte[] TextBytes(string text) => Encoding.UTF8.GetBytes(text);
}
