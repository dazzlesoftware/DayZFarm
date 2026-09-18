using GameFarm.Controller.Services;

namespace GameFarm.Tests.Fakes;

/// <summary>In-memory <see cref="ISecretStore"/> fake — no DPAPI/filesystem in tests.</summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();

    public void Set(string name, string value) => _values[name] = value;
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Delete(string name) => _values.Remove(name);
}
