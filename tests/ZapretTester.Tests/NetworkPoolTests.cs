using System.Net;
using ZapretTester.Windows;
using Xunit;

namespace ZapretTester.Tests;

public sealed class NetworkPoolTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    public void PoolValidationIgnoresIpv6OnlyAddressesButRetainsPossibleDualStackConflicts(string address, bool relevant)
    {
        Assert.Equal(relevant, NetworkEnvironment.CanUseIpv4TestPort(new IPEndPoint(IPAddress.Parse(address), 42050)));
    }
}
