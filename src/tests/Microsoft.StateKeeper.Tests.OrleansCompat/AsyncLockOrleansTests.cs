// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

namespace Microsoft.StateKeeper.Tests.OrleansCompat;

/// <summary>
/// Tests for <see cref="AsyncLock{TState}"/> on the Orleans grain scheduler.
/// Locking semantics (contention, cancellation, dispose, double-dispose, etc.)
/// are covered by <see cref="RawAsyncLockOrleansTests"/> — these tests cover
/// only the <see cref="StateHandle{TState}"/> wrapping behavior unique to this class.
/// </summary>
[TestClass]
public class AsyncLockOrleansTests
{
    [TestMethod]
    public async Task AcquireAsync_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "hello" };
            using var sut = new AsyncLock<List<string>>(state);

            using var handle = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            Assert.AreSame(state, handle.State);
            Assert.AreEqual("hello", handle.State[0]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquire_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "world" };
            using var sut = new AsyncLock<List<string>>(state);

            var succeeded = sut.TryAcquire(out var handle);
            await checkOrleans();

            Assert.IsTrue(succeeded);
            Assert.IsNotNull(handle);
            Assert.AreSame(state, handle.State);
            await checkOrleans();

            handle.Dispose();
            await checkOrleans();
        });
    }
}
