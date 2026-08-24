using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Sharing;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Telegram;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// A real relational database for the Telegram slice, in memory and gone at the end of the test.
///
/// SQLite rather than EF's in-memory provider, for the reason <c>ServiceTestHarness</c> gives and
/// which is sharper here: most of what this layer promises is SQL. Two unique indexes that decide
/// whether a second Telegram account can bind to a user, a conditional UPDATE whose rows-affected is
/// the arbiter of a race, and a transaction that keeps the consumption and the binding together. The
/// in-memory provider has none of those and would pass every test below without testing anything.
///
/// The connection is opened here and held for the harness's life: a SQLite <c>:memory:</c> database
/// belongs to its connection, and closing it takes the schema with it.
/// </summary>
public sealed class TelegramTestHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly List<DriveUnionDbContext> _contexts = [];

    private TelegramTestHarness(SqliteConnection connection)
    {
        _connection = connection;
        Clock = new FixedClock(Now);
        Protector = new ReversibleProtector();
        Drive = new FakeDriveClient { Clock = Clock };
        Telegram = new FakeTelegramBotGateway();
        Disk = new FakeDiskSpace();
        Db = NewContext();
    }

    public FixedClock Clock { get; }

    /// <summary>The in-memory Telegram. Nothing in this suite reaches a real one.</summary>
    public FakeTelegramBotGateway Telegram { get; }

    /// <summary>The in-memory Drive, so the byte-moving paths have somewhere to read from.</summary>
    public FakeDriveClient Drive { get; }

    /// <summary>
    /// Free space, which is the one number in the disk arithmetic that cannot be produced by any
    /// other means: there is no way to make a real volume nearly full inside a unit test.
    /// </summary>
    public FakeDiskSpace Disk { get; }

    /// <summary>
    /// The configuration under test. Defaults are the cloud API's, which is what an unconfigured
    /// deployment is actually talking to; a test that is about the deployed box sets the two ceilings
    /// itself, which is the point of them being configuration.
    /// </summary>
    public TelegramOptions Options { get; } = new()
    {
        MaxSendBytes = 50_000_000,
        MaxReceiveBytes = 20_000_000,
        PanelBaseUrl = "https://panel.example.test",
    };

    /// <summary>
    /// Stands in for Data Protection. It is reversible and deliberately not encryption: what the
    /// tests need is a distinguishable wrapper, so "the raw value is not in the column" is a real
    /// assertion rather than a tautology about ciphertext.
    /// </summary>
    public ReversibleProtector Protector { get; }

    public DriveUnionDbContext Db { get; }

    public static TelegramTestHarness Create()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var harness = new TelegramTestHarness(connection);
        harness.Db.Database.EnsureCreated();

        return harness;
    }

    /// <summary>A second context over the same database, for two callers holding their own snapshots.</summary>
    public DriveUnionDbContext NewContext()
    {
        var context = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>()
                .UseSqlite(_connection)
                .Options);

        _contexts.Add(context);

        return context;
    }

    public TelegramBotSettingsStore Bot(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Protector, Clock, NullLogger<TelegramBotSettingsStore>.Instance);

    public TelegramLinkService Links(DriveUnionDbContext? context = null)
    {
        var db = context ?? Db;
        return new TelegramLinkService(db, Bot(db), Clock, NullLogger<TelegramLinkService>.Instance);
    }

    public TelegramIdentityReader Identities(DriveUnionDbContext? context = null) =>
        new(context ?? Db);

    public TelegramOperatorView OperatorView(DriveUnionDbContext? context = null) =>
        new(context ?? Db, WorkDirectory(), Wrapped, Clock);

    /// <summary><see cref="Options"/> in the shape the production classes take it.</summary>
    public IOptions<TelegramOptions> Wrapped => Microsoft.Extensions.Options.Options.Create(Options);

    public TelegramWorkDirectory WorkDirectory() =>
        new(Wrapped, Disk, Clock, NullLogger<TelegramWorkDirectory>.Instance);

    public TelegramUpdateLedger Ledger(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Clock);

    public TelegramOutboxWriter Outbox(DriveUnionDbContext? context = null) =>
        new(context ?? Db, Wrapped, Clock);

    public TelegramStrangerBudget Strangers { get; } = new(new FixedClock(Now));

    public TelegramFairnessCursor Fairness { get; } = new();

    /// <summary>The bot's surface, wired to the in-memory Telegram and the real service layer.</summary>
    public TelegramUpdateHandler Handler(DriveUnionDbContext? context = null)
    {
        var db = context ?? Db;

        return new TelegramUpdateHandler(
            Identities(db),
            Links(db),
            Telegram,
            Ledger(db),
            Outbox(db),
            new FileCatalog(db, Clock),
            new ShareLinkService(db, new SlugGenerator(), Clock),
            WorkDirectory(),
            Strangers,
            Wrapped,
            Clock,
            NullLogger<TelegramUpdateHandler>.Instance);
    }

    /// <summary>The drainer's brain, without the loop around it.</summary>
    public TelegramOutboxProcessor Processor(DriveUnionDbContext? context = null)
    {
        var db = context ?? Db;

        return new TelegramOutboxProcessor(
            db,
            Telegram,
            new TelegramDeliverySource(db),
            Bot(db),
            Drive,
            new UploadCoordinator(db, Drive, new SingleAccountUploadTargetSelector(db), Clock),
            WorkDirectory(),
            Fairness,
            Wrapped,
            Clock,
            NullLogger<TelegramOutboxProcessor>.Instance);
    }

    /// <summary>A pool account with room, so the inbound path has somewhere to write.</summary>
    public GoogleAccount SeedAccount()
    {
        var account = new GoogleAccount
        {
            Id = Guid.NewGuid(),
            Email = $"pool-{Guid.NewGuid():N}@example.com",
            Label = "A1",
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = 5L * 1024 * 1024 * 1024 * 1024,
            QuotaUsedBytes = 0,
            Status = GoogleAccountStatus.Healthy,
            CreatedAt = Now,
        };

        Db.GoogleAccounts.Add(account);
        Db.SaveChanges();

        return account;
    }

    /// <summary>A file the tenant owns, with its bytes in the fake Drive so a delivery can read them.</summary>
    public StoredFile SeedFile(
        Guid tenantId,
        Guid accountId,
        string name = "quarterly.pdf",
        long sizeBytes = 4096,
        byte[]? content = null)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
            DriveFileId = $"drive-{Guid.NewGuid():N}",
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = sizeBytes,
            CreatedAt = Now,
            ModifiedAt = Now,
        };

        Db.StoredFiles.Add(file);
        Db.SaveChanges();

        // Only when the test intends the bytes to be readable. A file that is only metadata is the
        // right fixture for the ceiling tests, where the whole assertion is that nothing was read.
        if (content is not null)
        {
            Drive.SeedFile(accountId, file.DriveFileId, name, file.MimeType, content);
        }

        return file;
    }

    /// <summary>An update carrying a text message from a private chat, which is all the bot answers.</summary>
    public static TelegramUpdate TextUpdate(long telegramUserId, string text, long updateId = 1) =>
        new(
            updateId,
            new TelegramIncomingMessage(
                1,
                new TelegramChat(telegramUserId, TelegramChat.PrivateType),
                new TelegramSender(telegramUserId, "someone", "Some One", "fa"),
                text,
                null),
            null);

    /// <summary>An update carrying a document.</summary>
    public static TelegramUpdate FileUpdate(
        long telegramUserId,
        string fileId,
        string fileName,
        long? sizeBytes,
        long updateId = 1) =>
        new(
            updateId,
            new TelegramIncomingMessage(
                1,
                new TelegramChat(telegramUserId, TelegramChat.PrivateType),
                new TelegramSender(telegramUserId, "someone", "Some One", "fa"),
                null,
                new TelegramIncomingFile(fileId, $"u-{fileId}", fileName, "application/pdf", sizeBytes)),
            null);

    /// <summary>An update carrying a button press.</summary>
    public static TelegramUpdate CallbackUpdate(
        long telegramUserId,
        string data,
        long? messageId = 42,
        long updateId = 1) =>
        new(
            updateId,
            null,
            new TelegramCallbackQuery(
                $"cb-{updateId}",
                new TelegramSender(telegramUserId, "someone", "Some One", "fa"),
                new TelegramChat(telegramUserId, TelegramChat.PrivateType),
                messageId,
                data));

    /// <summary>A configured bot, so the linking flow has a @username to build a deep link from.</summary>
    public async Task<string> SeedBotAsync(string username = "DriveUnionBot")
    {
        await Bot().SaveAsync("123456789:AAHtestTokenValue", username, null, CancellationToken.None);

        return username;
    }

    public Tenant SeedTenant(string slug = "acme")
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = $"{slug}-{Guid.NewGuid():N}"[..16],
            CreatedAt = Now,
        };

        Db.Tenants.Add(tenant);
        Db.SaveChanges();

        return tenant;
    }

    /// <summary>A panel user. A null tenant is operator staff, which is what <c>AppUser</c> means by it.</summary>
    public AppUser SeedUser(Guid? tenantId, bool isOperator = false)
    {
        var unique = Guid.NewGuid().ToString("N");

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsOperator = isOperator,
            UserName = $"user-{unique}@example.test",
            NormalizedUserName = $"USER-{unique}@EXAMPLE.TEST",
            Email = $"user-{unique}@example.test",
            NormalizedEmail = $"USER-{unique}@EXAMPLE.TEST",
            SecurityStamp = unique,
            CreatedAt = Now,
        };

        Db.Users.Add(user);
        Db.SaveChanges();

        return user;
    }

    /// <summary>
    /// Walks the whole two-leg flow and returns the bound Telegram id, for the tests that are about
    /// what happens afterwards rather than about the flow itself.
    /// </summary>
    public async Task<long> LinkAsync(
        Guid appUserId,
        long telegramUserId,
        string? username = null,
        string? displayName = null,
        string? languageCode = null)
    {
        var links = Links();

        var start = await links.StartAsync(appUserId, CancellationToken.None);
        Assert.Equal(TelegramLinkStartStatus.Issued, start.Status);

        var token = TokenOf(start.DeepLink!);

        var presented = await links.PresentAsync(
            new TelegramStartRequest(
                token,
                telegramUserId,
                telegramUserId,
                username,
                displayName,
                languageCode),
            CancellationToken.None);

        Assert.Equal(TelegramStartStatus.CodeIssued, presented.Status);

        var confirmed = await links.ConfirmAsync(
            appUserId,
            presented.ConfirmationCode,
            CancellationToken.None);

        Assert.Equal(TelegramConfirmStatus.Linked, confirmed.Status);

        return telegramUserId;
    }

    /// <summary>The <c>start</c> parameter out of a deep link — what the bot would receive.</summary>
    public static string TokenOf(string deepLink)
    {
        ArgumentNullException.ThrowIfNull(deepLink);

        var marker = deepLink.IndexOf("?start=", StringComparison.Ordinal);
        Assert.True(marker > 0, $"The deep link carries no start parameter: {deepLink}");

        return deepLink[(marker + "?start=".Length)..];
    }

    /// <summary>Everything in the database, as text, for the tests that assert a secret is not in it.</summary>
    public async Task<string> DumpAsync()
    {
        var tokens = await Db.TelegramLinkTokens.AsNoTracking().ToListAsync();
        var accounts = await Db.TelegramAccounts.AsNoTracking().ToListAsync();
        var settings = await Db.TelegramBotSettings.AsNoTracking().ToListAsync();

        var text = new System.Text.StringBuilder();

        foreach (var row in tokens)
        {
            text.AppendLine($"{row.Id}|{row.TokenHash}|{row.ConfirmationCodeHash}|{row.Attempts}");
        }

        foreach (var row in accounts)
        {
            text.AppendLine($"{row.Id}|{row.TelegramUserId}|{row.ChatId}|{row.Username}");
        }

        foreach (var row in settings)
        {
            text.AppendLine($"{row.Id}|{row.BotTokenProtected}|{row.BotUsername}|{row.BotUserId}");
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts) await context.DisposeAsync();

        Telegram.Dispose();

        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Free space, on demand.
    ///
    /// <see cref="FreeBytes"/> null is "no answer", which is what a machine with no local Bot API
    /// server reports and which the pre-flight treats as "carry on" — the branch that writes to that
    /// volume does not exist unless the server does.
    /// </summary>
    public sealed class FakeDiskSpace : ITelegramDiskSpace
    {
        public long? FreeBytes { get; set; }

        public long? FreeBytesOn(string path) => FreeBytes;
    }

    /// <summary>
    /// Reversible, and deliberately not encryption — what the tests need is a wrapper that does not
    /// contain its own input, so "the bot token is not sitting in the column" is a real assertion
    /// rather than a tautology about ciphertext.
    ///
    /// <see cref="Broken"/> makes every stored value undecryptable, which is what a lost Data
    /// Protection key looks like from here.
    /// </summary>
    public sealed class ReversibleProtector : ITokenProtector
    {
        private const string Prefix = "wrapped:";

        /// <summary>Set to true to simulate a key that no longer exists.</summary>
        public bool Broken { get; set; }

        public string Protect(string plaintext) =>
            Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string? Unprotect(string protectedValue)
        {
            if (Broken || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) return null;

            return System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedValue[Prefix.Length..]));
        }
    }
}
