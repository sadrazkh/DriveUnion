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
        var account = await _db.GoogleAccounts
            .FirstOrDefaultAsync(a => a.Email == about.Email, cancellationToken)
            .ConfigureAwait(false);

        if (account is null)
        {
            account = new GoogleAccount
            {
                Id = Guid.CreateVersion7(),
                Email = about.Email,
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
            account.RefreshTokenProtected = _protector.Protect(grant.RefreshToken!);
        }

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
    /// <c>A1</c>, <c>A2</c>, … — the short handle the operator's account cards show. Derived from the
    /// highest one already taken rather than from the row count, so deleting A1 does not hand a
    /// second account the name a screenshot still calls A2.
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
