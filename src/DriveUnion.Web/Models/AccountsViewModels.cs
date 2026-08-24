using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One card on «اکانت‌های گوگل». This is operator-only screen data — the email on it is the
/// operator's own, and it must never be reachable by a tenant session.
/// </summary>
public sealed record AccountCardViewModel(
    Guid Id,
    string Email,
    string Label,
    string StatusText,
    GoogleAccountStatus Status,
    string UsedText,
    string TotalText,
    int UsedPercent)
{
    public static AccountCardViewModel From(GoogleAccountSummary account)
    {
        var percent = account.QuotaTotalBytes <= 0
            ? 0
            : (int)Math.Clamp(account.QuotaUsedBytes * 100 / account.QuotaTotalBytes, 0, 100);

        return new AccountCardViewModel(
            account.Id,
            account.Email,
            account.Label,
            account.Status switch
            {
                GoogleAccountStatus.Healthy => UiText.Accounts.StatusHealthy,
                GoogleAccountStatus.Paused => UiText.Accounts.StatusPaused,
                _ => UiText.Accounts.StatusDisconnected,
            },
            account.Status,
            DisplayFormats.Bytes(account.QuotaUsedBytes),
            DisplayFormats.Bytes(account.QuotaTotalBytes),
            percent);
    }
}

public sealed record AccountsPageViewModel(
    IReadOnlyList<AccountCardViewModel> Accounts,
    string? Notice,
    string? Error,
    bool ConsentConfigured,
    GoogleSetupViewModel Setup);

/// <summary>
/// What the operator types to give the panel a Google OAuth client.
///
/// <see cref="ClientSecret"/> is nullable and blank means «unchanged», which is the only shape a
/// form can have when the value it edits can be written but never read back.
/// </summary>
public sealed class GoogleCredentialsForm
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RedirectUri { get; set; }
}

/// <summary>
/// The setup panel on «اکانت‌های گوگل»: what is in force, where each part came from, and the
/// instructions for producing the parts that are missing.
///
/// This is the first screen a new operator meets, so it is written as instructions rather than as an
/// error. Everything on it that a human has to copy into Google Cloud is rendered from this running
/// panel — <see cref="RedirectUri"/> above all — because a redirect URI the operator assembles by
/// hand is the single most common way a first connection fails, and Google's answer to a mismatch
/// says nothing useful.
/// </summary>
public sealed record GoogleSetupViewModel(
    bool IsComplete,
    string ClientId,
    GoogleCredentialSource ClientIdSource,
    bool SecretIsSet,
    GoogleCredentialSource SecretSource,
    string RedirectUri,
    GoogleCredentialSource RedirectUriSource,
    string SuggestedRedirectUri,
    string FormClientId,
    string FormRedirectUri,
    bool HasStoredClient,
    bool StoredSecretIsSet,
    string? StoredUpdatedText,
    bool ConfigurationOutranksPanel)
{
    /// <summary>
    /// The restricted scope this product asks for. Rendered on the screen because it is what the
    /// operator will be warned about at Google's consent screen, and meeting that warning without
    /// having been told it is coming looks like something has gone wrong.
    /// </summary>
    public static string Scope => GoogleOAuthUrls.DriveScope;

    public static GoogleSetupViewModel From(GoogleOAuthCredentialState state, string suggestedRedirectUri)
    {
        ArgumentNullException.ThrowIfNull(state);

        var stored = state.Stored;

        return new GoogleSetupViewModel(
            state.IsComplete,
            state.ClientId.Value,
            state.ClientId.Source,
            state.ClientSecretSource is not GoogleCredentialSource.None,
            state.ClientSecretSource,

            // The effective redirect URI when there is one, and the suggestion when there is not.
            // Never a placeholder: whatever is shown here is what the operator is being told to
            // register with Google, so it has to be a URI that would work.
            state.RedirectUri.IsSet ? state.RedirectUri.Value : suggestedRedirectUri,
            state.RedirectUri.Source,
            suggestedRedirectUri,

            // The form edits the panel's own copy, so it is pre-filled from the panel's own copy —
            // not from the effective value, which may be the environment's and which typing over
            // would not change.
            stored?.ClientId ?? string.Empty,
            stored?.RedirectUri ?? suggestedRedirectUri,
            stored is not null,
            stored?.HasClientSecret ?? false,
            stored is null ? null : DisplayFormats.PanelDateTime(stored.UpdatedAt),
            state.ConfigurationOutranksPanel);
    }

    /// <summary>Where a value comes from, in the words the screen uses for it.</summary>
    public static string SourceText(GoogleCredentialSource source) => source switch
    {
        GoogleCredentialSource.Configuration => UiText.Accounts.SourceConfiguration,
        GoogleCredentialSource.Panel => UiText.Accounts.SourcePanel,
        _ => UiText.Accounts.SourceUnset,
    };
}

/// <summary>
/// The one card the OAuth popup ever shows: how the consent flow ended, in the window the operator
/// is looking at, before it tells the accounts page and closes.
///
/// <paramref name="Hint"/> is for the configuration keys — the failure this machine meets first, and
/// the only one where naming the missing setting is worth more than an apology.
/// </summary>
public sealed record ConnectPopupViewModel(
    bool Succeeded,
    string Title,
    string Message,
    string? Hint);
