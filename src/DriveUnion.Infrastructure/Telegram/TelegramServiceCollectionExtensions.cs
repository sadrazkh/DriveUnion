using DriveUnion.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriveUnion.Infrastructure.Telegram;

public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Telegram identity, account linking and the operator's bot settings.
    ///
    /// <para>Everything is scoped, because it all shares the request's
    /// <c>DriveUnionDbContext</c> — including <see cref="ITelegramIdentityReader"/>, which a
    /// sessionless caller resolves out of its own scope rather than out of a request's.</para>
    ///
    /// <para><c>TryAdd</c> throughout, so a caller that has already chosen an implementation keeps
    /// it. That is how the transport slice supplies a real <see cref="ITelegramBotGateway"/>: it
    /// registers one first and the unconfigured gateway below is never added.</para>
    ///
    /// <para>It must be called after <c>AddDataProtection</c> and after whatever registers
    /// <c>ITokenProtector</c>: the bot token is encrypted with the same key ring as the Google
    /// refresh tokens, and it must not be possible to store one under a key that will not survive a
    /// redeploy.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTelegram(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ITelegramBotSettingsStore, TelegramBotSettingsStore>();
        services.TryAddScoped<ITelegramIdentityReader, TelegramIdentityReader>();
        services.TryAddScoped<ITelegramLinkService, TelegramLinkService>();
        services.TryAddScoped<ITelegramOperatorView, TelegramOperatorView>();
        services.TryAddScoped<ITelegramBotGateway, UnconfiguredTelegramBotGateway>();

        return services;
    }
}
