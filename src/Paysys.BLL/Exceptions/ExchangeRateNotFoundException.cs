namespace Paysys.BLL.Exceptions;

public class ExchangeRateNotFoundException : Exception
{
    public string FromCurrency { get; }
    public string ToCurrency { get; }

    public ExchangeRateNotFoundException(string fromCurrency, string toCurrency)
        : base($"No exchange rate available for {fromCurrency} -> {toCurrency}.")
    {
        FromCurrency = fromCurrency;
        ToCurrency = toCurrency;
    }
}
