using GameFarm.HyperV;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFarm.Tests;

public class ManualGpuVirtualizationProviderTests
{
    private static ManualGpuVirtualizationProvider CreateSut(FakePowerShellRunner runner) =>
        new(runner, NullLogger<ManualGpuVirtualizationProvider>.Instance);

    [Fact]
    public async Task IsConfiguredAsync_TrueWhenAdapterCountPositive()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "1" };
        var sut = CreateSut(runner);

        Assert.True(await sut.IsConfiguredAsync("DayZ-001"));
    }

    [Fact]
    public async Task IsConfiguredAsync_FalseWhenNoAdapters()
    {
        var runner = new FakePowerShellRunner { NextStdOut = "0" };
        var sut = CreateSut(runner);

        Assert.False(await sut.IsConfiguredAsync("DayZ-001"));
    }

    [Fact]
    public async Task IsConfiguredAsync_FalseWhenQueryFails()
    {
        var runner = new FakePowerShellRunner { NextExitCode = 1, NextStdErr = "cmdlet not found" };
        var sut = CreateSut(runner);

        Assert.False(await sut.IsConfiguredAsync("DayZ-001"));
    }

    [Fact]
    public async Task IsConfiguredAsync_RejectsInvalidVmName()
    {
        var sut = CreateSut(new FakePowerShellRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => sut.IsConfiguredAsync("bad;name"));
    }

    [Fact]
    public async Task ConfigureAsync_ThrowsNotSupported_DocumentingManualStep()
    {
        var sut = CreateSut(new FakePowerShellRunner());
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.ConfigureAsync("DayZ-001"));
    }
}
