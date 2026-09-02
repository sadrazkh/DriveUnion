using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Services;

/// <summary>
/// Giving a file a different name.
///
/// <para>The feature is small and two of its answers are not: another workspace's id and a name with
/// a path in it. Both are refusals that have to be the same refusal every other file-by-id write
/// here gives, and the second is a security boundary reached through a text box a customer types
/// into — which is a different threat model from the one <see cref="FileNames"/> was written for.</para>
/// </summary>
public class FileRenameTests
{
    [Fact]
    public async Task A_file_takes_the_new_name()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "holiday.mp4");

        (await harness.Files().RenameAsync(tenant.Id, file.Id, "summer 2026.mp4", default))
            .Should().Be(RenameOutcome.Renamed);

        var after = await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id);

        after.Name.Should().Be("summer 2026.mp4");
    }

    /// <summary>
    /// <b>The rule the shared sanitiser exists for, reached this time through a text box.</b>
    ///
    /// <para>Stripped rather than refused, which is what the fetch path decided and what this one
    /// inherits: a name carrying a path is almost always a paste rather than an attack, and what is
    /// left after the separators go is the name the person meant.</para>
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("a/b/c.bin", "abc.bin")]
    [InlineData("C:\\Windows\\x.dll", "CWindowsx.dll")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    public async Task A_name_carrying_a_path_keeps_only_the_name(string typed, string expected)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "before.bin");

        (await harness.Files().RenameAsync(tenant.Id, file.Id, typed, default))
            .Should().Be(RenameOutcome.Renamed);

        (await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id))
            .Name.Should().Be(expected);
    }

    /// <summary>
    /// A name that is only separators leaves nothing, and «nothing» is not a filename. Refused with
    /// a sentence rather than silently keeping the old one, which would read as the button not
    /// working.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("..")]
    [InlineData(null)]
    public async Task A_name_with_nothing_left_in_it_is_refused(string? typed)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "before.bin");

        (await harness.Files().RenameAsync(tenant.Id, file.Id, typed, default))
            .Should().Be(RenameOutcome.NoName);

        (await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id))
            .Name.Should().Be("before.bin", "a refused rename changes nothing");
    }

    /// <summary>
    /// Another workspace's file is <c>NotFound</c>, never «forbidden». The two are one answer here
    /// for the reason they are one answer everywhere else in this product: the difference is an
    /// oracle for walking file ids.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_file_is_not_found_rather_than_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var mine = harness.SeedTenant("acme");
        var theirs = harness.SeedTenant("globex");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(theirs.Id, account.Id, "theirs.bin");

        (await harness.Files().RenameAsync(mine.Id, file.Id, "mine.bin", default))
            .Should().Be(RenameOutcome.NotFound);

        (await harness.Db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == file.Id))
            .Name.Should().Be("theirs.bin");
    }

    [Fact]
    public async Task A_file_in_the_trash_cannot_be_renamed()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");
        var account = harness.SeedAccount();
        var file = harness.SeedFile(tenant.Id, account.Id, "gone.bin", deletedAt: DateTimeOffset.UtcNow);

        (await harness.Files().RenameAsync(tenant.Id, file.Id, "back.bin", default))
            .Should().Be(RenameOutcome.NotFound);
    }
}
