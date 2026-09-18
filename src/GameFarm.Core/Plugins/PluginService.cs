using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GameFarm.Core.Plugins;

/// <summary>
/// Loads admin-authored plugin definitions (one JSON file per plugin) from a directory and runs
/// them by name. Shared by both the Controller (host-side plugins -- one-off host/VMware/
/// Hyper-V maintenance tasks) and the Agent (guest-side plugins, run inside a client VM): same
/// loader/runner, each pointed at its own plugin directory via the constructor. See
/// docs/PLUGINS.md for the full safety model -- in short: only a plugin's NAME is ever accepted
/// from an HTTP request; what it actually executes is fixed by whoever authored the JSON file on
/// that machine's own disk, never anything supplied over the network.
///
/// This deliberately runs with whatever privileges the calling process already has (the
/// Controller already manages Hyper-V/VMware; the Agent already runs interactively as the VM's
/// own user, per Program.cs/scripts/Install-Agent.ps1) -- it does not itself elevate anything.
/// </summary>
public sealed class PluginService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _pluginsDirectory;
    private readonly ILogger _logger;

    public PluginService(string pluginsDirectory, ILogger logger)
    {
        _pluginsDirectory = pluginsDirectory;
        _logger = logger;
    }

    public IReadOnlyList<PluginSummary> ListPlugins() =>
        LoadDefinitions()
            .Select(p => new PluginSummary { Name = p.Name, Description = p.Description })
            .ToList();

    public async Task<PluginRunResult> RunAsync(string name, CancellationToken ct = default)
    {
        var definition = LoadDefinitions().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            throw new InvalidOperationException($"No plugin named '{name}' is configured under '{_pluginsDirectory}'.");

        var stopwatch = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(definition.Executable, definition.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory))
            psi.WorkingDirectory = definition.WorkingDirectory;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _logger.LogInformation("Running plugin '{Name}'.", definition.Name);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Bounded the same defensive way as GameFarm.VMware's ProcessVmrunRunner -- a plugin
        // that hangs (a stuck command, a process waiting on input that never comes) must never
        // block forever; it's killed and reported as an ordinary failure instead.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        int exitCode;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            stderr.AppendLine($"Plugin '{definition.Name}' timed out after {definition.TimeoutSeconds}s and was killed.");
            exitCode = -1;
        }

        stopwatch.Stop();
        var result = new PluginRunResult
        {
            Name = definition.Name,
            Success = exitCode == 0,
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            DurationSeconds = stopwatch.Elapsed.TotalSeconds
        };

        if (!result.Success)
            _logger.LogWarning("Plugin '{Name}' exited with code {ExitCode}: {Error}", definition.Name, exitCode, result.StandardError);

        return result;
    }

    private IReadOnlyList<PluginDefinition> LoadDefinitions()
    {
        if (!Directory.Exists(_pluginsDirectory)) return Array.Empty<PluginDefinition>();

        var definitions = new List<PluginDefinition>();
        foreach (var file in Directory.EnumerateFiles(_pluginsDirectory, "*.json"))
        {
            try
            {
                var definition = JsonSerializer.Deserialize<PluginDefinition>(File.ReadAllText(file), JsonOptions);
                if (definition is not null) definitions.Add(definition);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load plugin definition from '{File}'; skipping it.", file);
            }
        }
        return definitions;
    }
}
