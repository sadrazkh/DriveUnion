using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;

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
                GoogleAccountStatus.Healthy => "سالم",
                GoogleAccountStatus.Paused => "متوقف",
                _ => "قطع شده",
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
    bool ConsentConfigured);
