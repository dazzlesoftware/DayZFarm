using GameFarm.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Services;

/// <summary>
/// Bounded concurrency for the three kinds of fan-out operations the dashboard can trigger, so
/// e.g. "Start All" on 50 clients cannot turn into 50 simultaneous Hyper-V/PowerShell calls or an
/// agent command storm. Configured via GameFarm:MaxConcurrent* / UpdateBatchSize.
/// </summary>
public sealed class ConcurrencyGates : IDisposable
{
    public SemaphoreSlim VmOperations { get; }
    public SemaphoreSlim AgentCommands { get; }
    public SemaphoreSlim Updates { get; }

    public ConcurrencyGates(IOptions<GameFarmOptions> options)
    {
        var o = options.Value;
        VmOperations = new SemaphoreSlim(o.MaxConcurrentVmOperations, o.MaxConcurrentVmOperations);
        AgentCommands = new SemaphoreSlim(o.MaxConcurrentAgentCommands, o.MaxConcurrentAgentCommands);
        Updates = new SemaphoreSlim(o.MaxConcurrentVmOperations, o.MaxConcurrentVmOperations);
    }

    public static async Task<T> RunAsync<T>(SemaphoreSlim gate, Func<Task<T>> action, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public static async Task RunAsync(SemaphoreSlim gate, Func<Task> action, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await action(); }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        VmOperations.Dispose();
        AgentCommands.Dispose();
        Updates.Dispose();
    }
}
