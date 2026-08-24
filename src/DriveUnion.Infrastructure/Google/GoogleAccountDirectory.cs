using System.Globalization;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The operator's pool of Google accounts.
///
/// There is no tenant anywhere in this file and there must never be one: these accounts are the
/// operator's, customers never authenticate with Google, and a customer must never learn which
/// account holds their file.
/// </summary>
public sealed class GoogleAccountDirectory : IGoogleAccountDirectory
{
    private readonly DriveUnionDbContext _db;
    private readonly IGoogleTokenService _tokens;
    private readonly IGoogleAboutReader _about;
    private readonly IDriveClient _drive;
    private readonly ITokenProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleAccountDirectory> _logger;

    public GoogleAccountDirectory(
        DriveUnionDbContext db,
        IGoogleTokenService tokens,
        IGoogleAboutReader about,
        IDriveClient drive,
        ITokenProtector protector,
        TimeProvider timeProvider,
        ILogger<GoogleAccountDirectory> logger)
    {
        _db = db;
        _tokens = tokens;
        _about = about;
        _drive = drive;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GoogleAccountSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var accounts = await _db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new GoogleAccountSummary(
                a.Id,
                a.Email,
                a.Label,
                a.Status,
                a.QuotaTotalBytes,
                a.QuotaUsedBytes,
                a.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered here rather than in the query. This table holds the operator's two or three
        // accounts and the whole projection is already materialised, so pushing the sort to the
        // database buys nothing and costs the query its portability.
        return [.. accounts.OrderBy(a => a.CreatedAt)];
    }

    public async Task<Guid> ConnectAsync(
        string authorizationCode,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var grant = await _tokens
            .ExchangeAuthorizationCodeAsync(authorizationCode, redirectUri, cancellationToken)
            .ConfigureAwait(false);

        // The address has to come from Google. It is the only handle that survives a reconnection,
        // and asking the operator to type it invites a typo that silently creates a second row for
        // an account that already exists.
        var about = await _about.GetAboutAsync(grant.AccessToken, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        // Identity first, address second, and the order is the point. Gmail treats
        // archive.main@gmail.com, archive.main+cold@gmail.com and archivemain@gmail.com as one
        // mailbox and echoes back whichever spelling was typed at the consent screen, so matching on
        // the address alone lets one account arrive twice — two labels, and a pool that believes it
        // has five terabytes it does not have. Drive's permissionId is the same string either way.
        //
        // The address is still consulted, for the rows written before permissionId was stored. They
        // match by address once and are backfilled below, so the fallback retires itself.
        var account = about.PermissionId is { Length: > 0 } identity
            ? await _db.GoogleAccounts
                .FirstOrDefaultAsync(
                    a => a.GoogleUserId == identity || (a.GoogleUserId == null && a.Email == about.Email),
                    cancellationToken)
                .ConfigureAwait(false)
            : await _db.GoogleAccounts
                .FirstOrDefaultAsync(a => a.Email == about.Email, cancellationToken)
                .ConfigureAwait(false);

        if (account is null)
        {
            account = new GoogleAccount
            {
                Id = Guid.CreateVersion7(),
                Email = about.Email,
                GoogleUserId = about.PermissionId,
                Label = await NextLabelAsync(cancellationToken).ConfigureAwait(false),
                RefreshTokenProtected = _protector.Protect(grant.RefreshToken!),
                CreatedAt = now,
            };

            _db.GoogleAccounts.Add(account);
        }
        else
        {
            // Reconnecting an existing account replaces its credentials rather than adding a row.
            // The unique index on Email would reject the duplicate anyway, and the operator's actual
            // intent — "this account stopped working, here it is again" — is this.
            //
            // Label is deliberately not touched. It is how the operator tells two accounts apart on
            // the cards, it is the handle they use in a support conversation, and every StoredFile
            // that already lives here points at this row's Id — so a reconnection that renumbered
            // anything would move files under the operator without moving a byte.
            account.RefreshTokenProtected = _protector.Protect(grant.RefreshToken!);

            // Backfill, so a row that predates this column stops depending on the address fallback
            // the moment it is reconnected once.
            account.GoogleUserId ??= about.PermissionId;

            // And take the spelling Google reports now. Matching found this row by identity, so a
            // different address here means the operator approved an alias of the same mailbox — the
            // card should read what they will see in Google, not the spelling used months ago.
            account.Email = about.Email;
        }

        // The client that issued this refresh token, and the only one that will ever be able to
        // present it. Written on a reconnection too, and not just on a new row: reconnecting under a
        // different client is exactly how an operator moves an account from one Google project to
        // another, and a stale binding here would make the account unrefreshable an hour later —
        // with the panel blaming the consent screen.
        account.OAuthClientId = grant.ClientId;

        // Whatever the account last failed with is answered by this grant.
        account.LastFailureReason = null;
        account.LastFailureAt = null;

        account.AccessTokenProtected = _protector.Protect(grant.AccessToken);
        account.AccessTokenExpiresAt = grant.ExpiresAt;
        account.QuotaTotalBytes = about.LimitBytes;
        account.QuotaUsedBytes = about.UsageBytes;
        account.Status = GoogleAccountStatus.Healthy;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Connected Google account {AccountId} ({Label}).",
            account.Id,
            account.Label);

        return account.Id;
    }

    public async Task<bool> DisconnectAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            return false;
        }

        account.Status = GoogleAccountStatus.Disconnected;

        // The refresh token stays. Revoking it at Google would kill every live /d/{slug} backed by
        // this account instantly and with no way back, because M1 has no way to move the files
        // somewhere else. Disconnecting takes the account out of upload routing; it does not take
        // customers' download links away from them.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Disconnected Google account {AccountId}. Its token was not revoked — existing download "
            + "links still resolve through it.",
            accountId);

        return true;
    }

    public async Task RefreshQuotaAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var quota = await _drive.GetStorageQuotaAsync(accountId, cancellationToken).ConfigureAwait(false);

        var account = await _db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new DriveAccountUnavailableException($"Google account {accountId} is not in the pool.");

        account.QuotaTotalBytes = quota.LimitBytes;
        account.QuotaUsedBytes = quota.UsageBytes;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>A1</c>, <c>A2</c>, … — the short handle the operator's account cards show.
    ///
    /// <para><b>The next label is one past the highest ever issued, not the row count, and gaps are
    /// never filled.</b> Connect three, disconnect A2, connect a fourth: the fourth is A4. A2's row
    /// survives its disconnection — <see cref="DisconnectAsync"/> only changes
    /// <see cref="GoogleAccount.Status"/>, because its files and their public links are still served
    /// through it — so its number stays taken, and a count-based rule would have handed the new
    /// account the name «A2» while a card labelled A2 sat directly above it. On a screen where the
    /// label <em>is</em> how the operator tells accounts apart, that is worse than a gap. M2 §2 says
    /// the same thing about <c>ShortCode</c> for the same reason: the label outlives the account in
    /// old job rows and in support conversations.</para>
    ///
    /// <para>Only labels shaped <c>A</c>-and-a-number are counted. Nothing in the panel writes any
    /// other shape today — there is no rename — so this is defensiveness rather than policy: an
    /// unrecognisable label reserves no number, and the sequence carries on from whatever else is
    /// there rather than throwing or guessing.</para>
    ///
    /// <para>Two connections racing here would both read the same highest number. Nothing serialises
    /// them, and nothing needs to: a consent flow is one operator in one browser answering Google,
    /// and there is no unique index on <see cref="GoogleAccount.Label"/> to turn the race into a
    /// failure the operator would have to understand.</para>
    /// </summary>
    private async Task<string> NextLabelAsync(CancellationToken cancellationToken)
    {
        var labels = await _db.GoogleAccounts
            .AsNoTracking()
            .Select(a => a.Label)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var highest = 0;
        foreach (var label in labels)
        {
            if (label.Length > 1
                && label[0] is 'A' or 'a'
                && int.TryParse(
                    label.AsSpan(1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                && number > highest)
            {
                highest = number;
            }
        }

        return $"A{highest + 1}";
    }
}
