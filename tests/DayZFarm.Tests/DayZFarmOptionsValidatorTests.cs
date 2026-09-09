using DayZFarm.Core.Configuration;

namespace DayZFarm.Tests;

public class DayZFarmOptionsValidatorTests
{
    private static DayZFarmOptions Valid() => new()
    {
        RootDirectory = @"D:\DayZFarm",
        MasterVhdx = @"D:\DayZFarm\Images\DayZ-Master.vhdx",
        VirtualSwitch = "DayZFarmSwitch",
        DefaultCpuCount = 4,
        DefaultMemoryGB = 6,
        StatusPollSeconds = 10,
        MaxConcurrentVmOperations = 10,
        MaxConcurrentAgentCommands = 20,
        UpdateBatchSize = 5
    };

    [Fact]
    public void ValidConfiguration_DoesNotThrow()
    {
        var ex = Record.Exception(() => DayZFarmOptionsValidator.ValidateAndThrow(Valid()));
        Assert.Null(ex);
    }

    [Fact]
    public void MissingRootDirectory_Throws()
    {
        var options = Valid();
        options.RootDirectory = "";
        Assert.Throws<InvalidOperationException>(() => DayZFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void ZeroUpdateBatchSize_Throws()
    {
        var options = Valid();
        options.UpdateBatchSize = 0;
        Assert.Throws<InvalidOperationException>(() => DayZFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void EmptyReconnectBackoff_Throws()
    {
        var options = Valid();
        options.ReconnectBackoffSeconds = Array.Empty<int>();
        Assert.Throws<InvalidOperationException>(() => DayZFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void NegativeReconnectBackoffValue_Throws()
    {
        var options = Valid();
        options.ReconnectBackoffSeconds = new[] { 5, -1 };
        Assert.Throws<InvalidOperationException>(() => DayZFarmOptionsValidator.ValidateAndThrow(options));
    }
}
