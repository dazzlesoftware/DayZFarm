using GameFarm.Core.Configuration;

namespace GameFarm.Tests;

public class GameFarmOptionsValidatorTests
{
    private static GameFarmOptions Valid() => new()
    {
        RootDirectory = @"D:\GameFarm",
        MasterVhdx = @"D:\GameFarm\Images\DayZ-Master.vhdx",
        VirtualSwitch = "GameFarmSwitch",
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
        var ex = Record.Exception(() => GameFarmOptionsValidator.ValidateAndThrow(Valid()));
        Assert.Null(ex);
    }

    [Fact]
    public void MissingRootDirectory_Throws()
    {
        var options = Valid();
        options.RootDirectory = "";
        Assert.Throws<InvalidOperationException>(() => GameFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void ZeroUpdateBatchSize_Throws()
    {
        var options = Valid();
        options.UpdateBatchSize = 0;
        Assert.Throws<InvalidOperationException>(() => GameFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void EmptyReconnectBackoff_Throws()
    {
        var options = Valid();
        options.ReconnectBackoffSeconds = Array.Empty<int>();
        Assert.Throws<InvalidOperationException>(() => GameFarmOptionsValidator.ValidateAndThrow(options));
    }

    [Fact]
    public void NegativeReconnectBackoffValue_Throws()
    {
        var options = Valid();
        options.ReconnectBackoffSeconds = new[] { 5, -1 };
        Assert.Throws<InvalidOperationException>(() => GameFarmOptionsValidator.ValidateAndThrow(options));
    }
}
