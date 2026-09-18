using GameFarm.Core.Configuration;
using GameFarm.Core.Models;
using GameFarm.VMware;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameFarm.Tests;

/// <summary>Fake runner so these tests never touch a real VMware Workstation/vmrun.exe install.</summary>
public sealed class FakeVmrunRunner : IVmrunRunner
{
    public string NextStdOut { get; set; } = string.Empty;
    public string NextStdErr { get; set; } = string.Empty;
    public int NextExitCode { get; set; }
    public List<IReadOnlyList<string>> ExecutedCommands { get; } = new();

    /// <summary>Optional per-subcommand override (keyed by arguments[2], e.g. "list", "clone",
    /// "listSnapshots") -- lets one test script different responses for different vmrun calls
    /// instead of a single fixed NextStdOut for the whole test.</summary>
    public Dictionary<string, Func<IReadOnlyList<string>, VmrunResult>> Responses { get; } = new();

    public Task<VmrunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        ExecutedCommands.Add(arguments);
        var subcommand = arguments.Count > 2 ? arguments[2] : string.Empty;
        if (Responses.TryGetValue(subcommand, out var handler))
            return Task.FromResult(handler(arguments));

        return Task.FromResult(new VmrunResult { ExitCode = NextExitCode, StandardOutput = NextStdOut, StandardError = NextStdErr });
    }
}

public sealed class VMwareVirtualMachineProviderTests : IDisposable
{
    private readonly string _tempRoot;

    public VMwareVirtualMachineProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "dayzfarm-vmware-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private VMwareVirtualMachineProvider CreateSut(FakeVmrunRunner runner, GameFarmOptions? options = null) =>
        new(runner, Options.Create(options ?? new GameFarmOptions { RootDirectory = _tempRoot }),
            NullLogger<VMwareVirtualMachineProvider>.Instance);

    private string CreateFakeClientVmx(string name)
    {
        var dir = Path.Combine(_tempRoot, "Instances", name);
        Directory.CreateDirectory(dir);
        var vmxPath = Path.Combine(dir, $"{name}.vmx");
        File.WriteAllText(vmxPath, $"""
            .encoding = "UTF-8"
            displayName = "{name}"
            numvcpus = "4"
            memsize = "6144"
            ethernet0.generatedAddress = "00:11:22:33:44:55"
            """);
        return vmxPath;
    }

    [Fact]
    public async Task GetMachinesAsync_ScansInstancesDirectoryForVmxFiles()
    {
        CreateFakeClientVmx("DayZ-001");
        var runner = new FakeVmrunRunner { NextStdOut = "Total running VMs: 0" };
        var sut = CreateSut(runner);

        var machines = await sut.GetMachinesAsync();

        Assert.Single(machines);
        Assert.Equal("DayZ-001", machines[0].Name);
        Assert.Equal(VirtualMachineStatus.Off, machines[0].Status);
        Assert.Equal(4, machines[0].CpuCount);
        Assert.Equal(6144L * 1024 * 1024, machines[0].MemoryStartupBytes);
        Assert.Equal("00:11:22:33:44:55", machines[0].MacAddress);
    }

    [Fact]
    public async Task GetMachinesAsync_MarksRunningWhenVmrunListIncludesIt()
    {
        var vmxPath = CreateFakeClientVmx("DayZ-002");
        var runner = new FakeVmrunRunner
        {
            Responses =
            {
                ["list"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = $"Total running VMs: 1\n{vmxPath}\n" },
                ["getGuestIPAddress"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = "10.0.0.9" }
            }
        };
        var sut = CreateSut(runner);

        var machines = await sut.GetMachinesAsync();

        Assert.Equal(VirtualMachineStatus.Running, machines[0].Status);
        Assert.Equal("10.0.0.9", machines[0].GuestIpAddress);
    }

    [Fact]
    public async Task GetMachinesAsync_IgnoresInstanceFoldersWithoutAMatchingVmx()
    {
        // e.g. a leftover Instances\<name>\ folder from a Hyper-V-era client that hasn't been
        // cleaned up -- must not be reported as a phantom VMware machine.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Instances", "LeftoverHyperVFolder"));
        var runner = new FakeVmrunRunner { NextStdOut = "Total running VMs: 0" };
        var sut = CreateSut(runner);

        var machines = await sut.GetMachinesAsync();

        Assert.Empty(machines);
    }

    [Theory]
    [InlineData("DayZ-001; Remove-Item C:\\")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    public async Task StartAsync_RejectsInvalidNames(string invalidName)
    {
        var sut = CreateSut(new FakeVmrunRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => sut.StartAsync(invalidName));
    }

    [Fact]
    public async Task StartAsync_ThrowsIfVmNotFound()
    {
        var sut = CreateSut(new FakeVmrunRunner());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync("DayZ-Missing"));
    }

    [Fact]
    public async Task StopAsync_FallsBackToHardStopWhenSoftFails()
    {
        CreateFakeClientVmx("DayZ-003");
        var softCalled = false;
        var hardCalled = false;
        var runner = new FakeVmrunRunner
        {
            Responses =
            {
                ["stop"] = args =>
                {
                    if (args[^1] == "soft") { softCalled = true; return new VmrunResult { ExitCode = 1, StandardError = "not responding" }; }
                    hardCalled = true;
                    return new VmrunResult { ExitCode = 0 };
                }
            }
        };
        var sut = CreateSut(runner);

        await sut.StopAsync("DayZ-003");

        Assert.True(softCalled);
        Assert.True(hardCalled);
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenSteamLibraryBasePathIsSet()
    {
        var sut = CreateSut(new FakeVmrunRunner());

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-004",
            ParentDiskPath = Path.Combine(_tempRoot, "Master.vmx"),
            InstanceDirectory = Path.Combine(_tempRoot, "Instances", "DayZ-004"),
            NetworkName = "vmnet2",
            SteamLibraryBasePath = "some-path.vmdk"
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => sut.CreateAsync(request));
    }

    [Fact]
    public async Task CreateAsync_ThrowsWhenMasterSnapshotMissing()
    {
        var masterVmx = Path.Combine(_tempRoot, "Master.vmx");
        File.WriteAllText(masterVmx, "displayName = \"Master\"");
        var runner = new FakeVmrunRunner
        {
            Responses = { ["listSnapshots"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = "Total snapshots: 1\nSomeOtherSnapshot\n" } }
        };
        var options = new GameFarmOptions { RootDirectory = _tempRoot, MasterVmxSnapshot = "Baseline" };
        var sut = CreateSut(runner, options);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-005",
            ParentDiskPath = masterVmx,
            InstanceDirectory = Path.Combine(_tempRoot, "Instances", "DayZ-005"),
            NetworkName = "vmnet2"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync(request));
        Assert.Contains("Baseline", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_ClonesAndConfiguresTheVmxOnSuccess()
    {
        var masterVmx = Path.Combine(_tempRoot, "Master.vmx");
        File.WriteAllText(masterVmx, "displayName = \"Master\"");
        var instanceDir = Path.Combine(_tempRoot, "Instances", "DayZ-006");

        var runner = new FakeVmrunRunner
        {
            Responses =
            {
                ["listSnapshots"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = "Total snapshots: 1\nBaseline\n" },
                ["clone"] = args =>
                {
                    // vmrun itself creates the destination directory/vmx -- simulate that.
                    Directory.CreateDirectory(instanceDir);
                    File.WriteAllText(args[3], "displayName = \"placeholder\"");
                    return new VmrunResult { ExitCode = 0 };
                }
            }
        };
        var options = new GameFarmOptions { RootDirectory = _tempRoot, MasterVmxSnapshot = "Baseline" };
        var sut = CreateSut(runner, options);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-006",
            ParentDiskPath = masterVmx,
            InstanceDirectory = instanceDir,
            NetworkName = "vmnet2",
            CpuCount = 2,
            MemoryGB = 4
        };

        await sut.CreateAsync(request);

        var vmxContent = File.ReadAllText(Path.Combine(instanceDir, "DayZ-006.vmx"));
        Assert.Contains("numvcpus = \"2\"", vmxContent);
        Assert.Contains("cpuid.coresPerSocket = \"1\"", vmxContent);
        Assert.Contains("memsize = \"4096\"", vmxContent);
        Assert.Contains("ethernet0.vnet = \"vmnet2\"", vmxContent);
    }

    [Fact]
    public async Task CreateAsync_OverridesAnInheritedCoresPerSocketFromTheMaster()
    {
        // Confirmed live: a master built with cpuid.coresPerSocket=3 produced clones that
        // wouldn't power on ("number of virtual CPUs is not a multiple of the number of cores
        // per socket") the moment a client's CpuCount wasn't itself a multiple of 3 -- the clone
        // only had numvcpus overwritten, not the inherited coresPerSocket. Pinning
        // coresPerSocket to 1 makes every positive CpuCount valid regardless of what the master
        // happened to be built with.
        var masterVmx = Path.Combine(_tempRoot, "Master.vmx");
        File.WriteAllText(masterVmx, "displayName = \"Master\"");
        var instanceDir = Path.Combine(_tempRoot, "Instances", "DayZ-007");

        var runner = new FakeVmrunRunner
        {
            Responses =
            {
                ["listSnapshots"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = "Total snapshots: 1\nBaseline\n" },
                ["clone"] = args =>
                {
                    Directory.CreateDirectory(instanceDir);
                    // Simulates vmrun cloning a master whose own vmx has coresPerSocket=3 --
                    // the clone inherits that line verbatim until VmxFile.Set overwrites it.
                    File.WriteAllText(args[3], "displayName = \"placeholder\"\ncpuid.coresPerSocket = \"3\"");
                    return new VmrunResult { ExitCode = 0 };
                }
            }
        };
        var options = new GameFarmOptions { RootDirectory = _tempRoot, MasterVmxSnapshot = "Baseline" };
        var sut = CreateSut(runner, options);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-007",
            ParentDiskPath = masterVmx,
            InstanceDirectory = instanceDir,
            NetworkName = "vmnet2",
            CpuCount = 4, // not a multiple of the master's inherited coresPerSocket=3
            MemoryGB = 4
        };

        await sut.CreateAsync(request);

        var vmxContent = File.ReadAllText(Path.Combine(instanceDir, "DayZ-007.vmx"));
        Assert.Contains("numvcpus = \"4\"", vmxContent);
        Assert.Contains("cpuid.coresPerSocket = \"1\"", vmxContent);
        Assert.DoesNotContain("cpuid.coresPerSocket = \"3\"", vmxContent);
    }

    [Fact]
    public async Task CreateAsync_CleansUpOnFailureAfterClone()
    {
        var masterVmx = Path.Combine(_tempRoot, "Master.vmx");
        File.WriteAllText(masterVmx, "displayName = \"Master\"");
        var instanceDir = Path.Combine(_tempRoot, "Instances", "DayZ-007");
        var deleteVmCalled = false;

        var runner = new FakeVmrunRunner
        {
            Responses =
            {
                ["listSnapshots"] = _ => new VmrunResult { ExitCode = 0, StandardOutput = "Total snapshots: 1\nBaseline\n" },
                ["clone"] = _ => new VmrunResult { ExitCode = 1, StandardError = "clone failed" },
                ["deleteVM"] = _ => { deleteVmCalled = true; return new VmrunResult { ExitCode = 0 }; }
            }
        };
        var options = new GameFarmOptions { RootDirectory = _tempRoot, MasterVmxSnapshot = "Baseline" };
        var sut = CreateSut(runner, options);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-007",
            ParentDiskPath = masterVmx,
            InstanceDirectory = instanceDir,
            NetworkName = "vmnet2"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync(request));

        Assert.True(deleteVmCalled);
        Assert.False(Directory.Exists(instanceDir));
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotentWhenVmDoesNotExist()
    {
        var sut = CreateSut(new FakeVmrunRunner());
        await sut.DeleteAsync("DayZ-NeverExisted"); // must not throw
    }
}
