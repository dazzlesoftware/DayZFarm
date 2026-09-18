namespace GameFarm.Core.Plugins;

/// <summary>
/// A single admin-authored, named command definition, loaded from one JSON file on disk. The
/// web dashboard/API can only ever trigger a plugin BY NAME (see <see cref="PluginService"/>) --
/// it never accepts or executes arbitrary command text from an HTTP request. This is the key
/// safety property of the whole plugin system: what a plugin actually runs is fixed by whoever
/// has filesystem access to author these JSON files on that machine, not by anything reachable
/// over the network. See docs/PLUGINS.md.
/// </summary>
public sealed class PluginDefinition
{
    /// <summary>Unique (case-insensitive) key used to reference this plugin from the API/dashboard.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Executable to run -- an absolute path, or anything resolvable on PATH
    /// (e.g. "powershell.exe", "cmd.exe").</summary>
    public required string Executable { get; set; }

    /// <summary>Fixed argument string passed to <see cref="Executable"/> exactly as written --
    /// never built from, or combined with, anything supplied over HTTP.</summary>
    public string Arguments { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }

    /// <summary>Killed and reported as a failure if it runs longer than this.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>What the dashboard/API is allowed to see about a configured plugin before running
/// it -- deliberately excludes <see cref="PluginDefinition.Executable"/>/<see
/// cref="PluginDefinition.Arguments"/>; an administrator who wants to audit exactly what a
/// plugin runs reads its JSON file directly on disk instead.</summary>
public sealed class PluginSummary
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public sealed class PluginRunResult
{
    public required string Name { get; set; }
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
}
