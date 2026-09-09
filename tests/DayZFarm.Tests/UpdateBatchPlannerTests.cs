using DayZFarm.Core;

namespace DayZFarm.Tests;

public class UpdateBatchPlannerTests
{
    [Fact]
    public void Plan_SplitsIntoConfiguredBatchSize()
    {
        var planner = new UpdateBatchPlanner(batchSize: 5);
        var names = ClientNaming.Range(1, 12);
        var batches = planner.Plan(names);

        Assert.Equal(3, batches.Count);
        Assert.Equal(5, batches[0].Count);
        Assert.Equal(5, batches[1].Count);
        Assert.Equal(2, batches[2].Count);
    }

    [Fact]
    public void Plan_AllJobsStartPending()
    {
        var planner = new UpdateBatchPlanner(batchSize: 3);
        var batches = planner.Plan(ClientNaming.Range(1, 3));
        Assert.All(batches.SelectMany(b => b), job => Assert.Equal(UpdateState.Pending, job.State));
    }

    [Fact]
    public void Constructor_ThrowsForNonPositiveBatchSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateBatchPlanner(0));
    }
}
