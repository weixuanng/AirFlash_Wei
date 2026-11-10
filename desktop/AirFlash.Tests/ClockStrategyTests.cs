using System.Collections.Concurrent;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ClockStrategyTests
{
    [Fact]
    public void ArrangementsAreOfferedInTurnAndWrapAround()
    {
        var plan = new ClockStrategyPlan();
        Assert.Equal(ClockStrategyPlan.Sender, plan.Current("pod"));
        Assert.Equal(ClockStrategyPlan.Sender, plan.Current("pod"));
        Assert.Equal(ClockStrategyPlan.FollowQuiet, plan.Failed("pod"));
        Assert.Equal(ClockStrategyPlan.FollowActive, plan.Failed("pod"));
        Assert.Equal(ClockStrategyPlan.FollowActive, plan.Current("pod"));
        Assert.Equal(ClockStrategyPlan.FollowPeers, plan.Failed("pod"));
        Assert.Equal(ClockStrategyPlan.Sender, plan.Failed("pod"));
        // Each receiver has its own turn.
        Assert.Equal(ClockStrategyPlan.Sender, plan.Current("other"));
    }

    [Fact]
    public void TheArrangementThatStreamedIsRememberedAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"airflash-clock-{Guid.NewGuid():N}", "clock-strategy.json");
        try
        {
            var plan = new ClockStrategyPlan(path);
            plan.Failed("pod");
            // A failed attempt alone is not worth remembering.
            Assert.False(File.Exists(path));
            Assert.False(plan.IsProven("pod"));
            // Another receiver that only failed is not saved along with the one that streamed.
            plan.Failed("other");
            plan.Worked("pod");
            Assert.True(plan.IsProven("pod"));
            Assert.Equal(ClockStrategyPlan.FollowQuiet, new ClockStrategyPlan(path).Current("pod"));
            Assert.True(new ClockStrategyPlan(path).IsProven("pod"));
            // Moving off the proven arrangement makes the current one unproven again.
            plan.Failed("pod");
            Assert.False(plan.IsProven("pod"));
            Assert.Equal(ClockStrategyPlan.Sender, new ClockStrategyPlan(path).Current("other"));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"pod\":\"something_else\"}")]
    [InlineData("[]")]
    public void ADamagedFileOnlyCostsTheRememberedChoice(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"airflash-clock-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            var plan = new ClockStrategyPlan(path);
            Assert.Equal(ClockStrategyPlan.Sender, plan.Current("pod"));
            plan.Worked("pod");
            Assert.Equal(ClockStrategyPlan.Sender, new ClockStrategyPlan(path).Current("pod"));
        }
        finally { File.Delete(path); }
    }
}
