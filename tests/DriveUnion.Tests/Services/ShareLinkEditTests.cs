using DriveUnion.Core.Application;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Changing a link after it has been handed out.
///
/// <para>Until now a link was create-or-revoke, so moving an expiry meant minting a new slug and
/// re-sending it to everybody who already had the old one. The edit is small; two of its refusals
/// are not, and both are here.</para>
/// </summary>
public class ShareLinkEditTests
{
    [Fact]
    public async Task An_expiry_and_a_ceiling_can_both_be_moved()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, null, null, null), default);

        var later = harness.Clock.GetUtcNow().AddDays(30);

        (await harness.Links().UpdateAsync(tenant.Id, link.Id, later, 25, "for Reza", default))
            .Should().Be(ShareLinkEdit.Changed);

        var after = await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id);

        after.MaxDownloads.Should().Be(25);
        after.Note.Should().Be("for Reza");
        after.ExpiresAt.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// <b>The refusal with a number in it.</b>
    ///
    /// <para>Accepting a ceiling below what has been spent would kill a live link on the spot in a
    /// way the person editing it did not ask for. Clamping it would store a figure they did not
    /// type. Neither is a thing to do quietly, so it is refused and the screen says both numbers.</para>
    /// </summary>
    [Fact]
    public async Task A_ceiling_below_what_has_been_spent_is_refused_and_changes_nothing()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, null, 10, null), default);

        await harness.Db.ShareLinks
            .Where(l => l.Id == link.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DownloadCount, 7), default);

        (await harness.Links().UpdateAsync(tenant.Id, link.Id, null, 3, null, default))
            .Should().Be(ShareLinkEdit.BelowWhatIsSpent);

        var after = await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id);

        after.MaxDownloads.Should().Be(10, "a refused edit changes nothing at all");
    }

    /// <summary>
    /// A link that ran out is not a link that was revoked, and raising its ceiling is the case this
    /// feature exists for.
    /// </summary>
    [Fact]
    public async Task Raising_the_ceiling_past_what_was_spent_puts_a_spent_link_back_to_work()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, null, 5, null), default);

        await harness.Db.ShareLinks
            .Where(l => l.Id == link.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DownloadCount, 5), default);

        (await harness.Links().UpdateAsync(tenant.Id, link.Id, null, 20, null, default))
            .Should().Be(ShareLinkEdit.Changed);

        var after = await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id);

        // Still active and now under its ceiling, which is what a public read checks before it
        // reserves a download. It ran out; it was never revoked.
        after.IsActive.Should().BeTrue();
        after.MaxDownloads.Should().Be(20);
        after.DownloadCount.Should().Be(5);
    }

    /// <summary>
    /// <b>Revoking has no undo and this must not become one.</b>
    ///
    /// <para>A revoked slug is burned for ever (M4 §2). A slug handed out, revoked, and quietly
    /// working again is the worst version of a share link there is — worse than one that stays dead,
    /// because the sender told people it was dead.</para>
    /// </summary>
    [Fact]
    public async Task A_revoked_link_cannot_be_edited_back_to_life()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, null, null, null), default);

        await harness.Links().RevokeAsync(tenant.Id, link.Id, default);

        (await harness.Links().UpdateAsync(
            tenant.Id, link.Id, harness.Clock.GetUtcNow().AddYears(1), 999, null, default))
            .Should().Be(ShareLinkEdit.NotFound);

        var after = await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id);

        after.IsActive.Should().BeFalse();
        after.MaxDownloads.Should().BeNull("nothing about a revoked link is editable");
    }

    /// <summary>
    /// Another workspace's link is <c>NotFound</c> and never «forbidden», for the reason it is
    /// everywhere else here: the difference is an oracle for walking link ids.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_link_is_not_found_rather_than_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(theirs.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            theirs.Id,
            new CreateShareLinkRequest(file.Id, null, 4, null), default);

        (await harness.Links().UpdateAsync(mine.Id, link.Id, null, 400, null, default))
            .Should().Be(ShareLinkEdit.NotFound);

        (await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id))
            .MaxDownloads.Should().Be(4);
    }

    /// <summary>Clearing both is «no expiry, no ceiling», which is a link with nothing stopping it.</summary>
    [Fact]
    public async Task Both_limits_can_be_taken_off_again()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id);

        var link = await harness.Links().CreateAsync(
            tenant.Id,
            new CreateShareLinkRequest(file.Id, harness.Clock.GetUtcNow().AddDays(1), 3, "note"),
            default);

        (await harness.Links().UpdateAsync(tenant.Id, link.Id, null, null, null, default))
            .Should().Be(ShareLinkEdit.Changed);

        var after = await harness.Db.ShareLinks.AsNoTracking().FirstAsync(l => l.Id == link.Id);

        after.ExpiresAt.Should().BeNull();
        after.MaxDownloads.Should().BeNull();
        after.Note.Should().BeNull("«no note» is null rather than an empty string — see Trimmed");
    }
}
