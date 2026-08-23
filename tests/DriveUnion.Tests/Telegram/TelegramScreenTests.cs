using System.Globalization;
using System.Net;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The two screens, fetched through the real pipeline. What is under test here is what the markup
/// says and who is allowed to see it — the flow itself is settled by the service tests.
/// </summary>
public class TelegramScreenTests
{
    private const long ChatId = 604_912_338;
    private const string CustomerHandle = "reza_files";
    private const string CustomerName = "رضا محمدی";
    private const string CustomerFile = "گزارش-فصلی-۱۴۰۵.pdf";

    [Fact]
    public async Task The_operator_screen_renders_no_chat_id_no_username_and_no_filename()
    {
        using var harness = new TelegramPanelHarness();
        await harness.ConfigureBotAsync();

        SeedSignedInUser(harness);
        SeedSomebodyElsesBinding(harness);

        using var client = harness.NewClient();
        var html = await TelegramPanelHarness.ReadTextAsync(client, "/telegram");

        // The count is genuinely rendered from the binding that exists, so the absences below are
        // not passing over an empty table. Matched with its element, because a bare «۱» also appears
        // in «۱۰ دقیقه‌ای» further down the card and would pass whatever the count said.
        html.Should().Contain("حساب‌های متصل");
        html.Should().Contain("<span class=\"setup-state-value\">۱</span>");

        html.Should().NotContain(
            ChatId.ToString(CultureInfo.InvariantCulture),
            "a chat id names a person, and the operator has no use for one");

        html.Should().NotContain(CustomerHandle, "a customer's Telegram @username is not operator data");
        html.Should().NotContain(CustomerName);
        html.Should().NotContain(CustomerFile, "no filename belonging to any tenant appears here");

        // And the bot's own token never reaches the markup, in any form.
        html.Should().NotContain(TelegramPanelHarness.BotToken);
        html.Should().NotContain("AAHharnessBotTokenValue");
    }

    [Fact]
    public async Task The_operator_screen_comes_up_when_nothing_is_configured()
    {
        using var harness = new TelegramPanelHarness();

        using var client = harness.NewClient();
        var response = await client.GetAsync("/telegram");
        var html = await TelegramPanelHarness.ReadTextAsync(response);

        // This is the state a fresh deployment starts in, and it is the screen the operator opens to
        // discover it. It has to read as instructions, not as a failure.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("BotFather");
        html.Should().Contain("ناقص");
    }

    [Fact]
    public async Task A_customer_cannot_reach_the_operator_screen_or_its_token_form()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);

        using var client = harness.NewClient();

        // A 403 rather than a missing link. A hidden button is not an access control, and this
        // screen holds the credential that reaches every customer's bot.
        (await client.GetAsync("/telegram")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var post = await client.PostAsync(
            "/telegram/bot",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BotToken"] = "111111111:AAsomethingElse",
                ["BotUsername"] = "HijackBot",
            }));

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_operator_cannot_reach_the_customers_linking_card()
    {
        using var harness = new TelegramPanelHarness();

        using var client = harness.NewClient();

        // Operator staff have no tenant, so there is nothing for the bot to show them.
        (await client.GetAsync("/telegram/link")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_customers_card_says_so_plainly_when_no_bot_is_configured()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);

        SeedSignedInUser(harness);

        using var client = harness.NewClient();
        var html = await TelegramPanelHarness.ReadTextAsync(client, "/telegram/link");

        // A control that cannot work must not be rendered as though it can — absent, not disabled.
        html.Should().Contain("راه‌اندازی نشده");
        html.Should().NotContain("/telegram/link/start");
    }

    [Fact]
    public async Task Starting_renders_the_deep_link_the_qr_code_and_the_six_digit_input()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        SeedSignedInUser(harness);

        using var client = harness.NewClient();
        var antiforgery = await TelegramPanelHarness.AntiforgeryTokenAsync(client, "/telegram/link");

        var response = await client.PostAsync(
            "/telegram/link/start",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = antiforgery,
            }));

        // Rendered rather than redirected: a redirect would have to carry the raw token through
        // TempData or the query string, and a linking token in either is worse than the screenshot
        // the second leg was built to devalue.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await TelegramPanelHarness.ReadTextAsync(response);

        html.Should().Contain($"https://t.me/{TelegramPanelHarness.BotUsername}?start=");
        html.Should().Contain("<svg", "the QR code is inline, so the token never rides in a URL");
        html.Should().Contain("one-time-code");

        using var db = harness.NewDbContext();
        var row = await db.TelegramLinkTokens.AsNoTracking().SingleAsync();

        html.Should().NotContain(
            row.TokenHash,
            "the response carries the token and the table carries its hash — never both");
    }

    [Fact]
    public async Task The_confirming_post_is_refused_without_an_antiforgery_token()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        SeedSignedInUser(harness);

        using var client = harness.NewClient();

        // This POST is the one request in the flow that writes a binding. Without the token it must
        // not run at all.
        var response = await client.PostAsync(
            "/telegram/link/confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = "123456",
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var db = harness.NewDbContext();
        (await db.TelegramAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_customers_card_shows_the_handle_and_never_the_numeric_id()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        SeedSignedInUser(harness);
        BindSignedInUser(harness);

        using var client = harness.NewClient();
        var html = await TelegramPanelHarness.ReadTextAsync(client, "/telegram/link");

        html.Should().Contain($"@{CustomerHandle}", "the handle is the one the customer recognises");
        html.Should().Contain("قطع اتصال");

        html.Should().NotContain(
            ChatId.ToString(CultureInfo.InvariantCulture),
            "the numeric Telegram id is an identifier the customer has no use for");
    }

    [Fact]
    public async Task Unlinking_from_the_card_removes_the_binding()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        SeedSignedInUser(harness);
        BindSignedInUser(harness);

        using var client = harness.NewClient();
        var antiforgery = await TelegramPanelHarness.AntiforgeryTokenAsync(client, "/telegram/link");

        var response = await client.PostAsync(
            "/telegram/unlink",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__RequestVerificationToken"] = antiforgery,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var db = harness.NewDbContext();
        (await db.TelegramAccounts.CountAsync()).Should().Be(0);
    }

    private static void SeedSignedInUser(TelegramPanelHarness harness)
    {
        using var db = harness.NewDbContext();

        db.Users.Add(NewUser(
            harness.UserId,
            harness.IsOperator ? null : harness.TenantId,
            harness.IsOperator));

        db.SaveChanges();
    }

    /// <summary>The signed-in customer, with a Telegram account bound to them.</summary>
    private static void BindSignedInUser(TelegramPanelHarness harness)
    {
        using var db = harness.NewDbContext();

        db.TelegramAccounts.Add(NewBinding(harness.UserId));
        db.SaveChanges();
    }

    /// <summary>
    /// Another tenant's customer, bound, with a file of their own — the data the operator's screen
    /// has to be able to see and must not print.
    /// </summary>
    private static void SeedSomebodyElsesBinding(TelegramPanelHarness harness)
    {
        using var db = harness.NewDbContext();

        var tenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var user = NewUser(Guid.NewGuid(), tenantId, isOperator: false);
        var now = DateTimeOffset.UtcNow;

        db.Tenants.Add(new Core.Tenancy.Tenant
        {
            Id = tenantId,
            Name = "acme",
            Slug = $"acme-{Guid.NewGuid():N}"[..16],
            CreatedAt = now,
        });

        db.GoogleAccounts.Add(new Core.Storage.GoogleAccount
        {
            Id = accountId,
            Email = $"pool-{Guid.NewGuid():N}@example.com",
            Label = "A1",
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = 5L * 1024 * 1024 * 1024 * 1024,
            QuotaUsedBytes = 0,
            Status = Core.Storage.GoogleAccountStatus.Healthy,
            CreatedAt = now,
        });

        db.Users.Add(user);

        db.StoredFiles.Add(new Core.Storage.StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = accountId,
            DriveFileId = $"drive-{Guid.NewGuid():N}",
            Name = CustomerFile,
            MimeType = "application/pdf",
            SizeBytes = 18_400_000,
            CreatedAt = now,
            ModifiedAt = now,
        });

        db.TelegramAccounts.Add(NewBinding(user.Id));

        db.SaveChanges();
    }

    private static AppUser NewUser(Guid id, Guid? tenantId, bool isOperator)
    {
        var unique = Guid.NewGuid().ToString("N");

        return new AppUser
        {
            Id = id,
            TenantId = tenantId,
            IsOperator = isOperator,
            UserName = $"user-{unique}@example.test",
            NormalizedUserName = $"USER-{unique}@EXAMPLE.TEST",
            Email = $"user-{unique}@example.test",
            NormalizedEmail = $"USER-{unique}@EXAMPLE.TEST",
            SecurityStamp = unique,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static TelegramAccount NewBinding(Guid appUserId) => new()
    {
        Id = Guid.NewGuid(),
        AppUserId = appUserId,
        TelegramUserId = ChatId,
        ChatId = ChatId,
        Username = CustomerHandle,
        DisplayName = CustomerName,
        LinkedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };
}
