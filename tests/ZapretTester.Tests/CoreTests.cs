using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class CoreTests
{
    [Fact]
    public void StartupFailureIsNotReportedAsUserCancellation()
    {
        Assert.Equal(RunState.Blocked, RunFailure.State(false));
        Assert.Equal(RunState.Cancelled, RunFailure.State(true));
    }
    [Fact]
    public void PortsAreActuallyOwnedAndNeverReusedDuringRun()
    {
        using var allocator = new PortAllocator(42000, 42999);
        var first = allocator.Allocate();
        using var competitor = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        Assert.Throws<SocketException>(() => competitor.Bind(new IPEndPoint(IPAddress.Any, first.Port)));
        var port = first.Port;
        first.Dispose();
        Assert.NotEqual(port, allocator.Allocate().Port);
    }
    [Fact]
    public void SettingsRoundTrip()
    {
        var settings = new ApplicationSettings { FlowsealFolder = @"C:\папка с пробелами" };
        Assert.Equal(settings, JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(settings)));
        settings.Validate();
        Assert.Throws<ArgumentException>(() => (settings with { MaxParallelTests = 0 }).Validate());
    }
    [Fact]
    public void UnreliableResultCanNeverBeGreenAndMedianUsesSuccessfulEndpointsOnly()
    {
        var values = TestTarget.Defaults.Select((t, i) => new EndpointResult(t.Name, EndpointStatus.Available, 41000 + i,
            "1.1.1.1", TimeSpan.FromMilliseconds(i * 100), TimeSpan.Zero, 200, null, null)).ToArray();
        var result = new ProfileTestResult("test", "hash", true, values, DateTimeOffset.UtcNow, null);
        Assert.Equal(ProfileStatus.Success, result.Status);
        Assert.Equal(150, result.MedianMilliseconds);
        Assert.Equal(ProfileStatus.Error, (result with { IsReliable = false }).Status);
        Assert.Equal(ProfileStatus.Partial, (result with { Endpoints = values.Take(3).ToArray() }).Status);
    }
    [Theory]
    [InlineData(200, true, true)] [InlineData(403, true, true)] [InlineData(404, false, true)]
    [InlineData(429, false, false)] [InlineData(503, false, false)]
    public void TargetStatusPoliciesAreExplicit(int code, bool website, bool service)
    {
        Assert.Equal(website, TestTarget.Defaults[0].Accepts(code));
        Assert.Equal(service, TestTarget.Defaults[1].Accepts(code));
    }
    [Fact]
    public void EmptyAndPrivilegedPortFiltersAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IsolationFilter.LocalPorts([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsolationFilter.LocalPorts([443]));
        Assert.Throws<ArgumentException>(() => IsolationFilter.Candidate("", [41001]));
    }
}
