using GameFarm.Core;

namespace GameFarm.Tests;

public class ClientNamingTests
{
    [Theory]
    [InlineData(1, "Client-001")]
    [InlineData(20, "Client-020")]
    [InlineData(150, "Client-150")]
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
        Assert.Equal(new[] { "Client-001", "Client-002", "Client-003", "Client-004", "Client-005" }, names);
    }

    [Fact]
    public void Range_SupportsArbitraryStart()
    {
        var names = ClientNaming.Range(18, 3).ToList();
        Assert.Equal(new[] { "Client-018", "Client-019", "Client-020" }, names);
    }

    [Theory]
    [InlineData("Client-007", true, 7)]
    [InlineData("Client-abc", false, 0)]
    [InlineData("NotAPrefix-1", false, 0)]
    public void TryParseIndex_Works(string name, bool expectedSuccess, int expectedIndex)
    {
        var success = ClientNaming.TryParseIndex(name, out var index);
        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess) Assert.Equal(expectedIndex, index);
    }
}
