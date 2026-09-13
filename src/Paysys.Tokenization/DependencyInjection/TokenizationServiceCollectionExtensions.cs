using Microsoft.Extensions.DependencyInjection;

namespace Paysys.Tokenization.DependencyInjection;

public static class TokenizationServiceCollectionExtensions
{
    public static IServiceCollection AddTokenization(this IServiceCollection services)
    {
        services.AddScoped<CardTokenizationService>();

        return services;
    }
}
