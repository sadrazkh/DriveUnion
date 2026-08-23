using System.Net;
using DriveUnion.Core.Abstractions;
using FluentAssertions;

namespace DriveUnion.Tests.Google;

public class GoogleDriveClientAboutTests
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    /// <summary>
    /// A 5 TB Google One account. <c>usage</c> and <c>usageInDrive</c> differ deliberately: what
    /// counts against the plan is Drive, Gmail and Photos together.
    /// </summary>
    private const string AboutJson = """
        {
          "user": { "emailAddress": "pool-a1@example.com", "displayName": "Pool A1" },
          "storageQuota": {
            "limit": "5497558138880",
            "usage": "1099511627776",
            "usageInDrive": "1000000000000",
            "usageInDriveTrash": "0"
          }
        }
        """;

    [Fact]
    public async Task The_quota_is_read_from_the_totals_that_count_against_the_plan()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.Json(HttpStatusCode.OK, AboutJson));
        var client = DriveClientHarness.Create(stub);

        var quota = await client.GetStorageQuotaAsync(DriveClientHarness.AccountId, CancellationToken.None);

        quota.LimitBytes.Should().Be(5497558138880);
        quota.UsageBytes.Should().Be(1099511627776, "usage, not usageInDrive — Gmail and Photos "
            + "share the same 5 TB and the operator's dashboard has to show the real number");

        stub.LastRequest.Uri!.Query.Should().Contain("fields=storageQuota");
    }

    [Fact]
    public async Task An_absent_limit_is_reported_as_zero_rather_than_guessed_at()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"storageQuota":{"usage":"42"}}"""));

        var client = DriveClientHarness.Create(stub);

        var quota = await client.GetStorageQuotaAsync(DriveClientHarness.AccountId, CancellationToken.None);

        // Drive omits `limit` only for unlimited storage, which consumer Google One is not. Assuming
        // infinity here would hand M2's router an account it can fill until uploads start failing.
        quota.LimitBytes.Should().Be(0);
        quota.UsageBytes.Should().Be(42);
    }

    [Fact]
    public async Task An_about_resource_with_no_storage_quota_is_a_failure()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"user":{"emailAddress":"a@b.c"}}"""));

        var client = DriveClientHarness.Create(stub);

        var act = async () => await client.GetStorageQuotaAsync(
            DriveClientHarness.AccountId, CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>().WithMessage("*storageQuota*");
    }

    [Fact]
    public async Task Connecting_an_account_learns_its_address_from_Google()
    {
        var stub = StubHttpMessageHandler.Always(() => StubResponses.Json(HttpStatusCode.OK, AboutJson));
        var client = DriveClientHarness.Create(stub);

        var about = await client.GetAboutAsync("ya29.fresh-token", CancellationToken.None);

        about.Email.Should().Be("pool-a1@example.com");
        about.LimitBytes.Should().Be(5497558138880);

        // The token is handed in directly here: at this point in a connection there is no account
        // row to resolve one from.
        stub.LastRequest.Header("Authorization").Should().Be("Bearer ya29.fresh-token");
        stub.LastRequest.Uri!.Query.Should().Contain("user");
    }

    [Fact]
    public async Task An_account_Google_will_not_name_is_refused()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"storageQuota":{"limit":"1","usage":"0"}}"""));

        var client = DriveClientHarness.Create(stub);

        var act = async () => await client.GetAboutAsync("ya29.fresh-token", CancellationToken.None);

        await act.Should().ThrowAsync<DriveApiException>().WithMessage("*email address*");
    }

    [Fact]
    public async Task An_existing_folder_is_found_rather_than_duplicated()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"files":[{"id":"folder-123"}]}"""));

        var client = DriveClientHarness.Create(stub);

        var id = await client.EnsureFolderAsync(
            DriveClientHarness.AccountId, "acme-corp", "root-folder", CancellationToken.None);

        id.Should().Be("folder-123");
        stub.CallCount.Should().Be(1);

        var query = Uri.UnescapeDataString(stub.LastRequest.Uri!.Query);
        query.Should().Contain("name = 'acme-corp'");
        query.Should().Contain("'root-folder' in parents");
        query.Should().Contain("trashed = false");
    }

    [Fact]
    public async Task A_missing_folder_is_created()
    {
        var stub = new StubHttpMessageHandler((_, attempt) => attempt == 1
            ? StubResponses.Json(HttpStatusCode.OK, """{"files":[]}""")
            : StubResponses.Json(HttpStatusCode.OK, """{"id":"folder-new"}"""));

        var client = DriveClientHarness.Create(stub);

        var id = await client.EnsureFolderAsync(
            DriveClientHarness.AccountId, "acme-corp", null, CancellationToken.None);

        id.Should().Be("folder-new");
        stub.CallCount.Should().Be(2);
        stub.LastRequest.Method.Should().Be(HttpMethod.Post);

        var body = System.Text.Encoding.UTF8.GetString(stub.LastRequest.Body);
        body.Should().Contain(FolderMime);
    }

    [Fact]
    public async Task An_apostrophe_in_a_folder_name_is_escaped_rather_than_changing_the_query()
    {
        var stub = StubHttpMessageHandler.Always(
            () => StubResponses.Json(HttpStatusCode.OK, """{"files":[{"id":"folder-9"}]}"""));

        var client = DriveClientHarness.Create(stub);

        await client.EnsureFolderAsync(
            DriveClientHarness.AccountId, "o'brien", null, CancellationToken.None);

        var query = Uri.UnescapeDataString(stub.LastRequest.Uri!.Query);
        query.Should().Contain(@"name = 'o\'brien'");
    }
}
