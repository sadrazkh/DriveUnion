using DriveUnion.Core.Sharing;
using FluentAssertions;

namespace DriveUnion.Tests.Sharing;

public class SlugGeneratorTests
{
    private readonly SlugGenerator _generator = new();

    [Fact]
    public void A_slug_is_eight_lowercase_alphanumerics()
    {
        var slug = _generator.Next();

        slug.Should().HaveLength(8);
        slug.Should().MatchRegex("^[a-z0-9]{8}$");
    }

    [Fact]
    public void Slugs_do_not_repeat_over_a_realistic_run()
    {
        // Not a proof of randomness — a guard against the generator being seeded per call, which is
        // the way this goes wrong in practice and which yields the same slug for every link created
        // in the same tick.
        var slugs = Enumerable.Range(0, 20_000).Select(_ => _generator.Next()).ToList();

        slugs.Distinct().Should().HaveCount(slugs.Count);
    }

    [Fact]
    public void The_alphabet_is_used_broadly()
    {
        // A truncated alphabet silently shrinks the keyspace, and nothing else would notice.
        var seen = new HashSet<char>();
        for (var i = 0; i < 5_000; i++) seen.UnionWith(_generator.Next());

        seen.Should().HaveCount(36);
    }

    [Theory]
    [InlineData("kx91mzq4", true)]
    [InlineData("00000000", true)]
    [InlineData("kx91mz", false)]      // the comp's six-character shape is no longer well-formed
    [InlineData("kx91mzq44", false)]
    [InlineData("KX91MZQ4", false)]
    [InlineData("kx91-zq4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Well_formedness_is_checked_before_a_slug_reaches_the_database(string? slug, bool expected)
    {
        SlugGenerator.IsWellFormed(slug).Should().Be(expected);
    }
}
