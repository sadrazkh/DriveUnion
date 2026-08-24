using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The operator's bot token, in the single row <c>TelegramBotSettings</c>, encrypted with the same
/// <see cref="ITokenProtector"/> that protects the Google refresh tokens.
///
/// <para>The discipline is <c>IGoogleOAuthCredentialStore</c>'s, followed rather than reinvented: a
/// read model that cannot express the secret, one accessor to the plaintext for the code that talks
/// to Telegram, and a save that keeps the stored secret when the field arrives empty — so correcting
/// the @username does not mean fetching the token out of @BotFather again.</para>
///
/// <para>The one deliberate difference is where it lives. The Google store is a JSON file and says in
/// its own comment that a single-row table would be stronger; this is the slice where that weakness
/// bites, because a redeploy that loses a Telegram token leaves Telegram delivering to a process
/// that no longer recognises what it is delivering, with nothing in any log to say so.</para>
/// </summary>
public sealed class TelegramBotSettingsStore(
    DriveUnionDbContext db,
    ITokenProtector protector,
    TimeProvider clock,
    ILogger<TelegramBotSettingsStore> logger) : ITelegramBotSettingsStore
{
    public async Task<StoredTelegramBot> ReadAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: false, cancellationToken);

        if (row is null) return Empty;

        // HasToken is true only when a stored token is present *and* still decrypts. One written
        // under a Data Protection key that has since been lost is reported as absent on purpose: the
        // operator's only fix is to paste it again, and a screen claiming the token was set would
        // send them hunting through @BotFather for a fault that is on this side.
        var token = Unprotect(row.BotTokenProtected);

        return new StoredTelegramBot(
            token is not null,
            row.BotUsername,
            row.BotUserId,
            row.UpdatedAt == DateTimeOffset.UnixEpoch ? null : row.UpdatedAt,
            Unprotect(row.WebhookSecretProtected) is not null,
            row.WebhookRegisteredAt);
    }

    public async Task<TelegramWebhookRegistration?> ReadWebhookAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: false, cancellationToken);

        if (row is null) return null;

        // Both or neither. Half a registration cannot authenticate anything, and answering an
        // inbound POST on a path with no secret to compare against is the one arrangement that would
        // turn the endpoint into an anonymous command channel.
        if (Unprotect(row.WebhookPathSegmentProtected) is not { Length: > 0 } segment) return null;
        if (Unprotect(row.WebhookSecretProtected) is not { Length: > 0 } secret) return null;

        return new TelegramWebhookRegistration(segment, secret);
    }

    public async Task SaveWebhookAsync(
        string pathSegment,
        string secret,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathSegment);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var row = await RowAsync(tracked: true, cancellationToken)
                  ?? throw new InvalidOperationException(
                      "There is no Telegram bot row to register a webhook against.");

        row.WebhookPathSegmentProtected = protector.Protect(pathSegment);
        row.WebhookSecretProtected = protector.Protect(secret);
        row.WebhookRegisteredAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        // No segment and no secret. Both are credentials, and this line exists only so that "the
        // webhook was re-registered" is findable in a log.
        logger.LogInformation("A Telegram webhook registration was recorded.");
    }

    public async Task<bool> ClearWebhookAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: true, cancellationToken);

        if (row is null) return false;
        if (row.WebhookPathSegmentProtected is null && row.WebhookSecretProtected is null) return false;

        row.WebhookPathSegmentProtected = null;
        row.WebhookSecretProtected = null;
        row.WebhookRegisteredAt = null;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("The Telegram webhook registration was removed.");

        return true;
    }

    public async Task SaveVerifiedProfileAsync(
        long botUserId,
        string botUsername,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botUsername);

        var row = await RowAsync(tracked: true, cancellationToken)
                  ?? throw new InvalidOperationException("There is no Telegram bot row to verify.");

        // getMe is authoritative for both values, and one of them is a cache key: every stored
        // file_id belongs to a bot id, so a token that turns out to be a different bot has to move
        // this column — which is exactly the cache miss the key was designed to produce.
        row.BotUserId = botUserId;
        row.BotUsername = Normalise(botUsername);
        row.UpdatedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("The Telegram bot identity was confirmed against Telegram.");
    }

    public async Task<string?> ReadBotTokenAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: false, cancellationToken);

        return row is null ? null : Unprotect(row.BotTokenProtected);
    }

    public async Task<StoredTelegramBot> SaveAsync(
        string? botToken,
        string? botUsername,
        Guid? updatedByUserId,
        CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: true, cancellationToken);

        if (row is null)
        {
            // The migration seeds this row, so its absence means somebody removed it. Writing it
            // back is better than refusing: the screen's whole job is to make the bot configurable
            // without a terminal on the box.
            row = new TelegramBotSettings { Id = TelegramBotSettings.SingletonId };
            db.TelegramBotSettings.Add(row);
        }

        var surviving = Unprotect(row.BotTokenProtected);
        var token = string.IsNullOrWhiteSpace(botToken) ? surviving : botToken.Trim();

        // Re-protecting the surviving token rather than copying its ciphertext across re-encrypts it
        // under whatever key is current, so a value written years ago is carried forward every time
        // the operator touches this form.
        row.BotTokenProtected = token is null ? null : protector.Protect(token);
        row.BotUserId = TelegramLinkSecrets.BotUserIdFromToken(token);
        row.BotUsername = Normalise(botUsername);
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedByUserId = updatedByUserId;

        await db.SaveChangesAsync(cancellationToken);

        // No values, not even the @username. This line exists so "the bot changed" is findable in a
        // log; nothing about it should ever need the credential itself.
        logger.LogInformation("The Telegram bot settings were updated from the panel.");

        return new StoredTelegramBot(token is not null, row.BotUsername, row.BotUserId, row.UpdatedAt);
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: true, cancellationToken);

        if (row is null) return false;
        if (row.BotTokenProtected is null
            && row.BotUsername is null
            && row.WebhookSecretProtected is null)
        {
            return false;
        }

        row.BotTokenProtected = null;
        row.BotUsername = null;
        row.BotUserId = null;

        // The registration goes with the bot. A path and a secret left behind would keep answering
        // updates for a token this process no longer has, which is the one state where the endpoint
        // is live and nothing behind it can do anything.
        row.WebhookPathSegmentProtected = null;
        row.WebhookSecretProtected = null;
        row.WebhookRegisteredAt = null;

        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedByUserId = null;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("The stored Telegram bot was removed from the panel.");

        return true;
    }

    private static readonly StoredTelegramBot Empty = new(false, null, null, null);

    /// <summary>Accepts «@name» and «name» alike, because the operator will paste either.</summary>
    private static string? Normalise(string? username)
    {
        var trimmed = username?.Trim().TrimStart('@');

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private Task<TelegramBotSettings?> RowAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked
            ? db.TelegramBotSettings
            : db.TelegramBotSettings.AsNoTracking();

        return query.FirstOrDefaultAsync(
            s => s.Id == TelegramBotSettings.SingletonId,
            cancellationToken);
    }

    private string? Unprotect(string? cipher) =>
        string.IsNullOrEmpty(cipher) ? null : protector.Unprotect(cipher);
}
