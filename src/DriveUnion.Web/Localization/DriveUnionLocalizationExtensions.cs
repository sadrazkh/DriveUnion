using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace DriveUnion.Web.Localization;

/// <summary>
/// How a request gets a language, before a byte of HTML is written.
///
/// The framework's <c>RequestLocalizationMiddleware</c> does the resolving. We supply the providers
/// and their order, and nothing else about localisation comes from the framework — the strings
/// themselves are <see cref="UiText"/>, for the reasons written there.
/// </summary>
public static class DriveUnionLocalizationExtensions
{
    /// <summary>The <c>?lang=</c> the public download page has always answered. One spelling, two pages.</summary>
    public const string LanguageQueryKey = "lang";

    /// <summary>
    /// Registers the options <c>app.UseRequestLocalization()</c> reads.
    ///
    /// Program.cs needs two calls, this one beside the other service registrations and
    /// <c>app.UseRequestLocalization()</c> in the pipeline before anything renders.
    /// </summary>
    public static IServiceCollection AddDriveUnionLocalization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<RequestLocalizationOptions>(Configure);

        return services;
    }

    /// <summary>
    /// The panel's culture policy, in one method so a test can ask the same object the middleware
    /// will be handed rather than a second transcription of it.
    /// </summary>
    internal static void Configure(RequestLocalizationOptions options)
    {
        // Formatting is pinned to the invariant culture in both languages, and that is not an
        // oversight.
        //
        // The panel's numbers are not the culture's to format: DisplayFormats writes «18.4 MB» and
        // «۱۴۰۵/۰۵/۳۱» with explicit invariant formatting, and PersianDigits translates digits per
        // value because the rule is about direction rather than language. Let CurrentCulture become
        // fa-IR and every remaining ToString() in the product quietly switches its decimal point to
        // «٫» and its group separator to «٬» — including the ones inside a mono, dir="ltr" readout
        // an operator copies into a Google support ticket. So: SupportedCultures is the invariant
        // culture alone, no request can move it, and only the UI culture varies.
        options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, PanelCulture.Persian);
        options.SupportedCultures = [CultureInfo.InvariantCulture];
        options.SupportedUICultures = [.. PanelCulture.Supported];

        // Off, and it is the public download page that decides it. That page resolves its own
        // language in PublicDownloadController and does not read this cookie yet, so a middleware
        // that stamped Content-Language on every response would be telling the truth about the
        // panel and lying about /d/{slug}. See Localization/README.md — turning this on is part of
        // the same change that unifies the two.
        options.ApplyCurrentCultureToResponseHeaders = false;

        // The default order is query, then cookie, then header. The panel wants the cookie first,
        // and the difference matters:
        //
        //   - In the panel the cookie is only ever written by the language switch in the shell, so
        //     it *is* the explicit choice. A ?lang= arriving on a panel URL is a link somebody was
        //     sent or a query string that survived a redirect, and it must not silently undo what
        //     the operator picked. The switch writes the cookie, so the switch always wins.
        //   - The public download page orders these the other way round for the opposite reason:
        //     over there ?lang= is the visitor clicking FA/EN, because there is nothing else to
        //     click. Both rules say the same thing — the explicit act outranks the ambient guess.
        //
        // Accept-Language last, and Persian below that, because a browser configured years ago in
        // another country is the weakest evidence on the request.
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
        options.RequestCultureProviders.Add(new QueryStringRequestCultureProvider
        {
            // One key for both halves: ?lang=en is a language, and the formatting culture is pinned
            // above regardless, so there is nothing a second key could usefully say.
            QueryStringKey = LanguageQueryKey,
            UIQueryStringKey = LanguageQueryKey,
        });
        options.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
    }
}
