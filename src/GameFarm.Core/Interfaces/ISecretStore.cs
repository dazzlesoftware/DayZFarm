namespace GameFarm.Core.Interfaces;

/// <summary>
/// Encrypted-at-rest secret storage abstraction (agent tokens, server passwords, the VMware
/// master VMX password -- see docs/VMWARE-SETUP.md). Lives in Core (rather than the Controller,
/// where its DPAPI implementation, <c>SecretStore</c>, actually lives) specifically so
/// <c>GameFarm.VMware.ProcessVmrunRunner</c> can depend on it without creating a circular project
/// reference back to the Controller. Also separated from the concrete implementation so tests can
/// supply an in-memory fake without touching Windows DPAPI or the filesystem.
/// </summary>
public interface ISecretStore
{
    void Set(string name, string value);
    string? Get(string name);
    void Delete(string name);
}
