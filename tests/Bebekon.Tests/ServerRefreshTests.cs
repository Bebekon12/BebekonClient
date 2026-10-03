using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ServerRefreshTests
{
    [Fact]
    public void RenameAndReorderPreserveSelectionFavoriteAndPing()
    {
        var first = CoreTests.Node(); first.SubscriptionId = "a"; first.Favorite = true; first.LatencyMs = 57; first.Latency = "57 мс";
        var other = CoreTests.Node(); other.Id = "other"; other.Host = "other.example"; other.SubscriptionId = "a";
        var renamed = CoreTests.Node(); renamed.Id = "new-provider-id"; renamed.Name = "New label";
        var result = ServerRefresh.Merge("a", [first, other], [other, renamed], first.Id);
        var selected = Assert.Single(result, s => s.Id == first.Id);
        Assert.True(selected.Favorite); Assert.Equal(57, selected.LatencyMs); Assert.Equal("New label", selected.Name);
        Assert.Equal(ServerRefresh.ConnectionKey(first), ServerRefresh.ConnectionKey(selected));
    }
    [Fact]
    public void RemovedSelectionStaysAvailableInsteadOfSwitchingServers()
    {
        var selected = CoreTests.Node(); selected.SubscriptionId = "a";
        Assert.Same(selected, Assert.Single(ServerRefresh.Merge("a", [selected], [], selected.Id)));
    }
    [Fact]
    public void RotatedCredentialsKeepTheLogicalSelectionButChangeConnectionKey()
    {
        var old = CoreTests.Node(); old.SubscriptionId = "a";
        var fresh = CoreTests.Node(); fresh.Id = "rotated"; fresh.Uuid = Guid.NewGuid().ToString();
        var result = Assert.Single(ServerRefresh.Merge("a", [old], [fresh], old.Id));
        Assert.Equal(old.Id, result.Id); Assert.NotEqual(ServerRefresh.ConnectionKey(old), ServerRefresh.ConnectionKey(result));
    }
    [Fact]
    public void DifferentSubscriptionsNeverTakeEachOthersIdentity()
    {
        var old = CoreTests.Node(); old.SubscriptionId = "a";
        var result = Assert.Single(ServerRefresh.Merge("b", [old], [CoreTests.Node()], old.Id));
        Assert.NotEqual(old.Id, result.Id); Assert.Equal("b", result.SubscriptionId);
    }
}
