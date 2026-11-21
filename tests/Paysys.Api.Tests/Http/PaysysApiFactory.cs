using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paysys.DAL.Persistence;

namespace Paysys.Api.Tests.Http;

// The in-process Api for the HTTP tests. One factory per test, so every test gets its own
// service provider and therefore its own RegistrationRateLimiter and LoginThrottle.
//
// Never Neon:
// - environment "Testing", so user-secrets (which hold the Neon connection string) and
//   appsettings.Development.json are not loaded;
// - every setting is passed with UseSetting, which lands in host configuration and is seen by
//   AddDataAccess/AddPaysysAuth, which read configuration while services are registered;
// - Verify() checks that the PaysysDbContext the app built uses exactly the connection string
//   this factory was given, and throws before any request otherwise (for example when a
//   ConnectionStrings__Paysys environment variable takes priority). Startup touches no database
//   (no migration at startup, and the audit chain check is delayed by an hour), so nothing is
//   opened before that check.
public sealed class PaysysApiFactory : WebApplicationFactory<Program>
{
    // Port 1: nothing listens there, so any query fails fast. Tests without [PostgresFact] use
    // this to prove that a code path makes no query at all.
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Timeout=1";

    // Test-only header read by the startup filter below to set Connection.RemoteIpAddress.
    // The Api itself never reads it.
    public const string TestRemoteIpHeader = "X-Test-Remote-Ip";

    private const string TestSigningKey = "test-signing-key-0123456789-abcdefghijkl"; // gitleaks:allow

    private readonly string _connectionString;
    private readonly bool _registrationEnabled;

    public PaysysApiFactory(string? connectionString = null, bool registrationEnabled = true)
    {
        _connectionString = connectionString ?? UnreachableConnectionString;
        _registrationEnabled = registrationEnabled;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Paysys", _connectionString);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Auth:Registration:Enabled", _registrationEnabled ? "true" : "false");
        builder.UseSetting("AuditVerification:InitialDelay", "01:00:00");

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>());
    }

    // Creates the app, checks the Neon guard, and returns a client. https base address, so
    // UseHttpsRedirection never redirects; redirects are not followed, so one would show as 3xx.
    public HttpClient CreateVerifiedClient()
    {
        Verify();
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
    }

    private void Verify()
    {
        using var scope = Services.CreateScope();
        var actual = scope.ServiceProvider.GetRequiredService<PaysysDbContext>().Database.GetConnectionString();
        if (actual != _connectionString)
            throw new InvalidOperationException(
                "The Api under test is not using the connection string the test gave it. Refusing to send any request.");
    }

    // Runs first in the pipeline and sets the client address from the test-only header, because
    // TestServer may leave RemoteIpAddress null (which would put every request in one bucket).
    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestRemoteIpHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
