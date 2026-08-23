using DriveUnion.Core.Storage;
using FluentAssertions;

namespace DriveUnion.Tests.Services;

/// <summary>
/// M1 has one account, so the answer is nearly always "that one". The tests that matter are the
/// ones where it is not.
/// </summary>
public class UploadTargetSelectorTests
{
    private const long OneTerabyte = 1024L * 1024 * 1024 * 1024;

    [Fact]
    public async Task The_only_healthy_account_takes_the_upload()
    {
        await using var harness = ServiceTestHarness.Create();
        var account = harness.SeedAccount();

        (await harness.Selector().SelectAsync(1024, default)).Should().Be(account.Id);
    }

    [Fact]
    public async Task An_empty_pool_takes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();

        (await harness.Selector().SelectAsync(1024, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(GoogleAccountStatus.Disconnected)]
    [InlineData(GoogleAccountStatus.Paused)]
    public async Task An_account_that_is_not_healthy_is_not_routed_to(GoogleAccountStatus status)
    {
        await using var harness = ServiceTestHarness.Create();
        harness.SeedAccount(status);

        (await harness.Selector().SelectAsync(1024, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_file_bigger_than_the_remaining_quota_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        harness.SeedAccount(quotaTotalBytes: 5 * OneTerabyte, quotaUsedBytes: 5 * OneTerabyte - 100);

        (await harness.Selector().SelectAsync(101, default)).Should().BeNull();
        (await harness.Selector().SelectAsync(100, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_account_whose_quota_has_never_been_read_is_still_usable()
    {
        await using var harness = ServiceTestHarness.Create();
        var account = harness.SeedAccount(quotaTotalBytes: 0, quotaUsedBytes: 0);

        // Zero means "nobody has asked Google yet", not "full". Reading it as full would refuse
        // every upload until the first quota refresh — a dead product wearing a storage problem's
        // clothes.
        (await harness.Selector().SelectAsync(OneTerabyte, default)).Should().Be(account.Id);
    }

    [Fact]
    public async Task The_account_with_the_most_room_wins_when_the_pool_grows()
    {
        await using var harness = ServiceTestHarness.Create();
        harness.SeedAccount(quotaTotalBytes: 5 * OneTerabyte, quotaUsedBytes: 4 * OneTerabyte);
        var roomy = harness.SeedAccount(quotaTotalBytes: 5 * OneTerabyte, quotaUsedBytes: OneTerabyte);

        // M2 replaces this ordering with a real policy. The seam is here so that the call site does
        // not have to change when it does.
        (await harness.Selector().SelectAsync(OneTerabyte, default)).Should().Be(roomy.Id);
    }
}
