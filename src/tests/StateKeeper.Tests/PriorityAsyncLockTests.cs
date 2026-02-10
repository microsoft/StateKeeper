using System.Collections.Concurrent;

namespace StateKeeper.Tests;

[TestClass]
public class PriorityAsyncLockTests
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
        using PriorityAsyncLock<TestState, int> sut = new PriorityAsyncLock<TestState, int>(new TestState { Value = 0 });

        tags.Enqueue("wait1");
        StateHandle<TestState> handle1 = await sut.AcquireAsync(1, this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var handle2 = await sut.AcquireAsync(2, this.TestContext.CancellationToken);
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
    public async Task AcquiresInPriorityOrder()
    {
        ConcurrentQueue<string> tags = new();
        // priority comparer: lower value = higher priority
        using PriorityAsyncLock<TestState, int> sut = new PriorityAsyncLock<TestState, int>(new TestState { Value = 0 });

        tags.Enqueue("wait1");
        StateHandle<TestState> handle1 = await sut.AcquireAsync(1, this.TestContext.CancellationToken);

        // Start two waiting tasks - the one with lower priority value should acquire first
        var user2 = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var handle2 = await sut.AcquireAsync(10, this.TestContext.CancellationToken); // lower priority (higher value)
            tags.Enqueue("use2");
            handle2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        var user3 = Task.Run(async () =>
        {
            tags.Enqueue("wait3");
            var handle3 = await sut.AcquireAsync(5, this.TestContext.CancellationToken); // higher priority (lower value)
            tags.Enqueue("use3");
            handle3.Dispose();
        })!;

        while (!tags.Contains("wait3"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);
        tags.Enqueue("use1");
        handle1.Dispose();

        await user2;
        await user3;

        // user3 should acquire before user2 because it has higher priority (lower value)
        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "wait3", "use1", "use3", "use2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task AcquiresWithCustomPriorityComparer()
    {
        ConcurrentQueue<string> tags = new();
        // Custom comparer: shorter strings have higher priority
        var stringLengthComparer = Comparer<string>.Create((a, b) => a.Length.CompareTo(b.Length));
        using PriorityAsyncLock<TestState, string> sut = new PriorityAsyncLock<TestState, string>(new TestState { Value = 0 }, stringLengthComparer);

        tags.Enqueue("wait1");
        StateHandle<TestState> handle1 = await sut.AcquireAsync("initial", this.TestContext.CancellationToken);

        var user2 = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var handle2 = await sut.AcquireAsync("longstring", this.TestContext.CancellationToken); // lower priority (longer)
            tags.Enqueue("use2");
            handle2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        var user3 = Task.Run(async () =>
        {
            tags.Enqueue("wait3");
            var handle3 = await sut.AcquireAsync("short", this.TestContext.CancellationToken); // higher priority (shorter)
            tags.Enqueue("use3");
            handle3.Dispose();
        })!;

        while (!tags.Contains("wait3"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);
        tags.Enqueue("use1");
        handle1.Dispose();

        await user2;
        await user3;

        // user3 should acquire before user2 because "short" is shorter than "longstring"
        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "wait3", "use1", "use3", "use2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public void TryAcquire_AcquiresWhenAvailable()
    {
        var state = new TestState { Value = 42 };
        using var sut = new PriorityAsyncLock<TestState, int>(state);

        bool succeeds1 = sut.TryAcquire(out StateHandle<TestState>? handle1);
        bool succeeds2 = sut.TryAcquire(out StateHandle<TestState>? handle2);

        Assert.IsTrue(succeeds1, "first TryAcquire should succeed");
        Assert.IsNotNull(handle1, "first TryAcquire should return a handle");
        Assert.AreEqual(42, handle1.State.Value, "handle should provide access to the state");
        Assert.IsFalse(succeeds2, "second TryAcquire should fail when lock is held");
        Assert.IsNull(handle2, "second TryAcquire should fail when lock is held");
        handle2?.Dispose();
        handle1?.Dispose();
    }

    [TestMethod]
    public async Task StopsWaitingTasksWhenDisposed()
    {
        using var sut = new PriorityAsyncLock<TestState, int>(new TestState { Value = 0 });

        StateHandle<TestState> handle1 = await sut.AcquireAsync(1, this.TestContext.CancellationToken);

        bool secondTaskStarted = false;

        Task user2 = Task.Run(async () =>
        {
            secondTaskStarted = true;
            var handle2 = await sut.AcquireAsync(2, this.TestContext.CancellationToken);
            handle2.Dispose();
        })!;

        while (!secondTaskStarted)
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => user2);

        // Dispose handle1 after the test assertion - the PriorityAsyncLock is already disposed,
        // so this just cleans up to avoid leak detection triggering
        handle1.Dispose();
    }

    [TestMethod]
    public void Constructor_ThrowsOnNullComparer()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new PriorityAsyncLock<TestState, int>(new TestState(), null!));
    }
}
