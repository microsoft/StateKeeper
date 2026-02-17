// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper.Tests.Raw;

[TestClass]
public class RawAsyncLockTests
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

    [TestMethod]
    public async Task PreventsSimultaneousAccess()
    {
        ConcurrentQueue<string> tags = new();

        using RawAsyncLock sut = new RawAsyncLock();

        tags.Enqueue("wait1");
        IDisposable releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var releaser2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            tags.Enqueue("use2");
            releaser2.Dispose();
        })!;

        // make sure that first user does not dispose until background task user has a chance to start
        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);
        tags.Enqueue("use1");
        releaser1.Dispose();

        await otherUser;

        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "use1", "use2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task FollowsFIFOOrder()
    {
        List<string> tags = new List<string>();

        using RawAsyncLock sut = new RawAsyncLock();

        tags.Add("wait1");
        IDisposable releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        var user2 = Task.Run(async () =>
        {
            tags.Add("wait2");
            var releaser2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            tags.Add("2");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        var user3 = Task.Run(async () =>
        {
            tags.Add("wait3");
            var releaser3 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            tags.Add("3");
            releaser3.Dispose();
        })!;

        while (!tags.Contains("wait3"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);
        tags.Add("1");
        releaser1.Dispose();

        await user2;
        await user3;

        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "wait3", "1", "2", "3" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags[i], $"actual order: {string.Join("-", tags)}");

        }
    }

    [TestMethod]
    public async Task FollowsLIFOOrder()
    {
        List<string> tags = new List<string>();

        using RawAsyncLock sut = new RawAsyncLock(acquisitionOrder: AcquisitionOrder.LIFO);

        tags.Add("wait1");
        IDisposable releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        var user2 = Task.Run(async () =>
        {
            tags.Add("wait2");
            var releaser2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            tags.Add("2");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        var user3 = Task.Run(async () =>
        {
            tags.Add("wait3");
            var releaser3 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            tags.Add("3");
            releaser3.Dispose();
        })!;

        while (!tags.Contains("wait3"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);
        tags.Add("1");
        releaser1.Dispose();

        await user2;
        await user3;

        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "wait3", "1", "3", "2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags[i], $"actual order: {string.Join("-", tags)}");

        }
    }

    [TestMethod]
    public async Task StopsWaitingTasksWhenDisposed()
    {
        using RawAsyncLock sut = new RawAsyncLock(acquisitionOrder: AcquisitionOrder.LIFO);

        IDisposable releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        bool secondTaskStarted = false;

        Task user2 = Task.Run(async () =>
        {
            secondTaskStarted = true;
            var releaser2 = await sut.AcquireAsync(this.TestContext.CancellationToken);
            releaser2.Dispose();
        })!;

        while (!secondTaskStarted)
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => user2);

        // Dispose releaser1 after the test assertion - the lock is already disposed,
        // so this just cleans up to avoid leak detection triggering
        releaser1.Dispose();
    }

    [TestMethod]
    public async Task CannotAcquireAfterDisposed()
    {
        using RawAsyncLock sut = new RawAsyncLock();
        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await sut.AcquireAsync(this.TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CancelsTasksForCanceledWaiters()
    {
        using RawAsyncLock sut = new RawAsyncLock(acquisitionOrder: AcquisitionOrder.LIFO);

        // if token is already canceled, attempts to acquire the lock are canceled immediately
        ValueTask<IDisposable> immediatelyCanceledWaiter = sut.AcquireAsync(new CancellationToken(canceled: true));
        Assert.IsTrue(immediatelyCanceledWaiter.IsCanceled);

        // create canceled waiter while lock is held
        using (await sut.AcquireAsync(this.TestContext.CancellationToken))
        {
            // if a waiter is canceled before the lock is released, an exception is immediately thrown to the waiting task,
            // and the next waiter is allowed to acquire the lock when the current holder releases it
            using var cts = new CancellationTokenSource();
            var waitTask = sut.AcquireAsync(cts.Token);

            // cancel the first waiter
            Assert.IsFalse(waitTask.IsCanceled, "waiting task should not be canceled before token is canceled");
            await cts.CancelAsync();
            Assert.IsTrue(waitTask.IsCanceled, "waiting task should be canceled when token is canceled");
        }
    }

    [TestMethod]
    public async Task SkipsCanceledWaiters()
    {
        using RawAsyncLock sut = new RawAsyncLock(acquisitionOrder: AcquisitionOrder.LIFO);

        // lock once
        IDisposable releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);

        // if token is already canceled, attempts to acquire the lock are canceled immediately
        Task<IDisposable> immediatelyCanceledWaiter = sut.AcquireAsync(new CancellationToken(canceled: true)).AsTask();
        Assert.IsTrue(immediatelyCanceledWaiter.IsCanceled);

        // if a waiter is canceled before the lock is released, an exception is immediately thrown to the waiting task,
        // and the next waiter is allowed to acquire the lock when the current holder releases it
        using var cts = new CancellationTokenSource();
#pragma warning disable CA2012 // Intentionally not awaiting this ValueTask since we know it will be canceled
        _ = sut.AcquireAsync(cts.Token); // this waiter is first in line, but will be canceled before the lock is released
#pragma warning restore CA2012 // Use ValueTasks correctly
        var waitTask2 = sut.AcquireAsync(new CancellationToken(canceled: false));

        // cancel the first waiter
        await cts.CancelAsync();

        // when current holder releases, the second waiter is allowed to acquire the lock because the first waiter was canceled
        releaser1.Dispose();
        var releaser2 = await waitTask2;
        releaser2.Dispose(); // Clean up: dispose the acquired releaser
    }

    [TestMethod]
    public async Task IgnoresDisposingReleaserTwice()
    {
        using var sut = new RawAsyncLock();

        var releaser1 = await sut.AcquireAsync(this.TestContext.CancellationToken);
        var releaser2Task = sut.AcquireAsync(this.TestContext.CancellationToken).AsTask();
        var releaser3Task = sut.AcquireAsync(this.TestContext.CancellationToken).AsTask();

        releaser1.Dispose();

        var releaser2 = await releaser2Task; // task 2 acquires when task 1 releases

        releaser1.Dispose(); // disposing twice should be ignored
        Assert.AreEqual(TaskStatus.WaitingForActivation, releaser3Task.Status, "task 3 should still be waiting");

        // Clean up: dispose releaser2 so releaser3 can acquire, then dispose releaser3
        releaser2.Dispose();
        var releaser3 = await releaser3Task;
        releaser3.Dispose();
    }

    [TestMethod]
    public void TryAcquire_AcquiresWhenAvailable()
    {
        using var sut = new RawAsyncLock();

        var succeeds1 = sut.TryAcquire(out var releaser1);
        var succeeds2 = sut.TryAcquire(out var releaser2);

        Assert.IsTrue(succeeds1);
        Assert.IsNotNull(releaser1);
        Assert.IsFalse(succeeds2);
        Assert.IsNull(releaser2);
        releaser2?.Dispose();
        releaser1!.Dispose();
    }
}
