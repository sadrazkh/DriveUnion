using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The two-leg flow: the panel hands out a deep link, the bot answers it with six digits, and the
/// authenticated panel request that carries those digits back is the only thing that writes a
/// binding.
/// </summary>
public class TelegramLinkingTests
{
    private const long Sender = 500_100_200;

    [Fact]
    public async Task Neither_the_raw_token_nor_the_raw_code_is_anywhere_in_the_database()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        var code = presented.ConfirmationCode!;

        var dump = await harness.DumpAsync();

        // This table is otherwise a table of live credentials. A database dump, a support query
        // pasted into a ticket, or a logged result set must not be a set of working keys.
        dump.Should().NotContain(token, "only the SHA-256 of the deep-link token is stored");
        dump.Should().NotContain(code, "the six digits are stored salted with the row id and hashed");

        // And the bot's own token is not sitting in its column either.
        dump.Should().NotContain("123456789:AAHtestTokenValue");

        // The hashes really are there, so the assertions above are not passing on an empty table.
        var row = await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync();
        row.TokenHash.Should().Be(TelegramLinkSecrets.HashToken(token));
        row.ConfirmationCodeHash.Should().Be(TelegramLinkSecrets.HashConfirmationCode(row.Id, code));
    }

    [Fact]
    public async Task A_forwarded_deep_link_alone_binds_nothing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        // A stranger's Telegram account opens the link. This is the screenshot-into-a-support-chat
        // case the second leg exists for.
        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, 999_999, 999_999, "stranger", "Stranger", "en"),
            CancellationToken.None);

        presented.Status.Should().Be(TelegramStartStatus.CodeIssued);
        presented.ConfirmationCode.Should().NotBeNullOrEmpty();

        // Six digits, and nothing else. Finishing needs the settings page of the account being
        // bound, and the stranger does not have it.
        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(0);

        var row = await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync();
        row.PresentedTelegramUserId.Should().Be(999_999);
        row.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_reply_says_where_to_start_and_names_nobody()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);
        var links = harness.Links();

        // Never linked.
        var neverLinked = await links.PresentAsync(
            new TelegramStartRequest(null, 1_000_001, 1_000_001, null, null, null),
            CancellationToken.None);

        // Linked and then unlinked.
        await harness.LinkAsync(user.Id, 1_000_002);
        await links.UnlinkAsync(user.Id, TelegramUnlinkReason.Customer, CancellationToken.None);

        var unlinked = await links.PresentAsync(
            new TelegramStartRequest(null, 1_000_002, 1_000_002, null, null, null),
            CancellationToken.None);

        // Belonged to a panel user who has been removed.
        var gone = harness.SeedUser(tenant.Id);
        await harness.LinkAsync(gone.Id, 1_000_003);
        harness.Db.Users.Remove(harness.Db.Users.Single(u => u.Id == gone.Id));
        harness.Db.SaveChanges();

        var deleted = await links.PresentAsync(
            new TelegramStartRequest(null, 1_000_003, 1_000_003, null, null, null),
            CancellationToken.None);

        // Byte-identical, all three. A bot that answered "your account was disconnected" to one and
        // "unknown account" to another is an oracle for which Telegram accounts are customers of
        // this service, and the whole id space is enumerable by anyone who can message the bot.
        neverLinked.ReplyText.Should().Be(TelegramMessages.Stranger);
        unlinked.ReplyText.Should().Be(TelegramMessages.Stranger);
        deleted.ReplyText.Should().Be(TelegramMessages.Stranger);

        TelegramMessages.Stranger.Should().NotContain(tenant.Name);
        TelegramMessages.Stranger.Should().NotContain("گوگل");
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_consumes_nothing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        harness.Clock.Advance(TelegramLinkService.TokenLifetime + TimeSpan.FromSeconds(1));

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        presented.Status.Should().Be(TelegramStartStatus.TokenNotUsable);
        presented.ReplyText.Should().Be(TelegramMessages.TokenNotUsable);
        presented.ConfirmationCode.Should().BeNull();

        var row = await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync();
        row.ConsumedAt.Should().BeNull("nothing was spent, so nothing was consumed");
        row.PresentedAt.Should().BeNull();
        row.ConfirmationCodeHash.Should().BeNull();
    }

    [Fact]
    public async Task An_already_consumed_token_is_refused_and_consumes_nothing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        await harness.Links().ConfirmAsync(user.Id, presented.ConfirmationCode, CancellationToken.None);

        var consumedAt = (await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync()).ConsumedAt;
        consumedAt.Should().NotBeNull();

        // The same link, opened again by somebody else.
        var again = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, 424_242, 424_242, null, null, null),
            CancellationToken.None);

        again.Status.Should().Be(TelegramStartStatus.TokenNotUsable);

        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(1);
        (await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync()).ConsumedAt
            .Should().Be(consumedAt, "a refused presentation must not touch the row it refused");
    }

    [Fact]
    public async Task An_unknown_token_is_refused_and_consumes_nothing()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        await harness.Links().StartAsync(user.Id, CancellationToken.None);

        // Well-formed, and not the one that was issued.
        var wrong = await harness.Links().PresentAsync(
            new TelegramStartRequest(TelegramLinkSecrets.NewToken(), Sender, Sender, null, null, null),
            CancellationToken.None);

        // And one that is not even the right shape, which is refused before any query runs.
        var malformed = await harness.Links().PresentAsync(
            new TelegramStartRequest("not-a-token", Sender, Sender, null, null, null),
            CancellationToken.None);

        wrong.Status.Should().Be(TelegramStartStatus.TokenNotUsable);
        malformed.Status.Should().Be(TelegramStartStatus.TokenNotUsable);

        var row = await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync();
        row.PresentedAt.Should().BeNull();
        row.ConsumedAt.Should().BeNull();
    }

    /// <summary>
    /// Two scopes, each with its own change tracker and its own snapshot of the token — which is
    /// what "simultaneous" means to a database.
    ///
    /// SQLite cannot run two write transactions against one in-memory database at the same instant,
    /// so the interleaving is staged rather than raced for: the second scope reads the token while
    /// it is still live, the first scope then consumes it and binds, and the second scope goes on to
    /// attempt its own write against a row that has moved underneath it. That is precisely the state
    /// a real race produces, and the only thing standing in the way at that point is the conditional
    /// UPDATE — the lookup has already said yes.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_confirmations_produce_exactly_one_binding()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        var code = presented.ConfirmationCode!;

        var firstScope = harness.NewContext();
        var secondScope = harness.NewContext();

        // The second scope reads the live token: the snapshot a second browser tab is holding at the
        // moment the first one presses the button.
        var seenLive = await secondScope.TelegramLinkTokens.SingleAsync(t => t.AppUserId == user.Id);
        seenLive.ConsumedAt.Should().BeNull();

        var first = await harness.Links(firstScope).ConfirmAsync(user.Id, code, CancellationToken.None);
        var second = await harness.Links(secondScope).ConfirmAsync(user.Id, code, CancellationToken.None);

        first.Status.Should().Be(TelegramConfirmStatus.Linked);
        second.Status.Should().Be(
            TelegramConfirmStatus.TokenDead,
            "the conditional UPDATE affected no rows, so the second confirmation owns nothing");

        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_attempt_budget_kills_the_token()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        var code = presented.ConfirmationCode!;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt < TelegramLinkToken.MaxAttempts; attempt++)
        {
            var outcome = await harness.Links().ConfirmAsync(user.Id, wrong, CancellationToken.None);

            outcome.Status.Should().Be(TelegramConfirmStatus.WrongCode);
            outcome.AttemptsLeft.Should().Be(TelegramLinkToken.MaxAttempts - attempt);
        }

        var last = await harness.Links().ConfirmAsync(user.Id, wrong, CancellationToken.None);

        last.Status.Should().Be(TelegramConfirmStatus.TokenDead);
        last.AttemptsLeft.Should().Be(0);

        // And now the right code is worth nothing either — which is the point of a budget.
        var correct = await harness.Links().ConfirmAsync(user.Id, code, CancellationToken.None);

        correct.Status.Should().Be(TelegramConfirmStatus.TokenDead);
        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Re_opening_the_deep_link_does_not_refresh_the_attempt_budget()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        await harness.Links().ConfirmAsync(user.Id, "000000", CancellationToken.None);
        await harness.Links().ConfirmAsync(user.Id, "111111", CancellationToken.None);

        // A second /start on the same token issues a fresh code and leaves the spent attempts spent.
        var again = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, null, null, null),
            CancellationToken.None);

        again.Status.Should().Be(TelegramStartStatus.CodeIssued);

        var outcome = await harness.Links().ConfirmAsync(user.Id, "222222", CancellationToken.None);

        outcome.AttemptsLeft.Should().Be(
            TelegramLinkToken.MaxAttempts - 3,
            "otherwise five guesses becomes as many as the guesser cares to ask for");
    }

    [Fact]
    public async Task A_second_telegram_account_cannot_bind_to_a_user_who_already_has_one()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(user.Id, 600_001);

        // The panel refuses to issue a second link at all.
        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);

        start.Status.Should().Be(TelegramLinkStartStatus.AlreadyLinked);
        start.DeepLink.Should().BeNull();

        // And the index refuses it even when the row is written straight past the flow.
        harness.Db.TelegramAccounts.Add(new TelegramAccount
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            TelegramUserId = 600_002,
            ChatId = 600_002,
            LinkedAt = TelegramTestHarness.Now,
            LastSeenAt = TelegramTestHarness.Now,
        });

        var direct = () => harness.Db.SaveChanges();
        direct.Should().Throw<DbUpdateException>("AppUserId is uniquely indexed");

        harness.Db.ChangeTracker.Clear();
        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_second_user_cannot_bind_to_a_telegram_account_that_is_taken()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var first = harness.SeedUser(tenant.Id);
        var second = harness.SeedUser(tenant.Id);

        await harness.LinkAsync(first.Id, 700_001);

        // The same Telegram account opens the second customer's deep link.
        var start = await harness.Links().StartAsync(second.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, 700_001, 700_001, null, null, null),
            CancellationToken.None);

        presented.Status.Should().Be(TelegramStartStatus.AlreadyBoundElsewhere);
        presented.ReplyText.Should().Be(TelegramMessages.AlreadyBoundElsewhere);
        presented.ConfirmationCode.Should().BeNull("no code is issued, so nothing can be confirmed");

        var confirm = await harness.Links().ConfirmAsync(second.Id, "123456", CancellationToken.None);

        confirm.Status.Should().Be(TelegramConfirmStatus.NotPresented);
        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(1);

        var identity = await harness.Identities().ResolveAsync(700_001, CancellationToken.None);
        identity!.AppUserId.Should().Be(first.Id, "the account still belongs to whoever bound it");
    }

    /// <summary>
    /// The bot's leg is the only moment anything hears from Telegram, and the panel's leg — minutes
    /// later, with no contact with Telegram at all — is what writes the row. So the profile has to be
    /// carried across on the token, and this is the test that says it was.
    ///
    /// Dropping it is invisible until somebody looks at the card: the binding works, the resolver
    /// resolves, and the customer's settings page renders a blank name over a language the bot will
    /// later have to guess.
    /// </summary>
    [Fact]
    public async Task The_binding_keeps_the_name_and_language_the_bot_presented()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, "@reza_files", "رضا محمدی", "fa"),
            CancellationToken.None);

        // Parked on the token first, because nothing can be written to TelegramAccount yet.
        var parked = await harness.Db.TelegramLinkTokens.AsNoTracking().SingleAsync();
        parked.PresentedUsername.Should().Be("reza_files", "the leading @ is not part of the handle");
        parked.PresentedDisplayName.Should().Be("رضا محمدی");
        parked.PresentedLanguageCode.Should().Be("fa");

        await harness.Links().ConfirmAsync(user.Id, presented.ConfirmationCode, CancellationToken.None);

        var account = await harness.Db.TelegramAccounts.AsNoTracking().SingleAsync();

        account.Username.Should().Be("reza_files");
        account.DisplayName.Should().Be("رضا محمدی");
        account.LanguageCode.Should().Be("fa");
        account.ChatId.Should().Be(Sender);

        // And the customer's card can therefore render a name rather than a placeholder.
        var state = await harness.Links().DescribeAsync(user.Id, CancellationToken.None);
        state.Linked!.DisplayName.Should().Be("رضا محمدی");
        state.Linked.Username.Should().Be("reza_files");
    }

    [Fact]
    public async Task A_presented_profile_longer_than_its_column_is_clipped_rather_than_thrown()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);

        // These three strings come from a third party. An over-long display name must not become a
        // failed insert on the one request in the flow that a customer is watching.
        var presented = await harness.Links().PresentAsync(
            new TelegramStartRequest(token, Sender, Sender, new string('u', 80), new string('n', 400), "fa-IR"),
            CancellationToken.None);

        await harness.Links().ConfirmAsync(user.Id, presented.ConfirmationCode, CancellationToken.None);

        var account = await harness.Db.TelegramAccounts.AsNoTracking().SingleAsync();

        account.Username.Should().HaveLength(32);
        account.DisplayName.Should().HaveLength(256);
        account.LanguageCode.Should().Be("fa-IR");
    }

    [Fact]
    public async Task Confirming_before_the_bot_has_seen_the_link_says_so()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        await harness.Links().StartAsync(user.Id, CancellationToken.None);

        var outcome = await harness.Links().ConfirmAsync(user.Id, "123456", CancellationToken.None);

        outcome.Status.Should().Be(TelegramConfirmStatus.NotPresented);
        (await harness.Db.TelegramAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Starting_is_refused_when_there_is_no_bot_or_no_tenant()
    {
        await using var harness = TelegramTestHarness.Create();

        var tenant = harness.SeedTenant();
        var customer = harness.SeedUser(tenant.Id);
        var staff = harness.SeedUser(null, isOperator: true);

        // Nothing configured yet: the card must say so rather than draw a button that fails.
        var unconfigured = await harness.Links().StartAsync(customer.Id, CancellationToken.None);
        unconfigured.Status.Should().Be(TelegramLinkStartStatus.BotNotConfigured);

        await harness.SeedBotAsync();

        var operatorStart = await harness.Links().StartAsync(staff.Id, CancellationToken.None);
        operatorStart.Status.Should().Be(TelegramLinkStartStatus.NoTenant);

        (await harness.Db.TelegramLinkTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Starting_again_replaces_the_previous_request()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var first = await harness.Links().StartAsync(user.Id, CancellationToken.None);
        var second = await harness.Links().StartAsync(user.Id, CancellationToken.None);

        first.DeepLink.Should().NotBe(second.DeepLink);
        (await harness.Db.TelegramLinkTokens.CountAsync()).Should().Be(1, "one live request per user");

        // The old link is dead the moment the new one exists.
        var stale = await harness.Links().PresentAsync(
            new TelegramStartRequest(
                TelegramTestHarness.TokenOf(first.DeepLink!), Sender, Sender, null, null, null),
            CancellationToken.None);

        stale.Status.Should().Be(TelegramStartStatus.TokenNotUsable);
    }

    [Fact]
    public async Task The_sweeper_reports_what_it_deleted()
    {
        await using var harness = TelegramTestHarness.Create();
        await harness.SeedBotAsync();

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        // Nothing to do yet, and it must say zero rather than look busy.
        (await harness.Links().SweepAsync(CancellationToken.None)).Should().Be(0);

        await harness.Links().StartAsync(user.Id, CancellationToken.None);

        (await harness.Links().SweepAsync(CancellationToken.None)).Should().Be(
            0,
            "a live request is not rubbish");

        harness.Clock.Advance(TelegramLinkService.TokenLifetime + TimeSpan.FromMinutes(1));

        // A sweeper that deletes nothing must not be indistinguishable from one that had nothing to
        // do, so the count is the answer rather than a log line.
        (await harness.Links().SweepAsync(CancellationToken.None)).Should().Be(1);
        (await harness.Db.TelegramLinkTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_deep_link_is_a_telegram_start_link_for_the_configured_bot()
    {
        await using var harness = TelegramTestHarness.Create();
        var username = await harness.SeedBotAsync("AcmeFilesBot");

        var tenant = harness.SeedTenant();
        var user = harness.SeedUser(tenant.Id);

        var start = await harness.Links().StartAsync(user.Id, CancellationToken.None);

        start.Status.Should().Be(TelegramLinkStartStatus.Issued);
        start.DeepLink.Should().StartWith($"https://t.me/{username}?start=");
        start.ExpiresAt.Should().Be(TelegramTestHarness.Now + TelegramLinkService.TokenLifetime);

        // 32 bytes as base64url, which fits Telegram's documented 64-character start parameter with
        // room to spare and needs no re-encoding on the way through a URL.
        var token = TelegramTestHarness.TokenOf(start.DeepLink!);
        token.Should().HaveLength(TelegramLinkSecrets.TokenLength);
        TelegramLinkSecrets.IsWellFormedToken(token).Should().BeTrue();
    }
}
