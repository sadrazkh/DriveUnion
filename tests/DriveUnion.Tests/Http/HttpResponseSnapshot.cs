using System.Globalization;

namespace DriveUnion.Tests.Http;

/// <summary>
/// Everything a visitor can observe about one response, in a value that compares by structure.
///
/// It exists so "these refusals are indistinguishable" can be a single equality between whole
/// responses rather than a handful of separate assertions. Four assertions that each happen to pass
/// still let a future change leak one refusal apart through a header nobody thought to check; one
/// equality cannot.
///
/// Only <c>Date</c> is dropped, because it moves on its own.
/// </summary>
public sealed record HttpResponseSnapshot(int StatusCode, string ReasonPhrase, string Headers, string Body)
{
    /// <summary>What a masked slug is replaced with. Deliberately not a legal slug.</summary>
    public const string SlugMask = "{slug}";

    public static async Task<HttpResponseSnapshot> CaptureAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => string.Create(
                CultureInfo.InvariantCulture,
                $"{h.Key}: {string.Join(", ", h.Value)}"));

        return new HttpResponseSnapshot(
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            string.Join("\n", headers),
            body);
    }

    public static async Task<HttpResponseSnapshot> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);

        return await CaptureAsync(response);
    }

    /// <summary>
    /// The same, with the slug the caller asked for replaced by <see cref="SlugMask"/> everywhere it
    /// appears.
    ///
    /// The public layout echoes the requested path back in its <c>hreflang</c> alternates and its
    /// FA/EN link, so two refusals for two different slugs can never be byte-identical. Masking that
    /// one string is what makes the comparison mean what it should: the response is a function of
    /// the slug the visitor typed and of nothing else. Every other difference — a reason in a
    /// header, a different card, a Content-Length that gives the word "expired" away — still fails
    /// the comparison, which is the whole point.
    /// </summary>
    public static async Task<HttpResponseSnapshot> GetMaskedAsync(HttpClient client, string url)
    {
        var slug = url.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
        var snapshot = await GetAsync(client, url);

        return snapshot with
        {
            Headers = snapshot.Headers.Replace(slug, SlugMask, StringComparison.Ordinal),
            Body = snapshot.Body.Replace(slug, SlugMask, StringComparison.Ordinal),
        };
    }
}
