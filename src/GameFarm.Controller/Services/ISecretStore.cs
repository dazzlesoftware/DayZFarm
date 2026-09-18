namespace GameFarm.Controller.Services;

/// <summary>
/// Encrypted-at-rest secret storage abstraction (agent tokens, server passwords). Separated
/// from <see cref="SecretStore"/> (the DPAPI implementation) so tests can supply an in-memory
/// fake without touching Windows DPAPI or the filesystem.
/// </summary>
public interface ISecretStore
{
    void Set(string name, string value);
    string? Get(string name);
    void Delete(string name);
}
