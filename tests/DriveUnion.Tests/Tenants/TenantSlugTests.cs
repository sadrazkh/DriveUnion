using DriveUnion.Infrastructure.Tenancy;
using FluentAssertions;

namespace DriveUnion.Tests.Tenants;

/// <summary>
/// The rule for a value that is at once a URL segment and a folder name inside somebody else's
/// Drive — and which cannot be changed once a file has been written under it.
///
/// <para>These are cheap tests for an expensive mistake. A slug that gets through here is a
/// directory this product will ask Google, or a local disk, to create; the failure arrives on a
/// customer's first upload, in an error that names a path rather than the workspace.</para>
/// </summary>
public class TenantSlugTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-corp")]
    [InlineData("a1b")]
    [InlineData("customer-42-north")]
    [InlineData("000")]
    public void A_slug_this_product_would_mint_is_accepted(string slug) =>
        TenantSlug.IsWellFormed(slug).Should().BeTrue();

    [Theory]
    [InlineData("", "empty")]
    [InlineData("ab", "shorter than the minimum")]
    [InlineData("Acme", "upper case — two workspaces would differ only by case")]
    [InlineData("acme corp", "a space is not a path segment")]
    [InlineData("acme_corp", "an underscore is outside the accepted set")]
    [InlineData("-acme", "a leading hyphen is what gets trimmed away downstream")]
    [InlineData("acme-", "a trailing hyphen, likewise")]
    [InlineData("ac--me", "a doubled hyphen makes two slugs that read alike")]
    [InlineData("acme/corp", "a slash is a second path segment")]
    [InlineData("acme.corp", "a dot invites . and .. through the same door")]
    [InlineData("شرکت", "not URL-safe, and not the same characters on every filesystem")]
    [InlineData("شرکت۱", "Persian digits are digits and are not ASCII")]
    public void A_slug_this_product_would_not_mint_is_refused(string slug, string why) =>
        TenantSlug.IsWellFormed(slug).Should().BeFalse(why);

    /// <summary>
    /// Windows has no folder called <c>con</c> — it is a device. The local-disk drive client writes
    /// these folders onto a real disk, and this product is built on Windows, so the workspace would
    /// create fine and fail on its first byte.
    /// </summary>
    [Theory]
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    public void A_reserved_device_name_is_refused(string slug) =>
        TenantSlug.IsWellFormed(slug).Should().BeFalse("it is a device on Windows, not a folder");

    [Fact]
    public void Anything_over_the_maximum_is_refused() =>
        TenantSlug.IsWellFormed(new string('a', TenantSlug.MaximumLength + 1)).Should().BeFalse();

    [Fact]
    public void The_maximum_itself_is_accepted() =>
        TenantSlug.IsWellFormed(new string('a', TenantSlug.MaximumLength)).Should().BeTrue();

    /// <summary>
    /// Normalising trims and lower-cases, and repairs nothing. A form that quietly turned «شرکت
    /// آلفا» into <c>shrkt-alfa</c> would have chosen a permanent folder name on the operator's
    /// behalf; refusing and showing the rule lets them choose it themselves.
    /// </summary>
    [Fact]
    public void Normalising_trims_and_lowercases_and_repairs_nothing()
    {
        TenantSlug.Normalise("  ACME-Corp  ").Should().Be("acme-corp");
        TenantSlug.Normalise("Acme Corp").Should().Be("acme corp");
        TenantSlug.Normalise(null).Should().BeEmpty();
    }
}
