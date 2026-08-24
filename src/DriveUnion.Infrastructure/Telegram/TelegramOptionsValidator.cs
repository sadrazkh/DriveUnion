using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

/// <summary>
/// Refuses at startup what would otherwise be discovered as a silent no-op months later.
///
/// <para>The one that matters most is the delivery lifetime. A bot may only delete a message that was
/// sent less than 48 hours ago, so a configured lifetime beyond that is a timer that arms, comes due,
/// and does nothing — with a message on the customer's screen promising it would go. <b>It is refused
/// here rather than clamped at run time</b>, because clamping is how a number nobody meant becomes the
/// number in production.</para>
///
/// <para>The free-space arithmetic is the other one, and it is checked only where it can be true: the
/// working directory exists on a box running its own Bot API server, and there is none on a
/// development machine. Where it does exist, the requirement is
/// <c>MaxConcurrentTransfers × (MaxSendBytes + MaxReceiveBytes) + headroom</c> — eight gigabytes at
/// the deployed defaults, on a volume that is also the upload spool's. If the number does not fit, the
/// answer is a smaller ceiling and not a smaller sweeper: a 500 MB ceiling on a small disk is a working
/// product, and a 2000 MB ceiling on a full disk is an outage.</para>
/// </summary>
public sealed class TelegramOptionsValidator(ITelegramDiskSpace disk) : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var complaints = new List<string>();

        if (options.DeliveryMessageTtlMinutes < 0
            || options.DeliveryMessageTtlMinutes > TelegramOptions.MaxDeliveryMessageTtlMinutes)
        {
            complaints.Add(
                $"Telegram:DeliveryMessageTtlMinutes is {options.DeliveryMessageTtlMinutes}. A bot may "
                + "only delete a message sent less than 48 hours ago, so anything above "
                + $"{TelegramOptions.MaxDeliveryMessageTtlMinutes} is a timer that would never fire — "
                + "and the message would have promised the customer it would.");
        }

        if (options.MaxSendBytes <= 0 || options.MaxReceiveBytes <= 0)
        {
            complaints.Add("Telegram:MaxSendBytes and Telegram:MaxReceiveBytes must both be positive.");
        }

        if (options.MaxConcurrentTransfers < 1)
        {
            complaints.Add("Telegram:MaxConcurrentTransfers must be at least 1.");
        }

        if (options.MaxAttempts < 1 || options.MaxTransferAttempts < 1)
        {
            complaints.Add("Telegram attempt budgets must be at least 1.");
        }

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _))
        {
            complaints.Add($"Telegram:ApiBaseUrl is not an absolute URL: '{options.ApiBaseUrl}'.");
        }

        if (options.WorkDirectory is { Length: > 0 } workDirectory
            && disk.FreeBytesOn(workDirectory) is { } free)
        {
            var required = (options.MaxConcurrentTransfers
                            * (options.MaxSendBytes + options.MaxReceiveBytes))
                           + options.WorkDirHeadroomBytes;

            if (free < required)
            {
                complaints.Add(
                    $"The Telegram working directory's volume has {free} bytes free and the configured "
                    + $"ceilings need {required}. Lower Telegram:MaxSendBytes and "
                    + "Telegram:MaxReceiveBytes — over the ceiling the bot hands over a share link, "
                    + "which is a working product, where a ceiling the disk cannot hold is an outage.");
            }
        }

        return complaints.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(complaints);
    }
}
