using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paysys.Infrastructure.Persistence;

namespace Paysys.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Paysys")
            ?? throw new InvalidOperationException(
                "Connection string 'Paysys' not found. Set it via 'dotnet user-secrets set ConnectionStrings:Paysys \"<value>\"' or the ConnectionStrings__Paysys environment variable.");

        services.AddDbContext<PaysysDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
