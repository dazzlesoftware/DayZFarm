using DayZFarm.Agent.DayZ;
using DayZFarm.Agent.Interop;
using DayZFarm.Agent.Steam;
using DayZFarm.Core;
using DayZFarm.Core.Models;
using DayZFarm.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DayZFarm.Agent.Watchdog;

/// <summary>
/// Background loop implementing per-client auto-reconnect. Distinguishes an intentional stop
/// (AutoReconnect flag toggled off, or an explicit Stop command was the most recent action) from
/// an unexpected exit, and applies the configured backoff schedule before relaunching.
/// </summary>
public sealed class ReconnectWatchdogService : BackgroundService
{
    private readonly WindowsDayZLauncher _launcher;
    private readonly SteamManager _steam;
    private readonly AgentRuntimeState _state;
    private readonly ILogger<ReconnectWatchdogService> _logger;
    private readonly ReconnectPolicy _policy;

    public ReconnectWatchdogService(
        WindowsDayZLauncher launcher,
        SteamManager steam,
        AgentRuntimeState state,
        IOptions<AgentOptions> options,
        ILogger<ReconnectWatchdogService> logger)
    {
        _launcher = launcher;
        _steam = steam;
        _state = state;
        _logger = logger;
        _policy = new ReconnectPolicy(options.Value.ReconnectBackoffSeconds, options.Value.MaxReconnectBackoffSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasRunning = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var running = _launcher.IsRunning(out _);

                // Refreshed every tick (the OS flag only holds until the next call) while DayZ
                // is running, unattended, in the interactive session -- see
                // IdleActivitySuppressor's remarks for why this matters for BattlEye specifically.
                if (running)
                    IdleActivitySuppressor.KeepSystemAndDisplayAwake();
                else if (wasRunning)
                    IdleActivitySuppressor.AllowSystemAndDisplaySleep();

                if (wasRunning && !running && _state.LastStopReason == StopReason.None)
                {
                    // Process disappeared without an explicit stop request => treat as a crash.
                    _state.LastStopReason = StopReason.Crashed;
                }

                if (!running && _state.AutoReconnect && ReconnectPolicy.ShouldAutoRestart(_state.LastStopReason) && _state.DesiredLaunchOptions is not null)
                {
                    var delay = _policy.NextDelay();
                    _logger.LogWarning("DayZ not running (reason: {Reason}). Reconnecting in {Delay}.", _state.LastStopReason, delay);
                    await Task.Delay(delay, stoppingToken);

                    if (!_steam.IsSteamRunning(out _))
                        _steam.Start();

                    await _launcher.LaunchAsync(_state.DesiredLaunchOptions, stoppingToken);
                    _state.LastStopReason = StopReason.None;
                }
                else if (running)
                {
                    _policy.Reset();
                }

                wasRunning = running;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Watchdog iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Shared in-memory state describing the current desired/observed client state.</summary>
public sealed class AgentRuntimeState
{
    public bool AutoReconnect { get; set; } = true;
    public StopReason LastStopReason { get; set; } = StopReason.None;
    public DayZLaunchOptions? DesiredLaunchOptions { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
}

public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public int[] ReconnectBackoffSeconds { get; set; } = { 5, 10, 30, 60 };
    public int MaxReconnectBackoffSeconds { get; set; } = 300;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 5099;
    public string ProfileDirectory { get; set; } = @"C:\DayZFarmAgent\Profile";
}
