using DriveUnion.Core.Sharing;
using FluentAssertions;

namespace DriveUnion.Tests.Links;

/// <summary>
/// What the public page may draw, and — the half that matters — what it may not.
///
/// <para>The route this governs sends <c>Content-Disposition: inline</c>, which is the difference
/// between a browser saving a file and running it. Everything below is about one rule: the list is
/// of the permitted, so a type nobody thought about is refused by default rather than served by
/// default.</para>
/// </summary>
public class PreviewRulesTests
{
    private const long Small = 1024;

    [Theory]
    [InlineData("image/png", PreviewKind.Image)]
    [InlineData("image/jpeg", PreviewKind.Image)]
    [InlineData("image/webp", PreviewKind.Image)]
    [InlineData("video/mp4", PreviewKind.Video)]
    [InlineData("video/webm", PreviewKind.Video)]
    [InlineData("audio/mpeg", PreviewKind.Audio)]
    [InlineData("application/pdf", PreviewKind.Document)]
    public void The_types_a_page_can_show_are_shown(string mimeType, PreviewKind expected) =>
        Previews.For(mimeType, Small, isEncrypted: false).Should().Be(expected);

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("application/javascript")]
    [InlineData("text/javascript")]
    public void Anything_that_could_run_on_this_origin_is_refused(string mimeType)
    {
        // These are not refused because somebody listed them. They are refused because they are not
        // on the list — which is the property, and the reason the list is of the permitted. An SVG
        // is an image and would otherwise belong; it carries script, so it does not.
        Previews.For(mimeType, Small, isEncrypted: false).Should().Be(PreviewKind.None);
        Previews.MayBeInline(mimeType).Should().BeFalse();
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("application/zip")]
    [InlineData("application/msword")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_gets_the_placeholder(string? mimeType) =>
        Previews.For(mimeType, Small, isEncrypted: false).Should().Be(PreviewKind.None);

    [Fact]
    public void The_half_after_the_semicolon_does_not_smuggle_a_type_past_the_list()
    {
        // A recorded type is «text/html; charset=utf-8» as often as it is «text/html», and a list of
        // bare strings compared with == would let the first one through while refusing the second.
        Previews.MayBeInline("text/html; charset=utf-8").Should().BeFalse();
        Previews.MayBeInline("image/svg+xml;charset=utf-8").Should().BeFalse();

        // And the same folding has to work in the direction that permits, or a perfectly ordinary
        // upload from a browser that writes its types in capitals gets no preview.
        Previews.For("IMAGE/PNG", Small, isEncrypted: false).Should().Be(PreviewKind.Image);
        Previews.For(" image/png ", Small, isEncrypted: false).Should().Be(PreviewKind.Image);
    }

    [Fact]
    public void A_locked_file_is_never_previewed()
    {
        // The bytes are ciphertext. Every element the page could draw would fail, and a broken image
        // is not a better answer than the placeholder — it is a worse one, because it looks like the
        // product is broken rather than like the file is locked.
        Previews.For("image/png", Small, isEncrypted: true).Should().Be(PreviewKind.None);
        Previews.For("video/mp4", Small, isEncrypted: true).Should().Be(PreviewKind.None);
    }

    [Fact]
    public void A_big_file_is_offered_rather_than_poured_into_the_page()
    {
        var ceiling = Previews.MostBytesToShowWhole;

        Previews.For("video/mp4", ceiling, isEncrypted: false).Should().Be(PreviewKind.Video);

        // One byte past, and it is the button instead. This is the whole of what stops a preview —
        // which deliberately does not spend a download — from being a way around a link's cap: a
        // capped link can leak 25 MB per request rather than the 214 GB file behind it.
        Previews.For("video/mp4", ceiling + 1, isEncrypted: false).Should().Be(PreviewKind.None);
        Previews.For("image/png", ceiling + 1, isEncrypted: false).Should().Be(PreviewKind.None);
        Previews.For("application/pdf", ceiling + 1, isEncrypted: false).Should().Be(PreviewKind.None);
    }

    [Fact]
    public void The_inline_question_and_the_size_question_are_asked_separately()
    {
        // MayBeInline answers what the disposition turns on — can this execute here — and nothing
        // else. Folding the size into it would mean the route's two refusals were one test, and a
        // later change to either would silently move the other.
        Previews.MayBeInline("image/png").Should().BeTrue();
        Previews.For("image/png", Previews.MostBytesToShowWhole + 1, isEncrypted: false)
            .Should().Be(PreviewKind.None);
    }
}
