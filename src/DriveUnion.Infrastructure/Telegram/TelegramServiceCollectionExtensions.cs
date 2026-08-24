using DriveUnion.Core.Application;
using DriveUnion.Core.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.Telegram;

public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Telegram identity, account linking, the operator's bot settings, and — since the transport
    /// slice — the client, both update sources, the outbox and its drainer, and the working-directory
    /// sweeper.
    ///
    /// <para>Everything that touches the database is scoped, including
    /// <see cref="ITelegramIdentityReader"/>, which a sessionless caller resolves out of its own scope
    /// rather than out of a request's. The three <c>BackgroundService</c>s are singletons that open a
    /// scope per unit of work — a transfer holds one for minutes, and sharing a
    /// <c>DbContext</c> across two of them is how a queue drains itself into an exception.</para>
    ///
    /// <para><c>TryAdd</c> throughout, so a caller that has already chosen an implementation keeps it.
    /// The order below matters in one place: the real client is registered <em>before</em> the
    /// unconfigured gateway, so the unconfigured one only survives where a deployment has deliberately
    /// removed the transport.</para>
    ///
    /// <para>It must be called after <c>AddDataProtection</c> and after whatever registers
    /// <c>ITokenProtector</c>: the bot token and the webhook secret are encrypted with the same key
    /// ring as the Google refresh tokens, and it must not be possible to store one under a key that
    /// will not survive a redeploy.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTelegram(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<TelegramOptions>()
            .BindConfiguration(TelegramOptions.SectionName)

            // The panel's own address, so a refusal can point at the uploader that will accept what
            // the bot cannot carry. Read from the key the rest of the product already uses rather
            // than duplicated, because two settings for one address is one that will be wrong.
            .Configure<IConfiguration>((options, configuration) =>
                options.PanelBaseUrl ??= configuration["DriveUnion:PublicBaseUrl"])

            // Refused at startup rather than discovered at run time. A delivery lifetime beyond what
            // the API can honour is a timer that never fires, and a ceiling the disk cannot hold is
            // an outage rather than a feature.
            .ValidateOnStart();

        services.TryAddSingleton<ITelegramDiskSpace, TelegramDiskSpace>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>());

        services.TryAddScoped<ITelegramBotSettingsStore, TelegramBotSettingsStore>();
        services.TryAddScoped<ITelegramIdentityReader, TelegramIdentityReader>();
        services.TryAddScoped<ITelegramLinkService, TelegramLinkService>();
        services.TryAddScoped<ITelegramOperatorView, TelegramOperatorView>();
        services.TryAddScoped<ITelegramUpdateLedger, TelegramUpdateLedger>();
        services.TryAddScoped<ITelegramOutboxWriter, TelegramOutboxWriter>();
        services.TryAddScoped<ITelegramDeliverySource, TelegramDeliverySource>();
        services.TryAddScoped<ITelegramUpdateHandler, TelegramUpdateHandler>();
        services.TryAddSingleton<ITelegramUpdateParser, TelegramUpdateParser>();
        services.TryAddScoped<TelegramOutboxProcessor>();

        // Singletons because they are process-wide budgets. A scoped limiter is one bucket per
        // request, which is the same as no limiter at all.
        services.TryAddSingleton<TelegramRateLimiter>();
        services.TryAddSingleton<TelegramStrangerBudget>();
        services.TryAddSingleton<TelegramFairnessCursor>();
        services.TryAddSingleton<TelegramWorkDirectory>();

        // The client's own timeout is infinite on purpose. HttpClient.Timeout covers the whole
        // request including the body, so a two-gigabyte upload would die at a hundred seconds
        // regardless of progress — a one-line bug that produces a feature which works in every test
        // and fails on every real file. Each call carries its own deadline instead.
        services.AddHttpClient(TelegramBotApiClient.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddHttpMessageHandler(provider => new TelegramRetryHandler(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelegramRetryHandler>>(),
                provider.GetRequiredService<TimeProvider>()));

        services.TryAddScoped(provider => new TelegramBotApiClient(
            provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(TelegramBotApiClient.HttpClientName),
            provider.GetRequiredService<ITelegramBotSettingsStore>(),
            provider.GetRequiredService<IOptions<TelegramOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelegramBotApiClient>>()));

        // The limiter goes in front of the single outbound seam, so no call site can route around it.
        services.TryAddScoped<ITelegramBotGateway>(provider => new ThrottledTelegramBotGateway(
            provider.GetRequiredService<TelegramBotApiClient>(),
            provider.GetRequiredService<TelegramRateLimiter>()));

        // Last, and therefore only where something above was deliberately removed.
        services.TryAddScoped<ITelegramBotGateway, UnconfiguredTelegramBotGateway>();

        return services;
    }

    /// <summary>
    /// The three long-running things: the outbox drainer, the poller and the working-directory
    /// sweeper.
    ///
    /// <para><b>Separate from <see cref="AddDriveUnionTelegram"/> on purpose.</b> That method is
    /// called by every in-process test host in the suite, and a background loop opening its own
    /// scopes against a shared SQLite connection turns unrelated tests into "database is locked". A
    /// host that wants the bot to actually run says so; a host that only wants the panel's Telegram
    /// screens gets them without a queue draining underneath it.</para>
    ///
    /// <para>The poller returns immediately unless <c>Telegram:UpdateSource</c> is <c>Polling</c>, so
    /// adding all three is right in every deployment: which transport runs is a configuration
    /// question and not a registration one.</para>
    /// </summary>
    public static IServiceCollection AddDriveUnionTelegramTransport(this IServiceCollection services)
    {
        services.AddHostedService<TelegramOutboxDrainer>();
        services.AddHostedService<TelegramPollingService>();
        services.AddHostedService<TelegramSweeperService>();

        return services;
    }
}
