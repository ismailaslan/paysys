using System.Net;
using System.Net.Sockets;

namespace Paysys.BLL.Services;

public sealed class BankEndpointPolicyOptions
{
    public const string DefaultDevelopmentStubUrl = "http://localhost:5121/api/stub/bank-approval";

    public bool IsDevelopment { get; set; }

    // The one URL allowed to point at loopback (the seeded dev stub bank). Only
    // honored when IsDevelopment - outside Development it is ignored entirely.
    public string? DevelopmentAllowlistedUrl { get; set; } = DefaultDevelopmentStubUrl;
}

public readonly record struct EndpointCheck(bool Allowed, string? Reason)
{
    public static EndpointCheck Ok { get; } = new(true, null);
    public static EndpointCheck Reject(string reason) => new(false, reason);
}

// Decides which URLs the server may POST cross-bank approval requests to. The
// stored ApiEndpoint is attacker-influenced (anyone who can register a bank picks
// it), and the server makes the request from inside its own network, so without
// this a registered bank is a way to make the server call internal addresses.
//
// Applied in three places, because each alone is bypassable:
//   * at registration (CheckShape + DNS)   - reject bad input early, with a reason;
//   * before every call (CheckShape)       - rows registered before this policy existed;
//   * when the socket is opened (ConnectAsync) - the real enforcement: it validates
//     the address actually being connected to, so a hostname that resolved to a public
//     IP at registration but to a private one now (DNS rebinding) is still refused.
public sealed class BankEndpointPolicy
{
    private readonly BankEndpointPolicyOptions _options;
    private readonly Uri? _allowlisted;

    public BankEndpointPolicy(BankEndpointPolicyOptions options)
    {
        _options = options;
        _allowlisted = options.IsDevelopment && Uri.TryCreate(options.DevelopmentAllowlistedUrl, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    // Everything decidable from the URL text alone - no DNS.
    public EndpointCheck CheckShape(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return EndpointCheck.Reject("ApiEndpoint must be an absolute URL.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return EndpointCheck.Reject("ApiEndpoint must not contain credentials.");

        if (IsAllowlisted(uri))
            return EndpointCheck.Ok;

        if (uri.Scheme != Uri.UriSchemeHttps && !(_options.IsDevelopment && uri.Scheme == Uri.UriSchemeHttp))
            return EndpointCheck.Reject("ApiEndpoint must use https.");

        var isLoopbackName = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        if (isLoopbackName)
            return EndpointCheck.Reject(PrivateAddressReason);

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !IsPublicAddress(literal))
            return EndpointCheck.Reject(PrivateAddressReason);

        return EndpointCheck.Ok;
    }

    // CheckShape plus resolving the host and checking every address it resolves to.
    public async Task<EndpointCheck> ValidateForRegistrationAsync(string? url, CancellationToken cancellationToken = default)
    {
        var shape = CheckShape(url);
        if (!shape.Allowed)
            return shape;

        var uri = new Uri(url!);
        if (IsAllowlisted(uri))
            return EndpointCheck.Ok;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return EndpointCheck.Reject("ApiEndpoint host could not be resolved.");
        }

        if (addresses.Length == 0)
            return EndpointCheck.Reject("ApiEndpoint host could not be resolved.");

        // Every address, not just the first: a name with one public and one private
        // record would otherwise pass here and connect to the private one later.
        if (addresses.Any(a => !IsAddressAllowed(a, uri.Host, uri.Port)))
            return EndpointCheck.Reject(PrivateAddressReason);

        return EndpointCheck.Ok;
    }

    // Opens the socket for SocketsHttpHandler.ConnectCallback. Resolves the name
    // itself, refuses if ANY resolved address is not allowed, then connects to an
    // address it has just checked - there is no gap between "check" and "use".
    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(a => !IsAddressAllowed(a, endPoint.Host, endPoint.Port)))
            throw new HttpRequestException("Destination address is not permitted.");

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the destination.", last);
    }

    public bool IsAddressAllowed(IPAddress address, string host, int port)
    {
        if (IsPublicAddress(address))
            return true;

        // The only exemption: the allowlisted dev stub's own host and port may be loopback.
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return _allowlisted is not null
            && IPAddress.IsLoopback(normalized)
            && string.Equals(host, _allowlisted.Host, StringComparison.OrdinalIgnoreCase)
            && port == _allowlisted.Port;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        var b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(
                b[0] == 0                                        // 0.0.0.0/8  "this network"
                || b[0] == 10                                    // 10.0.0.0/8 private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10 carrier-grade NAT
                || b[0] == 127                                   // 127.0.0.0/8 loopback
                || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16 link-local, incl. cloud metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16.0.0/12 private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16 private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // Forms that embed an IPv4 address: judge the embedded address.
            if (b[..12].All(x => x == 0))                                         // ::a.b.c.d (IPv4-compatible)
                return IsPublicAddress(new IPAddress(b[12..16]));
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
                return IsPublicAddress(new IPAddress(b[12..16]));                 // 64:ff9b::/96 NAT64
            if (b[0] == 0x20 && b[1] == 0x02)
                return IsPublicAddress(new IPAddress(b[2..6]));                   // 2002::/16 6to4

            return true;
        }

        return false;
    }

    // For logs: never includes credentials or the query string, either of which can carry secrets.
    public static string SafeForLog(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}"
            : "(unparseable)";

    private const string PrivateAddressReason =
        "ApiEndpoint must not point to a private, loopback or link-local address.";

    private bool IsAllowlisted(Uri uri) =>
        _allowlisted is not null
        && uri.Scheme == _allowlisted.Scheme
        && string.Equals(uri.Host, _allowlisted.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _allowlisted.Port
        && uri.PathAndQuery == _allowlisted.PathAndQuery;
}
