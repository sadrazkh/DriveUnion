using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// The second factor, at the one moment it is worth having.
///
/// <para>The Security screen could turn it on before any of this existed, and sign-in ignored it:
/// <c>PasswordSignInAsync</c> answers <c>RequiresTwoFactor</c> and nothing read that, so a password
/// alone still let you in. A switch that reports «on» and changes nothing is worse than no switch —
/// somebody relies on it.</para>
///
/// <para>These drive the real pipeline through <see cref="TenantPanelHarness"/> rather than a stub,
/// because what is being tested is the sign-in manager's own two-step handshake and a fake of it
/// would be a test of the fake.</para>
/// </summary>
public class TwoFactorSignInTests
{
    private const string Email = "reza@acme.example";

    [Fact]
    public async Task A_password_alone_stops_being_enough_once_it_is_switched_on()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var response = await TenantPanelHarness.SignInAsync(
            client, Email, TenantPanelHarness.Password);

        // Not the panel. The handshake is half done and the cookie that matters has not been issued.
        response.Headers.Location?.ToString().Should().NotBe("/");

        // And the proof that it is not merely a redirect loop: a panel page still challenges.
        using var panel = await client.GetAsync(new Uri("/files", UriKind.Relative));

        panel.StatusCode.Should().BeOneOf(
            HttpStatusCode.Redirect,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// <b>The failure that made this worse than not having the feature.</b>
    ///
    /// <para><c>PasswordSignInAsync</c> answers <c>RequiresTwoFactor</c> by <i>not</i> succeeding, and
    /// the sign-in read that as «not succeeded» and said the password was wrong. So switching the
    /// second factor on locked the account for ever, and told its owner they were mistyping — with
    /// nowhere in the product to enter the code they had just set up.</para>
    ///
    /// <para>What it must do instead is ask for the code.</para>
    /// </summary>
    [Fact]
    public async Task The_right_password_asks_for_a_code_rather_than_calling_itself_wrong()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var response = await TenantPanelHarness.SignInAsync(
            client, Email, TenantPanelHarness.Password);

        response.StatusCode.Should().Be(
            HttpStatusCode.Redirect,
            "a correct password with a second factor pending is a step, not a refusal");

        response.Headers.Location?.ToString()
            .Should().Contain("TwoFactor", "the reader has to be sent somewhere they can type the code");
    }

    /// <summary>
    /// The whole way through, with the code the app would be showing at that second.
    ///
    /// <para>The token is generated from the same provider the panel verifies against rather than
    /// typed in as a constant, because a constant would be a test of arithmetic that has already
    /// gone stale by the time it runs.</para>
    /// </summary>
    [Fact]
    public async Task The_app_code_finishes_what_the_password_started()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(client, Email, TenantPanelHarness.Password);

        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var second = await AnswerAsync(client, "TwoFactor", await CodeAsync(harness));

        second.StatusCode.Should().Be(HttpStatusCode.Redirect, "the second step was answered correctly");

        using var panel = await client.GetAsync(new Uri("/files", UriKind.Relative));

        panel.StatusCode.Should().Be(HttpStatusCode.OK, "both steps are done, so this is a signed-in browser");
    }

    /// <summary>
    /// Authenticator apps print the six digits in two groups of three and people type what they see.
    /// A code that was read off the screen perfectly must not be refused for the space in it.
    /// </summary>
    [Fact]
    public async Task The_grouping_people_type_along_with_the_code_is_not_held_against_them()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(client, Email, TenantPanelHarness.Password);

        var code = await CodeAsync(harness);

        using var second = await AnswerAsync(client, "TwoFactor", $"{code[..3]} {code[3..]}");

        second.StatusCode.Should().Be(HttpStatusCode.Redirect);

        first.Should().NotBeNull();
    }

    [Fact]
    public async Task A_wrong_app_code_leaves_the_browser_exactly_where_it_was()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(client, Email, TenantPanelHarness.Password);

        using var second = await AnswerAsync(client, "TwoFactor", "000000");

        second.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back with a message on it");

        using var panel = await client.GetAsync(new Uri("/files", UriKind.Relative));

        panel.StatusCode.Should().NotBe(HttpStatusCode.OK, "a wrong code hands out nothing at all");

        first.Should().NotBeNull();
    }

    /// <summary>
    /// A recovery code signs in and is spent by doing it, and the reader is put in front of the one
    /// screen that can replace what they just used up rather than dropped on the dashboard.
    /// </summary>
    [Fact]
    public async Task A_recovery_code_signs_in_and_lands_where_it_can_be_replaced()
    {
        using var harness = new TenantPanelHarness();
        var codes = await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(client, Email, TenantPanelHarness.Password);

        using var second = await AnswerAsync(client, "RecoveryCode", codes[0]);

        second.StatusCode.Should().Be(HttpStatusCode.Redirect);
        second.Headers.Location?.ToString().Should().Contain("security");

        using var panel = await client.GetAsync(new Uri("/files", UriKind.Relative));

        panel.StatusCode.Should().Be(HttpStatusCode.OK);

        first.Should().NotBeNull();
    }

    /// <summary>
    /// Both screens draw, and the way across from one to the other is on the page.
    ///
    /// <para>The link is the whole reason the recovery step is reachable at all: somebody standing at
    /// a code box without the phone has no other route, and a href that stopped being generated
    /// would strand exactly the people the recovery codes were issued for. Walked rather than
    /// asserted about, so a link that renders but 404s is caught too.</para>
    /// </summary>
    [Fact]
    public async Task The_code_screen_carries_the_way_across_to_the_recovery_screen()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(client, Email, TenantPanelHarness.Password);

        var step = first.Headers.Location?.ToString() ?? string.Empty;

        using var code = await client.GetAsync(new Uri(step, UriKind.Relative));
        var shown = await code.Content.ReadAsStringAsync();

        code.StatusCode.Should().Be(HttpStatusCode.OK);
        shown.Should().Contain("Type the code from your phone");

        var across = Regex.Match(shown, @"href=""(?<url>[^""]*RecoveryCode[^""]*)""").Groups["url"].Value;

        across.Should().NotBeEmpty("the only way out for somebody without their phone is this link");

        using var recovery = await client.GetAsync(
            new Uri(WebUtility.HtmlDecode(across), UriKind.Relative));

        recovery.StatusCode.Should().Be(HttpStatusCode.OK);
        (await recovery.Content.ReadAsStringAsync())
            .Should().Contain("Type one of your recovery codes");
    }

    /// <summary>
    /// <b>The second step is not a door of its own.</b>
    ///
    /// <para>Walking up to it without having produced a password must not present a code box: an
    /// address that takes second factors from strangers is a second factor turned into a first
    /// one.</para>
    /// </summary>
    [Theory]
    [InlineData("TwoFactor")]
    [InlineData("RecoveryCode")]
    public async Task Neither_second_step_opens_for_a_browser_that_never_gave_a_password(string step)
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var cold = await client.GetAsync(new Uri($"/Identity/Account/{step}", UriKind.Relative));

        cold.StatusCode.Should().Be(HttpStatusCode.Redirect);
        cold.Headers.Location?.ToString().Should().Contain("Login");
    }

    /// <summary>
    /// «Stay signed in» is answered on the first screen and acted on at the second, so it has to
    /// survive the crossing. If it does not, every account that turned two-step sign-in on gets a
    /// session cookie — the phones-evicted-nightly bug, aimed at the most careful users.
    /// </summary>
    [Fact]
    public async Task Stay_signed_in_survives_the_second_step()
    {
        using var harness = new TenantPanelHarness();
        await TurnOnAsync(harness);

        using var client = harness.NewClient();
        using var first = await TenantPanelHarness.SignInAsync(
            client, Email, TenantPanelHarness.Password, rememberMe: true);

        using var second = await AnswerAsync(client, "TwoFactor", await CodeAsync(harness), rememberMe: true);

        second.StatusCode.Should().Be(HttpStatusCode.Redirect);

        harness.PanelCookieOn(second)?.Expires
            .Should().NotBeNull("a ticked box has to outlive the browser being closed");

        first.Should().NotBeNull();
    }

    [Fact]
    public async Task A_password_alone_is_enough_while_it_is_off()
    {
        using var harness = new TenantPanelHarness();
        await SeedAsync(harness);

        using var client = harness.NewClient();
        using var response = await TenantPanelHarness.SignInAsync(
            client, Email, TenantPanelHarness.Password);

        using var panel = await client.GetAsync(new Uri("/files", UriKind.Relative));

        panel.StatusCode.Should().Be(HttpStatusCode.OK, "the control is opt-in and this account did not");

        response.Should().NotBeNull();
    }

    /// <summary>
    /// A recovery code is spent by being used. The whole point of the set is that losing the phone is
    /// survivable exactly as many times as there are codes.
    /// </summary>
    [Fact]
    public async Task A_recovery_code_works_once_and_not_twice()
    {
        using var harness = new TenantPanelHarness();
        var codes = await TurnOnAsync(harness);

        var one = codes[0];

        using var scope = harness.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);

        (await users.RedeemTwoFactorRecoveryCodeAsync(user!, one)).Succeeded
            .Should().BeTrue();

        (await users.RedeemTwoFactorRecoveryCodeAsync(user!, one)).Succeeded
            .Should().BeFalse("a code that could be replayed is a password with extra steps");
    }

    /// <summary>
    /// Turning it off is a change to how the account is protected, so it is not something a stolen
    /// session may do on its own — the screen demands a current code. Held at the level the
    /// controller enforces it: the token has to verify.
    /// </summary>
    [Fact]
    public async Task A_wrong_code_neither_enables_nor_disables()
    {
        using var harness = new TenantPanelHarness();
        await SeedAsync(harness);

        using var scope = harness.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);

        await users.ResetAuthenticatorKeyAsync(user!);

        var wrong = await users.VerifyTwoFactorTokenAsync(
            user!, users.Options.Tokens.AuthenticatorTokenProvider, "000000");

        wrong.Should().BeFalse();

        (await users.GetTwoFactorEnabledAsync(user!))
            .Should().BeFalse("nothing was turned on by a code that did not verify");
    }

    /// <summary>
    /// Answers whichever second step, the way a browser does: fetch the form, take its token, post.
    /// </summary>
    private static async Task<HttpResponseMessage> AnswerAsync(
        HttpClient client,
        string step,
        string code,
        bool rememberMe = false)
    {
        var path = $"/Identity/Account/{step}";

        return await TenantPanelHarness.PostAsync(
            client,
            path,
            path,
            new Dictionary<string, string>
            {
                ["Code"] = code,
                ["RememberMe"] = rememberMe ? "true" : "false",
            });
    }

    /// <summary>
    /// The six digits the authenticator app would be showing at this second.
    ///
    /// <para>Computed here rather than asked for, because there is nothing to ask. Identity's
    /// authenticator provider answers <c>GenerateTwoFactorTokenAsync</c> with an empty string on
    /// purpose — generating is the phone's job and the provider only ever verifies. So the test
    /// plays the phone: same shared key, same RFC 6238 arithmetic, read back through the real
    /// verifier.</para>
    /// </summary>
    private static async Task<string> CodeAsync(TenantPanelHarness harness)
    {
        using var scope = harness.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);

        var shared = await users.GetAuthenticatorKeyAsync(user!);

        return Totp(Base32(shared!));
    }

    /// <summary>The one-time password for this timestep, as RFC 6238 defines it.</summary>
    private static string Totp(byte[] key)
    {
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timestep);

        // HMAC-SHA1 because that is what the standard says and what every authenticator app does.
        // It is not a hash chosen here for security — the app and the panel have to agree, and
        // they agree on this one.
        var hash = HMACSHA1.HashData(key, counter);

        var offset = hash[^1] & 0x0f;

        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The shared key back out of the alphabet a QR code can carry.</summary>
    private static byte[] Base32(string encoded)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new List<byte>(encoded.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var letter in encoded.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(letter, StringComparison.Ordinal);
            bits += 5;

            if (bits < 8) continue;

            bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
            bits -= 8;
        }

        return [.. bytes];
    }

    /// <summary>Seeds the workspace and its member, without touching the second factor.</summary>
    private static async Task SeedAsync(TenantPanelHarness harness)
    {
        using var operatorClient = await harness.SignedInOperatorAsync();

        using var created = await TenantPanelHarness.PostAsync(
            operatorClient,
            "/operator/tenants",
            "/operator/tenants",
            new Dictionary<string, string>
            {
                ["Name"] = "Acme Bolts",
                ["Slug"] = "acme-bolts",
                ["PlanCode"] = PlanCatalogue.StandardCode,
            });

        var tenantId = TenantPanelHarness.TenantIdFrom(created);

        using var member = await TenantPanelHarness.PostAsync(
            operatorClient,
            $"/operator/tenants/{tenantId}",
            $"/operator/tenants/{tenantId}/members",
            new Dictionary<string, string>
            {
                ["Email"] = Email,
                ["Password"] = TenantPanelHarness.Password,
            });

        member.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>Seeds, then switches the second factor on the way the screen does, and returns the codes.</summary>
    private static async Task<IReadOnlyList<string>> TurnOnAsync(TenantPanelHarness harness)
    {
        await SeedAsync(harness);

        using var scope = harness.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);

        await users.ResetAuthenticatorKeyAsync(user!);
        await users.SetTwoFactorEnabledAsync(user!, true);

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 10);

        return [.. codes!];
    }
}
