using DayZFarm.Core;

namespace DayZFarm.Tests;

public class ClientNamingTests
{
    [Theory]
    [InlineData(1, "DayZ-001")]
    [InlineData(20, "DayZ-020")]
    [InlineData(150, "DayZ-150")]
    public void ForIndex_PadsToThreeDigits(int index, string expected)
    {
        Assert.Equal(expected, ClientNaming.ForIndex(index));
    }

    [Fact]
    public void ForIndex_ThrowsForZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientNaming.ForIndex(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientNaming.ForIndex(-1));
    }

    [Fact]
    public void Range_ProducesSequentialNames()
    {
        var names = ClientNaming.Range(1, 5).ToList();
        Assert.Equal(new[] { "DayZ-001", "DayZ-002", "DayZ-003", "DayZ-004", "DayZ-005" }, names);
    }

    [Fact]
    public void Range_SupportsArbitraryStart()
    {
        var names = ClientNaming.Range(18, 3).ToList();
        Assert.Equal(new[] { "DayZ-018", "DayZ-019", "DayZ-020" }, names);
    }

    [Theory]
    [InlineData("DayZ-007", true, 7)]
    [InlineData("DayZ-abc", false, 0)]
    [InlineData("NotAPrefix-1", false, 0)]
    public void TryParseIndex_Works(string name, bool expectedSuccess, int expectedIndex)
    {
        var success = ClientNaming.TryParseIndex(name, out var index);
        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess) Assert.Equal(expectedIndex, index);
    }
}
