using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DayZFarm.HyperV;

/// <summary>
/// Executes scripts via an out-of-process PowerShell host. Using the CLI rather than the
/// System.Management.Automation SDK avoids pulling in a native PowerShell host runtime
/// dependency and keeps this assembly portable to build/test on any machine; only actually
/// running Hyper-V cmdlets requires Windows + admin.
///
/// Deliberately prefers Windows PowerShell (powershell.exe) over PowerShell 7+ (pwsh), even
/// though pwsh is otherwise the more modern choice: the Hyper-V module is a legacy CDXML/WMI
/// module that is only fully, natively compatible with Windows PowerShell. Under pwsh it either
/// needs the WinCompat shim (`Import-Module Hyper-V -UseWindowsPowerShell`) or can silently
/// return empty/incomplete results for some cmdlets instead of throwing. Since this runner is
/// only ever used for Hyper-V operations (Windows only, no cross-platform concern), there's no
/// upside to preferring pwsh here. See docs/TROUBLESHOOTING.md.
///
/// The script is written to a temporary .ps1 file and run with `-File` rather than piped via
/// stdin with `-Command -`: the latter is a documented PowerShell 7+ convention for "read the
/// script from stdin" that Windows PowerShell 5.1 does not reliably honor the same way.
///
/// The script's actual result value is captured to a temporary output file (written with an
/// explicit encoding we control) rather than read from the process's redirected stdout: Windows
/// PowerShell 5.1's console output encoding does not reliably match what .NET decodes a
/// redirected stream as, even after forcing `[Console]::OutputEncoding` -- a fully redirected,
/// windowless child process often has no real console to reconfigure at all, so that had no
/// effect. Routing the payload through a file sidesteps pipe-encoding ambiguity entirely; stdout
/// is still drained (to avoid the child blocking on a full pipe buffer) but no longer trusted for
/// the actual data. See docs/TROUBLESHOOTING.md.
/// </summary>
public sealed class ProcessPowerShellRunner : IPowerShellRunner
{
    private readonly ILogger<ProcessPowerShellRunner> _logger;
    private static readonly Lazy<string> ExecutableName = new(ResolveExecutable);
    private static int _loggedResolvedExecutable;

    // Each call spawns a real powershell.exe process (non-trivial startup cost). Without a cap,
    // many overlapping callers (dashboard polling + multiple browser tabs/SignalR reconnects, or
    // a retry storm from some other failure) can pile up dozens of concurrent process spawns and
    // start timing out on each other -- observed directly during a debugging session. This is a
    // safety net independent of the Controller's own ConcurrencyGates, which don't cover every
    // call path (e.g. status polling's VM enumeration).
    private static readonly SemaphoreSlim ConcurrentProcessLimit = new(4, 4);

    public ProcessPowerShellRunner(ILogger<ProcessPowerShellRunner> logger)
    {
        _logger = logger;
    }

    private static string ResolveExecutable()
    {
        foreach (var candidate in new[] { "powershell", "pwsh" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-NoLogo -NoProfile -Command \"$PSVersionTable.PSVersion.Major\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                probe?.WaitForExit(5000);
                if (probe is { ExitCode: 0 })
                    return candidate;
            }
            catch
            {
                // candidate not found on PATH; try the next one.
            }
        }
        return "powershell";
    }

    public async Task<PowerShellResult> RunAsync(string script, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        // Log which PowerShell host got resolved exactly once, at Information level (not
        // Debug) -- useful when diagnosing any future Hyper-V issue without needing to
        // reproduce it separately. See docs/TROUBLESHOOTING.md.
        if (Interlocked.Exchange(ref _loggedResolvedExecutable, 1) == 0)
            _logger.LogInformation("Hyper-V PowerShell operations will run via '{Executable}'.", ExecutableName.Value);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"dayzfarm-{Guid.NewGuid():N}.ps1");
        var outputPath = Path.Combine(Path.GetTempPath(), $"dayzfarm-out-{Guid.NewGuid():N}.txt");

        // Wrap the caller's script so its pipeline result (if any) is written to a file we
        // control the encoding of, instead of relying on the process's stdout encoding to match
        // what .NET expects. `Out-String` on already-string input (every caller here ends with
        // ConvertTo-Json, or produces no output at all) passes it through materially unchanged.
        var wrapped = $$"""
            $__dayzFarmOutput = & {
            {{script}}
            } | Out-String -Width 8192
            [System.IO.File]::WriteAllText('{{outputPath}}', $__dayzFarmOutput, [System.Text.Encoding]::UTF8)
            """;

        // PowerShell needs a BOM (or at least a Unicode-aware encoding) to reliably read a script
        // file containing non-ASCII characters; UTF8 with BOM is the safe default for both hosts.
        await File.WriteAllTextAsync(scriptPath, wrapped, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

        await ConcurrentProcessLimit.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(ExecutableName.Value)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);

            using var process = new Process { StartInfo = psi };
            var stderr = new StringBuilder();

            // stdout is drained but intentionally discarded -- see class remarks. Still need to
            // read it so the child process never blocks on a full output pipe buffer.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            _logger.LogDebug("Executing PowerShell script ({Length} chars) via {ScriptPath}", script.Length, scriptPath);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            var stdout = File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath, Encoding.UTF8, ct)
                : string.Empty;

            var result = new PowerShellResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr.ToString()
            };

            if (!result.Success)
                _logger.LogWarning("PowerShell script exited with code {ExitCode}: {Error}", result.ExitCode, result.StandardError);

            return result;
        }
        finally
        {
            ConcurrentProcessLimit.Release();
            try { File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
            try { File.Delete(outputPath); } catch { /* best-effort cleanup */ }
        }
    }
}
