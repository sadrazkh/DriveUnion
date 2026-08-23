using DriveUnion.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Persistence;

public class MigrationConsistencyTests
{
    [Fact]
    public void The_migrations_describe_the_current_model()
    {
        // No database is contacted — this compares the model built from the entity classes against
        // the snapshot the last migration wrote.
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse(
            "a property added without a migration is invisible until the first query against a real "
            + "database, which on this machine means it is invisible until production");
    }

    [Fact]
    public void The_public_lookup_column_is_uniquely_indexed()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        var slug = context.Model
            .FindEntityType(typeof(Core.Sharing.ShareLink))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Core.Sharing.ShareLink.Slug));

        // Without this, a slug collision produces two links that resolve to different files
        // depending on row order — and the loser is somebody else's private file.
        slug.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void No_entity_carries_a_global_query_filter()
    {
        // The filter that would seem obvious here is the one that breaks /d/{slug}: an anonymous
        // request has no tenant, so a tenant-filtered read finds nothing and every public link in
        // the product reports "not found" while its row sits plainly in the table.
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        var filtered = context.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count > 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        filtered.Should().BeEmpty(
            "tenant scoping is an explicit argument in this product — see DriveUnionDbContext");
    }
}
