using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Persistence;

namespace Paysys.DAL.DependencyInjection;

public static class DataAccessServiceCollectionExtensions
{
    public static IServiceCollection AddDataAccess(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Paysys")
            ?? throw new InvalidOperationException(
                "Connection string 'Paysys' not found. Set it via 'dotnet user-secrets set ConnectionStrings:Paysys \"<value>\"' or the ConnectionStrings__Paysys environment variable.");

        services.AddDbContext<PaysysDbContext>(options => options.UseNpgsql(connectionString));

        return services;
    }
}
