using DriveUnion.Tests.Links;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Presentation;

/// <summary>
/// The QR beside a share link, and the one thing it must not do.
///
/// <para><c>LinkQrCode</c> has its own tests for the encoding. What is held here is the wiring — that
/// the picture reaches the page at all, which it did not for a while: the encoder existed, was eight
/// hundred lines long, was correct, and no view called it. A feature nothing renders is
/// indistinguishable from a feature nobody wrote.</para>
/// </summary>
public class LinkQrOnTheFilePageTests
{
    [Fact]
    public async Task A_file_with_a_live_link_carries_the_code_as_markup_rather_than_a_request()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        var markup = await FilePageAsync(harness, tenant.Id);

        // Inline, not an <img src> — a request per link per row, to draw something most readers never
        // open, would be the expensive way round.
        markup.Should().Contain("link-qr");
        markup.Should().Contain("<svg");
        markup.Should().Contain("shape-rendering=\"crispEdges\"");
    }

    /// <summary>
    /// <b>The slug lives in the modules and nowhere else.</b>
    ///
    /// <para>A title element is text in the markup. Putting the address there would undo the whole
    /// exercise — anything reading the page, including the page's own «view source», would have the
    /// link back in plain text beside a picture whose point was that it is the only copy.</para>
    /// </summary>
    [Fact]
    public async Task The_picture_does_not_carry_the_address_it_encodes()
    {
        using var harness = new PanelPageHarness();
        var tenant = harness.SeedTenant("Acme", "Q3-Report-Final.pdf", "kx91mzq4");

        var markup = await FilePageAsync(harness, tenant.Id);

        var opened = markup.IndexOf("link-qr", StringComparison.Ordinal);

        opened.Should().BePositive("the disclosure is on the page");

        var closed = markup.IndexOf("</details>", opened, StringComparison.Ordinal);

        closed.Should().BePositive("the disclosure is closed somewhere");

        markup[opened..closed].Should().NotContain("kx91mzq4");
    }

    /// <summary>
    /// A code drawn on this panel's dark surface will not scan, and looks perfectly fine to the
    /// person holding the phone. The white field is part of the format, not decoration.
    /// </summary>
    [Fact]
    public void The_code_is_given_a_light_field_in_both_themes()
    {
        var css = File.ReadAllText(
            Path.Combine(RepositoryRoot().FullName, "src/DriveUnion.Web/wwwroot/css/app.css"));

        var rule = css[css.IndexOf(".link-qr svg", StringComparison.Ordinal)..];

        rule[..rule.IndexOf('}')].Should().Contain("background: #fff");
    }

    /// <summary>
    /// The file's own page, which is where the detail partial always renders.
    ///
    /// <para>The list draws that partial only for a selected file, so fetching <c>/files</c> and
    /// looking for a link finds an empty panel and proves nothing — which is what the first draft of
    /// these tests did.</para>
    ///
    /// <para>The id comes from the database rather than from scraping the list's markup for something
    /// GUID-shaped. A test that has to parse the page it is about to assert on can fail for two
    /// unrelated reasons and only reports one.</para>
    /// </summary>
    private static async Task<string> FilePageAsync(PanelPageHarness harness, Guid tenantId)
    {
        Guid id;

        await using (var db = harness.NewDbContext())
        {
            id = await db.StoredFiles.AsNoTracking()
                .Where(f => f.TenantId == tenantId)
                .Select(f => f.Id)
                .FirstAsync();
        }

        using var client = harness.NewClient(tenantId);

        return await client.GetStringAsync(new Uri($"/files/{id}", UriKind.Relative));
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("No DriveUnion.slnx above the test binaries.");
    }
}
