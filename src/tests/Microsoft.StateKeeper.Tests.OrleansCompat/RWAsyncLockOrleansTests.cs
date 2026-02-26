// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

namespace Microsoft.StateKeeper.Tests.OrleansCompat;

/// <summary>
/// Tests for <see cref="RWAsyncLock{TMutableState, TReadOnlyState}"/> on the Orleans grain scheduler.
/// Locking semantics (contention, cancellation, dispose, reader-writer interactions, etc.)
/// are covered by <see cref="RawRWAsyncLockOrleansTests"/> — these tests cover
/// only the <see cref="StateHandle{TState}"/> wrapping and projection behavior unique to this class.
/// </summary>
[TestClass]
public class RWAsyncLockOrleansTests
{
    [TestMethod]
    public async Task AcquireWriter_ExposesMutableState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "initial" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            using var handle = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();

            Assert.AreSame(state, handle.State);
            handle.State.Add("mutated");
            Assert.AreEqual(2, state.Count);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task AcquireReader_ExposesReadOnlyState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "hello", "world" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            using var handle = await sut.AcquireReaderAsync(CancellationToken.None);
            await checkOrleans();

            Assert.AreEqual(2, handle.State.Count);
            Assert.AreEqual("hello", handle.State[0]);
            Assert.AreEqual("world", handle.State[1]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task AcquireReader_ProjectionThrows_ReleasesLock()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string>();
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(
                state,
                _ => throw new InvalidOperationException("projection failed"));

            // The projection throws, but the lock should still be released
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await sut.AcquireReaderAsync(CancellationToken.None));
            await checkOrleans();

            // Prove the lock was released — a writer can acquire immediately
            using var writerHandle = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReader_ProjectionThrows_ReleasesLock()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string>();
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(
                state,
                _ => throw new InvalidOperationException("projection failed"));

            Assert.ThrowsExactly<InvalidOperationException>(() => sut.TryAcquireReader(out _));
            await checkOrleans();

            // Prove the lock was released — a writer can acquire immediately
            using var writerHandle = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task AcquireWriterAsync_Contested_ExposesMutableState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "contested-write" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            // Hold a reader so the writer must wait (contested / slow path)
            using var reader = await sut.AcquireReaderAsync(CancellationToken.None);
            await checkOrleans();

            var writerTask = sut.AcquireWriterAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(writerTask.IsCompleted, "writer should be waiting while reader is active");

            reader.Dispose();
            await checkOrleans();

            using var writer = await writerTask;
            await checkOrleans();

            Assert.AreSame(state, writer.State);
            Assert.AreEqual("contested-write", writer.State[0]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task AcquireReaderAsync_Contested_ExposesReadOnlyState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "contested-read" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => {
                // the closure to get the read-only state must be executed in the correct task scheduler,
                // otherwise we would violate orleans' single-threaded guarantees
                checkTaskScheduler();
                return s.AsReadOnly();
            });

            // Hold a writer so the reader must wait (contested / slow path)
            using var writer = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();

            var readerTask = sut.AcquireReaderAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(readerTask.IsCompleted, "reader should be waiting while writer is active");

            writer.Dispose();
            await checkOrleans();

            using var reader = await readerTask;
            await checkOrleans();

            Assert.AreEqual(1, reader.State.Count);
            Assert.AreEqual("contested-read", reader.State[0]);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireWriter_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "try-write" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            var succeeded = sut.TryAcquireWriter(out var handle);
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
    public async Task TryAcquireWriter_WhenLocked_Fails()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string>();
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            using var writer = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquireWriter(out var handle2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(handle2);
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReader_ExposesState()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string> { "try-read" };
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            var succeeded = sut.TryAcquireReader(out var handle);
            await checkOrleans();

            Assert.IsTrue(succeeded);
            Assert.IsNotNull(handle);
            Assert.AreEqual(1, handle.State.Count);
            Assert.AreEqual("try-read", handle.State[0]);
            await checkOrleans();

            handle.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireReader_WhenWriterActive_Fails()
    {
        await ClusterFixture.ExecuteOnGrain(async (checkOrleans, checkTaskScheduler) =>
        {
            var state = new List<string>();
            using var sut = new RWAsyncLock<List<string>, IReadOnlyList<string>>(state, s => s.AsReadOnly());

            using var writer = await sut.AcquireWriterAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquireReader(out var handle2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(handle2);
            await checkOrleans();
        });
    }
}
