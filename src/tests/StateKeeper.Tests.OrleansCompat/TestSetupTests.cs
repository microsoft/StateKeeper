using StateKeeper.Tests.OrleansCompat.Helper;

namespace StateKeeper.Tests.OrleansCompat;

/// <summary>
/// Validates that the test setup itself works:
/// <see cref="ClusterFixture"/>, <see cref="ExecutorGrain"/>, and <see cref="CheckOrleans"/>.
/// </summary>
[TestClass]
public class TestSetupTests
{
    [TestMethod]
    public async Task CheckOrleans_OnGrainScheduler_Succeeds()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task CheckOrleans_OffGrainScheduler_Throws()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
            {
                // Escape to the thread pool, then try to check Orleans
                await Task.Run(async () => await checkOrleans());
            });
        });
    }
}
