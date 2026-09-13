using Paysys.Domain.ExchangeRates;

namespace Paysys.Application.ExchangeRates;

public class FixedExchangeRateProvider : IExchangeRateProvider
{
    // Illustrative-only, approximate real-world figures as of testing - not a live feed.
    private static readonly Dictionary<string, decimal> RatesFromUsd = new()
    {
        ["USD"] = 1m,
        ["EUR"] = 0.87m,
        ["GBP"] = 0.75m,
        ["JPY"] = 158.50m,
        ["CHF"] = 0.80m,
        ["CAD"] = 1.39m,
        ["AUD"] = 1.44m,
        ["NZD"] = 1.73m,
        ["CNY"] = 7.10m,
        ["INR"] = 92.50m,
        ["MXN"] = 17.90m,
        ["SGD"] = 1.28m,
        ["HKD"] = 7.82m,
        ["ZAR"] = 17.05m,
        ["SEK"] = 9.40m,
        ["BRL"] = 5.35m,
    };

    public IReadOnlyCollection<string> SupportedCurrencies { get; } = RatesFromUsd.Keys.Order().ToArray();

    public Task<decimal> GetRateAsync(string fromCurrency, string toCurrency)
    {
        if (fromCurrency == toCurrency)
            return Task.FromResult(1m);

        if (!RatesFromUsd.TryGetValue(fromCurrency, out var fromRate) ||
            !RatesFromUsd.TryGetValue(toCurrency, out var toRate))
        {
            throw new ExchangeRateNotFoundException(fromCurrency, toCurrency);
        }

        // Triangulate through USD: rate(A, B) = rate(USD, B) / rate(USD, A).
        // Rounded to match Transaction.ExchangeRate's numeric(18,6) column, so the
        // immediate API response and a later re-fetch from the DB always agree.
        var rate = Math.Round(toRate / fromRate, 6, MidpointRounding.ToEven);
        return Task.FromResult(rate);
    }
}
