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
        services.AddScoped<TransactionAuditLogService>();
        services.AddScoped<TransactionProcessingService>();

        return services;
    }
}
