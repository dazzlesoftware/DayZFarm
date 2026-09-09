namespace DayZFarm.Core;

public enum StopReason
{
    /// <summary>Not stopped — currently running or never started.</summary>
    None,
    Crashed,
    ServerUnavailable,
    SteamOffline,
    UpdateRequired,
    AdministratorStop,
    ControllerShutdown
}

/// <summary>
/// Backoff state machine deciding whether/when a stopped DayZ client should be relaunched.
/// Administrator-initiated and controller-initiated stops are never auto-restarted.
/// </summary>
public sealed class ReconnectPolicy
{
    private readonly int[] _backoffSeconds;
    private readonly int _maxBackoffSeconds;
    private int _attempt;

    public ReconnectPolicy(int[] backoffSeconds, int maxBackoffSeconds)
    {
        if (backoffSeconds is null || backoffSeconds.Length == 0)
            throw new ArgumentException("At least one backoff value is required.", nameof(backoffSeconds));
        _backoffSeconds = backoffSeconds;
        _maxBackoffSeconds = maxBackoffSeconds;
    }

    public static bool ShouldAutoRestart(StopReason reason) => reason switch
    {
        StopReason.AdministratorStop => false,
        StopReason.ControllerShutdown => false,
        StopReason.None => false,
        _ => true
    };

    /// <summary>Call once each time a restart attempt is made; returns the delay to use next time.</summary>
    public TimeSpan NextDelay()
    {
        var index = Math.Min(_attempt, _backoffSeconds.Length - 1);
        var seconds = Math.Min(_backoffSeconds[index], _maxBackoffSeconds);
        _attempt++;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Reset after a successful, stable connection.</summary>
    public void Reset() => _attempt = 0;

    public int AttemptCount => _attempt;
}
