using GameFarm.Core;

namespace GameFarm.Tests;

public class ReconnectPolicyTests
{
    [Theory]
    [InlineData(StopReason.Crashed, true)]
    [InlineData(StopReason.ServerUnavailable, true)]
    [InlineData(StopReason.SteamOffline, true)]
    [InlineData(StopReason.UpdateRequired, true)]
    [InlineData(StopReason.AdministratorStop, false)]
    [InlineData(StopReason.ControllerShutdown, false)]
    [InlineData(StopReason.None, false)]
    public void ShouldAutoRestart_RespectsStopReason(StopReason reason, bool expected)
    {
        Assert.Equal(expected, ReconnectPolicy.ShouldAutoRestart(reason));
    }

    [Fact]
    public void NextDelay_FollowsConfiguredSchedule()
    {
        var policy = new ReconnectPolicy(new[] { 5, 10, 30, 60 }, maxBackoffSeconds: 300);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(10), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), policy.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay());
        // Holds at the last configured value once the schedule is exhausted.
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay());
    }

    [Fact]
    public void NextDelay_IsCappedByMaxBackoff()
    {
        var policy = new ReconnectPolicy(new[] { 5, 500 }, maxBackoffSeconds: 60);
        policy.NextDelay();
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay());
    }

    [Fact]
    public void Reset_RestartsScheduleFromBeginning()
    {
        var policy = new ReconnectPolicy(new[] { 5, 10 }, maxBackoffSeconds: 300);
        policy.NextDelay();
        policy.NextDelay();
        policy.Reset();
        Assert.Equal(TimeSpan.FromSeconds(5), policy.NextDelay());
    }

    [Fact]
    public void Constructor_ThrowsForEmptySchedule()
    {
        Assert.Throws<ArgumentException>(() => new ReconnectPolicy(Array.Empty<int>(), 60));
    }
}
