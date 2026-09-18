using System.Security.Cryptography;
using System.Text.Json;
using GameFarm.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Services;

/// <summary>Metadata about the currently uploaded Agent update package (see
/// docs/AGENT-UPDATES.md). <see cref="VersionHash"/> is the package's own SHA256 -- computed
/// automatically on upload rather than asking an administrator to type a version in, and
/// guaranteed to change if and only if the actual bytes do.</summary>
public sealed record AgentPackageInfo(string VersionHash, string OriginalFileName, long SizeBytes, DateTimeOffset UploadedUtc);

/// <summary>
/// Stores the single "currently uploaded" Agent update package on disk -- a zip built via
/// `dotnet publish src/GameFarm.Agent`, pushed out to guest agents on request (see
/// docs/AGENT-UPDATES.md). Only one package is kept at a time; uploading a new one replaces it.
/// No history/rollback in v1 -- keep a copy of a previous build yourself if you want one.
/// </summary>
public sealed class AgentPackageStore
{
    private readonly string _packagePath;
    private readonly string _metaPath;

    public AgentPackageStore(IOptions<GameFarmOptions> options)
    {
        var dir = options.Value.AgentPackageDirectory;
        Directory.CreateDirectory(dir);
        _packagePath = Path.Combine(dir, "agent-package.zip");
        _metaPath = Path.Combine(dir, "agent-package.meta.json");
    }

    public async Task<AgentPackageInfo> SaveAsync(Stream content, string originalFileName, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        using (var fileStream = new FileStream(_packagePath, FileMode.Create, FileAccess.Write))
        using (var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashingStream, ct);
        }
        // sha256.Hash is only populated once the CryptoStream above has been disposed (that's
        // what flushes its final block) -- must not be read before the using block closes.

        var info = new AgentPackageInfo(
            VersionHash: Convert.ToHexString(sha256.Hash!).ToLowerInvariant(),
            OriginalFileName: originalFileName,
            SizeBytes: new FileInfo(_packagePath).Length,
            UploadedUtc: DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(_metaPath, JsonSerializer.Serialize(info), ct);
        return info;
    }

    public AgentPackageInfo? GetInfo() =>
        File.Exists(_metaPath) ? JsonSerializer.Deserialize<AgentPackageInfo>(File.ReadAllText(_metaPath)) : null;

    public byte[]? ReadPackageBytes() => File.Exists(_packagePath) ? File.ReadAllBytes(_packagePath) : null;
}
