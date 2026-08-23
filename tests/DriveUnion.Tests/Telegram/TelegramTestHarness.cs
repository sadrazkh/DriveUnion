using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Tenancy;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        Db = NewContext();
    }

    public FixedClock Clock { get; }

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
        new(context ?? Db, Clock);

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

        await _connection.DisposeAsync();
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
