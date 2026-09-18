using GameFarm.Agent.Games.DayZ;
using GameFarm.Core.Models;

namespace GameFarm.Tests;

public class DayZLaunchArgumentBuilderTests
{
    [Fact]
    public void Build_IncludesConnectAndPort()
    {
        var options = new GameLaunchOptions { ServerAddress = "192.168.1.50", ServerPort = 2302 };
        var args = DayZLaunchArgumentBuilder.Build(options);
        Assert.Contains("-connect=192.168.1.50", args);
        Assert.Contains("-port=2302", args);
    }

    [Fact]
    public void Build_IncludesPasswordOnlyWhenSet()
    {
        var withPassword = DayZLaunchArgumentBuilder.Build(new GameLaunchOptions { ServerAddress = "a", ServerPort = 1, ServerPassword = "secret" });
        Assert.Contains("-password=secret", withPassword);

        var withoutPassword = DayZLaunchArgumentBuilder.Build(new GameLaunchOptions { ServerAddress = "a", ServerPort = 1 });
        Assert.DoesNotContain(withoutPassword, a => a.StartsWith("-password="));
    }

    [Fact]
    public void Build_JoinsModsWithSemicolons()
    {
        var options = new GameLaunchOptions
        {
            ServerAddress = "a",
            ServerPort = 1,
            Mods = new List<string> { "1559212036", "1564026768" }
        };
        var args = DayZLaunchArgumentBuilder.Build(options);
        Assert.Contains("-mod=1559212036;1564026768", args);
    }

    [Fact]
    public void Build_AppendsAdditionalArgumentsLast()
    {
        var options = new GameLaunchOptions { ServerAddress = "a", ServerPort = 1, AdditionalArguments = new List<string> { "-world=empty" } };
        var args = DayZLaunchArgumentBuilder.Build(options);
        Assert.Equal("-world=empty", args[^1]);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 0)]
    [InlineData("a", 70000)]
    public void Build_ThrowsForInvalidInput(string address, int port)
    {
        Assert.Throws<ArgumentException>(() => DayZLaunchArgumentBuilder.Build(new GameLaunchOptions { ServerAddress = address, ServerPort = port }));
    }

    [Fact]
    public void BuildArgumentString_QuotesArgumentsContainingSpaces()
    {
        var options = new GameLaunchOptions { ServerAddress = "a", ServerPort = 1, AdditionalArguments = new List<string> { "-profiles=C:\\My Profiles" } };
        var str = DayZLaunchArgumentBuilder.BuildArgumentString(options);
        Assert.Contains("\"-profiles=C:\\My Profiles\"", str);
    }
}
