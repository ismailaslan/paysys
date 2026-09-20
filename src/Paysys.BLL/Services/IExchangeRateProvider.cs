namespace Paysys.BLL.Services;

public interface IExchangeRateProvider
{
    Task<decimal> GetRateAsync(string fromCurrency, string toCurrency);

    IReadOnlyCollection<string> SupportedCurrencies { get; }
}
