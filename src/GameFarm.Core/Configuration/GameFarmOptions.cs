using System.ComponentModel.DataAnnotations;
using GameFarm.Core.Models;

namespace GameFarm.Core.Configuration;

/// <summary>Root configuration section ("GameFarm" in appsettings.json). Validated at startup.</summary>
public sealed class GameFarmOptions
{
    public const string SectionName = "GameFarm";

    [Required]
    public string RootDirectory { get; set; } = @"D:\GameFarm";

    /// <summary>Which hypervisor backend to use for the whole farm. The two are mutually
    /// exclusive in practice on one host -- see <see cref="HypervisorType"/>.</summary>
    public HypervisorType Hypervisor { get; set; } = HypervisorType.HyperV;

    // --- Hyper-V-specific (ignored when Hypervisor is VMwareWorkstation) ---

    [Required]
    public string MasterVhdx { get; set; } = @"D:\GameFarm\Images\DayZ-Master.vhdx";

    [Required]
    public string VirtualSwitch { get; set; } = "GameFarmSwitch";

    // --- VMware Workstation-specific (ignored when Hypervisor is HyperV) --- see docs/VMWARE-SETUP.md.

    /// <summary>Path to the master VMX. Must already have at least one snapshot -- linked
    /// clones are created against it, mirroring how the Hyper-V provider differences against
    /// MasterVhdx. See docs/VMWARE-SETUP.md.</summary>
    public string MasterVmx { get; set; } = @"D:\GameFarm\Images\DayZ-Master\DayZ-Master.vmx";

    /// <summary>Name of the snapshot on <see cref="MasterVmx"/> to clone every client from.</summary>
    public string MasterVmxSnapshot { get; set; } = "Baseline";

    /// <summary>VMware Workstation custom network (vmnetX) every client attaches to.</summary>
    public string VMwareNetworkName { get; set; } = "vmnet2";

    /// <summary>Path to vmrun.exe, VMware Workstation's command-line automation tool.</summary>
    public string VmrunPath { get; set; } = @"C:\Program Files\VMware\VMware Workstation\vmrun.exe";

    /// <summary>The active master image path for whichever <see cref="Hypervisor"/> is
    /// configured -- lets callers (e.g. <c>ClientOrchestrator</c>) stay hypervisor-agnostic
    /// rather than branching on <see cref="Hypervisor"/> themselves.</summary>
    public string ActiveMasterImagePath => Hypervisor == HypervisorType.VMwareWorkstation ? MasterVmx : MasterVhdx;

    /// <summary>The active virtual network name for whichever <see cref="Hypervisor"/> is
    /// configured.</summary>
    public string ActiveNetworkName => Hypervisor == HypervisorType.VMwareWorkstation ? VMwareNetworkName : VirtualSwitch;

    [Range(1, 64)]
    public int DefaultCpuCount { get; set; } = 4;

    [Range(1, 256)]
    public int DefaultMemoryGB { get; set; } = 6;

    [Range(1, 3600)]
    public int StatusPollSeconds { get; set; } = 10;

    [Range(1, 200)]
    public int MaxConcurrentVmOperations { get; set; } = 10;

    [Range(1, 500)]
    public int MaxConcurrentAgentCommands { get; set; } = 20;

    [Range(1, 500)]
    public int UpdateBatchSize { get; set; } = 5;

    /// <summary>TCP port the guest agent listens on inside every VM.</summary>
    [Range(1, 65535)]
    public int AgentPort { get; set; } = 5099;

    /// <summary>Default reconnect backoff schedule in seconds, applied in order then held at the last value.</summary>
    public int[] ReconnectBackoffSeconds { get; set; } = { 5, 10, 30, 60 };

    [Range(1, 86400)]
    public int MaxReconnectBackoffSeconds { get; set; } = 300;

    public string InstancesDirectory => Path.Combine(RootDirectory, "Instances");
    public string ImagesDirectory => Path.Combine(RootDirectory, "Images");
    public string ConfigDirectory => Path.Combine(RootDirectory, "Config");
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    public string BackupsDirectory => Path.Combine(RootDirectory, "Backups");
    public string ControllerLogsDirectory => Path.Combine(LogsDirectory, "Controller");
    public string ClientLogsDirectory => Path.Combine(LogsDirectory, "Clients");
    public string DatabasePath => Path.Combine(ConfigDirectory, "dayzfarm.db");

    /// <summary>Directory of admin-authored host-side plugin JSON files (see docs/PLUGINS.md).
    /// Empty/missing means no plugins are configured -- never a failure, just an empty list.</summary>
    public string PluginsDirectory => Path.Combine(ConfigDirectory, "Plugins");

    /// <summary>
    /// Conventional path for the shared Steam Library disk (see scripts/Hyper-V/Create-Master.ps1 /
    /// docs/MASTER-IMAGE.md). This is a real disk installed into directly — never copied — so
    /// this path either exists (because Create-Master.ps1 built it, or it was built manually via
    /// scripts/Hyper-V/Create-SteamLibraryDisk.ps1) or it doesn't; <see cref="ClientOrchestrator"/> auto-
    /// detects its presence and gives dashboard-created clients a differencing disk directly
    /// against it when found, exactly like scripts/Hyper-V/Create-Client.ps1 does.
    /// </summary>
    public string SteamLibraryVhdx => Path.Combine(ImagesDirectory, "SteamLibrary-Master.vhdx");

    /// <summary>Directory holding the currently uploaded Agent update package (see
    /// docs/AGENT-UPDATES.md) -- a zip built via `dotnet publish src/GameFarm.Agent`, plus a
    /// small metadata file recording its SHA256 "version" and upload time. Only one package is
    /// kept at a time; uploading a new one replaces it.</summary>
    public string AgentPackageDirectory => Path.Combine(ConfigDirectory, "AgentPackage");
}

/// <summary>Fails fast with a clear message rather than letting the app start half-configured.</summary>
public static class GameFarmOptionsValidator
{
    public static void ValidateAndThrow(GameFarmOptions options)
    {
        var ctx = new ValidationContext(options);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, ctx, results, validateAllProperties: true))
        {
            var msg = string.Join("; ", results.Select(r => r.ErrorMessage));
            throw new InvalidOperationException($"Invalid GameFarm configuration: {msg}");
        }

        if (options.ReconnectBackoffSeconds is null || options.ReconnectBackoffSeconds.Length == 0)
            throw new InvalidOperationException("ReconnectBackoffSeconds must contain at least one value.");

        if (options.ReconnectBackoffSeconds.Any(s => s <= 0))
            throw new InvalidOperationException("ReconnectBackoffSeconds values must be positive.");

        // MasterVhdx/VirtualSwitch carry [Required] unconditionally (kept simple/backward
        // compatible for existing Hyper-V configs), but the VMware-specific settings don't --
        // only actually required when that's the active hypervisor.
        if (options.Hypervisor == HypervisorType.VMwareWorkstation)
        {
            if (string.IsNullOrWhiteSpace(options.MasterVmx))
                throw new InvalidOperationException("MasterVmx is required when Hypervisor is VMwareWorkstation.");
            if (string.IsNullOrWhiteSpace(options.VmrunPath))
                throw new InvalidOperationException("VmrunPath is required when Hypervisor is VMwareWorkstation.");
        }
    }
}
