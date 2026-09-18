namespace GameFarm.Core;

public enum UpdateState
{
    Pending,
    Updating,
    Completed,
    Failed
}

public sealed class UpdateJob
{
    public required string ClientName { get; init; }
    public UpdateState State { get; set; } = UpdateState.Pending;
    public string? Error { get; set; }
}

/// <summary>
/// Splits a set of clients into fixed-size batches so an "Update All" doesn't make every VM
/// hammer the WAN connection simultaneously. Callers drive batches sequentially, and clients
/// within a batch concurrently (bounded by MaxConcurrentUpdates elsewhere).
/// </summary>
public sealed class UpdateBatchPlanner
{
    private readonly int _batchSize;

    public UpdateBatchPlanner(int batchSize)
    {
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _batchSize = batchSize;
    }

    public IReadOnlyList<IReadOnlyList<UpdateJob>> Plan(IEnumerable<string> clientNames)
    {
        var jobs = clientNames.Select(n => new UpdateJob { ClientName = n }).ToList();
        var batches = new List<IReadOnlyList<UpdateJob>>();
        for (var i = 0; i < jobs.Count; i += _batchSize)
            batches.Add(jobs.Skip(i).Take(_batchSize).ToList());
        return batches;
    }
}
