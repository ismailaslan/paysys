namespace Paysys.Domain.ExchangeRates;

public interface IExchangeRateProvider
{
    Task<decimal> GetRateAsync(string fromCurrency, string toCurrency);
}
