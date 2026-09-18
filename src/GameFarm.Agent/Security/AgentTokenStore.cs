using System.Security.Cryptography;
using GameFarm.Shared;
using Microsoft.Extensions.Logging;

namespace GameFarm.Agent.Security;

/// <summary>
/// Generates (on first run) and loads the per-agent bearer token used to authenticate the
/// Controller. The token is protected at rest with Windows DPAPI (machine scope) so the file on
/// disk cannot be read on another machine. Never logged.
/// </summary>
public sealed class AgentTokenStore
{
    private readonly string _tokenFilePath;
    private readonly ILogger<AgentTokenStore> _logger;
    private string? _cachedToken;

    public AgentTokenStore(ILogger<AgentTokenStore> logger, string? tokenDirectory = null)
    {
        _logger = logger;
        var dir = tokenDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameFarmAgent");
        Directory.CreateDirectory(dir);
        _tokenFilePath = Path.Combine(dir, AgentAuthConstants.TokenFileName);
    }

    public string GetOrCreateToken()
    {
        if (_cachedToken is not null) return _cachedToken;

        if (File.Exists(_tokenFilePath))
        {
            var protectedBytes = File.ReadAllBytes(_tokenFilePath);
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine)
                : protectedBytes; // non-Windows dev/test fallback only; production targets Windows guests.
            _cachedToken = Convert.ToBase64String(plain);
            return _cachedToken;
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        _cachedToken = Convert.ToBase64String(tokenBytes);

        var toProtect = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(tokenBytes, null, DataProtectionScope.LocalMachine)
            : tokenBytes;
        File.WriteAllBytes(_tokenFilePath, toProtect);
        _logger.LogInformation("Generated new agent token at {Path}. Register it with the controller during provisioning.", _tokenFilePath);

        return _cachedToken;
    }
}
