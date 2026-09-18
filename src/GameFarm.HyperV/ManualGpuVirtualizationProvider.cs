using GameFarm.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace GameFarm.HyperV;

/// <summary>
/// GPU-P (GPU Partitioning) status/configuration for a VM. Querying whether a VM already has a
/// GPU partition adapter is fully automated (<see cref="IsConfiguredAsync"/>); actually
/// provisioning one (<see cref="ConfigureAsync"/>) depends heavily on the host's specific GPU
/// model/driver package and is left as a documented manual step for v1 — see
/// docs/MASTER-IMAGE.md's Graphics section. This satisfies the spec's "GPU config may be manual
/// in v1" while still giving the dashboard something real to query.
/// </summary>
public sealed class ManualGpuVirtualizationProvider : IGpuVirtualizationProvider
{
    private readonly IPowerShellRunner _runner;
    private readonly ILogger<ManualGpuVirtualizationProvider> _logger;

    public ManualGpuVirtualizationProvider(IPowerShellRunner runner, ILogger<ManualGpuVirtualizationProvider> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(string vmName, CancellationToken ct = default)
    {
        ValidateName(vmName);
        var script = $"(Get-VMGpuPartitionAdapter -VMName '{Escape(vmName)}' -ErrorAction SilentlyContinue | Measure-Object).Count";
        var result = await _runner.RunAsync(script, ct);
        if (!result.Success)
        {
            _logger.LogDebug("GPU partition query failed for {Vm}: {Error}", vmName, result.StandardError);
            return false;
        }
        return int.TryParse(result.StandardOutput.Trim(), out var count) && count > 0;
    }

    public Task ConfigureAsync(string vmName, CancellationToken ct = default)
    {
        // Deliberately not automated through this general-purpose provider: the matching host
        // GPU driver package path (C:\Windows\System32\DriverStore\FileRepository\...) varies per
        // GPU vendor/model and Windows build, and guessing wrong can leave a VM unable to boot
        // its display adapter at all. An NVIDIA-specific, tested automated helper does exist —
        // scripts/Hyper-V/Tools/Enable-GpuPartitionForVMs.ps1 — for hosts with an NVIDIA GPU; it isn't
        // wired into this interface because it's vendor-specific and host-driver-version
        // dependent, not a general IGpuVirtualizationProvider implementation. See
        // docs/MASTER-IMAGE.md's GPU-P section.
        throw new NotSupportedException(
            "GPU-P configuration is a manual step through this provider — see docs/MASTER-IMAGE.md's GPU-P section. " +
            "For an NVIDIA host, scripts/Hyper-V/Tools/Enable-GpuPartitionForVMs.ps1 automates assignment + driver copy instead.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
            throw new ArgumentException($"Invalid VM name '{name}'.", nameof(name));
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
