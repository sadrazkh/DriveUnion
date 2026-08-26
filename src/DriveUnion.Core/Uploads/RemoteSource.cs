namespace DriveUnion.Core.Uploads;

/// <summary>Why a link cannot be fetched. Every one of these is said to the customer.</summary>
public enum RemoteSourceRefusal
{
    None = 0,

    /// <summary>Not a URL at all, or not an absolute one.</summary>
    Malformed = 1,

    /// <summary>Something other than http or https — <c>file:</c>, <c>gopher:</c>, <c>ftp:</c>.</summary>
    UnsupportedScheme = 2,

    /// <summary>A username or password in the URL itself.</summary>
    CarriesCredentials = 3,

    /// <summary>The address is one this server will not dial. See <see cref="RemoteAddressPolicy"/>.</summary>
    AddressRefused = 4,

    /// <summary>Nothing answered, or what answered was not a success.</summary>
    Unreachable = 5,

    /// <summary>The source would not say how big the file is.</summary>
    LengthUnknown = 6,

    /// <summary>Bigger than the plan's per-file ceiling, or than the workspace has room for.</summary>
    TooLarge = 7,
}

/// <summary>
/// The URL checks that need no network, done before one is opened.
///
/// <para>Separate from <see cref="RemoteAddressPolicy"/> because they answer different questions:
/// this one is about the URL a customer typed, that one is about the address it turns out to mean.
/// Both have to pass, and neither is sufficient — a perfectly well-formed <c>https://</c> URL can
/// still resolve to the metadata service, which is why the address check happens at connect time
/// and not here.</para>
/// </summary>
public static class RemoteSource
{
    /// <summary>The longest URL that will be accepted, so the column and the log line are bounded.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>
    /// Whether this is a URL worth trying, and why not when it is not.
    /// </summary>
    public static RemoteSourceRefusal Inspect(string? url, out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return RemoteSourceRefusal.Malformed;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return RemoteSourceRefusal.Malformed;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            // file: reads the server's own disk. gopher: and friends have been used to speak other
            // protocols entirely through a URL fetcher. Two schemes, named, and nothing else.
            return RemoteSourceRefusal.UnsupportedScheme;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // http://user:pass@host — the credentials would be logged, stored on the job row, and
            // sent to whatever the host turns out to be. Refused rather than stripped, because a
            // link that needs credentials is a link that will not work without them and the customer
            // should find that out now.
            return RemoteSourceRefusal.CarriesCredentials;
        }

        parsed = uri;
        return RemoteSourceRefusal.None;
    }
}
