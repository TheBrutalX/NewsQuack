using System.Net;
using NewsQuack.App.Http;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-005: unit tests for the pure address classification behind the anti-SSRF redirect check — no sockets, no DNS.
/// Table-driven over public/private/loopback/link-local IPv4 and IPv6 literals.</summary>
public class SsrfValidationTests
{
    [Theory]
    // public — never blocked
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("172.15.0.1", false)]      // just below 172.16/12
    [InlineData("172.32.0.5", false)]      // just above 172.16/12
    [InlineData("192.169.0.1", false)]     // just above 192.168/16
    [InlineData("::ffff:8.8.8.8", false)]  // IPv4-mapped public stays public
    [InlineData("2001:db8::1", false)]
    [InlineData("2a03:28c7:f0::5", false)]
    [InlineData("fe00::1", false)]         // just below both fe80::/10 and fc00::/7
    [InlineData("fec0::1", false)]         // just above fe80::/10 (and below fc00::/7)
    // loopback
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]        // all of 127/8, not just .1
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)] // IPv4-mapped loopback is still loopback
    // link-local
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.255.254", true)]
    [InlineData("fe80::abcd:1234", true)]
    [InlineData("febf::1", true)]          // last address of fe80::/10
    // private
    [InlineData("10.255.255.254", true)]   // top of 10/8
    [InlineData("172.16.0.1", true)]       // bottom of 172.16/12
    [InlineData("172.31.255.9", true)]     // top of 172.16/12
    [InlineData("192.168.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]   // IPv4-mapped private is still private
    [InlineData("fc00::dead:beef", true)]      // bottom of fc00::/7
    [InlineData("fdff::abcd", true)]           // top of fc00::/7
    public void SsrfValidation_IpLiteralsAndRanges_ClassifiedCorrectly(string address, bool expectedBlocked)
        => Assert.Equal(expectedBlocked, SsrfValidation.IsBlockedAddress(IPAddress.Parse(address)));

    [Fact]
    public void IsBlockedAddress_0Dot0Dot0Dot0_Blocked()
        => Assert.True(SsrfValidation.IsBlockedAddress(IPAddress.Any));   // T-058: unspecified IPv4 (INADDR_ANY) — on Linux connect() to it reaches loopback

    [Fact]
    public void IsBlockedAddress_IPv6Any_Blocked()
        => Assert.True(SsrfValidation.IsBlockedAddress(IPAddress.IPv6Any));   // T-058: unspecified IPv6 (::) — the same bypass in 16-byte form
}
