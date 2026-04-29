// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper.Tests.Raw;

[TestClass]
public class RawRWAsyncLockTests
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
    public async Task PreventsSimultaneousWrites()
    {
        ConcurrentQueue<string> tags = new();

        using RawRWAsyncLock sut = new RawRWAsyncLock();

        tags.Enqueue("wait1");
        var releaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var releaser2 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("use2");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(1);
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
    public async Task AllowsSimultaneousReads()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var releaser1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
        var releaser2 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
        releaser2.Dispose();
        releaser1.Dispose();
    }

    [TestMethod]
    public async Task PreventsReadDuringWrite()
    {
        ConcurrentQueue<string> tags = new();

        using RawRWAsyncLock sut = new RawRWAsyncLock();

        tags.Enqueue("wait1");
        var releaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var releaser2 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("use2");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(1);
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
    public async Task PreventsWriteDuringRead()
    {
        ConcurrentQueue<string> tags = new();

        using RawRWAsyncLock sut = new RawRWAsyncLock();

        tags.Enqueue("wait1");
        var releaser1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var releaser2 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("use2");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait2"))
        {
            await Task.Delay(1);
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
    public async Task BlocksNewReaderIfWaitingWriter()
    {
        ConcurrentQueue<string> tags = new();

        using RawRWAsyncLock sut = new RawRWAsyncLock();

        tags.Enqueue("wait-reader1");
        var releaser1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);

        var writerTask = Task.Run(async () =>
        {
            tags.Enqueue("wait-writer");
            var releaser2 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("writer");
            releaser2.Dispose();
        })!;

        while (!tags.Contains("wait-writer"))
        {
            await Task.Delay(1);
        }

        await Task.Delay(5);

        var otherReaderTask = Task.Run(async () =>
        {
            tags.Enqueue("wait-reader2");
            var releaser2 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("reader2");
            releaser2.Dispose();
        });

        while (!tags.Contains("wait-reader2"))
        {
            await Task.Delay(1);
        }

        await Task.Delay(5);
        tags.Enqueue("reader1");
        releaser1.Dispose();

        await writerTask;
        await otherReaderTask;

        List<string> expectedOrder = new List<string>() { "wait-reader1", "wait-writer", "wait-reader2", "reader1", "writer", "reader2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task CannotAcquireAfterDisposed()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await sut.AcquireReadLockAsync(this.TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task StopsWaitingTasksWhenDisposed()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var releaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        bool task2Started = false;
        bool task3Started = false;

        Task user2 = Task.Run(async () =>
        {
            task2Started = true;
            var releaser2 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            releaser2.Dispose();
        })!;

        Task user3 = Task.Run(async () =>
        {
            task3Started = true;
            var releaser3 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
            releaser3.Dispose();
        })!;

        while (!task2Started || !task3Started)
        {
            await Task.Delay(1);
        }

        await Task.Delay(5);

        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => user2);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => user3);

        // Dispose releaser1 after the test assertion - the lock is already disposed,
        // so this just cleans up to avoid leak detection triggering
        releaser1.Dispose();
    }

    [TestMethod]
    public async Task CancelsAcquireIfTokenAlreadyCanceled()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        var releaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        // if token is already canceled, attempts to acquire the lock are canceled immediately
        var immediatelyCanceledWriteWaiter = sut.AcquireWriteLockAsync(new CancellationToken(canceled: true));
        Assert.IsTrue(immediatelyCanceledWriteWaiter.IsCanceled);

        var immediatelyCanceledReadWaiter = sut.AcquireReadLockAsync(new CancellationToken(canceled: true));
        Assert.IsTrue(immediatelyCanceledReadWaiter.IsCanceled);

        // Clean up: dispose the acquired releaser
        releaser1.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelsTasksForCanceledWaiters(bool canceledWaiterIsReader)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        // if token is already canceled, attempts to acquire the lock are canceled immediately
        var immediatelyCanceledWaiter = sut.AcquireWriteLockAsync(new CancellationToken(canceled: true));
        Assert.IsTrue(immediatelyCanceledWaiter.IsCanceled);

        // create canceled waiter while lock is held
        using (await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken))
        {
            // if a waiter is canceled before the lock is released, an exception is immediately thrown to the waiting task,
            // and the next waiter is allowed to acquire the lock when the current holder releases it
            using var cts = new CancellationTokenSource();
            var waitTask = canceledWaiterIsReader
                ? sut.AcquireReadLockAsync(cts.Token)
                : sut.AcquireWriteLockAsync(cts.Token);

            // cancel the first waiter
            Assert.IsFalse(waitTask.IsCanceled, "waiting task should not be canceled before token is canceled");
            await cts.CancelAsync();
            Assert.IsTrue(waitTask.IsCanceled, "waiting task should be canceled when token is canceled");
        }
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SkipsCanceledWaiters(bool canceledWaiterIsReader, bool nextWaiterIsReader)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        // lock once
        var releaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        // if a waiter is canceled before the lock is released, an exception is immediately thrown to the waiting task,
        // and the next waiter is allowed to acquire the lock when the current holder releases it
        using var cts = new CancellationTokenSource();
#pragma warning disable CA2012 // Intentionally not awaiting this ValueTask since we know it will be canceled
        _ = canceledWaiterIsReader
            ? sut.AcquireReadLockAsync(cts.Token)
            : sut.AcquireWriteLockAsync(cts.Token);
#pragma warning restore CA2012 // Use ValueTasks correctly
        var waitTask2 = nextWaiterIsReader
            ? sut.AcquireReadLockAsync(new CancellationToken(canceled: false))
            : sut.AcquireWriteLockAsync(new CancellationToken(canceled: false));

        // cancel the first waiter
        await cts.CancelAsync();

        // when current holder releases, the second waiter is allowed to acquire the lock
        releaser1.Dispose();
        var releaser2 = await waitTask2;
        releaser2.Dispose(); // Clean up: dispose the acquired releaser
    }

    [TestMethod]
    public async Task ReadersBlockedByWaitingWriterAcquireLockWhenWriterCancelled()
    {
        using CancellationTokenSource writeWaiterTokenSource = new CancellationTokenSource();

        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var readReleaser1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
#pragma warning disable CA2012 // Intentionally not awaiting this ValueTask since we know it will be canceled
        _ = sut.AcquireWriteLockAsync(writeWaiterTokenSource.Token);
#pragma warning restore CA2012 // Use ValueTasks correctly
        var readWaiter = sut.AcquireReadLockAsync(this.TestContext.CancellationToken);

        // even though lock is currently in read-state, reader waiter cannot immediately acquire because a writer is waiting
        Assert.IsFalse(readWaiter.IsCompleted);

        await writeWaiterTokenSource.CancelAsync();
        var readReleaser2 = await readWaiter; // now the blocked reader acquires the lock

        // Clean up: dispose both read releasers
        readReleaser1.Dispose();
        readReleaser2.Dispose();
    }

    [TestMethod]
    [DataRow(AcquisitionOrder.FIFO)]
    [DataRow(AcquisitionOrder.LIFO)]
    public async Task WritersAcquireInSpecifiedOrder(AcquisitionOrder acquisitionOrder)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock(writerAcquisitionOrder: acquisitionOrder);

        ConcurrentQueue<int> tags = new();

        Func<int, Task> useWriter = async (id) =>
        {
            var releaser = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue(id);
            releaser.Dispose();
        };

        List<Task> writerTasks = new();
        using (await sut.AcquireReadLockAsync(this.TestContext.CancellationToken))
        {
            // these cant start immediately since read lock is held
            for (int i = 1; i <= 5; i++)
            {
                writerTasks.Add(useWriter(i));
            }
        }

        // once read lock is released, wait for all writers to complete
        await Task.WhenAll(writerTasks);

        var expectedOrder = acquisitionOrder == AcquisitionOrder.FIFO
            ? Enumerable.Range(1, 5)
            : Enumerable.Range(1, 5).Reverse();

        CollectionAssert.AreEqual(expectedOrder.ToList(), tags.ToList());
    }

    [TestMethod]
    public async Task IgnoresDisposingWriterReleaserTwice()
    {
        using var sut = new RawRWAsyncLock();

        var writerReleaser1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
        var writerReleaser2Task = sut.AcquireWriteLockAsync(this.TestContext.CancellationToken).AsTask();
        var writerReleaser3Task = sut.AcquireWriteLockAsync(this.TestContext.CancellationToken).AsTask();
        var readerReleaserTask = sut.AcquireReadLockAsync(this.TestContext.CancellationToken).AsTask();

        writerReleaser1.Dispose();

        var writerReleaser2 = await writerReleaser2Task; // writer 2 acquires when writer 1 releases

        writerReleaser1.Dispose(); // disposing twice should be ignored
        Assert.AreEqual(TaskStatus.WaitingForActivation, writerReleaser3Task.Status, "writer 3 should still be waiting");
        Assert.AreEqual(TaskStatus.WaitingForActivation, readerReleaserTask.Status, "reader should still be waiting");

        // Clean up: dispose remaining releasers
        writerReleaser2.Dispose();
        var writerReleaser3 = await writerReleaser3Task;
        writerReleaser3.Dispose();
        var readerReleaser = await readerReleaserTask;
        readerReleaser.Dispose();
    }

    [TestMethod]
    public async Task IgnoresDisposingReaderReleaserTwice()
    {
        using var sut = new RawRWAsyncLock();

        var readerReleaser1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
        var readerReleaser2 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);
        var writerReleaserTask = sut.AcquireWriteLockAsync(this.TestContext.CancellationToken).AsTask();

        Assert.AreEqual(TaskStatus.WaitingForActivation, writerReleaserTask.Status, "writer should be waiting while readers hold lock");

        readerReleaser1.Dispose();

        // writer should still be waiting because reader 2 still holds the lock
        Assert.AreEqual(TaskStatus.WaitingForActivation, writerReleaserTask.Status, "writer should still be waiting after reader 1 releases");

        readerReleaser1.Dispose(); // disposing twice should be ignored
        Assert.AreEqual(TaskStatus.WaitingForActivation, writerReleaserTask.Status, "writer should still be waiting after reader 1 disposes twice");

        readerReleaser2.Dispose();

        // now the writer should be able to acquire
        var writerReleaser = await writerReleaserTask;
        writerReleaser.Dispose(); // Clean up: dispose the writer releaser
    }

    [TestMethod]
    public void TryAcquireWriteLock_AcquiresIfAvailable()
    {
        using var sut = new RawRWAsyncLock();

        var acqired1 = sut.TryAcquireWriteLock(out IDisposable? releaser1);
        var acqired2 = sut.TryAcquireWriteLock(out IDisposable? releaser2);

        Assert.IsTrue(acqired1, "should acquire write lock when available");
        Assert.IsNotNull(releaser1, "should return non-null releaser when write lock acquired");
        Assert.IsFalse(acqired2, "should not acquire write lock when already held");
        Assert.IsNull(releaser2, "should return null releaser when write lock not acquired");
        releaser2?.Dispose();
        releaser1.Dispose();
    }

    [TestMethod]
    public void TryAcquireReadLock_AcquiresIfAvailable()
    {
        using var sut = new RawRWAsyncLock();

        var acquired1 = sut.TryAcquireReadLock(out IDisposable? releaser1);
        var acquired2 = sut.TryAcquireReadLock(out IDisposable? releaser2);
        Assert.IsTrue(acquired1, "should acquire read lock when available");
        Assert.IsNotNull(releaser1, "should return non-null releaser when read lock acquired");
        Assert.IsTrue(acquired2, "should acquire read lock when already held by another reader");
        Assert.IsNotNull(releaser2, "should return non-null releaser when read lock acquired by another reader");

        var writeAcquired = sut.TryAcquireWriteLock(out IDisposable? writeReleaser);
        Assert.IsFalse(writeAcquired, "should not acquire write lock when read lock held");
        Assert.IsNull(writeReleaser, "should return null releaser when write lock not acquired");
        writeReleaser?.Dispose();

        releaser1.Dispose();
        releaser2.Dispose();

        var writeAcquired2 = sut.TryAcquireWriteLock(out IDisposable? writeReleaser2);
        Assert.IsTrue(writeAcquired2, "should acquire write lock when read locks released");
        Assert.IsNotNull(writeReleaser2, "should return non-null releaser when write lock acquired");

        var acquired3 = sut.TryAcquireReadLock(out IDisposable? releaser3);
        Assert.IsFalse(acquired3, "should not acquire read lock when write lock held");
        Assert.IsNull(releaser3, "should return null releaser when read lock not acquired");
        releaser3?.Dispose();

        // Clean up: dispose the write releaser
        writeReleaser2!.Dispose();
    }

    /// <remarks>
    /// this test exists because an earlier prototype had a bug
    /// where we incremented the active reader count even for canceled readers.
    /// </remarks>
    [TestMethod]
    public async Task CancelledMiddleReaderDoesNotSubsequentWriter()
    {
        ConcurrentQueue<string> tags = new();
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        // Acquire write lock first
        var writerReleaser = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        // Queue three readers, with the middle one having a cancellable token
        var reader1Task = sut.AcquireReadLockAsync(this.TestContext.CancellationToken).AsTask();
        using var reader2Cts = new CancellationTokenSource();
        var reader2Task = sut.AcquireReadLockAsync(reader2Cts.Token).AsTask();
        var reader3Task = sut.AcquireReadLockAsync(this.TestContext.CancellationToken).AsTask();

        // Verify all readers are waiting
        Assert.AreEqual(TaskStatus.WaitingForActivation, reader1Task.Status);
        Assert.AreEqual(TaskStatus.WaitingForActivation, reader2Task.Status);
        Assert.AreEqual(TaskStatus.WaitingForActivation, reader3Task.Status);

        // Cancel the middle reader's token
        await reader2Cts.CancelAsync();
        Assert.IsTrue(reader2Task.IsCanceled);

        // Release the write lock
        writerReleaser.Dispose();

        // The two uncancelled readers should acquire the lock
        var reader1Releaser = await reader1Task;
        tags.Enqueue("reader1");
        var reader3Releaser = await reader3Task;
        tags.Enqueue("reader3");

        // Queue a writer while readers hold the lock
        var newWriterTask = Task.Run(async () =>
        {
            tags.Enqueue("wait-writer");
            var releaser = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);
            tags.Enqueue("writer");
            releaser.Dispose();
        });

        while (!tags.Contains("wait-writer"))
        {
            await Task.Delay(1);
        }
        await Task.Delay(5);

        Assert.IsFalse(newWriterTask.IsCompleted);

        reader1Releaser.Dispose();
        reader3Releaser.Dispose();

        await newWriterTask;

        Assert.IsTrue(tags.Contains("reader1"));
        Assert.IsTrue(tags.Contains("reader3"));
        Assert.AreEqual("writer", tags.ToList().Last());
    }

    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [TestMethod]
    public async Task ReadersCanAcquireIfAllWaitingWritersAreCanceled(bool firstHolderIsWriter, bool firstHolderDisposedBeforeReaderEnters)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var firstReleaser = firstHolderIsWriter
            ? await sut.AcquireWriteLockAsync(new CancellationToken())
            : await sut.AcquireReadLockAsync(new CancellationToken());

        // Enqueue a waiting writer directly (no Task.Run) so it is guaranteed to be
        // in WriteWaiters before we proceed. The call won't complete synchronously
        // because the lock is already held.
        using var waitingWriterCts = new CancellationTokenSource();
        var waitingWriterTask = sut.AcquireWriteLockAsync(waitingWriterCts.Token);

        // Cancel the waiting writer while the lock is still held.
        // The canceled TCS remains in WriteWaiters because the cancellation callback
        // only unblocks existing ReadWaiters (there are none yet).
        await waitingWriterCts.CancelAsync();

        // Try to acquire a read lock.
        // If the first holder is a reader, the new reader should acquire immediately
        // since the only writer is canceled and there is no active writer.
        // If the first holder is a writer, the reader can't acquire yet (activeWriter is true)
        // and will be unblocked when the first holder releases.
        var readerWaitTask = sut.AcquireReadLockAsync(new CancellationToken()).AsTask();

        if (firstHolderIsWriter)
        {
            Assert.IsFalse(readerWaitTask.IsCompleted,
                "reader should not acquire while a writer is still active");
        }
        else
        {
            Assert.IsTrue(readerWaitTask.IsCompleted,
                "reader should acquire immediately alongside existing reader when all waiting writers are canceled");
        }

        if (firstHolderDisposedBeforeReaderEnters)
        {
            firstReleaser.Dispose();
        }

        // If the writer acquired the lock during release (before cancellation took effect),
        // dispose it to avoid leak detection.
        if (waitingWriterTask.IsCompleted && !waitingWriterTask.IsCanceled && !waitingWriterTask.IsFaulted)
        {
            waitingWriterTask.Result.Dispose();
        }

        if (!firstHolderDisposedBeforeReaderEnters)
        {
            firstReleaser.Dispose();
        }

        var readerHandle = await readerWaitTask;
        readerHandle.Dispose();
    }

    [TestMethod]
    public async Task TryAcquireReadLockSucceedsIfAllWaitingWritersAreCanceled()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        // Acquire a read lock (so writers will queue, but other readers could normally join)
        var firstReleaser = await sut.AcquireReadLockAsync(new CancellationToken());

        // Enqueue a waiting writer
        using var waitingWriterCts = new CancellationTokenSource();
#pragma warning disable CA2012 // Intentionally not awaiting this ValueTask since we know it will be canceled
        _ = sut.AcquireWriteLockAsync(waitingWriterCts.Token);
#pragma warning restore CA2012 // Use ValueTasks correctly

        // Cancel the waiting writer. The canceled TCS stays in WriteWaiters.
        await waitingWriterCts.CancelAsync();

        // TryAcquireReadLock should succeed: the only writer in the queue is canceled,
        // no active writer, and the lock is in read-mode.
        try
        {
            var acquired = sut.TryAcquireReadLock(out IDisposable? releaser);
            Assert.IsTrue(acquired, "TryAcquireReadLock should succeed when all waiting writers are canceled");
            Assert.IsNotNull(releaser);
            releaser!.Dispose();
        }
        finally
        {
            // Always clean up to prevent leak detection finalizer from crashing the process
            firstReleaser.Dispose();
        }
    }

    [TestMethod]
    public async Task AcquireReadLockAsyncSucceedsImmediatelyIfAllWaitingWritersAreCanceled()
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();

        // r1 acquires a read lock
        var r1Releaser = await sut.AcquireReadLockAsync(new CancellationToken());

        // w1 queues as a waiting writer
        using var w1Cts = new CancellationTokenSource();
#pragma warning disable CA2012 // Intentionally not awaiting this ValueTask since we know it will be canceled
        _ = sut.AcquireWriteLockAsync(w1Cts.Token);
#pragma warning restore CA2012 // Use ValueTasks correctly

        // w1 is canceled. The canceled TCS stays in WriteWaiters.
        await w1Cts.CancelAsync();

        // r2 tries to acquire. Since r1 still holds and the only writer is canceled,
        // r2 should acquire immediately (synchronously) without waiting for r1 to release.
        try
        {
            var r2Task = sut.AcquireReadLockAsync(new CancellationToken());
            Assert.IsTrue(r2Task.IsCompleted,
                "reader should acquire immediately when the only waiting writer is canceled");

            var r2Releaser = await r2Task;
            r2Releaser.Dispose();
        }
        finally
        {
            r1Releaser.Dispose();
        }
    }

    /// <summary>
    /// Helper that awaits an acquisition and immediately disposes the resulting releaser
    /// with no awaits in between. When the awaited task completes via TrySetResult on the
    /// holder's release path, this helper's continuation runs synchronously inside that
    /// TrySetResult call. This is the scenario the following tests are designed to exercise.
    /// </summary>
    private static async Task AcquireAndDisposeImmediatelyAsync(ValueTask<IDisposable> acquireTask, bool continueOnCapturedContext)
    {
        var releaser = await acquireTask.ConfigureAwait(continueOnCapturedContext);
        releaser.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(5000)]
    public async Task WriterReleaseToImmediateDisposeWriterLeavesLockUsable(bool continueOnCapturedContext)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var w1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        // Queue a waiter whose continuation disposes the releaser synchronously inside TrySetResult.
        var consumer = AcquireAndDisposeImmediatelyAsync(sut.AcquireWriteLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);

        w1.Dispose();
        await consumer;

        Assert.IsTrue(sut.TryAcquireWriteLock(out IDisposable? w3),
            "lock should be free after writer-release transferred to an immediately-disposed writer");
        w3!.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(5000)]
    public async Task ReaderReleaseToImmediateDisposeWriterLeavesLockUsable(bool continueOnCapturedContext)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var r1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);

        // Queue a writer whose continuation disposes the releaser synchronously inside TrySetResult.
        var consumer = AcquireAndDisposeImmediatelyAsync(sut.AcquireWriteLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);

        r1.Dispose();
        await consumer;

        Assert.IsTrue(sut.TryAcquireWriteLock(out IDisposable? w2),
            "lock should be free after reader-release transferred to an immediately-disposed writer");
        w2!.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(5000)]
    public async Task WriterReleaseToImmediateDisposeReadersLeavesLockUsable(bool continueOnCapturedContext)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var w1 = await sut.AcquireWriteLockAsync(this.TestContext.CancellationToken);

        // Queue multiple readers whose continuations all dispose synchronously inside TrySetResult.
        var c1 = AcquireAndDisposeImmediatelyAsync(sut.AcquireReadLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);
        var c2 = AcquireAndDisposeImmediatelyAsync(sut.AcquireReadLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);

        w1.Dispose();
        await c1;
        await c2;

        Assert.IsTrue(sut.TryAcquireWriteLock(out IDisposable? w2),
            "lock should be free after writer-release transferred to immediately-disposed readers");
        w2!.Dispose();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(5000)]
    public async Task CancelingWaitingWriterTransfersToImmediateDisposeReadersLeavesLockUsable(bool continueOnCapturedContext)
    {
        using RawRWAsyncLock sut = new RawRWAsyncLock();
        var r1 = await sut.AcquireReadLockAsync(this.TestContext.CancellationToken);

        // Enqueue a writer whose cancellation will trigger the readers-unblock callback path.
        using var writerCts = new CancellationTokenSource();
        var canceledWriterTask = sut.AcquireWriteLockAsync(writerCts.Token);
        Assert.IsFalse(canceledWriterTask.IsCanceled);

        // Queue readers behind the waiting writer; their continuations dispose synchronously
        // inside TrySetResult when the writer's cancellation callback unblocks them.
        var c1 = AcquireAndDisposeImmediatelyAsync(sut.AcquireReadLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);
        var c2 = AcquireAndDisposeImmediatelyAsync(sut.AcquireReadLockAsync(this.TestContext.CancellationToken), continueOnCapturedContext);

        await writerCts.CancelAsync();
        Assert.IsTrue(canceledWriterTask.IsCanceled);

        await c1;
        await c2;

        r1.Dispose();

        Assert.IsTrue(sut.TryAcquireWriteLock(out IDisposable? w2),
            "lock should be free after canceling a waiting writer that unblocked immediately-disposed readers");
        w2!.Dispose();
    }
}
