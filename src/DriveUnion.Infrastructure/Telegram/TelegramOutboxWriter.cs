using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// The queue's writing end, and the two bounds on it.
///
/// <para><b>Both bounds, and the tighter one wins.</b> Fifty pending items per tenant was two and a
/// half gigabytes of work when a send was capped at fifty megabytes; at a two-gigabyte ceiling the
/// same fifty items are a hundred gigabytes, which is days of a shared uplink. A bound in items only
/// is not a bound, so a byte bound sits beside it — and over either the bot answers honestly and
/// enqueues nothing, because a bounded queue with an honest message beats an unbounded one that looks
/// like it is working.</para>
/// </summary>
public sealed class TelegramOutboxWriter(
    DriveUnionDbContext db,
    IOptions<TelegramOptions> options,
    TimeProvider clock) : ITelegramOutboxWriter
{
    private readonly TelegramOptions _options = options.Value;

    public async Task<TelegramEnqueueResult> EnqueueAsync(
        Guid tenantId,
        long chatId,
        TelegramOutboxKind kind,
        Guid? storedFileId,
        string? payload,
        long sizeBytes,
        DateTimeOffset? notBefore,
        CancellationToken cancellationToken)
    {
        // A deletion is never refused for being over a bound, and that is deliberate rather than an
        // oversight: it is the item that makes the chat smaller, it moves no bytes, and refusing it
        // would leave a document in a chat the customer asked to have cleaned up.
        if (kind is not TelegramOutboxKind.DeleteMessage)
        {
            var pending = await db.TelegramOutbox
                .AsNoTracking()
                .Where(o => o.TenantId == tenantId
                    && (o.Status == TelegramOutboxStatus.Pending || o.Status == TelegramOutboxStatus.Claimed))
                .Select(o => o.SizeBytes)
                .ToListAsync(cancellationToken);

            if (pending.Count >= _options.MaxQueuedPerTenant
                || pending.Sum() + sizeBytes > _options.MaxQueuedBytesPerTenant)
            {
                return new TelegramEnqueueResult(TelegramEnqueueStatus.QueueFull, null);
            }
        }

        var item = new TelegramOutbox
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChatId = chatId,
            Kind = kind,
            StoredFileId = storedFileId,
            Payload = payload,
            Status = TelegramOutboxStatus.Pending,
            SizeBytes = sizeBytes < 0 ? 0 : sizeBytes,
            NextAttemptAt = notBefore,
            CreatedAt = clock.GetUtcNow(),
        };

        db.TelegramOutbox.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        return new TelegramEnqueueResult(TelegramEnqueueStatus.Queued, item.Id);
    }
}
