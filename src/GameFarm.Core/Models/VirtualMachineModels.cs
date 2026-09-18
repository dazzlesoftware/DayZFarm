namespace GameFarm.Core.Models;

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

/// <summary>
/// Parameters needed to create a new VM from the master differencing/linked-clone chain.
/// Deliberately hypervisor-neutral: <see cref="ParentDiskPath"/> and <see cref="NetworkName"/>
/// replaced the earlier Hyper-V-specific <c>ParentVhdxPath</c>/<c>VirtualSwitchName</c> names so
/// a non-Hyper-V <see cref="IVirtualMachineProvider"/> (e.g. VMware Workstation) doesn't have to
/// pretend to be Hyper-V-shaped. See docs/TROUBLESHOOTING.md and docs/VMWARE-SETUP.md.
/// </summary>
public sealed class VirtualMachineCreateRequest
{
    public required string Name { get; set; }

    /// <summary>Path to the parent/template image this VM differences or links off of: a
    /// Hyper-V VHDX for the Hyper-V provider, or a VMX (which must already have at least one
    /// snapshot) for the VMware Workstation provider.</summary>
    public required string ParentDiskPath { get; set; }

    public required string InstanceDirectory { get; set; }
    public int CpuCount { get; set; } = 4;
    public int MemoryGB { get; set; } = 6;
    public bool DynamicMemory { get; set; } = false;

    /// <summary>Name of the virtual network to attach this VM to: a Hyper-V virtual switch name,
    /// or a VMware Workstation custom network name (e.g. "vmnet2").</summary>
    public required string NetworkName { get; set; }

    /// <summary>Hyper-V-only: VM generation (1 or 2). Ignored by other providers -- null means
    /// "let the provider pick its own default" rather than forcing every provider to understand
    /// a Hyper-V-specific concept.</summary>
    public int? Generation { get; set; } = 2;

    /// <summary>VMware Workstation-only: name of the snapshot on <see cref="ParentDiskPath"/> to
    /// linked-clone from. Ignored by other providers. Null means "use the provider's own
    /// configured default" (e.g. GameFarmOptions.MasterVmxSnapshot).</summary>
    public string? SourceSnapshotName { get; set; }

    public bool StartAfterCreate { get; set; }

    /// <summary>
    /// Path to the (already-copied-once) shared Steam Library base disk, if the farm is
    /// configured to use one. When set, the new VM gets its own differencing disk against this
    /// base attached as a second disk — mirroring scripts/Hyper-V/Create-Client.ps1 so dashboard-created
    /// and script-created clients behave identically. Null means no second disk is attached
    /// (backward compatible with farms not using a Steam Library disk). Hyper-V-provider-only
    /// for now; the VMware Workstation provider throws <see cref="NotSupportedException"/> if
    /// this is set, rather than silently ignoring it — see docs/VMWARE-SETUP.md.
    /// </summary>
    public string? SteamLibraryBasePath { get; set; }
}

/// <summary>
/// Which hypervisor backend is active. The two are mutually exclusive in practice on a single
/// Windows host -- enabling Hyper-V puts Windows itself in a hypervisor role that VMware
/// Workstation then has to run nested under (real performance/feature loss), so this picks
/// exactly one <see cref="IVirtualMachineProvider"/> for the whole farm rather than trying to
/// run both at once. See docs/VMWARE-SETUP.md.
/// </summary>
public enum HypervisorType
{
    HyperV,
    VMwareWorkstation
}

/// <summary>
/// Abstraction over the virtualization backend. The rest of the application only depends on
/// this interface, so Hyper-V can be swapped for VMware (or anything else) later without
/// touching Controller/API/dashboard code. See <see cref="HypervisorType"/> and
/// docs/VMWARE-SETUP.md for how the active one is selected.
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
