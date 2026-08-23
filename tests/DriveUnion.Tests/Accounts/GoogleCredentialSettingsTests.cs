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
/// The settings the operator types in, and the rule that decides which of two sources wins.
///
/// The rule is the whole point of this file: a deployment that puts <c>Google:ClientId</c> in the
/// environment must not have it silently replaced by something typed into a web form. The reverse —
/// a form that quietly loses to an environment variable nobody mentioned — is just as bad, so the
/// screen is also required to say when it is happening.
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
        var store = new FakeCredentialStore();
        store.Save(PanelClientId, PanelSecret, PanelRedirectUri);

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
            new FakeCredentialStore(),
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
                // Its own file, so this test cannot write into the test host's content root.
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
        setup.StoredSecretIsSet.Should().BeTrue();

        // Every string the view model carries, checked as a set: the secret is not in any of them,
        // so no future change to the template can start rendering one.
        foreach (var value in new[]
                 {
                     setup.ClientId,
                     setup.RedirectUri,
                     setup.SuggestedRedirectUri,
                     setup.FormClientId,
                     setup.FormRedirectUri,
                     setup.StoredUpdatedText ?? string.Empty,
                 })
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

        // And the form is pre-filled with it, so a first-time operator's only action is «ذخیره».
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

    [Fact]
    public async Task The_form_is_pre_filled_from_the_panels_own_copy_and_not_from_the_environment()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        var setup = (await PageAsync(harness)).Setup;

        // Typing over a box that showed the environment's value would do nothing, which is the most
        // confusing form a form can take.
        setup.FormClientId.Should().Be(PanelClientId);
        setup.ClientId.Should().Be(ConnectFlowHarness.ClientId);
        setup.ConfigurationOutranksPanel.Should().BeTrue();
    }

    [Fact]
    public async Task With_nothing_anywhere_the_screen_still_renders_and_says_it_is_incomplete()
    {
        var harness = ConnectFlowHarness.Create(configured: false);

        var page = await PageAsync(harness);

        page.ConsentConfigured.Should().BeFalse();
        page.Setup.IsComplete.Should().BeFalse();
        page.Setup.HasStoredClient.Should().BeFalse();
        page.Setup.SecretIsSet.Should().BeFalse();
        page.Setup.ClientId.Should().BeEmpty();
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

    [Fact]
    public void The_secret_may_be_left_blank_once_one_is_stored()
    {
        var harness = ConnectFlowHarness.Create(configured: false);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        Save(harness, "corrected.apps.googleusercontent.com", clientSecret: null, PanelRedirectUri);

        harness.TempData["Error"].Should().BeNull();
        harness.Credentials.Value.ClientId.Should().Be("corrected.apps.googleusercontent.com");
        harness.Credentials.Value.ClientSecret.Should().Be(PanelSecret);
    }

    [Fact]
    public void Clearing_takes_the_panels_copy_away_and_leaves_configuration_alone()
    {
        var harness = ConnectFlowHarness.Create(configured: true);
        Save(harness, PanelClientId, PanelSecret, PanelRedirectUri);

        harness.Controller.ClearGoogleCredentials().Should().BeOfType<RedirectToActionResult>();

        harness.Credentials.Describe().Stored.Should().BeNull();
        harness.Credentials.Value.ClientId.Should().Be(ConnectFlowHarness.ClientId);
    }

    private static IActionResult Save(
        ConnectFlowHarness harness,
        string clientId,
        string? clientSecret,
        string redirectUri) =>
        harness.Controller.SaveGoogleCredentials(new GoogleCredentialsForm
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = redirectUri,
        });

    private static async Task<AccountsPageViewModel> PageAsync(ConnectFlowHarness harness) =>
        ConnectFlowHarness.PageModel(await harness.Controller.Index(CancellationToken.None));

    private static GoogleOAuthCredentialResolver Resolver(
        FakeCredentialStore store,
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
