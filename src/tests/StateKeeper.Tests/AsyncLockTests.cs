using System.Collections.Concurrent;

namespace StateKeeper.Tests;

[TestClass]
public class AsyncLockTests
{
    public required TestContext TestContext { get; set; }

#if DEBUG
    [TestCleanup]
    public void Cleanup()
    {
        // Force finalizers to run, which will trigger leak detection immediately
        // rather than relying on GC timing
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
#endif

    private sealed class TestState
    {
        public int Value { get; set; }
    }

    [TestMethod]
    public async Task PreventsSimultaneousAccess()
    {
        ConcurrentQueue<string> tags = new();
        using AsyncLock<TestState> sut = new AsyncLock<TestState>(new TestState { Value = 0 });

        tags.Enqueue("wait1");
        StateHandle<TestState> handle1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var handle2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            Assert.AreEqual(42, handle2.State.Value, "state change from first holder should be visible");
            tags.Enqueue("use2");
            handle2.Dispose();
        })!;

        // make sure that first user does not dispose until background task user has a chance to start
        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        tags.Enqueue("use1");
        Assert.AreEqual(0, handle1.State.Value, "initial state should be 0");
        handle1.State.Value = 42;
        handle1.Dispose();

        await otherUser;

        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "use1", "use2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public void TryAcquire_AcquiresWhenAvailable()
    {
        var state = new TestState { Value = 42 };
        using var sut = new AsyncLock<TestState>(state);

        bool succeeds1 = sut.TryAcquire(out StateHandle<TestState>? handle1);
        bool succeeds2 = sut.TryAcquire(out StateHandle<TestState>? handle2);

        Assert.IsTrue(succeeds1, "first TryAcquire should succeed");
        Assert.IsNotNull(handle1, "first TryAcquire should succeed");
        Assert.AreEqual(42, handle1.State.Value, "handle should provide access to the state");
        Assert.IsFalse(succeeds2, "second TryAcquire should fail when lock is held");
        Assert.IsNull(handle2, "second TryAcquire should fail when lock is held");
        handle2?.Dispose();
        handle1?.Dispose();
    }

    [TestMethod]
    public async Task StopsWaitingTasksWhenDisposed()
    {
        using var sut = new AsyncLock<TestState>(new TestState { Value = 0 });

        StateHandle<TestState> handle1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        bool secondTaskStarted = false;

        Task user2 = Task.Run(async () =>
        {
            secondTaskStarted = true;
            var handle2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            handle2.Dispose();
        })!;

        while (!secondTaskStarted)
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => user2);

        // Dispose handle1 after the test assertion - the AsyncLock is already disposed,
        // so this just cleans up to avoid leak detection triggering
        handle1.Dispose();
    }
}
