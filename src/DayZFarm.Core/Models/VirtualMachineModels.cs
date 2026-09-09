namespace DayZFarm.Core.Models;

public enum VirtualMachineStatus
{
    Unknown,
    Off,
    Starting,
    Running,
    Stopping,
    Saved,
    Paused,
    Error
}

/// <summary>Snapshot of a virtual machine as reported by whichever hypervisor backend is in use.</summary>
public sealed class VirtualMachineInfo
{
    public required string Name { get; set; }
    public VirtualMachineStatus Status { get; set; } = VirtualMachineStatus.Unknown;
    public int CpuCount { get; set; }
    public long MemoryStartupBytes { get; set; }
    public string? GuestIpAddress { get; set; }
    public string? MacAddress { get; set; }
    public TimeSpan Uptime { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Parameters needed to create a new VM from the master differencing chain.</summary>
public sealed class VirtualMachineCreateRequest
{
    public required string Name { get; set; }
    public required string ParentVhdxPath { get; set; }
    public required string InstanceDirectory { get; set; }
    public int CpuCount { get; set; } = 4;
    public int MemoryGB { get; set; } = 6;
    public bool DynamicMemory { get; set; } = false;
    public required string VirtualSwitchName { get; set; }
    public int Generation { get; set; } = 2;
    public bool StartAfterCreate { get; set; }

    /// <summary>
    /// Path to the (already-copied-once) shared Steam Library base VHDX, if the farm is
    /// configured to use one. When set, the new VM gets its own differencing disk against this
    /// base attached as a second SCSI disk (SCSI 0:1) — mirroring scripts/Create-Client.ps1 so
    /// dashboard-created and script-created clients behave identically. Null means no second
    /// disk is attached (backward compatible with farms not using a Steam Library disk).
    /// </summary>
    public string? SteamLibraryBasePath { get; set; }
}

/// <summary>
/// Abstraction over the virtualization backend. The rest of the application only depends on
/// this interface, so Hyper-V can be swapped for VMware (or anything else) later without
/// touching Controller/API/dashboard code.
/// </summary>
public interface IVirtualMachineProvider
{
    Task<IReadOnlyList<VirtualMachineInfo>> GetMachinesAsync(CancellationToken ct = default);
    Task<VirtualMachineInfo?> GetMachineAsync(string name, CancellationToken ct = default);

    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task ShutdownAsync(string name, CancellationToken ct = default);
    Task RestartAsync(string name, CancellationToken ct = default);

    Task CreateAsync(VirtualMachineCreateRequest request, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);

    Task<VirtualMachineStatus> GetStatusAsync(string name, CancellationToken ct = default);
}
