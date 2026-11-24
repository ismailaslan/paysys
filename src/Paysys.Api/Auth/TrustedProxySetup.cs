using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Paysys.Api.Auth;

// The client address for the rate limiters when the API runs behind a load balancer or proxy.
//
// Off by default: with no ForwardedHeaders:TrustedNetworks the API reads Connection.RemoteIpAddress only and never
// looks at X-Forwarded-For (so a caller cannot choose its own address). Behind a proxy that makes every user look like
// one address.
//
// On: list the networks your proxy connects from (CIDR, for example 10.0.0.0/8) in ForwardedHeaders:TrustedNetworks:0, :1 ...
// (environment ForwardedHeaders__TrustedNetworks__0). Only when the connection really comes from one of those networks
// is X-Forwarded-For used, and only the LAST entry in it (the one the trusted proxy added itself; ForwardLimit = 1), so
// whatever a caller puts at the front of the header changes nothing. A connection from anywhere else is treated as
// before: the header is ignored. A network that does not parse stops the API at startup rather than running with a
// list nobody meant. X-Forwarded-Proto and -Host are not used.
public static class TrustedProxySetup
{
    public const string Section = "ForwardedHeaders:TrustedNetworks";

    public static ForwardedHeadersOptions? Build(IConfiguration configuration)
    {
        var networks = configuration.GetSection(Section).Get<string[]>();
        if (networks is null || networks.Length == 0)
            return null;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = 1,
            RequireHeaderSymmetry = false,
        };
        // By default only loopback is trusted; the configured list replaces that.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var network in networks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsed))
                throw new InvalidOperationException(
                    $"{Section} contains '{network}', which is not a network in CIDR form (for example 10.0.0.0/8).");
            options.KnownIPNetworks.Add(parsed);
        }

        return options;
    }
}
