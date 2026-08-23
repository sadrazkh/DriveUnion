using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// Account linking, in the two legs the design settles on: the panel hands out a deep link, the bot
/// answers it with six digits, and <b>the binding is written by the authenticated panel request that
/// carries those digits back</b>.
///
/// <para>The reason for the second leg is not interception. It is the customer who screenshots their
/// settings page into a support conversation, which happens in the first month of every product:
/// whoever sees that screenshot inside the token's lifetime could otherwise bind <em>their</em>
/// Telegram account to <em>that customer's</em> tenant and read every file the tenant owns.
/// Shortening the lifetime narrows the window and does not close it, because the screenshot and the
/// "it's not working" message arrive together. Requiring the settings page of the account being
/// bound closes it: possession of the link alone buys a stranger six digits and nothing else.</para>
/// </summary>
public sealed class TelegramLinkService(
    DriveUnionDbContext db,
    ITelegramBotSettingsStore bot,
    TimeProvider clock,
    ILogger<TelegramLinkService> logger) : ITelegramLinkService
{
    /// <summary>
    /// Ten minutes. Long enough to walk to a phone, short enough that a link left on a screen is
    /// dead before anybody wanders past it — and the second leg means the window is not the control.
    /// </summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    public async Task<TelegramLinkState> DescribeAsync(
        Guid appUserId,
        CancellationToken cancellationToken)
    {
        var settings = await bot.ReadAsync(cancellationToken);
        var configured = settings.HasToken && !string.IsNullOrEmpty(settings.BotUsername);

        var account = await db.TelegramAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AppUserId == appUserId, cancellationToken);

        if (account is not null)
        {
            return new TelegramLinkState(
                configured,
                settings.BotUsername,
                new TelegramLinkedAccount(
                    account.Username,
                    account.DisplayName,
                    account.LinkedAt,
                    account.DeliveryStatus),
                null);
        }

        var now = clock.GetUtcNow();
        var pending = await PendingAsync(appUserId, tracked: false, cancellationToken);

        var live = pending is not null
            && pending.ExpiresAt > now
            && pending.Attempts < TelegramLinkToken.MaxAttempts;

        return new TelegramLinkState(
            configured,
            settings.BotUsername,
            null,
            live
                ? new TelegramPendingLink(
                    pending!.ExpiresAt,
                    pending.ConfirmationCodeHash is not null,
                    TelegramLinkToken.MaxAttempts - pending.Attempts)
                : null);
    }

    public async Task<TelegramLinkStart> StartAsync(Guid appUserId, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == appUserId)
            .Select(u => new { u.TenantId, u.IsOperator })
            .FirstOrDefaultAsync(cancellationToken);

        // A user with no tenant has nothing for the bot to show. Refusing here as well as in the
        // resolver is deliberate: this one keeps the row out of the table, and the resolver's keeps
        // a row that got in anyway from resolving to anything.
        if (user is null || user.IsOperator || user.TenantId is not { } tenantId || tenantId == Guid.Empty)
        {
            return new TelegramLinkStart(TelegramLinkStartStatus.NoTenant, null, null);
        }

        var settings = await bot.ReadAsync(cancellationToken);
        if (!settings.HasToken || string.IsNullOrEmpty(settings.BotUsername))
        {
            return new TelegramLinkStart(TelegramLinkStartStatus.BotNotConfigured, null, null);
        }

        if (await db.TelegramAccounts.AnyAsync(a => a.AppUserId == appUserId, cancellationToken))
        {
            return new TelegramLinkStart(TelegramLinkStartStatus.AlreadyLinked, null, null);
        }

        // At most one live request per user. It is what lets the confirming POST say "the pending
        // request" without ambiguity, and it means pressing the button twice replaces the link
        // rather than leaving two working ones behind.
        await db.TelegramLinkTokens
            .Where(t => t.AppUserId == appUserId && t.ConsumedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var token = TelegramLinkSecrets.NewToken();

        db.TelegramLinkTokens.Add(new TelegramLinkToken
        {
            Id = Guid.CreateVersion7(),
            AppUserId = appUserId,
            TokenHash = TelegramLinkSecrets.HashToken(token),
            CreatedAt = now,
            ExpiresAt = now + TokenLifetime,
        });

        await db.SaveChangesAsync(cancellationToken);

        return new TelegramLinkStart(
            TelegramLinkStartStatus.Issued,
            $"https://t.me/{settings.BotUsername}?start={token}",
            now + TokenLifetime);
    }

    public async Task<TelegramStartOutcome> PresentAsync(
        TelegramStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bound = await db.TelegramAccounts
            .AsNoTracking()
            .AnyAsync(a => a.TelegramUserId == request.TelegramUserId, cancellationToken);

        if (string.IsNullOrEmpty(request.Token))
        {
            // A bare /start. Bound or not, this says nothing about anybody else — the sender is
            // being told about their own account, which they already have.
            return bound
                ? new TelegramStartOutcome(
                    TelegramStartStatus.AlreadyLinked, TelegramMessages.AlreadyLinked, null)
                : new TelegramStartOutcome(
                    TelegramStartStatus.Stranger, TelegramMessages.Stranger, null);
        }

        if (bound)
        {
            return new TelegramStartOutcome(
                TelegramStartStatus.AlreadyBoundElsewhere,
                TelegramMessages.AlreadyBoundElsewhere,
                null);
        }

        // Shape first, so a stream of garbage parameters costs a string length rather than an
        // indexed read each. It leaks nothing: the shape is published in every deep link.
        if (!TelegramLinkSecrets.IsWellFormedToken(request.Token)) return NotUsable();

        var hash = TelegramLinkSecrets.HashToken(request.Token);
        var row = await db.TelegramLinkTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ConsumedAt == null, cancellationToken);

        var now = clock.GetUtcNow();

        if (row is null || row.ExpiresAt <= now || row.Attempts >= TelegramLinkToken.MaxAttempts)
        {
            return NotUsable();
        }

        var code = TelegramLinkSecrets.NewConfirmationCode();

        row.ConfirmationCodeHash = TelegramLinkSecrets.HashConfirmationCode(row.Id, code);
        row.PresentedTelegramUserId = request.TelegramUserId;
        row.PresentedChatId = request.ChatId;
        row.PresentedAt = now;

        // Attempts is deliberately not reset. Re-opening the deep link must not refresh the budget,
        // or five guesses becomes as many as the guesser cares to ask for.
        await db.SaveChangesAsync(cancellationToken);

        return new TelegramStartOutcome(
            TelegramStartStatus.CodeIssued,
            TelegramMessages.ConfirmationCode(code),
            code);

        static TelegramStartOutcome NotUsable() => new(
            TelegramStartStatus.TokenNotUsable,
            TelegramMessages.TokenNotUsable,
            null);
    }

    public async Task<TelegramConfirmOutcome> ConfirmAsync(
        Guid appUserId,
        string? code,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Nothing is checked against TelegramAccounts before the write, deliberately. A read that
        // precedes a write is advisory by construction — whatever it found may be untrue by the time
        // the insert runs — so the two unique indexes and the conditional statement below are the
        // controls, and a pre-check here would only be a second guard that hides which one is real.
        var row = await PendingAsync(appUserId, tracked: true, cancellationToken);

        if (row is null) return new TelegramConfirmOutcome(TelegramConfirmStatus.NoPendingRequest, 0);

        if (row.ExpiresAt <= now || row.Attempts >= TelegramLinkToken.MaxAttempts)
        {
            return new TelegramConfirmOutcome(TelegramConfirmStatus.TokenDead, 0);
        }

        if (row.ConfirmationCodeHash is null
            || row.PresentedTelegramUserId is not { } telegramUserId
            || row.PresentedChatId is not { } chatId)
        {
            return new TelegramConfirmOutcome(TelegramConfirmStatus.NotPresented, 0);
        }

        var candidate = code?.Trim() ?? string.Empty;
        var matches = TelegramLinkSecrets.HashesMatch(
            row.ConfirmationCodeHash,
            TelegramLinkSecrets.HashConfirmationCode(row.Id, candidate));

        if (!matches) return await SpendAttemptAsync(row, cancellationToken);

        return await BindAsync(row, telegramUserId, chatId, now, cancellationToken);
    }

    public async Task<TelegramUnlinkOutcome> UnlinkAsync(
        Guid appUserId,
        TelegramUnlinkReason reason,
        CancellationToken cancellationToken)
    {
        var account = await db.TelegramAccounts
            .FirstOrDefaultAsync(a => a.AppUserId == appUserId, cancellationToken);

        // Unfinished requests go too. Leaving one behind would let a deep link handed out before the
        // unlink still produce a code afterwards.
        var pending = await db.TelegramLinkTokens
            .Where(t => t.AppUserId == appUserId && t.ConsumedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        if (account is null)
        {
            return new TelegramUnlinkOutcome(pending > 0, null, null);
        }

        var chatId = account.ChatId;

        db.TelegramAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);

        // The reason, and nothing that identifies anybody: not the chat id, not the @username, not
        // the panel user. "Why did my bot stop answering" is a question support gets, and the answer
        // is in the shape of the traffic rather than in any one row.
        logger.LogInformation("A Telegram binding was removed. Reason: {Reason}.", reason);

        // The farewell travels out rather than being sent from here: sending is the transport's job,
        // and the caller is the one that holds a gateway. Both reasons produce the same sentence,
        // because «چرا» is not the customer's problem — that the connection ended is.
        return new TelegramUnlinkOutcome(true, chatId, TelegramMessages.Farewell);
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // Filtered here rather than in the WHERE clause: SQLite will not compare a DateTimeOffset,
        // and this layer runs on SQLite in the tests and Postgres in production. The table only ever
        // holds ten-minute-old rows, so reading their stamps costs nothing.
        var stamps = await db.TelegramLinkTokens
            .AsNoTracking()
            .Select(t => new { t.Id, t.ExpiresAt, t.ConsumedAt })
            .ToListAsync(cancellationToken);

        // Consumed rows go as well as expired ones. The row's only job after consumption is to have
        // been the thing that could not be consumed twice, and the binding it produced outlives it.
        var doomed = stamps
            .Where(t => t.ExpiresAt <= now || t.ConsumedAt is not null)
            .Select(t => t.Id)
            .ToList();

        if (doomed.Count == 0) return 0;

        return await db.TelegramLinkTokens
            .Where(t => doomed.Contains(t.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<TelegramConfirmOutcome> SpendAttemptAsync(
        TelegramLinkToken row,
        CancellationToken cancellationToken)
    {
        // One conditional statement again, so two wrong guesses arriving together cost two attempts
        // rather than one. Reading the count and writing count + 1 loses one of them, and the budget
        // is the only thing standing between six digits and however many tries somebody wants.
        await db.TelegramLinkTokens
            .Where(t => t.Id == row.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.Attempts, t => t.Attempts + 1),
                cancellationToken);

        // ExecuteUpdate goes round the change tracker, so the copy loaded above still holds the old
        // count. Left attached, the next SaveChanges in this scope would write that number back.
        db.Entry(row).State = EntityState.Detached;

        var attempts = await db.TelegramLinkTokens
            .AsNoTracking()
            .Where(t => t.Id == row.Id)
            .Select(t => t.Attempts)
            .FirstOrDefaultAsync(cancellationToken);

        var left = Math.Max(0, TelegramLinkToken.MaxAttempts - attempts);

        return new TelegramConfirmOutcome(
            left == 0 ? TelegramConfirmStatus.TokenDead : TelegramConfirmStatus.WrongCode,
            left);
    }

    private async Task<TelegramConfirmOutcome> BindAsync(
        TelegramLinkToken row,
        long telegramUserId,
        long chatId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transaction = await DbTransactions.BeginIfNoneAsync(db, cancellationToken);

        try
        {
            // ── The consumption, and the whole of the race protection ──────────────────────────
            // One conditional UPDATE whose rows-affected is the answer: the test and the write are
            // the same statement, so the database decides which of two simultaneous confirmations
            // owns this token. Reading ConsumedAt and then setting it hands the token to both.
            //
            // Expiry is deliberately not in the predicate, for the reason PublicLinkReader gives:
            // SQLite keeps a DateTimeOffset as text and will not compare one, so a WHERE on
            // ExpiresAt would mean one rule on Postgres and another under the tests. It costs
            // nothing — expiry was evaluated against the clock a moment ago, and unlike the token
            // itself it is not a value two requests can take from each other.
            var consumed = await db.TelegramLinkTokens
                .Where(t => t.Id == row.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.ConsumedAt, now),
                    cancellationToken);

            db.Entry(row).State = EntityState.Detached;

            if (consumed != 1)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                return new TelegramConfirmOutcome(TelegramConfirmStatus.TokenDead, 0);
            }

            db.TelegramAccounts.Add(new TelegramAccount
            {
                Id = Guid.CreateVersion7(),
                AppUserId = row.AppUserId,
                TelegramUserId = telegramUserId,
                ChatId = chatId,
                LinkedAt = now,
                LastSeenAt = now,
                DeliveryStatus = TelegramDeliveryStatus.Active,
            });

            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            return new TelegramConfirmOutcome(TelegramConfirmStatus.Linked, 0);
        }
        catch (DbUpdateException)
        {
            // One of the two unique indexes. Both directions are enforced above as well; this is
            // what holds when two requests pass those checks at the same moment, and it is the
            // reason the checks above are convenience rather than control.
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);

            db.ChangeTracker.Clear();

            return new TelegramConfirmOutcome(TelegramConfirmStatus.TelegramAccountTaken, 0);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private Task<TelegramLinkToken?> PendingAsync(
        Guid appUserId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = tracked ? db.TelegramLinkTokens : db.TelegramLinkTokens.AsNoTracking();

        // No ordering, because there is at most one: StartAsync removes the user's previous
        // unconsumed rows before it writes a new one. Ordering here would also have to be done in
        // memory — SQLite will not ORDER BY a DateTimeOffset — for a set that cannot exceed one row.
        return query
            .Where(t => t.AppUserId == appUserId && t.ConsumedAt == null)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
