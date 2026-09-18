using GameFarm.Core.Configuration;
using GameFarm.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameFarm.VMware;

/// <summary>
/// <see cref="IVirtualMachineProvider"/> implementation backed by VMware Workstation's
/// vmrun.exe CLI. All state is read/mutated via vmrun and direct .vmx edits (see
/// <see cref="VmxFile"/>) -- nothing here talks to any VMware API/COM surface directly, mirroring
/// how GameFarm.HyperV only ever talks to the standard Hyper-V PowerShell cmdlets.
///
/// Unlike Hyper-V's Get-VM (which enumerates every VM Hyper-V knows about, regardless of
/// location or power state), vmrun has no equivalent "list every VM this host knows about"
/// command -- `vmrun list` only lists currently RUNNING VMs. So this provider treats "a client
/// VM exists" as "a &lt;name&gt;\&lt;name&gt;.vmx file exists under the farm's InstancesDirectory" --
/// the same on-disk naming convention already used for Hyper-V's differencing disks
/// (Instances\&lt;name&gt;\&lt;name&gt;.vhdx) -- and only consults vmrun list to determine which of
/// those known VMs are currently running. See docs/VMWARE-SETUP.md.
/// </summary>
public sealed class VMwareVirtualMachineProvider : IVirtualMachineProvider
{
    private const string VmType = "ws";

    private readonly IVmrunRunner _runner;
    private readonly GameFarmOptions _options;
    private readonly ILogger<VMwareVirtualMachineProvider> _logger;

    public VMwareVirtualMachineProvider(IVmrunRunner runner, IOptions<GameFarmOptions> options, ILogger<VMwareVirtualMachineProvider> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VirtualMachineInfo>> GetMachinesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_options.InstancesDirectory))
            return Array.Empty<VirtualMachineInfo>();

        var runningVmxPaths = await GetRunningVmxPathsAsync(ct);
        var machines = new List<VirtualMachineInfo>();

        foreach (var clientDir in Directory.EnumerateDirectories(_options.InstancesDirectory))
        {
            var name = Path.GetFileName(clientDir);
            var vmxPath = Path.Combine(clientDir, $"{name}.vmx");
            // Not every instance directory is necessarily a VMware-managed one (e.g. a farm
            // migrated from Hyper-V could still have old Instances\<name>\<name>.vhdx folders
            // around) -- skip anything without a matching .vmx rather than erroring.
            if (!File.Exists(vmxPath)) continue;
            machines.Add(await BuildInfoAsync(name, vmxPath, runningVmxPaths, ct));
        }

        return machines;
    }

    public async Task<VirtualMachineInfo?> GetMachineAsync(string name, CancellationToken ct = default)
    {
        var vmxPath = FindVmxPath(name);
        if (vmxPath is null) return null;
        var runningVmxPaths = await GetRunningVmxPathsAsync(ct);
        return await BuildInfoAsync(name, vmxPath, runningVmxPaths, ct);
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        var vmxPath = RequireVmxPath(name);
        var result = await _runner.RunAsync(new[] { "-T", VmType, "start", vmxPath, "nogui" }, ct);
        ThrowIfFailed(result, $"start VM '{name}'");
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        var vmxPath = RequireVmxPath(name);
        // Graceful (soft) shutdown first (requires VMware Tools running in the guest), then a
        // hard power-off if that didn't work -- mirrors HyperVVirtualMachineProvider.StopAsync's
        // same two-step fallback.
        var soft = await _runner.RunAsync(new[] { "-T", VmType, "stop", vmxPath, "soft" }, ct);
        if (soft.Success) return;

        _logger.LogWarning("Graceful stop of '{Name}' failed ({Error}); forcing a hard stop.", name, soft.StandardError);
        var hard = await _runner.RunAsync(new[] { "-T", VmType, "stop", vmxPath, "hard" }, ct);
        ThrowIfFailed(hard, $"stop VM '{name}'");
    }

    public async Task ShutdownAsync(string name, CancellationToken ct = default)
    {
        var vmxPath = RequireVmxPath(name);
        var result = await _runner.RunAsync(new[] { "-T", VmType, "stop", vmxPath, "soft" }, ct);
        ThrowIfFailed(result, $"shut down VM '{name}'");
    }

    public async Task RestartAsync(string name, CancellationToken ct = default)
    {
        var vmxPath = RequireVmxPath(name);
        var result = await _runner.RunAsync(new[] { "-T", VmType, "reset", vmxPath, "soft" }, ct);
        ThrowIfFailed(result, $"restart VM '{name}'");
    }

    public async Task CreateAsync(VirtualMachineCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.Name);

        // Scoped out for v1 -- see docs/VMWARE-SETUP.md. Fail loudly rather than silently
        // ignoring a setting the caller explicitly asked for.
        if (request.SteamLibraryBasePath is not null)
            throw new NotSupportedException(
                "A separate Steam Library base disk is not yet supported by the VMware Workstation provider. " +
                "Install DayZ directly onto each client's own disk for now. See docs/VMWARE-SETUP.md.");

        if (FindVmxPath(request.Name) is not null)
            throw new InvalidOperationException($"VM '{request.Name}' already exists. Remove it first or choose a different name.");

        if (!File.Exists(request.ParentDiskPath))
            throw new FileNotFoundException($"Master VMX not found at '{request.ParentDiskPath}'.", request.ParentDiskPath);

        var snapshotName = request.SourceSnapshotName ?? _options.MasterVmxSnapshot;
        var snapshots = await _runner.RunAsync(new[] { "-T", VmType, "listSnapshots", request.ParentDiskPath }, ct);
        ThrowIfFailed(snapshots, $"list snapshots on master '{request.ParentDiskPath}'");
        var hasSnapshot = snapshots.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Equals(snapshotName, StringComparison.OrdinalIgnoreCase));
        if (!hasSnapshot)
            throw new InvalidOperationException(
                $"Master VMX has no snapshot named '{snapshotName}'. Linked clones require one -- take a snapshot " +
                $"named '{snapshotName}' on the master VM once (VMware Workstation -> the master VM -> " +
                "VM menu -> Snapshot -> Take Snapshot) before creating clients. See docs/VMWARE-SETUP.md.");

        Directory.CreateDirectory(request.InstanceDirectory);
        var vmxPath = Path.Combine(request.InstanceDirectory, $"{request.Name}.vmx");

        // The whole creation sequence tears down whatever this same attempt already created on
        // any failure, mirroring HyperVVirtualMachineProvider.CreateAsync -- a half-created
        // linked clone must never block every subsequent retry with "already exists". See
        // docs/TROUBLESHOOTING.md.
        try
        {
            var clone = await _runner.RunAsync(new[]
            {
                "-T", VmType, "clone", request.ParentDiskPath, vmxPath, "linked",
                $"-snapshot={snapshotName}", $"-cloneName={request.Name}"
            }, ct);
            ThrowIfFailed(clone, $"clone VM '{request.Name}'");

            // vmrun has no cmdlet-equivalent for CPU count, memory size, or network adapter
            // settings -- these are configured by editing the cloned .vmx directly, the
            // standard, documented way VMware itself expects this to be done. See VmxFile.
            var memoryMb = (long)request.MemoryGB * 1024;
            VmxFile.Set(vmxPath, new Dictionary<string, string>
            {
                ["numvcpus"] = request.CpuCount.ToString(),
                ["memsize"] = memoryMb.ToString(),
                ["ethernet0.connectionType"] = "custom",
                ["ethernet0.vnet"] = request.NetworkName,
                ["displayName"] = request.Name
            });

            if (request.StartAfterCreate)
                await StartAsync(request.Name, ct);
        }
        catch
        {
            try { await _runner.RunAsync(new[] { "-T", VmType, "deleteVM", vmxPath }, CancellationToken.None); }
            catch { /* best-effort */ }
            if (Directory.Exists(request.InstanceDirectory))
            {
                try { Directory.Delete(request.InstanceDirectory, recursive: true); }
                catch { /* best-effort */ }
            }
            throw;
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var vmxPath = FindVmxPath(name);
        if (vmxPath is null) return; // Already gone -- matches HyperVVirtualMachineProvider's idempotent behavior.

        var runningVmxPaths = await GetRunningVmxPathsAsync(ct);
        if (runningVmxPaths.Contains(NormalizePath(vmxPath)))
            await _runner.RunAsync(new[] { "-T", VmType, "stop", vmxPath, "hard" }, ct);

        var delete = await _runner.RunAsync(new[] { "-T", VmType, "deleteVM", vmxPath }, ct);
        if (!delete.Success)
            _logger.LogWarning(
                "vmrun deleteVM failed for '{Name}' ({Error}); falling back to deleting its instance directory directly.",
                name, delete.StandardError);

        // Belt-and-braces: deleteVM sometimes leaves the directory behind (e.g. a lingering
        // snapshot lock) -- always also remove the instance directory afterward if still there,
        // mirroring HyperVVirtualMachineProvider.DeleteAsync's own disk cleanup.
        var instanceDir = Path.GetDirectoryName(vmxPath);
        if (instanceDir is not null && Directory.Exists(instanceDir))
        {
            try { Directory.Delete(instanceDir, recursive: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove leftover instance directory '{Dir}' for '{Name}'.", instanceDir, name);
            }
        }
    }

    public async Task<VirtualMachineStatus> GetStatusAsync(string name, CancellationToken ct = default)
    {
        var vm = await GetMachineAsync(name, ct);
        return vm?.Status ?? VirtualMachineStatus.Unknown;
    }

    private string? FindVmxPath(string name)
    {
        var path = Path.Combine(_options.InstancesDirectory, name, $"{name}.vmx");
        return File.Exists(path) ? path : null;
    }

    private string RequireVmxPath(string name)
    {
        ValidateName(name);
        return FindVmxPath(name) ?? throw new InvalidOperationException($"VM '{name}' not found.");
    }

    private async Task<HashSet<string>> GetRunningVmxPathsAsync(CancellationToken ct)
    {
        var result = await _runner.RunAsync(new[] { "-T", VmType, "list" }, ct);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Success) return empty;

        // Output looks like:
        //   Total running VMs: 2
        //   C:\...\DayZ-001\DayZ-001.vmx
        //   C:\...\DayZ-002\DayZ-002.vmx
        var paths = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("Total running VMs", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizePath);

        return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private async Task<VirtualMachineInfo> BuildInfoAsync(string name, string vmxPath, HashSet<string> runningVmxPaths, CancellationToken ct)
    {
        var settings = VmxFile.Read(vmxPath);
        var isRunning = runningVmxPaths.Contains(NormalizePath(vmxPath));

        int.TryParse(settings.GetValueOrDefault("numvcpus"), out var cpuCount);
        long.TryParse(settings.GetValueOrDefault("memsize"), out var memoryMb);

        var macAddress = settings.GetValueOrDefault("ethernet0.generatedAddress")
            ?? settings.GetValueOrDefault("ethernet0.address");

        var guestIp = isRunning ? await TryGetGuestIpAsync(vmxPath, ct) : null;

        return new VirtualMachineInfo
        {
            Name = name,
            Status = isRunning ? VirtualMachineStatus.Running : VirtualMachineStatus.Off,
            CpuCount = cpuCount,
            MemoryStartupBytes = memoryMb * 1024 * 1024,
            GuestIpAddress = guestIp,
            MacAddress = string.IsNullOrWhiteSpace(macAddress) ? null : macAddress,
            // vmrun exposes no equivalent to Hyper-V's $vm.Uptime -- not tracked for now.
            Uptime = TimeSpan.Zero
        };
    }

    /// <summary>Best-effort guest IP lookup with its own short timeout: `vmrun getGuestIPAddress`
    /// without `-wait` is documented to return immediately (with an error if unavailable), but
    /// this guards against any misbehaving vmrun version hanging anyway -- called once per VM on
    /// every status poll, so a hang here must never block the whole farm's dashboard.</summary>
    private async Task<string?> TryGetGuestIpAsync(string vmxPath, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            var result = await _runner.RunAsync(new[] { "-T", VmType, "getGuestIPAddress", vmxPath }, timeoutCts.Token);
            if (!result.Success) return null;
            var ip = result.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
            throw new ArgumentException($"Invalid VM name '{name}'.", nameof(name));
    }

    private void ThrowIfFailed(VmrunResult result, string action)
    {
        if (result.Success) return;
        _logger.LogError("Failed to {Action}: {Error}", action, result.StandardError);
        throw new InvalidOperationException($"Failed to {action}: {result.StandardError.Trim()}");
    }
}
