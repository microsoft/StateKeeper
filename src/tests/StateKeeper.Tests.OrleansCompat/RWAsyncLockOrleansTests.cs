using StateKeeper.Tests.OrleansCompat.Helper;

namespace StateKeeper.Tests.OrleansCompat;

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
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
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
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
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
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
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
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
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
}
