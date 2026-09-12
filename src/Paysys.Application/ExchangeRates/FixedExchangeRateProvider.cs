using Paysys.Domain.ExchangeRates;

namespace Paysys.Application.ExchangeRates;

public class FixedExchangeRateProvider : IExchangeRateProvider
{
    private static readonly Dictionary<(string From, string To), decimal> Rates = new()
    {
        [("USD", "EUR")] = 0.92m,
        [("EUR", "USD")] = 1.0870m,
    };

    public Task<decimal> GetRateAsync(string fromCurrency, string toCurrency)
    {
        if (fromCurrency == toCurrency)
            return Task.FromResult(1m);

        if (Rates.TryGetValue((fromCurrency, toCurrency), out var rate))
            return Task.FromResult(rate);

        throw new ExchangeRateNotFoundException(fromCurrency, toCurrency);
    }
}
