using Gateway.Api.Security;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Api.Hubs;

// Live payments for a merchant's browser. The connection must present a one-use ticket from its bank
// (as the access_token query value); without a good one it is closed at once. The ticket decides
// which accounts the connection hears about, so a browser can never ask for another merchant's.
public sealed class MerchantHub : Hub
{
    private readonly TicketStore _tickets;

    public MerchantHub(TicketStore tickets) => _tickets = tickets;

    public static string GroupName(string bankCode, Guid accountId) => $"{bankCode.ToUpperInvariant()}:{accountId}";

    public override async Task OnConnectedAsync()
    {
        var ticket = _tickets.Redeem(Context.GetHttpContext()?.Request.Query["access_token"].ToString());
        if (ticket is null)
        {
            Context.Abort();
            return;
        }

        foreach (var accountId in ticket.AccountIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(ticket.BankCode, accountId));

        await base.OnConnectedAsync();
    }
}
