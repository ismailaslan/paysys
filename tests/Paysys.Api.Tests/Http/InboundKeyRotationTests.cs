using Paysys.Api.Endpoints;
using Paysys.Api.Gateway;

namespace Paysys.Api.Tests.Http;

// Key change without downtime on the bank side: the previous inbound key is accepted next to the
// current one until it is removed.
public class InboundKeyRotationTests
{
    [Fact]
    public void DuringAChange_TheOldAndTheNewInboundKeyBothWork()
    {
        var settings = new GatewayOptions { InboundKey = "new-key", PreviousInboundKey = "old-key" };

        Assert.True(GatewayEndpoints.InboundKeyMatches("new-key", settings));
        Assert.True(GatewayEndpoints.InboundKeyMatches("old-key", settings));
        Assert.False(GatewayEndpoints.InboundKeyMatches("other-key", settings));
    }

    [Fact]
    public void AfterTheChange_TheOldInboundKeyIsRefused()
    {
        var settings = new GatewayOptions { InboundKey = "new-key" };

        Assert.True(GatewayEndpoints.InboundKeyMatches("new-key", settings));
        Assert.False(GatewayEndpoints.InboundKeyMatches("old-key", settings));
    }
}
