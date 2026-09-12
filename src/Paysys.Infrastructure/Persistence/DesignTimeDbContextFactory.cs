using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Paysys.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef` commands construct a <see cref="PaysysDbContext"/> at design time,
/// without running the Paysys.Api host. Reads the same 'ConnectionStrings:Paysys' user
/// secret as the running app, keyed to Paysys.Api's UserSecretsId.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaysysDbContext>
{
    private const string ApiUserSecretsId = "923f2ff7-17d3-45e2-ab3d-1b6bd0e0d460";

    public PaysysDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Paysys")
            ?? throw new InvalidOperationException(
                "Connection string 'Paysys' not found. Set it via 'dotnet user-secrets set ConnectionStrings:Paysys \"<value>\" --project src/Paysys.Api' or the ConnectionStrings__Paysys environment variable.");

        var optionsBuilder = new DbContextOptionsBuilder<PaysysDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new PaysysDbContext(optionsBuilder.Options);
    }
}
