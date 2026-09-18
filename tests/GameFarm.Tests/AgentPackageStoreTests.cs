using GameFarm.Controller.Services;
using GameFarm.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GameFarm.Tests;

/// <summary>Exercises <see cref="AgentPackageStore"/> against real temp-directory files -- no
/// network/HTTP required, per the project's testing requirements.</summary>
public class AgentPackageStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "GameFarmTests-" + Guid.NewGuid());

    private AgentPackageStore CreateSut() =>
        new(Options.Create(new GameFarmOptions { RootDirectory = _tempRoot }));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void GetInfo_ReturnsNullBeforeAnyUpload()
    {
        var sut = CreateSut();
        Assert.Null(sut.GetInfo());
        Assert.Null(sut.ReadPackageBytes());
    }

    [Fact]
    public async Task SaveAsync_PersistsBytesAndComputesVersionHash()
    {
        var sut = CreateSut();
        var bytes = "fake zip contents"u8.ToArray();

        var info = await sut.SaveAsync(new MemoryStream(bytes), "agent-v1.zip");

        Assert.Equal("agent-v1.zip", info.OriginalFileName);
        Assert.Equal(bytes.Length, info.SizeBytes);
        Assert.Equal(64, info.VersionHash.Length); // SHA256 as lowercase hex
        Assert.Equal(bytes, sut.ReadPackageBytes());
    }

    [Fact]
    public async Task SaveAsync_SameContentTwice_ProducesSameVersionHash()
    {
        var sut = CreateSut();
        var bytes = "identical content"u8.ToArray();

        var first = await sut.SaveAsync(new MemoryStream(bytes), "a.zip");
        var second = await sut.SaveAsync(new MemoryStream(bytes), "b.zip");

        Assert.Equal(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public async Task SaveAsync_DifferentContent_ProducesDifferentVersionHash()
    {
        var sut = CreateSut();

        var first = await sut.SaveAsync(new MemoryStream("content one"u8.ToArray()), "a.zip");
        var second = await sut.SaveAsync(new MemoryStream("content two"u8.ToArray()), "b.zip");

        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public async Task SaveAsync_ReplacesThePreviousPackage()
    {
        var sut = CreateSut();
        await sut.SaveAsync(new MemoryStream("old"u8.ToArray()), "old.zip");
        var replaced = await sut.SaveAsync(new MemoryStream("new"u8.ToArray()), "new.zip");

        var info = sut.GetInfo();
        Assert.Equal(replaced.VersionHash, info!.VersionHash);
        Assert.Equal("new.zip", info.OriginalFileName);
        Assert.Equal("new"u8.ToArray(), sut.ReadPackageBytes());
    }

    [Fact]
    public async Task GetInfo_PersistsAcrossStoreInstances()
    {
        var options = Options.Create(new GameFarmOptions { RootDirectory = _tempRoot });
        var first = new AgentPackageStore(options);
        var saved = await first.SaveAsync(new MemoryStream("payload"u8.ToArray()), "agent.zip");

        var second = new AgentPackageStore(options);
        var info = second.GetInfo();

        Assert.NotNull(info);
        Assert.Equal(saved.VersionHash, info!.VersionHash);
        Assert.Equal(saved.OriginalFileName, info.OriginalFileName);
    }
}
