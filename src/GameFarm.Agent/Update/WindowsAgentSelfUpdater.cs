using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using GameFarm.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameFarm.Agent.Update;

/// <summary>
/// Applies a new Agent build received from the Controller as a zip (see docs/AGENT-UPDATES.md).
/// Windows won't let a running process's own .exe/.dll be overwritten, so this stages the new
/// files under %TEMP%, then hands off to a short, detached PowerShell script that: waits a few
/// seconds (letting this HTTP response actually reach the Controller before the process
/// disappears), kills this agent process by PID, copies the staged files over the live install
/// directory -- never deleting anything not present in the new package, so admin-authored
/// Plugins/ and this VM's own Logs/ survive an update untouched -- writes a version marker file,
/// and restarts the "Game Farm Agent" Scheduled Task. There is nothing left for managed code to
/// do once that script is launched; the running process simply gets killed out from under it
/// moments later.
/// </summary>
public sealed class WindowsAgentSelfUpdater : IAgentSelfUpdater
{
    private readonly ILogger<WindowsAgentSelfUpdater> _logger;

    public WindowsAgentSelfUpdater(ILogger<WindowsAgentSelfUpdater> logger)
    {
        _logger = logger;
    }

    public string ApplyUpdate(byte[] zipBytes)
    {
        if (zipBytes.Length == 0)
            throw new ArgumentException("Update package is empty.", nameof(zipBytes));

        var version = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

        var stagingRoot = Path.Combine(Path.GetTempPath(), "GameFarmAgentUpdate", Guid.NewGuid().ToString("N"));
        var extractedDir = Path.Combine(stagingRoot, "extracted");
        Directory.CreateDirectory(extractedDir);

        var zipPath = Path.Combine(stagingRoot, "package.zip");
        File.WriteAllBytes(zipPath, zipBytes);
        ZipFile.ExtractToDirectory(zipPath, extractedDir);

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var scriptPath = Path.Combine(stagingRoot, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildScript(Environment.ProcessId, extractedDir, installDir, version, stagingRoot));

        _logger.LogInformation(
            "Staged agent update (version {Version}) at {StagingRoot}; launching detached updater script.",
            version, stagingRoot);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        Process.Start(psi);

        return version;
    }

    // Single braces below are literal PowerShell syntax (if/for/try blocks); only {{...}} holes
    // are C# interpolation, per the $$ raw-string prefix.
    private static string BuildScript(int agentPid, string extractedDir, string installDir, string version, string stagingRoot) => $$"""
        $ErrorActionPreference = 'Stop'
        $logFile = Join-Path "{{stagingRoot}}" 'apply-update.log'
        function Log($msg) { Add-Content -Path $logFile -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg" }

        Log 'Waiting for the Controller''s HTTP response to flush before stopping the agent...'
        Start-Sleep -Seconds 3

        try {
            $proc = Get-Process -Id {{agentPid}} -ErrorAction SilentlyContinue
            if ($proc) {
                Log 'Stopping agent process (PID {{agentPid}})...'
                Stop-Process -Id {{agentPid}} -Force -ErrorAction SilentlyContinue
                for ($i = 0; $i -lt 20; $i++) {
                    if (-not (Get-Process -Id {{agentPid}} -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 500
                }
            } else {
                Log 'Agent process (PID {{agentPid}}) was already gone.'
            }

            Log 'Copying updated files into the install directory...'
            Copy-Item -Path (Join-Path "{{extractedDir}}" '*') -Destination "{{installDir}}" -Recurse -Force

            Set-Content -Path (Join-Path "{{installDir}}" 'agent-version.txt') -Value '{{version}}'
            Log 'Wrote version marker: {{version}}'

            Log 'Restarting the Game Farm Agent scheduled task...'
            Start-ScheduledTask -TaskName 'Game Farm Agent'
            Log 'Update applied successfully.'
        } catch {
            Log "Update FAILED: $($_.Exception.Message)"
        } finally {
            Start-Sleep -Seconds 2
            Remove-Item -LiteralPath "{{stagingRoot}}" -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;
}
