using DayZFarm.Core.Interfaces;
using DayZFarm.Shared;
using Microsoft.Extensions.Logging;

namespace DayZFarm.Agent.DayZ;

/// <summary>
/// Best-effort <see cref="IDayZStatusProvider"/> based on tailing DayZ's own RPT/script log
/// files. DayZ does not expose a supported connection-state API, so this is inherently
/// heuristic — it is deliberately isolated behind the interface so a more precise mechanism
/// (e.g. reading BattlEye logs, or a future in-game reporting mod) can replace it later without
/// touching any caller.
/// </summary>
public sealed class DayZLogStatusProvider : IDayZStatusProvider
{
    private readonly string _profileDirectory;
    private readonly WindowsDayZLauncher _launcher;
    private readonly ILogger<DayZLogStatusProvider> _logger;

    private static readonly (string Marker, ConnectionStatus Status)[] Markers =
    {
        ("BattlEye Client: Initialized", ConnectionStatus.Connecting),
        ("Dedicated host session", ConnectionStatus.Connected),
        ("Multiplayer session", ConnectionStatus.Connected),
        ("You have been disconnected", ConnectionStatus.Disconnected),
        ("Connection failed", ConnectionStatus.Error),
        ("Kicked off", ConnectionStatus.Disconnected),
    };

    public DayZLogStatusProvider(string profileDirectory, WindowsDayZLauncher launcher, ILogger<DayZLogStatusProvider> logger)
    {
        _profileDirectory = profileDirectory;
        _launcher = launcher;
        _logger = logger;
    }

    public Task<ConnectionStatus> DetectConnectionStatusAsync(CancellationToken ct = default)
    {
        if (!_launcher.IsRunning(out _))
            return Task.FromResult(ConnectionStatus.Offline);

        try
        {
            var latestLog = FindLatestLogFile();
            if (latestLog is null)
                return Task.FromResult(ConnectionStatus.StartingDayZ);

            var tail = ReadTail(latestLog, maxLines: 200);

            ConnectionStatus? found = null;
            foreach (var line in tail)
            {
                foreach (var (marker, status) in Markers)
                {
                    if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        found = status;
                }
            }

            return Task.FromResult(found ?? ConnectionStatus.Connecting);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect DayZ logs for connection status.");
            return Task.FromResult(ConnectionStatus.Error);
        }
    }

    public Task<string?> DetectInstalledVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var latestLog = FindLatestLogFile();
            if (latestLog is null) return Task.FromResult<string?>(null);
            var firstLines = File.ReadLines(latestLog).Take(5);
            var versionLine = firstLines.FirstOrDefault(l => l.Contains("Version", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(versionLine);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    private string? FindLatestLogFile()
    {
        if (!Directory.Exists(_profileDirectory)) return null;
        return Directory.EnumerateFiles(_profileDirectory, "*.RPT", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(_profileDirectory, "*.ADM", SearchOption.TopDirectoryOnly))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static List<string> ReadTail(string path, int maxLines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var all = new List<string>();
        while (reader.ReadLine() is { } line)
            all.Add(line);
        return all.Count <= maxLines ? all : all.GetRange(all.Count - maxLines, maxLines);
    }
}
