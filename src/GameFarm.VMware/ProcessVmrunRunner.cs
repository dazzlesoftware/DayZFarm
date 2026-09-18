using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameFarm.VMware;

/// <summary>
/// Executes vmrun.exe (VMware Workstation's command-line automation tool) as an out-of-process
/// command. Unlike GameFarm.HyperV's ProcessPowerShellRunner, this needs none of that project's
/// workarounds (temp-script-file routing, encoding fixes) -- vmrun is a plain, well-behaved
/// native console app with a stable argument-based CLI, not PowerShell, so a straightforward
/// redirected-stdout/stderr process invocation works correctly as-is.
///
/// Every call is bounded by <see cref="DefaultTimeout"/>: a "soft" stop/reset depends on the
/// guest's VMware Tools responding to an ACPI-style shutdown request, and if Tools isn't
/// installed/running there (confirmed directly -- a guest with no Tools left `vmrun ... stop
/// ... soft` hanging indefinitely, never returning control at all until the process was killed
/// externally), vmrun itself never times out on its own. Without a bound here, that hang would
/// permanently block whichever ConcurrencyGates slot issued it and leave the dashboard's
/// Stop/Restart action stuck forever. A caller-supplied `ct` is honored on top of this and can
/// still cancel sooner (see VMwareVirtualMachineProvider.TryGetGuestIpAsync's own shorter bound).
/// </summary>
public sealed class ProcessVmrunRunner : IVmrunRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    private readonly string _vmrunPath;
    private readonly ILogger<ProcessVmrunRunner> _logger;

    // Mirrors GameFarm.HyperV.ProcessPowerShellRunner's concurrency cap: without one, many
    // overlapping callers (dashboard polling across several clients at once) can pile up
    // concurrent vmrun process spawns and start timing out on each other.
    private static readonly SemaphoreSlim ConcurrentProcessLimit = new(4, 4);

    public ProcessVmrunRunner(string vmrunPath, ILogger<ProcessVmrunRunner> logger)
    {
        _vmrunPath = vmrunPath;
        _logger = logger;
    }

    public async Task<VmrunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        if (!File.Exists(_vmrunPath))
            throw new FileNotFoundException($"vmrun.exe not found at '{_vmrunPath}'. Check VmrunPath in configuration.", _vmrunPath);

        await ConcurrentProcessLimit.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(_vmrunPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            _logger.LogDebug("Executing vmrun with arguments: {Args}", string.Join(' ', arguments));

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own timeout fired, not the caller's -- vmrun hung (confirmed directly:
                // a "soft" stop/reset against a guest with no responsive VMware Tools). Kill it
                // so it never leaks a zombie process or holds the concurrency slot forever, and
                // surface this as an ordinary failed VmrunResult rather than an exception, so
                // existing failure handling (e.g. StopAsync's hard-stop fallback) applies exactly
                // as it would for any other vmrun failure. See docs/VMWARE-SETUP.md.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                _logger.LogWarning("vmrun timed out after {Timeout} and was killed: {Args}", DefaultTimeout, string.Join(' ', arguments));
                return new VmrunResult
                {
                    ExitCode = -1,
                    StandardError = $"vmrun timed out after {DefaultTimeout} and was killed. Arguments: {string.Join(' ', arguments)}"
                };
            }

            var result = new VmrunResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString()
            };

            if (!result.Success)
                _logger.LogWarning("vmrun exited with code {ExitCode}: {Error}", result.ExitCode, result.StandardError);

            return result;
        }
        finally
        {
            ConcurrentProcessLimit.Release();
        }
    }
}
