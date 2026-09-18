using GameFarm.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameFarm.Tests;

public sealed class PluginServiceTests : IDisposable
{
    private readonly string _pluginsDir;

    public PluginServiceTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "dayzfarm-plugin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_pluginsDir))
            Directory.Delete(_pluginsDir, recursive: true);
    }

    private PluginService CreateSut() => new(_pluginsDir, NullLogger.Instance);

    private void WritePlugin(string fileName, string json) =>
        File.WriteAllText(Path.Combine(_pluginsDir, fileName), json);

    [Fact]
    public void ListPlugins_ReturnsEmptyWhenDirectoryMissing()
    {
        var sut = new PluginService(Path.Combine(_pluginsDir, "does-not-exist"), NullLogger.Instance);
        Assert.Empty(sut.ListPlugins());
    }

    [Fact]
    public void ListPlugins_ReturnsNameAndDescriptionOnly()
    {
        WritePlugin("echo.json", """
            { "name": "echo-test", "description": "Prints a line", "executable": "cmd.exe", "arguments": "/c echo hi" }
            """);
        var sut = CreateSut();

        var plugins = sut.ListPlugins();

        Assert.Single(plugins);
        Assert.Equal("echo-test", plugins[0].Name);
        Assert.Equal("Prints a line", plugins[0].Description);
    }

    [Fact]
    public void ListPlugins_SkipsUnparseableFilesRatherThanThrowing()
    {
        WritePlugin("broken.json", "{ not valid json");
        WritePlugin("good.json", """{ "name": "good", "executable": "cmd.exe" }""");
        var sut = CreateSut();

        var plugins = sut.ListPlugins();

        Assert.Single(plugins);
        Assert.Equal("good", plugins[0].Name);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenPluginNotFound()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync("nope"));
    }

    [Fact]
    public async Task RunAsync_CapturesStdoutAndSucceedsOnZeroExit()
    {
        WritePlugin("echo.json", """
            { "name": "echo-test", "executable": "cmd.exe", "arguments": "/c echo hello-plugin" }
            """);
        var sut = CreateSut();

        var result = await sut.RunAsync("echo-test");

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-plugin", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_IsCaseInsensitiveOnName()
    {
        WritePlugin("echo.json", """{ "name": "Echo-Test", "executable": "cmd.exe", "arguments": "/c echo hi" }""");
        var sut = CreateSut();

        var result = await sut.RunAsync("echo-test");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunAsync_ReportsFailureOnNonZeroExit()
    {
        WritePlugin("fail.json", """{ "name": "fail-test", "executable": "cmd.exe", "arguments": "/c exit 3" }""");
        var sut = CreateSut();

        var result = await sut.RunAsync("fail-test");

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_KillsAndReportsFailureOnTimeout()
    {
        WritePlugin("hang.json", """
            { "name": "hang-test", "executable": "cmd.exe", "arguments": "/c ping -n 30 127.0.0.1 > nul", "timeoutSeconds": 1 }
            """);
        var sut = CreateSut();

        var result = await sut.RunAsync("hang-test");

        Assert.False(result.Success);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }
}
