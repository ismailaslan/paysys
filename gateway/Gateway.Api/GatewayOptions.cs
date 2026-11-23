namespace Gateway.Api;

// The banks this gateway talks to (Gateway__Banks__0__Code and so on). Banks are configured, not
// registered by users, so their addresses are trusted.
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    public List<BankOptions> Banks { get; set; } = new();

    public BankOptions? Find(string? code) =>
        Banks.FirstOrDefault(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase));
}

public sealed class BankOptions
{
    // The bank's code, for example SEB. Sent by the bank in X-Bank-Code.
    public string Code { get; set; } = "";

    // Where the bank's API lives, for example https://paysys-hw7d.onrender.com
    public string BaseUrl { get; set; } = "";

    // What the bank must send as X-Bank-Key when it calls the gateway.
    public string KeyFromBank { get; set; } = "";

    // What the gateway sends as X-Gateway-Key when it calls the bank.
    public string KeyToBank { get; set; } = "";

    // Signs the completion messages the bank sends.
    public string WebhookSecret { get; set; } = "";
}
