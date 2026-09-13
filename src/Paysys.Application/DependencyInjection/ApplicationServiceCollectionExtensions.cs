using Microsoft.Extensions.DependencyInjection;
using Paysys.Application.Audit;
using Paysys.Application.BankRouting;
using Paysys.Application.Cards;
using Paysys.Application.ExchangeRates;
using Paysys.Application.Transactions;
using Paysys.Domain.ExchangeRates;

namespace Paysys.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
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
