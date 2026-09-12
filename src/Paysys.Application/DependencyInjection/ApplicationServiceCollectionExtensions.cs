using Microsoft.Extensions.DependencyInjection;
using Paysys.Application.Transactions;

namespace Paysys.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<TransactionProcessingService>();

        return services;
    }
}
