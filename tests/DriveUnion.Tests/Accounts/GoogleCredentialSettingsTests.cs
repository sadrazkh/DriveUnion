using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Web.Models;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The settings the operator types in, the rule that decides which of two sources wins, and — since
/// the panel can hold more than one client — which client a given account is refreshed with.
///
/// The precedence rule is the older half of this file: a deployment that puts <c>Google:ClientId</c>
/// in the environment must not have it silently replaced by something typed into a web form, and the
/// reverse is just as bad, so the screen is required to say when it is happening.
///
/// The newer half is the binding. A refresh token can only be presented by the client that issued
/// it, so "which client is in force" and "which client refreshes this account" are two different
/// questions with two different answers, and conflating them is the failure that looks like working
/// multi-client support until the first hour elapses.
/// </summary>
public class GoogleCredentialSettingsTests
{
    private const string PanelClientId = "typed-into-the-panel.apps.googleusercontent.com";
    private const string PanelSecret = "GOCSPX-typed-into-the-panel";
    private const string PanelRedirectUri = "https://typed.example.test/accounts/callback";

    [Fact]
    public void A_client_saved_in_the_panel_is_in_force_on_the_very_next_read()
    {
        // Nothing in configuration: this machine, and the box the owner actually has.
        var harness = ConnectFlowHarness.Create(configured: false);

        harness.Credentials.Value.IsConfigured().Should().BeFalse();

        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        // No restart, no rebinding, no second container. The options are resolved per read, which is
        // the only reason a form on a running panel can configure OAuth at all.
        var options = harness.Credentials.Value;
        options.IsConfigured().Should().BeTrue();
        options.ClientId.Should().Be(PanelClientId);
        options.ClientSecret.Should().Be(PanelSecret);
        options.RedirectUri.Should().Be(PanelRedirectUri);
    }

    [Fact]
    public void A_client_saved_in_the_panel_is_what_the_consent_URL_carries()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var redirect = harness.Controller.Connect(popup: false).Should().BeOfType<RedirectResult>().Subject;
        var query = QueryHelpers.ParseQuery(new Uri(redirect.Url).Query);

        query["client_id"].ToString().Should().Be(PanelClientId);
        query["redirect_uri"].ToString().Should().Be(PanelRedirectUri);
    }

    /// <summary>
    /// The rule, field by field. Configuration is the deployment's statement of record; a form on a
    /// web page must not beat it, or an environment variable somebody set at three in the morning
    /// looks broken and the box is sending Google a client id nobody can find.
    /// </summary>
    [Fact]
    public void Configuration_outranks_everything_typed_into_the_panel()
    {
        var harness = ConnectFlowHarness.Create(configured: true);

        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var options = harness.Credentials.Value;
        options.ClientId.Should().Be(ConnectFlowHarness.ClientId);
        options.ClientSecret.Should().Be(ConnectFlowHarness.ClientSecret);
        options.RedirectUri.Should().Be(ConnectFlowHarness.RedirectUri);

        var state = harness.Credentials.Describe();
        state.ClientId.Source.Should().Be(GoogleCredentialSource.Configuration);
        state.ClientSecretSource.Should().Be(GoogleCredentialSource.Configuration);
        state.RedirectUri.Source.Should().Be(GoogleCredentialSource.Configuration);

        // And the panel is still holding what was typed, unharmed — removing the environment
        // variable brings it back rather than leaving the deployment with nothing.
        state.Stored!.ClientId.Should().Be(PanelClientId);
        state.ConfigurationOutranksPanel.Should().BeTrue();
    }

    [Fact]
    public void Being_outranked_is_said_out_loud_rather_than_left_to_be_discovered()
    {
        var harness = ConnectFlowHarness.Create(configured: true);

        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        harness.TempData["Notice"].Should().Be(
            "اطلاعات ذخیره شد، اما پیکربندی سرور اولویت دارد و همان اعمال می‌شود.");
    }

    /// <summary>
    /// <c>appsettings.Development.json</c> ships <c>"ClientId": ""</c> to document that the key
    /// exists. A present-but-blank key counting as configured would make this machine permanently
    /// unable to save anything from the screen.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configuration_value_is_not_a_configuration_value(string blank)
    {
        var store = new FakeGoogleOAuthClientStore();
        store.Save(id: null, PanelClientId, PanelSecret, PanelRedirectUri);

        var resolver = Resolver(store, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ClientId"] = blank,
            ["ClientSecret"] = blank,
            ["RedirectUri"] = blank,
        });

        resolver.Value.ClientId.Should().Be(PanelClientId);
        resolver.Describe().ClientId.Source.Should().Be(GoogleCredentialSource.Panel);
    }

    /// <summary>
    /// A client id pasted with a trailing newline — out of an env file, out of a terminal, out of
    /// Google's own console — is otherwise an <c>invalid_client</c> that shows nothing wrong on
    /// screen because the screen cannot draw whitespace.
    /// </summary>
    [Fact]
    public void Whitespace_around_a_value_is_not_part_of_the_value()
    {
        var resolver = Resolver(
            new FakeGoogleOAuthClientStore(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ClientId"] = $"  {PanelClientId}\n",
                ["ClientSecret"] = $"\t{PanelSecret} ",
                ["RedirectUri"] = $" {PanelRedirectUri} ",
            });

        var options = resolver.Value;
        options.ClientId.Should().Be(PanelClientId);
        options.ClientSecret.Should().Be(PanelSecret);
        options.RedirectUri.Should().Be(PanelRedirectUri);
    }

    [Fact]
    public void A_stored_secret_that_no_longer_decrypts_counts_as_no_secret()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        // The Data Protection key that wrote it is gone. There is no partial answer here: an OAuth
        // client without a usable secret cannot exchange a code, so the screen must not claim one.
        harness.Store.SecretIsUnreadable = true;

        harness.Credentials.Value.IsConfigured().Should().BeFalse();
        harness.Credentials.Describe().ClientSecretSource.Should().Be(GoogleCredentialSource.None);
    }

    // ────────────────────────────────────────── which client refreshes which account

    /// <summary>
    /// The lookup the whole multi-client story rests on. An account connected under one client
    /// cannot be refreshed by another — Google answers <c>invalid_grant</c>, which this codebase
    /// turns into "reconnect this account" — so the refresh asks for a client by id rather than for
    /// whatever is in force.
    /// </summary>
    [Fact]
    public void A_stored_client_can_be_resolved_by_its_own_client_id_even_when_another_is_in_force()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);
        Save(harness, "second.apps.googleusercontent.com", "GOCSPX-second", PanelRedirectUri);

        // The first is still what a new connection would use — adding a client must not move that.
        harness.Credentials.Value.ClientId.Should().Be(PanelClientId);

        var second = harness.Credentials.ForClientId("second.apps.googleusercontent.com");

        second.Should().NotBeNull();
        second!.ClientSecret.Should().Be(
            "GOCSPX-second",
            "an account connected under the second client is refreshed with the second client's secret");
    }

    /// <summary>
    /// The configured client has no row, and an account connected under it still has to be
    /// refreshable — so the lookup answers for it too, and answers for it first.
    /// </summary>
    [Fact]
    public void The_configured_client_answers_for_its_own_id_and_a_stored_row_cannot_shadow_it()
    {
        var harness = ConnectFlowHarness.Create(configured: true);

        // The same client id, saved into the panel with a different secret. Configuration wins.
        Save(harness, ConnectFlowHarness.ClientId, "GOCSPX-typed-over-the-environment", PanelRedirectUri);

        var resolved = harness.Credentials.ForClientId(ConnectFlowHarness.ClientId);

        resolved.Should().NotBeNull();
        resolved!.ClientSecret.Should().Be(ConnectFlowHarness.ClientSecret);
    }

    [Fact]
    public void A_client_id_nothing_holds_resolves_to_nothing_rather_than_to_the_wrong_client()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        harness.Credentials.ForClientId("removed-last-week.apps.googleusercontent.com").Should().BeNull();
    }

    [Fact]
    public void A_stored_client_whose_secret_no_longer_decrypts_resolves_to_nothing()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        harness.Store.SecretIsUnreadable = true;

        harness.Credentials.ForClientId(PanelClientId).Should().BeNull(
            "half a client cannot refresh anything, and pretending otherwise is a 401 an hour later");
    }

    /// <summary>
    /// The mechanism the whole feature rests on, pinned in the container.
    ///
    /// <c>AddOptions().Bind()</c> would compute <see cref="GoogleOAuthOptions"/> once while the
    /// container is being built, and the operator's client id arrives long after that — by hand,
    /// into a running panel. If the ordinary options pipeline ever won this registration back,
    /// saving from the screen would appear to work and would take effect at the next restart, which
    /// is the kind of failure nobody reproduces.
    /// </summary>
    [Fact]
    public void Everything_that_asks_for_the_options_gets_the_resolver_that_reads_live()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // A path that does not exist, so the one-time import of the retired JSON file walks
                // straight back out and this test never touches the database it has no schema for.
                [$"{GoogleOAuthOptions.SectionName}:CredentialStorePath"] =
                    Path.Combine(Path.GetTempPath(), $"driveunion-di-{Guid.NewGuid():N}.json"),
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddGoogleDrive(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        var options = provider.GetRequiredService<IOptions<GoogleOAuthOptions>>();

        options.Should().BeOfType<GoogleOAuthCredentialResolver>();

        // One object, so what the panel writes is what the token service reads.
        provider.GetRequiredService<IGoogleOAuthCredentials>().Should().BeSameAs(options);
    }

    // ─────────────────────────────────────────────────────────────────── what the screen shows

    [Fact]
    public async Task The_secret_is_never_part_of_what_the_screen_is_given()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var setup = (await PageAsync(harness)).Setup;

        setup.SecretIsSet.Should().BeTrue();
        setup.Clients.Should().ContainSingle().Which.SecretIsSet.Should().BeTrue();

        // Every string the view model carries, checked as a set: the secret is not in any of them,
        // so no future change to the template can start rendering one.
        var rendered = new[]
        {
            setup.ClientId,
            setup.RedirectUri,
            setup.SuggestedRedirectUri,
            setup.FormRedirectUri,
        }.Concat(setup.Clients.SelectMany(c => new[]
        {
            c.Label,
            c.ClientId,
            c.RedirectUri,
            c.UpdatedText,
        }));

        foreach (var value in rendered)
        {
            value.Should().NotContain(PanelSecret);
        }
    }

    /// <summary>
    /// The redirect URI is the one string the operator must copy character-perfect into Google
    /// Cloud, and Google answers a mismatch with nothing anybody can debug from. So the panel builds
    /// it out of the address it is being viewed at rather than asking the operator to assemble one.
    /// </summary>
    [Fact]
    public async Task The_suggested_redirect_uri_is_this_panels_own_address()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var setup = (await PageAsync(harness)).Setup;

        setup.SuggestedRedirectUri.Should().Be(ConnectFlowHarness.SuggestedRedirectUri);

        // With nothing configured the screen shows the suggestion as the value to register — never a
        // placeholder, and never blank.
        setup.RedirectUri.Should().Be(ConnectFlowHarness.SuggestedRedirectUri);
        setup.RedirectUriSource.Should().Be(GoogleCredentialSource.None);

        // And the «add» form is pre-filled with it, so a first-time operator's only action is «ذخیره».
        setup.FormRedirectUri.Should().Be(ConnectFlowHarness.SuggestedRedirectUri);
    }

    [Fact]
    public async Task A_configured_redirect_uri_is_shown_instead_of_the_suggestion()
    {
        var harness = ConnectFlowHarness.Create(configured: true);

        var setup = (await PageAsync(harness)).Setup;

        setup.RedirectUri.Should().Be(ConnectFlowHarness.RedirectUri);
        setup.RedirectUriSource.Should().Be(GoogleCredentialSource.Configuration);

        // The suggestion is still offered, because it is what this address would need — the screen
        // then says the two differ rather than letting the operator register the wrong one.
        setup.SuggestedRedirectUri.Should().Be(ConnectFlowHarness.SuggestedRedirectUri);
    }

    /// <summary>
    /// Each stored client is edited on its own row, pre-filled with its own values. Typing over a
    /// box that showed the environment's value would do nothing, which is the most confusing form a
    /// form can take — and with several clients there is no single "the panel's copy" to show.
    /// </summary>
    [Fact]
    public async Task Every_stored_client_is_listed_with_its_own_values_whatever_is_in_force()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var setup = (await PageAsync(harness)).Setup;

        var row = setup.Clients.Should().ContainSingle().Subject;
        row.ClientId.Should().Be(PanelClientId);
        row.RedirectUri.Should().Be(PanelRedirectUri);
        row.IsDefault.Should().BeTrue();

        setup.ClientId.Should().Be(ConnectFlowHarness.ClientId, "the environment is what is in force");
        setup.ConfigurationOutranksPanel.Should().BeTrue();
        setup.ConfigurationSuppliesTheClient.Should().BeTrue();
    }

    [Fact]
    public async Task With_nothing_anywhere_the_screen_still_renders_and_says_it_is_incomplete()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var page = await PageAsync(harness);

        page.ConsentConfigured.Should().BeFalse();
        page.Setup.IsComplete.Should().BeFalse();
        page.Setup.SecretIsSet.Should().BeFalse();
        page.Setup.ClientId.Should().BeEmpty();
        page.Setup.Clients.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────── refusals

    [Theory]
    [InlineData("", PanelSecret, PanelRedirectUri, "شناسه‌ی کلاینت (Client ID) را وارد کنید.")]
    [InlineData(PanelClientId, "", PanelRedirectUri, "کلید محرمانه (Client Secret) را وارد کنید.")]
    [InlineData(PanelClientId, PanelSecret, "", "آدرس بازگشت (Redirect URI) را وارد کنید.")]
    [InlineData(PanelClientId, PanelSecret, "/accounts/callback", "آدرس بازگشت باید یک نشانی کامل با http یا https باشد.")]
    [InlineData(PanelClientId, PanelSecret, "ftp://example.test/cb", "آدرس بازگشت باید یک نشانی کامل با http یا https باشد.")]
    [InlineData(PanelClientId, PanelSecret, "https://example.test/cb#x", "آدرس بازگشت نباید بخش # داشته باشد.")]
    [InlineData(PanelClientId, PanelSecret, "http://drive.example/cb", "گوگل http را فقط برای localhost می‌پذیرد؛ برای بقیه‌ی آدرس‌ها https لازم است.")]
    public void What_Google_would_refuse_is_refused_here_where_the_form_is_still_on_screen(
        string clientId,
        string clientSecret,
        string redirectUri,
        string complaint)
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var result = Save(harness, clientId, clientSecret, redirectUri);

        harness.Store.SaveCalls.Should().Be(0);
        harness.TempData["Error"].Should().Be(complaint);
        result.Should().BeOfType<RedirectToActionResult>();
    }

    /// <summary>Google allows plain http for a loopback address, and only for one.</summary>
    [Fact]
    public void Localhost_over_http_is_accepted_because_Google_accepts_it()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        Save(harness, PanelClientId, PanelSecret, "http://localhost:7169/accounts/callback");

        harness.Credentials.Value.RedirectUri.Should().Be("http://localhost:7169/accounts/callback");
    }

    /// <summary>
    /// Adding a client always needs its own secret; only an edit of the client that already holds
    /// one may leave the field blank. A blank secret on an add would store a client that cannot
    /// exchange anything, and the operator would meet that at Google's screen.
    /// </summary>
    [Fact]
    public void A_second_client_cannot_borrow_the_first_ones_secret_by_leaving_the_field_blank()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        Save(harness, "second.apps.googleusercontent.com", clientSecret: null, PanelRedirectUri);

        harness.TempData["Error"].Should().Be("کلید محرمانه (Client Secret) را وارد کنید.");
        harness.Credentials.Describe().StoredClients.Should().ContainSingle();
    }

    [Fact]
    public void The_secret_may_be_left_blank_when_editing_the_client_that_holds_one()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var id = harness.Credentials.Describe().StoredClients.Single().Id;

        Save(harness, "corrected.apps.googleusercontent.com", clientSecret: null, PanelRedirectUri, id);

        harness.TempData["Error"].Should().BeNull();
        harness.Credentials.Value.ClientId.Should().Be("corrected.apps.googleusercontent.com");
        harness.Credentials.Value.ClientSecret.Should().Be(PanelSecret);
    }

    [Fact]
    public void Removing_a_client_takes_the_panels_copy_away_and_leaves_configuration_alone()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var id = harness.Credentials.Describe().StoredClients.Single().Id;

        harness.Controller.RemoveGoogleClient(id).Should().BeOfType<RedirectToActionResult>();

        harness.Credentials.Describe().Stored.Should().BeNull();
        harness.Credentials.Value.ClientId.Should().Be(ConnectFlowHarness.ClientId);
    }

    /// <summary>
    /// The refusal, in a sentence, naming the accounts. Removing a client accounts still depend on
    /// does not fail when it is pressed — it fails an hour later, on every one of them at once, as
    /// uploads reporting that storage is unavailable. Which is how this product lost its pool once.
    /// </summary>
    [Fact]
    public void Removing_a_client_accounts_still_need_is_refused_and_the_screen_names_them()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var id = harness.Credentials.Describe().StoredClients.Single().Id;
        harness.Store.DependentAccounts[PanelClientId] = ["A1", "A2"];

        harness.Controller.RemoveGoogleClient(id).Should().BeOfType<RedirectToActionResult>();

        harness.TempData["Error"].Should().NotBeNull();
        harness.TempData["Error"]!.ToString().Should().Contain("A1، A2");
        harness.Credentials.Describe().StoredClients.Should().ContainSingle("nothing was removed");
    }

    [Fact]
    public void Promoting_a_client_moves_what_the_next_connection_uses()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);
        Save(harness, "second.apps.googleusercontent.com", "GOCSPX-second", PanelRedirectUri);

        var second = harness.Credentials.Describe().StoredClients
            .Single(c => c.ClientId == "second.apps.googleusercontent.com");

        harness.Controller.UseGoogleClient(second.Id).Should().BeOfType<RedirectToActionResult>();

        harness.Credentials.Value.ClientId.Should().Be("second.apps.googleusercontent.com");
        harness.TempData["Notice"].Should().Be(
            "اتصال‌های بعدی با این کلاینت انجام می‌شود. اکانت‌های موجود دست‌نخورده می‌مانند.");
    }

    /// <summary>
    /// Promoting a stored client while the environment supplies one changes nothing about the next
    /// connection. An operator who was not told that would go looking for the fault in Google Cloud.
    /// </summary>
    [Fact]
    public void Promoting_a_client_that_configuration_outranks_says_so()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);
        Save(harness, "second.apps.googleusercontent.com", "GOCSPX-second", PanelRedirectUri);

        var second = harness.Credentials.Describe().StoredClients
            .Single(c => c.ClientId == "second.apps.googleusercontent.com");

        harness.Controller.UseGoogleClient(second.Id);

        harness.Credentials.Value.ClientId.Should().Be(ConnectFlowHarness.ClientId);
        harness.TempData["Notice"].Should().Be(
            "انتخاب شد، اما پیکربندی سرور کلاینت خودش را اعمال می‌کند و اتصال بعدی با همان انجام می‌شود.");
    }

    [Fact]
    public void One_client_id_cannot_be_saved_twice()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        Save(harness, PanelClientId, "GOCSPX-a-second-secret", PanelRedirectUri);

        harness.TempData["Error"].Should().Be(
            "این Client ID از قبل ذخیره شده است. همان ردیف را ویرایش کنید.");
        harness.Credentials.Describe().StoredClients.Should().ContainSingle();
    }

    private static IActionResult Save(
        ConnectFlowHarness harness,
        string clientId,
        string? clientSecret,
        string redirectUri,
        Guid? id = null) =>
        harness.Controller.SaveGoogleCredentials(new GoogleCredentialsForm
        {
            Id = id,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = redirectUri,
        });

    private static async Task<AccountsPageViewModel> PageAsync(ConnectFlowHarness harness) =>
        ConnectFlowHarness.PageModel(await harness.Controller.Index(CancellationToken.None));

    private static GoogleOAuthCredentialResolver Resolver(
        FakeGoogleOAuthClientStore store,
        Dictionary<string, string?> googleSection)
    {
        var settings = googleSection.ToDictionary(
            pair => $"{GoogleOAuthOptions.SectionName}:{pair.Key}",
            pair => pair.Value,
            StringComparer.Ordinal);

        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build()
            .GetSection(GoogleOAuthOptions.SectionName);

        return new GoogleOAuthCredentialResolver(section, store);
    }
}
