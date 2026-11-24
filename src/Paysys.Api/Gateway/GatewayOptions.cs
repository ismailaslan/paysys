namespace Paysys.Api.Gateway;

// Settings for talking to the payment gateway (Gateway__ environment variables). When BaseUrl or a
// key is missing the gateway is off: nothing is sent, and the gateway endpoints answer 503.
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    // Public address of the gateway, for example https://paysys-gateway.onrender.com
    public string? BaseUrl { get; set; }

    // How this bank introduces itself to the gateway.
    public string BankCode { get; set; } = "";

    // Sent to the gateway as X-Bank-Key when Paysys calls it (register a terminal, mint a ticket).
    public string? OutboundKey { get; set; }

    // Signs the completion messages Paysys sends to the gateway.
    public string? WebhookSecret { get; set; }

    // What the gateway must send as X-Gateway-Key when it calls Paysys (create a payment code).
    public string? InboundKey { get; set; }

    // Key rotation: the inbound key being replaced. While it is set the bank accepts the old and the new
    // key from the gateway; remove it once the gateway uses the new one.
    public string? PreviousInboundKey { get; set; }

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(BankCode)
        && !string.IsNullOrWhiteSpace(OutboundKey) && !string.IsNullOrWhiteSpace(WebhookSecret)
        && !string.IsNullOrWhiteSpace(InboundKey);
}
