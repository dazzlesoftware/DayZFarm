using GameFarm.Core.Models;
using GameFarm.HyperV;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFarm.Tests;

/// <summary>Fake runner so these tests never touch real Hyper-V/PowerShell.</summary>
public sealed class FakePowerShellRunner : IPowerShellRunner
{
    public string NextStdOut { get; set; } = string.Empty;
    public string NextStdErr { get; set; } = string.Empty;
    public int NextExitCode { get; set; }
    public List<string> ExecutedScripts { get; } = new();

    public Task<PowerShellResult> RunAsync(string script, CancellationToken ct = default)
    {
        ExecutedScripts.Add(script);
        return Task.FromResult(new PowerShellResult { ExitCode = NextExitCode, StandardOutput = NextStdOut, StandardError = NextStdErr });
    }
}

public class HyperVVirtualMachineProviderTests
{
    private static HyperVVirtualMachineProvider CreateSut(FakePowerShellRunner runner) =>
        new(runner, NullLogger<HyperVVirtualMachineProvider>.Instance);

    [Fact]
    public async Task GetMachinesAsync_ParsesJsonArray()
    {
        var runner = new FakePowerShellRunner
        {
            NextStdOut = """
                [
                  { "Name": "DayZ-001", "State": "Running", "CpuCount": 4, "MemoryStartupBytes": 6442450944, "UptimeSeconds": 120, "IpAddress": "10.0.0.5", "MacAddress": "00-11-22-33-44-55" }
                ]
                """
        };
        var sut = CreateSut(runner);

        var machines = await sut.GetMachinesAsync();

        Assert.Single(machines);
        Assert.Equal("DayZ-001", machines[0].Name);
        Assert.Equal(VirtualMachineStatus.Running, machines[0].Status);
        Assert.Equal(4, machines[0].CpuCount);
        Assert.Equal("10.0.0.5", machines[0].GuestIpAddress);
        Assert.Equal(TimeSpan.FromSeconds(120), machines[0].Uptime);
    }

    [Fact]
    public async Task GetMachinesAsync_HandlesEmptyOutput()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "" };
        var sut = CreateSut(runner);

        var machines = await sut.GetMachinesAsync();

        Assert.Empty(machines);
    }

    [Fact]
    public async Task StartAsync_ThrowsOnPowerShellFailure()
    {
        var runner = new FakePowerShellRunner { NextExitCode = 1, NextStdErr = "VM not found" };
        var sut = CreateSut(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync("DayZ-001"));
        Assert.Contains("VM not found", ex.Message);
    }

    [Theory]
    [InlineData("DayZ-001; Remove-Item C:\\")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    public async Task StartAsync_RejectsInvalidNames(string invalidName)
    {
        var sut = CreateSut(new FakePowerShellRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => sut.StartAsync(invalidName));
    }

    [Fact]
    public async Task CreateAsync_ThrowsIfVmAlreadyExists()
    {
        var runner = new FakePowerShellRunner
        {
            NextStdOut = """[{ "Name": "DayZ-001", "State": "Off", "CpuCount": 4, "MemoryStartupBytes": 1, "UptimeSeconds": 0 }]"""
        };
        var sut = CreateSut(runner);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-001",
            ParentDiskPath = Path.GetTempFileName(),
            InstanceDirectory = Path.GetTempPath(),
            NetworkName = "GameFarmSwitch"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync(request));
    }

    [Fact]
    public async Task CreateAsync_WithSteamLibraryBasePath_AttachesDifferencingDiskAsSecondScsiDisk()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "" }; // no existing VM
        var sut = CreateSut(runner);
        var steamLibraryBase = Path.GetTempFileName();

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-001",
            ParentDiskPath = Path.GetTempFileName(),
            InstanceDirectory = Path.GetTempPath(),
            NetworkName = "GameFarmSwitch",
            SteamLibraryBasePath = steamLibraryBase
        };

        await sut.CreateAsync(request);

        var createScript = runner.ExecutedScripts.Last();
        Assert.Contains("Add-VMHardDiskDrive", createScript);
        Assert.Contains("ControllerLocation 1", createScript);
        Assert.Contains(steamLibraryBase, createScript);
    }

    [Fact]
    public async Task CreateAsync_WithMissingSteamLibraryBasePath_ThrowsWithoutRunningCreateScript()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "" }; // no existing VM
        var sut = CreateSut(runner);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-001",
            ParentDiskPath = Path.GetTempFileName(),
            InstanceDirectory = Path.GetTempPath(),
            NetworkName = "GameFarmSwitch",
            SteamLibraryBasePath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".vhdx")
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.CreateAsync(request));
        Assert.DoesNotContain(runner.ExecutedScripts, s => s.Contains("New-VM"));
    }

    [Fact]
    public async Task CreateAsync_WithoutSteamLibraryBasePath_DoesNotAttachSecondDisk()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "" }; // no existing VM
        var sut = CreateSut(runner);

        var request = new VirtualMachineCreateRequest
        {
            Name = "DayZ-001",
            ParentDiskPath = Path.GetTempFileName(),
            InstanceDirectory = Path.GetTempPath(),
            NetworkName = "GameFarmSwitch"
        };

        await sut.CreateAsync(request);

        var createScript = runner.ExecutedScripts.Last();
        Assert.DoesNotContain("Add-VMHardDiskDrive", createScript);
    }
}
