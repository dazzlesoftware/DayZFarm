using System.Text.Json;
using System.Text.Json.Serialization;
using GameFarm.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameFarm.HyperV;

/// <summary>
/// <see cref="IVirtualMachineProvider"/> implementation backed by the Hyper-V PowerShell
/// module. All state is read via <c>Get-VM</c>/<c>Get-VMNetworkAdapter</c> and mutated via the
/// standard Hyper-V cmdlets — nothing here talks to Hyper-V's WMI/COM surface directly, which
/// keeps the implementation simple and keeps behavior consistent with what an administrator
/// would see running the same cmdlets by hand.
/// </summary>
public sealed class HyperVVirtualMachineProvider : IVirtualMachineProvider
{
    private readonly IPowerShellRunner _runner;
    private readonly ILogger<HyperVVirtualMachineProvider> _logger;

    public HyperVVirtualMachineProvider(IPowerShellRunner runner, ILogger<HyperVVirtualMachineProvider> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<VirtualMachineInfo>> GetMachinesAsync(CancellationToken ct = default)
    {
        const string script = """
            Get-VM | ForEach-Object {
                $vm = $_
                $adapter = Get-VMNetworkAdapter -VM $vm | Select-Object -First 1
                # $adapter.IPAddresses is an empty array (not $null) when the VM has no lease
                # yet (e.g. State=Off). Piping an empty array through Select-Object -First 1
                # yields PowerShell's internal "AutomationNull" value, which ConvertTo-Json
                # renders as an empty JSON object `{}` rather than `null` or a string -- that
                # breaks deserialization into RawVmInfo.IpAddress (string?). Explicit if/else
                # guarantees a real string or an explicit $null every time. See docs/TROUBLESHOOTING.md.
                $ip = $adapter.IPAddresses | Where-Object { $_ -notmatch ':' } | Select-Object -First 1
                [PSCustomObject]@{
                    Name = $vm.Name
                    State = $vm.State.ToString()
                    CpuCount = $vm.ProcessorCount
                    MemoryStartupBytes = $vm.MemoryStartup
                    UptimeSeconds = [int]$vm.Uptime.TotalSeconds
                    IpAddress = if ($ip) { [string]$ip } else { $null }
                    MacAddress = $adapter.MacAddress
                }
            } | ConvertTo-Json -Depth 3
            """;

        var result = await _runner.RunAsync(script, ct);
        ThrowIfFailed(result, "list virtual machines");

        // Temporary exact byte-level diagnostic (see docs/TROUBLESHOOTING.md): two different
        // fixes for a stdout-encoding theory both produced an identical "BytePositionInLine: 1"
        // JsonException, which means that theory was wrong -- print the actual UTF-16 code units
        // of the first characters so there's no more guessing about what's actually in this
        // string.
        var head = result.StandardOutput.Length > 0
            ? string.Join(" ", result.StandardOutput[..Math.Min(30, result.StandardOutput.Length)].Select(c => $"U+{(int)c:X4}('{(char.IsControl(c) ? '.' : c)}')"))
            : "(empty)";
        _logger.LogInformation(
            "Get-VM enumeration returned {Length} chars of stdout, {ErrLength} chars of stderr. First chars: {Head} | Stderr: {Stderr}",
            result.StandardOutput.Length, result.StandardError.Length, head,
            result.StandardError.Length > 2000 ? result.StandardError[..2000] + "...(truncated)" : result.StandardError);

        var raw = ParseJsonArray<RawVmInfo>(result.StandardOutput);
        return raw.Select(Map).ToList();
    }

    public async Task<VirtualMachineInfo?> GetMachineAsync(string name, CancellationToken ct = default)
    {
        var machines = await GetMachinesAsync(ct);
        return machines.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync($"Start-VM -Name '{Escape(name)}' -ErrorAction Stop", ct);
        ThrowIfFailed(result, $"start VM '{name}'");
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        // Graceful shutdown attempt first (requires guest integration services), then force stop.
        var result = await _runner.RunAsync(
            $"Stop-VM -Name '{Escape(name)}' -ErrorAction SilentlyContinue; " +
            $"if ((Get-VM -Name '{Escape(name)}').State -ne 'Off') {{ Stop-VM -Name '{Escape(name)}' -Force -ErrorAction Stop }}", ct);
        ThrowIfFailed(result, $"stop VM '{name}'");
    }

    public async Task ShutdownAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync($"Stop-VM -Name '{Escape(name)}' -Force -ErrorAction Stop", ct);
        ThrowIfFailed(result, $"shut down VM '{name}'");
    }

    public async Task RestartAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync($"Restart-VM -Name '{Escape(name)}' -Force -ErrorAction Stop", ct);
        ThrowIfFailed(result, $"restart VM '{name}'");
    }

    public async Task CreateAsync(VirtualMachineCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.Name);

        // Idempotency: never silently clobber an existing VM.
        var existing = await GetMachineAsync(request.Name, ct);
        if (existing is not null)
            throw new InvalidOperationException($"VM '{request.Name}' already exists. Remove it first or choose a different name.");

        if (!File.Exists(request.ParentDiskPath))
            throw new FileNotFoundException($"Master VHDX not found at '{request.ParentDiskPath}'.", request.ParentDiskPath);

        Directory.CreateDirectory(request.InstanceDirectory);
        var diskPath = Path.Combine(request.InstanceDirectory, $"{request.Name}.vhdx");
        if (File.Exists(diskPath))
            throw new InvalidOperationException($"A disk already exists at '{diskPath}'. Remove it before recreating this client.");

        // Optional second disk: a differencing child of the shared Steam Library base, so
        // dashboard-created clients get the same "install once, difference per client" Steam
        // Library layout as scripts/Hyper-V/Create-Client.ps1 instead of each installing DayZ from
        // scratch onto their OS disk. See docs/MASTER-IMAGE.md's Steam Library section.
        string? steamDiskPath = null;
        if (request.SteamLibraryBasePath is { } basePath)
        {
            if (!File.Exists(basePath))
                throw new FileNotFoundException($"Steam Library base VHDX not found at '{basePath}'.", basePath);
            steamDiskPath = Path.Combine(request.InstanceDirectory, $"{request.Name}-SteamLibrary.vhdx");
            if (File.Exists(steamDiskPath))
                throw new InvalidOperationException($"A Steam Library disk already exists at '{steamDiskPath}'. Remove it before recreating this client.");
        }

        var memoryBytes = (long)request.MemoryGB * 1024 * 1024 * 1024;

        var steamDiskScript = steamDiskPath is not null
            ? $$"""
                New-VHD -Path '{{Escape(steamDiskPath)}}' -ParentPath '{{Escape(request.SteamLibraryBasePath!)}}' -Differencing | Out-Null
                Add-VMHardDiskDrive -VMName '{{Escape(request.Name)}}' -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 1 -Path '{{Escape(steamDiskPath)}}'
                """
            : "";

        // The whole creation sequence is wrapped in try/catch: without this, any step failing
        // partway (e.g. a bad Set-VMMemory call, or any other mid-script error) left a
        // half-configured VM/disk behind -- New-VM had already succeeded -- which then blocked
        // every subsequent retry with "VM already exists" until someone noticed and manually
        // ran Remove-VM/deleted the leftover disk(s). Any failure now best-effort tears down
        // whatever this same attempt already created (never anything pre-existing) before
        // re-throwing the original error, so a failed creation is safe to simply retry. See
        // docs/TROUBLESHOOTING.md.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                New-VHD -Path '{{Escape(diskPath)}}' -ParentPath '{{Escape(request.ParentDiskPath)}}' -Differencing | Out-Null
                $vm = New-VM -Name '{{Escape(request.Name)}}' -Generation {{request.Generation ?? 2}} -MemoryStartupBytes {{memoryBytes}} -VHDPath '{{Escape(diskPath)}}' -SwitchName '{{Escape(request.NetworkName)}}'
                {{steamDiskScript}}
                Set-VMProcessor -VMName '{{Escape(request.Name)}}' -Count {{request.CpuCount}}
                Set-VMMemory -VMName '{{Escape(request.Name)}}' -DynamicMemoryEnabled {{(request.DynamicMemory ? "$true" : "$false")}}
                Set-VMFirmware -VMName '{{Escape(request.Name)}}' -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'
                Enable-VMIntegrationService -VMName '{{Escape(request.Name)}}' -Name 'Guest Service Interface','Heartbeat','Key-Value Pair Exchange','Shutdown','Time Synchronization','VSS'
                Set-VM -Name '{{Escape(request.Name)}}' -AutomaticCheckpointsEnabled $false
            } catch {
                $failure = $_
                $existingVm = Get-VM -Name '{{Escape(request.Name)}}' -ErrorAction SilentlyContinue
                if ($existingVm) {
                    if ($existingVm.State -ne 'Off') { Stop-VM -Name '{{Escape(request.Name)}}' -Force -ErrorAction SilentlyContinue }
                    Remove-VM -Name '{{Escape(request.Name)}}' -Force -ErrorAction SilentlyContinue
                }
                foreach ($diskToRemove in @('{{Escape(diskPath)}}', '{{Escape(steamDiskPath ?? "")}}')) {
                    if ($diskToRemove -and (Test-Path $diskToRemove)) { Remove-Item $diskToRemove -Force -ErrorAction SilentlyContinue }
                }
                throw $failure
            }
            """;
        var result = await _runner.RunAsync(script, ct);
        ThrowIfFailed(result, $"create VM '{request.Name}'");

        if (request.StartAfterCreate)
            await StartAsync(request.Name, ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $vm = Get-VM -Name '{{Escape(name)}}' -ErrorAction SilentlyContinue
            if ($vm) {
                if ($vm.State -ne 'Off') { Stop-VM -Name '{{Escape(name)}}' -Force }
                $disks = (Get-VMHardDiskDrive -VMName '{{Escape(name)}}').Path
                Remove-VM -Name '{{Escape(name)}}' -Force
                foreach ($d in $disks) { if (Test-Path $d) { Remove-Item $d -Force } }
            }
            """;
        var result = await _runner.RunAsync(script, ct);
        ThrowIfFailed(result, $"delete VM '{name}'");
    }

    public async Task<VirtualMachineStatus> GetStatusAsync(string name, CancellationToken ct = default)
    {
        var vm = await GetMachineAsync(name, ct);
        return vm?.Status ?? VirtualMachineStatus.Unknown;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
            throw new ArgumentException($"Invalid VM name '{name}'.", nameof(name));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private void ThrowIfFailed(PowerShellResult result, string action)
    {
        if (result.Success) return;
        _logger.LogError("Failed to {Action}: {Error}", action, result.StandardError);
        throw new InvalidOperationException($"Failed to {action}: {result.StandardError.Trim()}");
    }

    private static List<T> ParseJsonArray<T>(string json)
    {
        json = json.Trim();
        if (string.IsNullOrEmpty(json)) return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch (JsonException listEx)
        {
            // A single object (not wrapped in an array) can happen depending on PS version
            // quirks -- e.g. exactly one VM, without -AsArray. Try that before giving up.
            try
            {
                var single = JsonSerializer.Deserialize<T>(json, JsonOptions);
                return single is null ? new List<T>() : new List<T> { single };
            }
            catch (JsonException singleEx)
            {
                // Neither interpretation worked. Previously this silently swallowed listEx and
                // let singleEx (from an attempt that was never going to succeed once the array
                // parse failed) surface alone, making the actual problem undiagnosable from the
                // exception message. Surface both, plus the content itself, so it's diagnosable
                // without needing extra logging rounds next time. See docs/TROUBLESHOOTING.md.
                throw new JsonException(
                    $"Could not parse PowerShell output as either a JSON array or a single {typeof(T).Name}. " +
                    $"List-parse error: {listEx.Message} Single-parse error: {singleEx.Message} " +
                    $"Content ({json.Length} chars): {(json.Length > 1000 ? json[..1000] + "...(truncated)" : json)}",
                    listEx);
            }
        }
    }

    private static VirtualMachineInfo Map(RawVmInfo raw) => new()
    {
        Name = raw.Name ?? string.Empty,
        Status = MapState(raw.State),
        CpuCount = raw.CpuCount,
        MemoryStartupBytes = raw.MemoryStartupBytes,
        GuestIpAddress = raw.IpAddress,
        MacAddress = raw.MacAddress,
        Uptime = TimeSpan.FromSeconds(raw.UptimeSeconds)
    };

    private static VirtualMachineStatus MapState(string? state) => state switch
    {
        "Running" => VirtualMachineStatus.Running,
        "Off" => VirtualMachineStatus.Off,
        "Starting" => VirtualMachineStatus.Starting,
        "Stopping" => VirtualMachineStatus.Stopping,
        "Saved" => VirtualMachineStatus.Saved,
        "Paused" => VirtualMachineStatus.Paused,
        _ => VirtualMachineStatus.Unknown
    };

    private sealed class RawVmInfo
    {
        public string? Name { get; set; }
        public string? State { get; set; }

        [JsonPropertyName("CpuCount")]
        public int CpuCount { get; set; }
        public long MemoryStartupBytes { get; set; }
        public int UptimeSeconds { get; set; }
        public string? IpAddress { get; set; }
        public string? MacAddress { get; set; }
    }
}
