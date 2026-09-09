using DayZFarm.Core.Models;

namespace DayZFarm.Controller.Data;

/// <summary>
/// Persistence abstraction for <see cref="DayZClientInstance"/> records. Separated from
/// <see cref="ClientRepository"/> (the SQLite implementation) purely so
/// <c>ClientOrchestrator</c> can be unit-tested against an in-memory fake — no SQLite/file I/O
/// required for that test suite.
/// </summary>
public interface IClientRepository
{
    Task<int> AddAsync(DayZClientInstance instance, CancellationToken ct = default);
    Task<IReadOnlyList<DayZClientInstance>> GetAllAsync(CancellationToken ct = default);
    Task<DayZClientInstance?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<DayZClientInstance?> GetByNameAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task UpdateAsync(DayZClientInstance instance, CancellationToken ct = default);
}
