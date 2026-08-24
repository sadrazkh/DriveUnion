using DriveUnion.Core.Application;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The operator's token gets the discipline the Google client secret already has: a read model that
/// cannot express it, one accessor to the plaintext, and a save that keeps what is stored when the
/// field arrives empty.
/// </summary>
public class TelegramBotSettingsStoreTests
{
    private const string Token = "987654321:AAExampleBotTokenValue";

    [Fact]
    public async Task The_read_model_reports_that_a_token_exists_and_never_what_it_is()
    {
        await using var harness = TelegramTestHarness.Create();

        await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        var read = await harness.Bot().ReadAsync(CancellationToken.None);

        read.HasToken.Should().BeTrue();
        read.BotUsername.Should().Be("AcmeBot");

        // A screen that could print the token is a screen that eventually will, so there is nothing
        // on this record to print. HasToken is the whole of what a browser ever learns.
        typeof(StoredTelegramBot).GetProperties()
            .Select(p => p.PropertyType)
            .Should().NotContain(typeof(string).MakeByRefType());

        typeof(StoredTelegramBot).GetProperties()
            .Should().NotContain(
                p => p.Name.Contains("Token", StringComparison.Ordinal) && p.PropertyType == typeof(string),
                "the only token-shaped member is the bool HasToken");
    }

    [Fact]
    public async Task The_token_is_protected_at_rest_and_readable_only_through_its_own_accessor()
    {
        await using var harness = TelegramTestHarness.Create();

        await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        var stored = await harness.Db.TelegramBotSettings.AsNoTracking().SingleAsync();

        stored.BotTokenProtected.Should().NotBeNull();
        stored.BotTokenProtected.Should().NotContain(Token, "a database dump is not a working bot");

        (await harness.Bot().ReadBotTokenAsync(CancellationToken.None)).Should().Be(Token);
    }

    [Fact]
    public async Task Saving_with_an_empty_token_keeps_the_stored_one()
    {
        await using var harness = TelegramTestHarness.Create();

        await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        // Correcting the @username must not mean fetching the token out of @BotFather again.
        var updated = await harness.Bot().SaveAsync(null, "AcmeFilesBot", null, CancellationToken.None);

        updated.HasToken.Should().BeTrue();
        updated.BotUsername.Should().Be("AcmeFilesBot");
        (await harness.Bot().ReadBotTokenAsync(CancellationToken.None)).Should().Be(Token);
    }

    [Fact]
    public async Task The_bot_id_is_read_out_of_the_token_rather_than_asked_for()
    {
        await using var harness = TelegramTestHarness.Create();

        var saved = await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        // getMe is the authoritative answer and needs a transport. The id is the part of the token
        // before the colon, so until then it costs no network call and no guesswork.
        saved.BotUserId.Should().Be(987654321);
    }

    [Fact]
    public async Task An_at_sign_on_the_username_is_accepted_and_dropped()
    {
        await using var harness = TelegramTestHarness.Create();

        var saved = await harness.Bot().SaveAsync(Token, "@AcmeBot", null, CancellationToken.None);

        saved.BotUsername.Should().Be("AcmeBot", "the operator will paste either form");
    }

    [Fact]
    public async Task A_token_that_no_longer_decrypts_is_reported_as_absent()
    {
        await using var harness = TelegramTestHarness.Create();

        await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        // A Data Protection key that has been lost. The operator's only fix is to paste the token
        // again, and a screen claiming it was set would send them hunting through @BotFather for a
        // fault that is on this side.
        harness.Protector.Broken = true;

        var read = await harness.Bot().ReadAsync(CancellationToken.None);

        read.HasToken.Should().BeFalse();
        read.BotUsername.Should().Be("AcmeBot", "the username is not a secret and still helps");
        (await harness.Bot().ReadBotTokenAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_forgets_the_bot_and_says_when_there_was_nothing_to_forget()
    {
        await using var harness = TelegramTestHarness.Create();

        (await harness.Bot().ClearAsync(CancellationToken.None)).Should().BeFalse();

        await harness.Bot().SaveAsync(Token, "AcmeBot", null, CancellationToken.None);

        (await harness.Bot().ClearAsync(CancellationToken.None)).Should().BeTrue();

        var read = await harness.Bot().ReadAsync(CancellationToken.None);

        read.HasToken.Should().BeFalse();
        read.BotUsername.Should().BeNull();
        read.BotUserId.Should().BeNull();
    }

    [Fact]
    public async Task The_single_row_exists_before_anything_is_saved()
    {
        await using var harness = TelegramTestHarness.Create();

        // Seeded by the migration, empty. The screen an operator opens because nothing is configured
        // is the screen that must come up when nothing is configured.
        var rows = await harness.Db.TelegramBotSettings.AsNoTracking().ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].Id.Should().Be(Core.Telegram.TelegramBotSettings.SingletonId);
        rows[0].BotTokenProtected.Should().BeNull();

        var read = await harness.Bot().ReadAsync(CancellationToken.None);

        read.HasToken.Should().BeFalse();
        read.UpdatedAt.Should().BeNull("nothing has been saved, so there is no last change to show");
    }

    [Fact]
    public async Task The_operator_view_counts_and_cannot_list()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var linked = harness.SeedUser(tenant.Id);
        var pending = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(linked.Id, 810_001);
        await harness.Links().StartAsync(pending.Id, CancellationToken.None);

        var health = await harness.OperatorView().ReadAsync(CancellationToken.None);

        health.LinkedAccounts.Should().Be(1);
        health.PendingRequests.Should().Be(1);

        // The absence is the point: a cross-tenant directory of customers' messenger identities is a
        // privacy surface with no product use behind it, and the read model cannot express one.
        typeof(TelegramOperatorHealth).GetProperties()
            .Should().OnlyContain(p => p.PropertyType == typeof(int));

        // The transport slice added the Bot API server's own numbers beside these counts. Nothing on
        // it names a customer either — a base URL, a byte total, a file count, an age and free space
        // — so the rule is restated as a shape rather than as a count of methods: every value either
        // read model can produce is a number, a flag, a timespan or a path this process owns.
        typeof(TelegramServerHealth).GetProperties()
            .Should().NotContain(
                p => p.PropertyType != typeof(string)
                     && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType),
                "a collection on this record is the directory the design refuses to build");

        // And the interface still cannot express a listing: neither method returns a collection.
        typeof(ITelegramOperatorView).GetMethods()
            .Should().OnlyContain(m => !typeof(System.Collections.IEnumerable).IsAssignableFrom(
                m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : m.ReturnType));
    }
}
