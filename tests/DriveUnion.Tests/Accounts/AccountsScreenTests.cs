using System.Net;
using System.Text.RegularExpressions;
using DriveUnion.Core.Storage;
using FluentAssertions;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// «اکانت‌های گوگل» with nothing in it, with one account and with three.
///
/// The screen used to spell «add an account» and «reconnect this one» with a single control at the
/// head of the page, so pressing it a second time was — to the operator — the same action they had
/// already taken, and the panel answered both with the same sentence. That is half of why a second
/// account looked impossible; the authorization URL was the other half, and it is pinned in
/// <c>GoogleOAuthUrlsTests</c>. These hold the screen's half: two actions, distinguishable, one per
/// card and one for the pool, all still operator-only and all still antiforgery-protected.
///
/// Nothing here reaches Google. The harness's IDriveClient throws on contact and every request below
/// either renders a page or stops at the redirect to the consent screen.
/// </summary>
public class AccountsScreenTests
{
    private const string AddAccount = "+ افزودن اکانت با OAuth";
    private const string AddAnother = "+ افزودن یک اکانت دیگر";

    [Fact]
    public async Task With_no_accounts_the_action_is_to_add_the_first_one_and_there_is_nothing_to_reconnect()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var content = MainContent(await client.GetStringAsync("/accounts"));

        content.Should().Contain(AddAccount);
        content.Should().NotContain(AddAnother, "there is no other account to be another one than");
        content.Should().NotContain("/reconnect", "a reconnect button belongs to a card, and there are none");
        content.Should().Contain("هیچ اکانتی متصل نیست");
    }

    /// <summary>
    /// One account, and the two actions are already distinct: the head of the page offers another
    /// account and says Google will ask which; the card offers this account's own repair.
    /// </summary>
    [Fact]
    public async Task With_one_account_adding_another_and_reconnecting_this_one_are_different_controls()
    {
        using var harness = new OperatorPanelHarness();
        var account = harness.SeedPool(("pool-a1@example.com", GoogleAccountStatus.Healthy))[0];

        using var client = harness.NewClient();
        var content = MainContent(await client.GetStringAsync("/accounts"));

        content.Should().Contain(AddAnother);
        content.Should().NotContain(AddAccount, "the label is about a pool that already has one in it");

        // The sentence that makes the chooser expected rather than surprising, and says what
        // choosing the account already in the list will do.
        content.Should().Contain("گوگل می‌پرسد کدام اکانت");

        content.Should().Contain($"action=\"/accounts/{account.Id}/reconnect\"");
        content.Should().Contain("اتصال دوباره");

        // The rest of the per-account surface, unchanged and still pointed at this account.
        content.Should().Contain($"action=\"/accounts/{account.Id}/refresh-quota\"");
        content.Should().Contain($"action=\"/accounts/{account.Id}/disconnect\"");

        content.Should().Contain("pool-a1@example.com").And.Contain("A1").And.Contain("سالم");
    }

    /// <summary>
    /// Three accounts: three cards, three sets of per-account controls aimed at three different ids,
    /// and exactly one control that adds a fourth. The count is the assertion — a reconnect form
    /// that rendered once for the whole list, or an add-another that rendered per card, would both
    /// read as "the buttons do something arbitrary".
    /// </summary>
    [Fact]
    public async Task With_three_accounts_every_card_carries_its_own_controls()
    {
        using var harness = new OperatorPanelHarness();
        var pool = harness.SeedPool(
            ("pool-a1@example.com", GoogleAccountStatus.Healthy),
            ("pool-a2@example.com", GoogleAccountStatus.Disconnected),
            ("pool-a3@example.com", GoogleAccountStatus.Healthy));

        using var client = harness.NewClient();
        var content = MainContent(await client.GetStringAsync("/accounts"));

        foreach (var account in pool)
        {
            content.Should().Contain($"action=\"/accounts/{account.Id}/reconnect\"");
            content.Should().Contain($"action=\"/accounts/{account.Id}/refresh-quota\"");
            content.Should().Contain($"action=\"/accounts/{account.Id}/disconnect\"");
            content.Should().Contain(account.Email);
        }

        Occurrences(content, "/reconnect\"").Should().Be(3);
        Occurrences(content, "action=\"/accounts/connect\"").Should().Be(1);

        // The label is how the operator tells three otherwise identical rows of buttons apart, so it
        // is what the accessible name of each one is built from.
        content.Should().Contain("اتصال دوباره‌ی اکانت A2");
        content.Should().Contain("قطع اتصال اکانت A3");

        // The disconnected account's card leads with its own repair rather than with the pool's
        // «add another», which would connect a different account entirely.
        content.Should().Contain("قطع شده");
    }

    /// <summary>
    /// Reconnecting names the account it is about. Google's chooser still opens — <c>select_account</c>
    /// is in the prompt — but it opens on this address rather than on whichever account the operator's
    /// browser session happens to hold, which is the thing that made the old flow unusable.
    /// </summary>
    [Fact]
    public async Task Reconnect_sends_the_account_address_to_Google_as_a_login_hint()
    {
        using var harness = new OperatorPanelHarness();
        var account = harness.SeedPool(("pool-a1@example.com", GoogleAccountStatus.Disconnected))[0];

        using var client = harness.NewClient();
        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            $"/accounts/{account.Id}/reconnect",
            Form(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = response.Headers.Location!;
        location.Host.Should().Be("accounts.google.com");

        var query = Uri.UnescapeDataString(location.Query);
        query.Should().Contain("login_hint=pool-a1@example.com");
        query.Should().Contain("prompt=select_account consent");

        // Same CSRF nonce discipline as the add-an-account flow: this is the same consent, started
        // from a different button.
        OperatorPanelHarness.IssuedState(response).Should().StartWith("top.");
    }

    /// <summary>
    /// Adding an account names none. A hint would tell Google which account to preselect, and the
    /// whole point of this control is that the operator is choosing a different one.
    /// </summary>
    [Fact]
    public async Task Adding_an_account_sends_no_login_hint()
    {
        using var harness = new OperatorPanelHarness();
        harness.SeedPool(("pool-a1@example.com", GoogleAccountStatus.Healthy));

        using var client = harness.NewClient();
        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/accounts/connect", Form(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Query.Should().NotContain("login_hint");
    }

    [Fact]
    public async Task Reconnecting_an_account_that_is_not_in_the_pool_says_so_and_starts_no_consent()
    {
        using var harness = new OperatorPanelHarness();
        using var client = harness.NewClient();

        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            $"/accounts/{Guid.CreateVersion7()}/reconnect",
            Form(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/accounts");
        OperatorPanelHarness.IssuedState(response).Should().BeNull("no consent was started");

        WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"))
            .Should().Contain("اکانت پیدا نشد.");
    }

    /// <summary>
    /// Three cards, three sets of buttons, and the one that was pressed is the one that acted. The
    /// screen renders the result, so this is also the per-account state test: A2 comes back «قطع
    /// شده» and the other two are untouched.
    /// </summary>
    [Fact]
    public async Task Disconnecting_the_middle_card_leaves_the_other_two_alone()
    {
        using var harness = new OperatorPanelHarness();
        var pool = harness.SeedPool(
            ("pool-a1@example.com", GoogleAccountStatus.Healthy),
            ("pool-a2@example.com", GoogleAccountStatus.Healthy),
            ("pool-a3@example.com", GoogleAccountStatus.Healthy));

        using var client = harness.NewClient();
        var token = await OperatorPanelHarness.AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync(
            $"/accounts/{pool[1].Id}/disconnect",
            Form(token, popup: "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var content = MainContent(await client.GetStringAsync("/accounts"));

        // Read off the cards rather than out of the database: what the operator has to be able to
        // trust is that the screen tells them which account they just took out of rotation.
        Status(content, pool[0].Id).Should().Be("سالم");
        Status(content, pool[1].Id).Should().Be("قطع شده");
        Status(content, pool[2].Id).Should().Be("سالم");

        // And the disconnected one still offers its repair, so the card is not a dead end.
        content.Should().Contain($"action=\"/accounts/{pool[1].Id}/reconnect\"");
    }

    [Fact]
    public async Task Reconnect_still_needs_the_antiforgery_token()
    {
        using var harness = new OperatorPanelHarness();
        var account = harness.SeedPool(("pool-a1@example.com", GoogleAccountStatus.Healthy))[0];

        using var client = harness.NewClient();

        using var response = await client.PostAsync(
            $"/accounts/{account.Id}/reconnect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["popup"] = "false" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        OperatorPanelHarness.IssuedState(response).Should().BeNull();
    }

    /// <summary>
    /// A customer must not reach the reconnect route either. It names an operator's Google address
    /// in a redirect to Google, which is precisely the fact M1 §1.4 says a tenant never learns.
    /// </summary>
    [Fact]
    public async Task A_customer_gets_403_from_reconnect()
    {
        using var harness = new OperatorPanelHarness(isOperator: false);
        using var client = harness.NewClient();

        using var response = await client.PostAsync(
            $"/accounts/{Guid.CreateVersion7()}/reconnect",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["popup"] = "false" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        OperatorPanelHarness.IssuedState(response).Should().BeNull();
    }

    private static FormUrlEncodedContent Form(string token, string popup) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["popup"] = popup,
        });

    /// <summary>
    /// The status badge on one account's card, found by the card that carries that account's own
    /// controls. Split on the card class rather than matched across the whole page: with three cards
    /// the page holds three badges, and a regex that reached past a card boundary would report a
    /// neighbour's state as this one's — which is the exact confusion being tested against.
    /// </summary>
    private static string Status(string content, Guid id)
    {
        var card = content
            .Split("class=\"card card--pad\"", StringSplitOptions.None)
            .Single(c => c.Contains($"/accounts/{id}/reconnect", StringComparison.Ordinal));

        var badge = Regex.Match(
            card,
            "<span class=\"badge[^\"]*\">\\s*([^<]*?)\\s*</span>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(badge.Success, $"The card for account {id} rendered no status badge.");

        return badge.Groups[1].Value;
    }

    private static int Occurrences(string content, string needle)
    {
        var count = 0;
        var at = content.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = content.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// The page's own markup, without the shell around it, and decoded — the same reading
    /// <c>GoogleConnectCallToActionTests</c> takes, and for the same reason: the layout renders
    /// navigation for milestones that have no controller yet, and Razor writes every Persian letter
    /// as a numeric entity.
    /// </summary>
    private static string MainContent(string html)
    {
        var match = Regex.Match(
            html,
            "<main class=\"app-content\">(.*)</main>",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, "The page rendered no <main class=\"app-content\"> region.");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
