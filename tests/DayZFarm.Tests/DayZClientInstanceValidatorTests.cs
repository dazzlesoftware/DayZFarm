using DayZFarm.Core.Models;

namespace DayZFarm.Tests;

public class DayZClientInstanceValidatorTests
{
    private static DayZClientInstance Valid() => new()
    {
        Name = "DayZ-001",
        VmName = "DayZ-001",
        ServerAddress = "192.168.1.50",
        ServerPort = 2302
    };

    [Fact]
    public void ValidInstance_HasNoErrors()
    {
        Assert.Empty(DayZClientInstanceValidator.Validate(Valid()));
    }

    [Theory]
    [InlineData("DayZ 001")]
    [InlineData("DayZ/001")]
    [InlineData("DayZ;rm -rf")]
    public void InvalidVmName_ProducesError(string vmName)
    {
        var instance = Valid();
        instance.VmName = vmName;
        Assert.NotEmpty(DayZClientInstanceValidator.Validate(instance));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void InvalidPort_ProducesError(int port)
    {
        var instance = Valid();
        instance.ServerPort = port;
        Assert.NotEmpty(DayZClientInstanceValidator.Validate(instance));
    }
}
