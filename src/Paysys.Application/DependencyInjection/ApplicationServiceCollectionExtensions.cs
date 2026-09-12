using Microsoft.Extensions.DependencyInjection;
using Paysys.Application.ExchangeRates;
using Paysys.Application.Transactions;
using Paysys.Domain.ExchangeRates;

namespace Paysys.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeRateProvider, FixedExchangeRateProvider>();
        services.AddScoped<TransactionProcessingService>();

        return services;
    }
}
