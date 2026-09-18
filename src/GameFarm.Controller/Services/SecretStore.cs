using System.Runtime.Versioning;
using System.Security.Cryptography;
using GameFarm.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Services;

/// <summary>
/// Encrypted-at-rest secret storage (agent tokens, server passwords) using Windows DPAPI
/// (current-user scope, matching the controller's service account). Secrets are never placed in
/// appsettings.json or the SQLite database — only a secret *name* is stored there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore : ISecretStore
{
    private readonly string _secretsDirectory;

    public SecretStore(IOptions<GameFarmOptions> options)
    {
        _secretsDirectory = Path.Combine(options.Value.ConfigDirectory, "secrets");
        Directory.CreateDirectory(_secretsDirectory);
    }

    public void Set(string name, string value)
    {
        ValidateName(name);
        var protectedBytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), protectedBytes);
    }

    public string? Get(string name)
    {
        ValidateName(name);
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public void Delete(string name)
    {
        ValidateName(name);
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string name) => Path.Combine(_secretsDirectory, $"{name}.secret");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException($"Invalid secret name '{name}'.", nameof(name));
    }
}
