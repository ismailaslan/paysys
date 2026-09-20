using Microsoft.Extensions.DependencyInjection;
using Paysys.BLL.Services;

namespace Paysys.BLL.DependencyInjection;

public static class BllServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessLogic(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeRateProvider, FixedExchangeRateProvider>();
        services.AddScoped<CardToAccountResolver>();
        services.AddHttpClient(CrossBankRoutingClient.HttpClientName);
        services.AddScoped<CrossBankRoutingClient>();
        // Singleton: the per-user denial buckets must outlive individual requests.
        services.AddSingleton<AccessDenialRateLimiter>();
        services.AddScoped<TransactionAuditLogService>();
        services.AddScoped<TransactionProcessingService>();

        return services;
    }
}
