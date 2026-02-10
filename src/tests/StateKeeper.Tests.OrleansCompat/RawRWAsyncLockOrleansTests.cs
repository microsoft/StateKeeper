using StateKeeper.Raw;
using StateKeeper.Tests.OrleansCompat.Helper;

namespace StateKeeper.Tests.OrleansCompat;

[TestClass]
public class RawRWAsyncLockOrleansTests
{
    [TestMethod]
    public async Task UncontestedAcquireWrite()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var releaser = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task MultipleConcurrentReaders()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            // First read is uncontested
            var reader1 = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            // Second read also succeeds — readers don't block each other
            var reader2 = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            reader1.Dispose();
            await checkOrleans();

            reader2.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task WriterBlocksReaders()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            var readerTask = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(readerTask.IsCompleted, "reader should be waiting while writer is active");

            writer.Dispose();
            await checkOrleans();

            var reader = await readerTask;
            await checkOrleans();

            reader.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task ReadersBlockWriter()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var reader = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            var writerTask = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(writerTask.IsCompleted, "writer should be waiting while readers are active");

            reader.Dispose();
            await checkOrleans();

            var writer = await writerTask;
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task WaitingWriterBlocksNewReaders()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer1 = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            // A second writer enqueues
            var writer2Task = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(writer2Task.IsCompleted);

            // A reader enqueues — should NOT jump ahead of the waiting writer
            var readerTask = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(readerTask.IsCompleted);

            // Release writer1 → writer2 gets the lock, not the reader
            writer1.Dispose();
            await checkOrleans();

            var writer2 = await writer2Task;
            await checkOrleans();
            Assert.IsFalse(readerTask.IsCompleted, "reader should still be waiting behind writer2");

            // Release writer2 → reader finally gets the lock
            writer2.Dispose();
            await checkOrleans();

            var reader = await readerTask;
            await checkOrleans();

            reader.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task WriterReleaseUnblocksAllWaitingReaders()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            var reader1Task = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();
            var reader2Task = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();
            var reader3Task = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();

            Assert.IsFalse(reader1Task.IsCompleted);
            Assert.IsFalse(reader2Task.IsCompleted);
            Assert.IsFalse(reader3Task.IsCompleted);

            // Release writer → all readers unblock at once
            writer.Dispose();
            await checkOrleans();

            var reader1 = await reader1Task;
            var reader2 = await reader2Task;
            var reader3 = await reader3Task;
            await checkOrleans();

            reader1.Dispose();
            reader2.Dispose();
            reader3.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task CancelWaitingForWrite()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            // Cancel via CTS
            using var cts = new CancellationTokenSource();
            var waitTask = sut.AcquireWriteLockAsync(cts.Token);
            Assert.IsFalse(waitTask.IsCanceled);

            await cts.CancelAsync();
            Assert.IsTrue(waitTask.IsCanceled);
            await checkOrleans();

            // Already-canceled token
            var alreadyCanceled = sut.AcquireWriteLockAsync(new CancellationToken(canceled: true));
            Assert.IsTrue(alreadyCanceled.IsCanceled);
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task CancelWaitingForRead()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            // Hold a write lock so readers must wait
            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            // Cancel via CTS
            using var cts = new CancellationTokenSource();
            var waitTask = sut.AcquireReadLockAsync(cts.Token);
            Assert.IsFalse(waitTask.IsCanceled);

            await cts.CancelAsync();
            Assert.IsTrue(waitTask.IsCanceled);
            await checkOrleans();

            // Already-canceled token
            var alreadyCanceled = sut.AcquireReadLockAsync(new CancellationToken(canceled: true));
            Assert.IsTrue(alreadyCanceled.IsCanceled);
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task CancellingOnlyWaitingWriterUnblocksReaders()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            // Hold a read lock — writer will wait for it
            var reader1 = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            // Enqueue a write waiter (blocked by active reader)
            using var cts = new CancellationTokenSource();
            var writeWaitTask = sut.AcquireWriteLockAsync(cts.Token);
            Assert.IsFalse(writeWaitTask.IsCanceled);

            // Enqueue a read waiter — blocked because a writer is waiting
            var reader2Task = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(reader2Task.IsCompleted);

            // Cancel the only waiting writer — should unblock the read waiter
            await cts.CancelAsync();
            Assert.IsTrue(writeWaitTask.IsCanceled);
            await checkOrleans();

            var reader2 = await reader2Task;
            await checkOrleans();

            reader1.Dispose();
            await checkOrleans();

            reader2.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireWriteLockSucceeds()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var succeeded = sut.TryAcquireWriteLock(out var releaser);
            Assert.IsTrue(succeeded);
            Assert.IsNotNull(releaser);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireWriteLockFailsWhenWriterActive()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquireWriteLock(out var releaser2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(releaser2);
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireWriteLockFailsWhenReadersActive()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var reader = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquireWriteLock(out var releaser2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(releaser2);
            await checkOrleans();

            reader.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReadLockSucceeds()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var succeeded = sut.TryAcquireReadLock(out var releaser);
            Assert.IsTrue(succeeded);
            Assert.IsNotNull(releaser);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReadLockFailsWhenWriterActive()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquireReadLock(out var releaser2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(releaser2);
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReadLockFailsWhenWriterWaiting()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            // Hold a read lock, then enqueue a write waiter
            var reader = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            var writerTask = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(writerTask.IsCompleted);

            // TryAcquireReadLock should fail because a writer is waiting
            var succeeded = sut.TryAcquireReadLock(out var releaser2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(releaser2);
            await checkOrleans();

            // Clean up
            reader.Dispose();
            var writer = await writerTask;
            await checkOrleans();
            writer.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task DisposeUnblocksWaitersAndPreventsNewAcquires()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var sut = new RawRWAsyncLock();

            var writer = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            // Enqueue both a write waiter and a read waiter
            var writeWaiterTask = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            var readWaiterTask = sut.AcquireReadLockAsync(CancellationToken.None).AsTask();

            sut.Dispose();
            await checkOrleans();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writeWaiterTask);
            await checkOrleans();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => readWaiterTask);
            await checkOrleans();

            // New acquires after dispose should also throw
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await sut.AcquireWriteLockAsync(CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await sut.AcquireReadLockAsync(CancellationToken.None));
            await checkOrleans();

            writer.Dispose();
        });
    }

    [TestMethod]
    public async Task WriteReleaserDoubleDisposeIsIdempotent()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var releaser1 = await sut.AcquireWriteLockAsync(CancellationToken.None);
            await checkOrleans();

            var releaser2Task = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            var releaser3Task = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();

            releaser1.Dispose();
            await checkOrleans();

            var releaser2 = await releaser2Task;
            await checkOrleans();

            // Second dispose of releaser1 should be a no-op
            releaser1.Dispose();
            await checkOrleans();

            Assert.AreEqual(TaskStatus.WaitingForActivation, releaser3Task.Status,
                "third waiter should still be waiting");

            releaser2.Dispose();
            await checkOrleans();

            var releaser3 = await releaser3Task;
            await checkOrleans();

            releaser3.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task ReadReleaserDoubleDisposeIsIdempotent()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawRWAsyncLock();

            var reader1 = await sut.AcquireReadLockAsync(CancellationToken.None);
            var reader2 = await sut.AcquireReadLockAsync(CancellationToken.None);
            await checkOrleans();

            // Enqueue a writer that needs both readers to release
            var writerTask = sut.AcquireWriteLockAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(writerTask.IsCompleted);

            reader1.Dispose();
            await checkOrleans();
            Assert.IsFalse(writerTask.IsCompleted, "writer still waiting for reader2");

            // Double-dispose reader1 — should not count as releasing reader2
            reader1.Dispose();
            await checkOrleans();
            Assert.IsFalse(writerTask.IsCompleted, "writer should still be waiting");

            reader2.Dispose();
            await checkOrleans();

            var writer = await writerTask;
            await checkOrleans();

            writer.Dispose();
            await checkOrleans();
        });
    }
}
