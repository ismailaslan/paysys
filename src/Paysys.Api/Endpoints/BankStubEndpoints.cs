using Paysys.Shared.BankRouting;

namespace Paysys.Api.Endpoints;

// Stands in for "the other bank's own API" for cross-bank routing. Always
// approves for now - see the CrossBankRoutingClient work this came in with.
public static class BankStubEndpoints
{
    public static WebApplication MapBankStubEndpoints(this WebApplication app)
    {
        app.MapPost("/api/stub/bank-approval", (BankApprovalRequest request) =>
            Results.Ok(new BankApprovalResponse(true, null)));

        return app;
    }
}
