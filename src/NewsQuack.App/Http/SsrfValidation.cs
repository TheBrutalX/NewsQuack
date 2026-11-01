using System.Net;

namespace NewsQuack.App.Http;

/// <summary>
/// T-005: pure classification of one resolved address for the anti-SSRF redirect check. An address is blocked when it is
/// unspecified, loopback, link-local, or private: IPv4 0.0.0.0, 127/8, 169.254/16, 10/8, 172.16/12, 192.168/16; IPv6 ::, ::1, fe80::/10, fc00::/7.
/// The unspecified addresses are blocked (T-058) because on Linux — the Docker container this service runs in — connect() to INADDR_ANY / ::
/// reaches loopback, so without the rule they would match no range below and bypass the check entirely. IPv4-mapped IPv6 addresses
/// (::ffff:a.b.c.d) are classified by their embedded IPv4 address so a mapped loopback/private literal cannot slip through the check.
/// Pure and allocation-free — unit-testable without sockets or DNS. Accepted limit (T-058): DNS-rebinding TOCTOU between validation and
/// connect is accepted as common practice — this class resolves nothing; the caller resolves the name once for validation and the socket
/// layer re-resolves it on connect.
/// </summary>
public static class SsrfValidation
{
    /// <summary>True when <paramref name="address"/> is in one of the blocked ranges (loopback, link-local, or private).</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();

        // T-058: unspecified address — on Linux connect() to INADDR_ANY/:: reaches loopback, so the all-zero form must be blocked like the ranges below.
        if (bytes.All(b => b == 0))
            return true;

        if (bytes.Length == 4)
            return bytes[0] == 127                                          // loopback 127/8
                || (bytes[0], bytes[1]) is (169, 254)                       // link-local 169.254/16
                || bytes[0] == 10                                           // private 10/8
                || (bytes[0], bytes[1]) is (172, >= 16 and <= 31)           // private 172.16/12
                || (bytes[0], bytes[1]) is (192, 168);                      // private 192.168/16

        return IPAddress.IsLoopback(address)                                // ::1
            || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)              // link-local fe80::/10
            || (bytes[0] & 0xfe) == 0xfc;                                   // unique local fc00::/7
    }
}
