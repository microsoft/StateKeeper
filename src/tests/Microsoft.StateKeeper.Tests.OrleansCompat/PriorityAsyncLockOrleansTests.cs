// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

namespace Microsoft.StateKeeper.Tests.OrleansCompat;

/// <summary>
/// Tests for <see cref="PriorityAsyncLock{TState, TWaiterPriority}"/> on the Orleans grain scheduler.
/// Locking semantics (contention, cancellation, dispose, double-dispose, etc.)
/// are covered by <see cref="RawPriorityAsyncLockOrleansTests"/> — these tests cover
/// only the <see cref="StateHandle{TState}"/> wrapping behavior unique to this class.
/// </summary>
[TestClass]
public class PriorityAsyncLockOrleansTests
{
    [TestMethod]
    public async Task AcquireAsync_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var state = new List<string> { "hello" };
            using var sut = new PriorityAsyncLock<List<string>, int>(state);

            using var handle = await sut.AcquireAsync(0, CancellationToken.None);
            await checkOrleans();

            Assert.AreSame(state, handle.State);
            Assert.AreEqual("hello", handle.State[0]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquire_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var state = new List<string> { "world" };
            using var sut = new PriorityAsyncLock<List<string>, int>(state);

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

    [TestMethod]
    public async Task AcquireAsync_Contested_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var state = new List<string> { "contested" };
            using var sut = new PriorityAsyncLock<List<string>, int>(state);

            // Hold the lock so the next acquire must wait (contested / slow path)
            using var handle1 = await sut.AcquireAsync(0, CancellationToken.None);
            await checkOrleans();

            var handle2Task = sut.AcquireAsync(0, CancellationToken.None).AsTask();
            Assert.IsFalse(handle2Task.IsCompleted, "second acquire should be waiting");

            handle1.Dispose();
            await checkOrleans();

            using var handle2 = await handle2Task;
            await checkOrleans();

            Assert.AreSame(state, handle2.State);
            Assert.AreEqual("contested", handle2.State[0]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquire_WhenLocked_Fails()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var state = new List<string> { "locked" };
            using var sut = new PriorityAsyncLock<List<string>, int>(state);

            using var handle = await sut.AcquireAsync(0, CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquire(out var handle2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(handle2);
            await checkOrleans();
        });
    }
}
