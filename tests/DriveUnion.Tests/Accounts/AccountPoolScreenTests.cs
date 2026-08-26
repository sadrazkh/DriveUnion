using System.Net;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The pool table on the accounts screen, and the drain it starts.
///
/// <para><c>AccountMigrationTests</c> holds what a move does. This holds the half a service test
/// cannot see: that the operator is shown what is on each account before being asked to retire one,
/// that the form reaches the action, and that the two states a drain can be refused in say so on
/// the screen rather than anywhere else.</para>
/// </summary>
public class AccountPoolScreenTests
{
    private static void SeedFiles(
        OperatorPanelHarness harness,
        Guid accountId,
        int count,
        long sizeBytes = 1000,
        Guid? tenantId = null,
        DateTimeOffset? deletedAt = null)
    {
        using var db = harness.NewDbContext();

        var tenant = tenantId ?? Guid.CreateVersion7();

        if (!db.Tenants.Any(t => t.Id == tenant))
        {
            db.Tenants.Add(new Core.Tenancy.Tenant
            {
                Id = tenant,
                Name = $"T-{tenant:N}"[..12],
                Slug = $"t{tenant:N}"[..12],
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        for (var i = 0; i < count; i++)
        {
            db.StoredFiles.Add(new StoredFile
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                GoogleAccountId = accountId,
                DriveFileId = $"drive-{Guid.NewGuid():N}",
                Name = $"file-{i}.bin",
                MimeType = "application/octet-stream",
                SizeBytes = sizeBytes,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow,
                DeletedAt = deletedAt,
            });
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task The_screen_says_what_is_on_each_account()
    {
        using var harness = new OperatorPanelHarness();
        var busy = harness.SeedAccount("busy@example.test", "A1");
        harness.SeedAccount("spare@example.test", "A2");

        // Mebibytes, because DisplayFormats.Bytes is binary and drops a trailing zero — three of
        // these render as «3 MB» and three million bytes would render as «2.9 MB».
        SeedFiles(harness, busy.Id, count: 3, sizeBytes: 1024 * 1024);

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/accounts");

        // The cards above this table report what Google says about an account. None of them has ever
        // said what is on it, which is the one thing an operator needs before retiring one.
        markup.Should().Contain("A1").And.Contain("A2");
        markup.Should().Contain("3 MB");
    }

    [Fact]
    public async Task A_deleted_file_is_not_counted_as_something_that_would_move()
    {
        using var harness = new OperatorPanelHarness();
        var account = harness.SeedAccount("busy@example.test", "A1");

        var tenant = Guid.CreateVersion7();
        SeedFiles(harness, account.Id, count: 1, sizeBytes: 2 * 1024 * 1024, tenantId: tenant);
        SeedFiles(harness, account.Id, count: 1, sizeBytes: 8 * 1024 * 1024, tenantId: tenant,
            deletedAt: DateTimeOffset.UtcNow);

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/accounts");

        // 2 MB and not 10. What is in the trash still occupies the account, and it is on its way out
        // — a drain that moved it would spend Google's bandwidth relocating something the purge is
        // about to delete.
        markup.Should().Contain("2 MB");
        markup.Should().NotContain("10 MB");
    }

    [Fact]
    public async Task An_empty_account_is_not_offered_a_drain()
    {
        using var harness = new OperatorPanelHarness();
        var busy = harness.SeedAccount("busy@example.test", "A1");
        var empty = harness.SeedAccount("spare@example.test", "A2");

        SeedFiles(harness, busy.Id, count: 1);

        using var client = harness.NewClient();
        var markup = await client.GetStringAsync("/accounts");

        // There is nothing to move off it, so the control would be a button that does nothing.
        markup.Should().Contain($"/accounts/{busy.Id}/drain");
        markup.Should().NotContain($"/accounts/{empty.Id}/drain");
    }

    [Fact]
    public async Task Starting_a_drain_pauses_the_source_and_says_so()
    {
        using var harness = new OperatorPanelHarness();
        var busy = harness.SeedAccount("busy@example.test", "A1");
        var spare = harness.SeedAccount("spare@example.test", "A2");

        SeedFiles(harness, busy.Id, count: 2);

        using var client = harness.NewClient();

        using var response = await OperatorPanelHarness.PostAsync(
            client,
            $"/accounts/{busy.Id}/drain",
            new Dictionary<string, string> { ["target"] = spare.Id.ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        await using var db = harness.NewDbContext();

        db.AccountMigrations.Should().ContainSingle()
            .Which.TargetAccountId.Should().Be(spare.Id);

        // The half that is not obvious from the button. Without it the drain races the uploads it is
        // trying to get ahead of and never reaches an empty account.
        (await db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == busy.Id))
            .Status.Should().Be(GoogleAccountStatus.Paused);
    }

    [Fact]
    public async Task A_refused_drain_is_a_sentence_on_the_screen()
    {
        using var harness = new OperatorPanelHarness();
        var busy = harness.SeedAccount("busy@example.test", "A1");
        var broken = harness.SeedAccount("dead@example.test", "A2", failureReason: "token expired");

        SeedFiles(harness, busy.Id, count: 1);

        using var client = harness.NewClient();

        using var response = await OperatorPanelHarness.PostAsync(
            client,
            $"/accounts/{busy.Id}/drain",
            new Dictionary<string, string> { ["target"] = broken.Id.ToString() });

        // Decoded, because Razor's default encoder writes every non-ASCII character as a numeric
        // reference — «ف» is «&#x0641;» in the response and «ف» in the browser.
        var markup = WebUtility.HtmlDecode(await client.GetStringAsync("/accounts"));

        // Everything that can refuse a drain is something the operator can see and fix, so it is a
        // sentence rather than a status code — and nothing was queued or paused on the way. The
        // panel renders Persian by default, so this is the Persian half of that one string.
        markup.Should().Contain("فایل قبول نمی‌کند");

        await using var db = harness.NewDbContext();
        db.AccountMigrations.Should().BeEmpty();

        (await db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == busy.Id))
            .Status.Should().Be(GoogleAccountStatus.Healthy, "a refused drain pauses nothing");
    }

    [Fact]
    public async Task A_customer_cannot_reach_any_of_it()
    {
        using var harness = new OperatorPanelHarness(isOperator: false);
        var busy = harness.SeedAccount("busy@example.test", "A1");
        var spare = harness.SeedAccount("spare@example.test", "A2");

        using var client = harness.NewClient();

        // The pool is the operator's and a customer must never learn it exists — which is an access
        // control on every route, not a screen they are simply not linked to.
        (await client.GetAsync(new Uri("/accounts", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Posted without a token, because there is no page to read one from — which is the point.
        // Authorization runs ahead of antiforgery in the filter pipeline, so what answers here is the
        // operator policy and not a missing token.
        using var refused = await client.PostAsync(
            new Uri($"/accounts/{busy.Id}/drain", UriKind.Relative),
            new FormUrlEncodedContent(
                new Dictionary<string, string> { ["target"] = spare.Id.ToString() }));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
