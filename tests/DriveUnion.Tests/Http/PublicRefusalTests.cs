using System.Net;
using FluentAssertions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// What a link that will not serve says — and, more to the point, what it does not say.
///
/// Revoked, expired, at its cap and never-existed have to be one identical response. Any difference
/// between them is an oracle: a scanner keeps the slugs whose refusal looks different and has found
/// live links without ever downloading one.
/// </summary>
public class PublicRefusalTests
{
    [Fact]
    public async Task Revoked_expired_capped_and_unknown_are_one_indistinguishable_response()
    {
        await using var harness = new PublicSiteHarness();

        harness.SeedLink("rvk00001", isActive: false);
        harness.SeedLink("exp00001", expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        harness.SeedLink("cap00001", maxDownloads: 1, downloadCount: 1);
        // "zzz99999" is never seeded. Eight characters on purpose: a shorter one is rejected by
        // SlugGenerator.IsWellFormed before the database is touched, so it would prove a different
        // and much weaker thing.

        using var client = harness.NewClient();

        var revoked = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/rvk00001");
        var expired = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/exp00001");
        var capped = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/cap00001");
        var unknown = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/zzz99999");

        revoked.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

        // Equality between whole responses, not four assertions that each happen to pass. A future
        // change that leaks one of these apart — a reason in a header, a different card, a
        // Content-Length that differs by the length of the word "expired" — fails right here.
        expired.Should().Be(revoked);
        capped.Should().Be(revoked);
        unknown.Should().Be(revoked);
    }

    [Fact]
    public async Task A_malformed_slug_is_refused_exactly_like_an_unknown_one()
    {
        // The comp's six-character /d/kx91mz never reaches the database: IsWellFormed rejects it
        // first. That shortcut is only safe if it is invisible from outside, which is what this
        // compares.
        await using var harness = new PublicSiteHarness();

        using var client = harness.NewClient();

        var unknown = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/zzz99999");
        var tooShort = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/kx91mz");
        var wrongAlphabet = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/KX91MZQ4");

        tooShort.Should().Be(unknown);
        wrongAlphabet.Should().Be(unknown);
    }

    [Fact]
    public async Task A_slug_carrying_markup_comes_back_escaped_rather_than_as_html()
    {
        // /d/{slug} takes an arbitrary segment from an anonymous stranger and the refusal card
        // echoes the path back into two links. Encoded output is the only thing standing between
        // that and a reflected script on the product's most-shared URL.
        await using var harness = new PublicSiteHarness();

        using var client = harness.NewClient();
        using var response = await client.GetAsync("/d/%3Cbadslug%3E");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("<badslug>");

        // PathString re-encodes on the way out, so the echo is percent-encoded before Razor ever
        // sees it and the angle brackets never become markup.
        body.Should().Contain("/d/%3Cbadslug%3E?lang=en");
    }

    [Fact]
    public async Task The_stream_route_refuses_a_capped_link_exactly_like_an_unknown_one()
    {
        // The card is the landing page's job, but the bytes route has to keep the same secret. A
        // 404 here and a 403 there is the same oracle wearing a different hat.
        await using var harness = new PublicSiteHarness();
        harness.SeedLink("cap00002", maxDownloads: 3, downloadCount: 3);

        using var client = harness.NewClient();

        var capped = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/cap00002/file");
        var unknown = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/zzz99999/file");

        capped.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
        capped.Should().Be(unknown);
    }

    [Fact]
    public async Task A_link_at_its_cap_serves_no_bytes_and_never_opens_a_drive_stream()
    {
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cap00003", maxDownloads: 2, downloadCount: 2);

        using var client = harness.NewClient();
        using var response = await client.GetAsync($"/d/{seeded.Slug}/file");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotEqual(seeded.Content);

        // The cap is spent before Google is called at all, so a capped link costs the operator's
        // quota nothing.
        harness.Drive.Calls.Should().BeEmpty();
        (await harness.DownloadCountAsync(seeded.LinkId)).Should().Be(2, "a refusal is not a download");
    }

    [Fact]
    public async Task The_last_permitted_download_closes_the_link_behind_it()
    {
        // The cap is enforced by the counter the previous request moved, which is the only way the
        // rule can hold across two anonymous requests that share nothing else.
        await using var harness = new PublicSiteHarness();
        var seeded = harness.SeedLink("cap00004", maxDownloads: 1, content: PublicSiteHarness.TestBytes(256));

        using var client = harness.NewClient();

        using var first = await client.GetAsync($"/d/{seeded.Slug}/file");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.Content);

        var second = await HttpResponseSnapshot.GetMaskedAsync(client, $"/d/{seeded.Slug}/file");
        var unknown = await HttpResponseSnapshot.GetMaskedAsync(client, "/d/zzz99999/file");

        second.Should().Be(unknown, "a spent link must be indistinguishable from one that never existed");
    }
}
