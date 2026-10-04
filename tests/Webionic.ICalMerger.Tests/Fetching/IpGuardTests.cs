using System.Net;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger.Tests.Fetching;

public class IpGuardTests
{
    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsPublic_BlocksInternalAddresses(string address)
    {
        Assert.False(IpGuard.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublic_AllowsPublicAddresses(string address)
    {
        Assert.True(IpGuard.IsPublic(IPAddress.Parse(address)));
    }
}
