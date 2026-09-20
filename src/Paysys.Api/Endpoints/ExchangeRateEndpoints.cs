using Paysys.BLL.Exceptions;
using Paysys.BLL.Services;
using Paysys.Shared.ExchangeRates;

namespace Paysys.Api.Endpoints;

public static class ExchangeRateEndpoints
{
    public static WebApplication MapExchangeRateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/currencies", (IExchangeRateProvider provider) => provider.SupportedCurrencies);

        app.MapGet("/api/exchange-rate", async (string from, string to, IExchangeRateProvider provider) =>
        {
            if (from is not { Length: 3 } || to is not { Length: 3 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currency"] = ["from/to must be 3-letter currency codes."]
                });
            }

            try
            {
                var rate = await provider.GetRateAsync(from.ToUpperInvariant(), to.ToUpperInvariant());
                return Results.Ok(new ExchangeRateResponse(rate));
            }
            catch (ExchangeRateNotFoundException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
            }
        });

        return app;
    }
}
