using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <param name="Secret">
/// The whole key, drawn once and never again. Null on every render but the one that follows a mint.
/// </param>
/// <param name="S3Secret">The access key id and its secret on two lines, shown once.</param>
public sealed record ApiKeysPageViewModel(
    IReadOnlyList<ApiKeyRowViewModel> Keys,
    IReadOnlyList<S3KeyRowViewModel> S3Keys,
    string? S3Secret,
    string? Notice,
    string? Secret,
    string BaseUrl)
{
    public bool IsEmpty => Keys.Count == 0;

    /// <summary>The address a customer points their program at, so the page can show a whole example.</summary>
    public string ExampleUrl => $"{BaseUrl}/api/v1/files";

    /// <summary>What goes in --endpoint-url. The path is not optional: the panel owns the root.</summary>
    public string S3EndpointUrl => $"{BaseUrl}/s3";
}

public sealed record S3KeyRowViewModel(
    Guid Id,
    string Name,
    string AccessKeyId,
    string ScopeText,
    string CreatedText,
    string LastUsedText,
    string StateText,
    bool IsLive)
{
    public static S3KeyRowViewModel From(S3CredentialSummary credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var now = DateTimeOffset.UtcNow;

        return new S3KeyRowViewModel(
            credential.Id,
            credential.Name,

            // In full, unlike an API key's prefix. This half of the pair is public — it travels in
            // the clear on every signed request — and a customer has to paste it into a config file.
            credential.AccessKeyId,
            credential.Scope == ApiScope.Write ? UiText.ApiKeys.ScopeWrite : UiText.ApiKeys.ScopeRead,
            DisplayFormats.PanelDateTime(credential.CreatedAt),
            credential.LastUsedAt is { } used
                ? DisplayFormats.Relative(used, now)
                : UiText.ApiKeys.NeverUsed,
            credential.RevokedAt is not null ? UiText.ApiKeys.StateRevoked : UiText.ApiKeys.StateLive,
            credential.RevokedAt is null);
    }
}

public sealed record ApiKeyRowViewModel(
    Guid Id,
    string Name,
    string PrefixText,
    string ScopeText,
    string CreatedText,
    string LastUsedText,
    string StateText,
    bool IsLive)
{
    public static ApiKeyRowViewModel From(ApiTokenSummary token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var now = DateTimeOffset.UtcNow;
        var live = token.RevokedAt is null && (token.ExpiresAt is null || now < token.ExpiresAt);

        return new ApiKeyRowViewModel(
            token.Id,
            token.Name,

            // The prefix with the marker in front of it and an ellipsis after: enough for somebody
            // to tell which of their keys a row is, and nothing anybody could present.
            $"{ApiToken.Marker}{token.Prefix}…",
            token.Scope == ApiScope.Write ? UiText.ApiKeys.ScopeWrite : UiText.ApiKeys.ScopeRead,
            DisplayFormats.PanelDateTime(token.CreatedAt),

            // «هرگز» rather than a blank cell, because a key that has never been used is a fact
            // worth reading — it is usually one somebody minted and then lost.
            token.LastUsedAt is { } used
                ? DisplayFormats.Relative(used, now)
                : UiText.ApiKeys.NeverUsed,
            token.RevokedAt is not null
                ? UiText.ApiKeys.StateRevoked
                : token.ExpiresAt is { } end && now >= end
                    ? UiText.ApiKeys.StateExpired
                    : token.ExpiresAt is { } future
                        ? UiText.ApiKeys.StateExpiresOn(DisplayFormats.PanelDateTime(future))
                        : UiText.ApiKeys.StateLive,
            live);
    }
}
