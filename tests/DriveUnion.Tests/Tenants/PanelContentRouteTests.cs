using System.Net;
using DriveUnion.Core.Metering;
using DriveUnion.Core.Plans;
using DriveUnion.Core.Storage;
using DriveUnion.Core.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// <c>/files/{id}/content</c> — the first cookie-authenticated byte route in the product.
///
/// <para>It was absent on purpose for a long time: a customer reached their own file by making a
/// share link, which is metered and capped like every other link. That worked, and it made watching
/// a film of your own into a link somebody else could also have — which is what this route is for.
/// What it must not be is a hole, and that is what these are about.</para>
///
/// <para>The traffic cap was closed on the API and the S3 gateway precisely because an exemption
/// there was not «your own files are free» but «your own files are free if you fetch them a
/// particular way». A panel route without the same gate would be that hole reopened with a nicer
/// front door, so the gate is asserted here rather than trusted to the fact that the code was
/// copied.</para>
/// </summary>
public class PanelContentRouteTests
{
    private const string CustomerEmail = "reza@acme.example";

    [Fact]
    public async Task It_is_not_reachable_without_signing_in()
    {
        using var harness = new TenantPanelHarness();
        using var stranger = harness.NewClient();

        using var response = await stranger.GetAsync(
            new Uri($"/files/{Guid.NewGuid()}/content", UriKind.Relative));

        // Challenged rather than answered. It serves a customer's file bytes, so it sits behind the
        // same policy as the screen that lists them.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().Contain("/Identity/Account/Login");
    }

    [Fact]
    public async Task Another_workspaces_file_is_not_found_rather_than_refused()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, _) = await OnboardAsync(harness, operatorClient);

        // A file belonging to nobody this customer can see.
        Guid strangersFile;
        await using (var db = harness.NewDbContext())
        {
            strangersFile = SeedFile(db, Guid.NewGuid());
            await db.SaveChangesAsync();
        }

        using var customer = harness.NewClient();
        await TenantPanelHarness.SignInAsync(customer, CustomerEmail, TenantPanelHarness.Password);

        using var response = await customer.GetAsync(
            new Uri($"/files/{strangersFile}/content", UriKind.Relative));

        // 404 and not 403: the two are the same answer, or the difference is a way to ask whether a
        // file id is real. The same rule the JSON API keeps for the same lookup.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        tenantId.Should().NotBeEmpty();
    }

    /// <summary>
    /// <b>The gate.</b> A workspace over its month cannot watch its way around the cap.
    /// </summary>
    [Fact]
    public async Task A_workspace_over_its_traffic_allowance_is_refused_here_too()
    {
        using var harness = new TenantPanelHarness();
        using var operatorClient = await harness.SignedInOperatorAsync();

        var (tenantId, _) = await OnboardAsync(harness, operatorClient);

        Guid fileId;
        await using (var db = harness.NewDbContext())
        {
            fileId = SeedFile(db, tenantId);

            // Sold a hundred thousand bytes, and every one of them already served.
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
            tenant.MonthlyEgressBytes = 100_000;

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            db.TenantUsageDays.Add(new TenantUsageDay
            {
                TenantId = tenantId,
                Day = today,
                EgressBytes = 100_000,
                Downloads = 1,
            });

            await db.SaveChangesAsync();
        }

        using var customer = harness.NewClient();
        await TenantPanelHarness.SignInAsync(customer, CustomerEmail, TenantPanelHarness.Password);

        using var response = await customer.GetAsync(
            new Uri($"/files/{fileId}/content", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "a panel route without this gate is the bypass the API and S3 gates were built to close");

        // It says when it lifts, from the same helper the other three surfaces use.
        response.Headers.RetryAfter?.Date.Should().NotBeNull();

        // And it is a 429 rather than a 403: nothing about this customer's right to their own file
        // has changed, and a 403 sends somebody looking for a permissions fault that does not exist.
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private static Guid SeedFile(DriveUnion.Infrastructure.Persistence.DriveUnionDbContext db, Guid tenantId)
    {
        var account = new GoogleAccount
        {
            Id = Guid.NewGuid(),
            Email = $"pool-{Guid.NewGuid():N}@gmail.com",
            Label = "A1",
            RefreshTokenProtected = "protected",
            QuotaTotalBytes = 1024L * 1024 * 1024,
            Status = GoogleAccountStatus.Healthy,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GoogleAccountId = account.Id,
            DriveFileId = $"drive-{Guid.NewGuid():N}",
            Name = "holiday.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4096,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
        };

        db.GoogleAccounts.Add(account);
        db.StoredFiles.Add(file);

        return file.Id;
    }

    private static async Task<(Guid TenantId, Guid UserId)> OnboardAsync(
        TenantPanelHarness harness,
        HttpClient operatorClient)
    {
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
                ["Email"] = CustomerEmail,
                ["Password"] = TenantPanelHarness.Password,
            });

        member.StatusCode.Should().Be(HttpStatusCode.Redirect);

        await using var db = harness.NewDbContext();

        return (tenantId, db.Users.Single(u => u.TenantId == tenantId).Id);
    }
}
