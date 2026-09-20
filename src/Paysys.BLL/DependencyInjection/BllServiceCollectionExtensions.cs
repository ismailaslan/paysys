using Microsoft.Extensions.DependencyInjection;
using Paysys.BLL.Services;

namespace Paysys.BLL.DependencyInjection;

public static class BllServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessLogic(
        this IServiceCollection services,
        bool isDevelopment = false,
        string? developmentAllowlistedBankUrl = null)
    {
        services.AddSingleton<IExchangeRateProvider, FixedExchangeRateProvider>();
        services.AddScoped<CardToAccountResolver>();

        services.AddSingleton(new BankEndpointPolicyOptions
        {
            IsDevelopment = isDevelopment,
            DevelopmentAllowlistedUrl = developmentAllowlistedBankUrl ?? BankEndpointPolicyOptions.DefaultDevelopmentStubUrl,
        });
        services.AddSingleton<BankEndpointPolicy>();

        // The bank endpoint URL is untrusted input, so this client is locked down:
        //   * 5 s total timeout (the default is 100 s) and a 64 KB response cap;
        //   * no redirects - a redirect would let a bank bounce the request to any URL
        //     (verified: the default client follows them, re-POSTing the body on a 307);
        //   * no proxy, so the address checked below is the address actually contacted;
        //   * ConnectCallback validates the resolved address at connection time.
        services.AddHttpClient(CrossBankRoutingClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.MaxResponseContentBufferSize = 64 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var policy = sp.GetRequiredService<BankEndpointPolicy>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = (context, cancellationToken) => policy.ConnectAsync(context.DnsEndPoint, cancellationToken),
                };
            });

        services.AddScoped<CrossBankRoutingClient>();
        // Singleton: the per-user denial buckets must outlive individual requests.
        services.AddSingleton<AccessDenialRateLimiter>();
        services.AddScoped<TransactionAuditLogService>();
        services.AddScoped<PayeeService>();
        services.AddScoped<TransactionProcessingService>();

        return services;
    }
}
