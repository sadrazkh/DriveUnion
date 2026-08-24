using System.Net;
using System.Text;
using System.Text.Json;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The product's fourth anonymous surface, exercised over HTTP through the real pipeline.
///
/// A unit test cannot see what these are about: the failure being guarded against is the absence of a
/// session, and every request below arrives with no cookie, no principal and no tenant.
/// </summary>
public class TelegramWebhookTests
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    [Fact]
    public async Task A_post_with_the_right_secret_is_accepted_and_acted_on()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();
        var response = await PostAsync(client, registration.PathSegment, registration.Secret, Update(1, "/help"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Answering 200 is not politeness: Telegram redelivers on anything else, and a redelivery of
        // a byte-moving update would start its own transfer on top of the first.
        using var db = harness.NewDbContext();
        (await db.TelegramUpdatesSeen.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_post_with_the_wrong_secret_is_refused_and_nothing_is_processed()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();
        var response = await PostAsync(client, registration.PathSegment, "not-the-secret", Update(2, "/help"));

        // This is the control. In production the endpoint is also bound to loopback, but anything on
        // the box that can open a socket can still POST an update — so the secret is what stands
        // between that and the bot.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var db = harness.NewDbContext();
        (await db.TelegramUpdatesSeen.CountAsync()).Should().Be(0);
        harness.Telegram.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_post_with_no_secret_header_at_all_is_refused()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();
        var response = await PostAsync(client, registration.PathSegment, null, Update(3, "/help"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_post_to_the_wrong_path_does_not_admit_the_right_one_exists()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();
        var response = await PostAsync(client, "some-other-segment", registration.Secret, Update(4, "/help"));

        // Not 401. A caller on the wrong path is not a caller with the wrong credential, and
        // answering the same way to both would confirm that a path exists somewhere.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_no_webhook_registered_the_endpoint_is_not_there()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        using var client = harness.NewClient();
        var response = await PostAsync(client, "anything", "anything", Update(5, "/help"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_redelivered_update_performs_the_action_once()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);
        await BindAsync(harness, 5001);

        using var client = harness.NewClient();

        var first = await PostAsync(client, registration.PathSegment, registration.Secret, Update(77, "/help"));
        var second = await PostAsync(client, registration.PathSegment, registration.Secret, Update(77, "/help"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // One reply, not two. Telegram's redelivery is documented behaviour rather than a
        // hypothetical, and a file uploaded twice because of one is a real cost to a real customer.
        harness.Telegram.Calls
            .Count(c => c.Operation == FakeTelegramOperation.SendMessage)
            .Should().Be(1);
    }

    [Fact]
    public async Task A_body_that_is_not_an_update_is_answered_rather_than_thrown_at()
    {
        using var harness = new TelegramPanelHarness(isOperator: false);
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/telegram/{registration.PathSegment}")
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(SecretHeader, registration.Secret);

        var response = await client.SendAsync(request);

        // The endpoint is anonymous and reachable by anything on the box, so a malformed body has to
        // be a 200 and nothing rather than an exception page.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_secret_and_the_path_never_reach_a_page()
    {
        using var harness = new TelegramPanelHarness();
        await harness.ConfigureBotAsync();

        var registration = await RegisterAsync(harness);

        using var client = harness.NewClient();
        var html = await TelegramPanelHarness.ReadTextAsync(client, "/telegram");

        // Both are stored credentials. A screen that could print either is a screen that eventually
        // will, into an HTML source view, a browser cache, or a bug-report screenshot.
        html.Should().NotContain(registration.Secret);
        html.Should().NotContain(registration.PathSegment);
    }

    /// <summary>Registers a webhook through the store, which is what the operator's button does.</summary>
    private static async Task<TelegramWebhookRegistration> RegisterAsync(TelegramPanelHarness harness)
    {
        using var scope = harness.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITelegramBotSettingsStore>();

        var segment = TelegramWebhookSecrets.NewValue();
        var secret = TelegramWebhookSecrets.NewValue();

        await store.SaveWebhookAsync(segment, secret, CancellationToken.None);

        return new TelegramWebhookRegistration(segment, secret);
    }

    private static async Task BindAsync(TelegramPanelHarness harness, long telegramUserId)
    {
        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnion.Infrastructure.Persistence.DriveUnionDbContext>();

        db.Tenants.Add(new DriveUnion.Core.Tenancy.Tenant
        {
            Id = harness.TenantId,
            Name = "Acme",
            Slug = $"t-{harness.TenantId:N}"[..16],
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.Users.Add(new DriveUnion.Infrastructure.Identity.AppUser
        {
            Id = harness.UserId,
            TenantId = harness.TenantId,
            IsOperator = false,
            UserName = "customer@driveunion.test",
            NormalizedUserName = "CUSTOMER@DRIVEUNION.TEST",
            Email = "customer@driveunion.test",
            NormalizedEmail = "CUSTOMER@DRIVEUNION.TEST",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.TelegramAccounts.Add(new TelegramAccount
        {
            Id = Guid.NewGuid(),
            AppUserId = harness.UserId,
            TelegramUserId = telegramUserId,
            ChatId = telegramUserId,
            LinkedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string segment,
        string? secret,
        string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/telegram/{segment}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (secret is not null) request.Headers.Add(SecretHeader, secret);

        return client.SendAsync(request);
    }

    private static string Update(long updateId, string text) => JsonSerializer.Serialize(new
    {
        update_id = updateId,
        message = new
        {
            message_id = 1,
            chat = new { id = 5001, type = "private" },
            from = new { id = 5001, first_name = "Some", username = "someone", language_code = "fa" },
            text,
        },
    });
}
