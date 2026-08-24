using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Web.Localization;

namespace DriveUnion.Web.Models;

/// <summary>
/// One card on «اکانت‌های گوگل». This is operator-only screen data — the email on it is the
/// operator's own, and it must never be reachable by a tenant session.
/// </summary>
/// <param name="ClientText">
/// Which OAuth client connected this account, in words. It is on the card because a refresh token
/// can only be presented by the client that issued it: with two clients in the panel, "which one"
/// is the difference between an account that can be repaired and one that cannot.
/// </param>
/// <param name="FailureReason">
/// Google's own words for why this account last stopped working, or null when nothing has. Not
/// translated and not paraphrased — it is a diagnostic the operator will search for, and it is shown
/// here and nowhere else because it can name a session URI or an address a tenant must never learn.
/// </param>
public sealed record AccountCardViewModel(
    Guid Id,
    string Email,
    string Label,
    string StatusText,
    GoogleAccountStatus Status,
    string UsedText,
    string TotalText,
    int UsedPercent,
    string ClientText,
    string? ClientId,
    bool ClientIsUsable,
    string? FailureReason,
    string? FailureAtText)
{
    /// <summary>
    /// True when this card's own repair is the thing to do next, which is what promotes «اتصال
    /// دوباره» from one small button among three to the card's primary action.
    ///
    /// A disconnected account is one whose refresh token Google stopped honouring — the seven-day
    /// expiry a Testing consent screen imposes is the usual cause. Its files and their public links
    /// are still served through it, so nothing about it is disposable; it just needs a new grant.
    /// </summary>
    public bool NeedsReconnect => Status is GoogleAccountStatus.Disconnected;

    public static AccountCardViewModel From(GoogleAccountSummary account, GoogleAccountClientNote? note)
    {
        ArgumentNullException.ThrowIfNull(account);

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
            percent,
            ClientSentence(note),
            note?.ClientIsUsable is false ? note.ClientId : null,
            note?.ClientIsUsable ?? true,
            note?.LastFailureReason,
            note?.LastFailureAt is { } at ? DisplayFormats.PanelDateTime(at) : null);
    }

    /// <summary>
    /// The four things a card can say about its client, and none of them is a client id on its own:
    /// seventy characters of base64 tells an operator nothing they can act on.
    /// </summary>
    private static string ClientSentence(GoogleAccountClientNote? note) => note switch
    {
        null or { ClientId: null } => UiText.Accounts.ClientNotRecorded,
        { ClientLabel: { } label } => UiText.Accounts.ClientNamed(label),
        { FromConfiguration: true } => UiText.Accounts.ClientFromConfiguration,
        _ => UiText.Accounts.ClientMissing,
    };
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
    /// <summary>
    /// The client being edited, or null to add one. A hidden field on each stored client's own form;
    /// the «add» form does not render it at all, so a blank id cannot be an accidental edit.
    /// </summary>
    public Guid? Id { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RedirectUri { get; set; }
}

/// <summary>
/// One stored OAuth client, as a row on the setup panel.
/// </summary>
/// <param name="AccountCount">
/// How many accounts are refreshed with this client. It is on the row so the operator learns that
/// removal is refused <em>before</em> pressing remove, rather than only from the sentence afterwards.
/// </param>
public sealed record GoogleClientRowViewModel(
    Guid Id,
    string Label,
    string ClientId,
    string RedirectUri,
    bool SecretIsSet,
    bool IsDefault,
    string UpdatedText,
    int AccountCount);

/// <summary>
/// The setup panel on «اکانت‌های گوگل»: what is in force, where each part came from, every client
/// the panel is holding, and the instructions for producing one.
///
/// This is the first screen a new operator meets, so it is written as instructions rather than as an
/// error. Everything on it that a human has to copy into Google Cloud is rendered from this running
/// panel — <see cref="RedirectUri"/> above all — because a redirect URI the operator assembles by
/// hand is the single most common way a first connection fails, and Google's answer to a mismatch
/// says nothing useful.
/// </summary>
/// <param name="Clients">
/// Every stored client, oldest first. More than one exists because a Google Cloud project has its
/// own quota and its own consent screen, and because an account connected under one client cannot be
/// refreshed by another — so replacing a client is not something that can be done in place.
/// </param>
public sealed record GoogleSetupViewModel(
    bool IsComplete,
    string ClientId,
    GoogleCredentialSource ClientIdSource,
    bool SecretIsSet,
    GoogleCredentialSource SecretSource,
    string RedirectUri,
    GoogleCredentialSource RedirectUriSource,
    string SuggestedRedirectUri,
    string FormRedirectUri,
    bool ConfigurationOutranksPanel,
    IReadOnlyList<GoogleClientRowViewModel> Clients)
{
    /// <summary>
    /// The restricted scope this product asks for. Rendered on the screen because it is what the
    /// operator will be warned about at Google's consent screen, and meeting that warning without
    /// having been told it is coming looks like something has gone wrong.
    /// </summary>
    public static string Scope => GoogleOAuthUrls.DriveScope;

    /// <summary>
    /// True when the operator's next connection will use the server's configuration and the stored
    /// clients are only there to refresh the accounts already bound to them. The screen says so,
    /// because otherwise promoting a stored client looks like it should have an effect and has none.
    /// </summary>
    public bool ConfigurationSuppliesTheClient =>
        ClientIdSource is GoogleCredentialSource.Configuration;

    public static GoogleSetupViewModel From(
        GoogleOAuthCredentialState state,
        string suggestedRedirectUri,
        IReadOnlyDictionary<string, int> accountsPerClientId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(accountsPerClientId);

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

            // Only the redirect URI is pre-filled: the «add» form adds, so a client id and a secret
            // in it would be somebody else's. Editing a client is done on that client's own row,
            // from that client's own values.
            suggestedRedirectUri,
            state.ConfigurationOutranksPanel,
            [.. state.StoredClients.Select(client => new GoogleClientRowViewModel(
                client.Id,
                client.Label,
                client.ClientId,
                client.RedirectUri,
                client.HasClientSecret,
                client.IsDefault,
                DisplayFormats.PanelDateTime(client.UpdatedAt),
                accountsPerClientId.GetValueOrDefault(client.ClientId)))]);
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
