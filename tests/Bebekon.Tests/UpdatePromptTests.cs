using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class UpdatePromptTests
{
    [Fact] public void NotNowDefersRepeatedChecksOfTheSameRelease()
    {
        var session = new UpdatePromptSession();
        Assert.True(session.ShouldShow("0.1.10"));
        session.Defer("0.1.10");
        Assert.False(session.ShouldShow("0.1.10"));
        Assert.False(session.ShouldShow(null));
    }
    [Fact] public void NewerReleaseCanPromptWithinTheSameSession()
    {
        var session = new UpdatePromptSession(); session.Defer("0.1.10");
        Assert.True(session.ShouldShow("0.1.11"));
    }
    [Fact] public void SettingsCanExplicitlyReopenADeferredRelease()
    {
        var session = new UpdatePromptSession(); session.Defer("0.1.10");
        Assert.True(session.ShouldShow("0.1.10", explicitRequest: true));
        Assert.False(session.ShouldShow(null, explicitRequest: true));
    }
    [Fact] public void RestartAllowsThePreviouslyDeferredRelease()
    {
        var session = new UpdatePromptSession(); session.Defer("0.1.10");
        Assert.True(new UpdatePromptSession().ShouldShow("0.1.10"));
    }
}
