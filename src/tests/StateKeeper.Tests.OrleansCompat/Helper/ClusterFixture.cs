using Orleans.TestingHost;

namespace StateKeeper.Tests.OrleansCompat.Helper;

/// <summary>
/// Shared test cluster that is started once per test run and reused across all test classes.
/// Individual tests use unique grain IDs (GUIDs) to avoid cross-test interference.
/// </summary>
[TestClass]
public static class ClusterFixture
{
    public static InProcessTestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// Convenience accessor for the cluster's grain factory.
    /// </summary>
    public static IGrainFactory GrainFactory => Cluster.Client;

    /// <summary>
    /// Registers the callback, dispatches it to a fresh grain, and runs it
    /// on the grain's task scheduler.
    /// </summary>
    public static async Task ExecuteOnGrain(GrainTestCallback callback)
    {
        var id = Guid.NewGuid().ToString();
        IExecutorGrain.RegisterCallback(id, callback);
        var grain = GrainFactory.GetGrain<IExecutorGrain>(id);
        await grain.Execute(id);
    }

    [AssemblyInitialize]
    public static async Task Initialize(TestContext _)
    {
        var builder = new InProcessTestClusterBuilder();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }
}
