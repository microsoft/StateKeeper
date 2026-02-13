using Microsoft.StateKeeper;

namespace Microsoft.StateKeeper.Samples;

/// <summary>
/// Demonstrates RWAsyncLock: concurrent reads, exclusive writes.
/// </summary>
internal static class RWAsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        // initialize with the mutable state accessible by writers
        // and a function to map to the read-only state accessible by readers
        using RWAsyncLock<List<string>, IReadOnlyList<string>> rwLock = new (
            mutableState: ["foo", "bar"],
            toReadOnlyState: list => list.AsReadOnly());

        // get exclusive writer access to the mutable state
        // other writers and readers have to wait
        using (var handle = await rwLock.AcquireWriterAsync(cancellationToken))
        {
            // state is List<string>
            handle.State.Add("baz");
        }

        // get reader access to the read-only state
        // other readers can acquire access concurrently, but writers have to wait
        using (var handle = await rwLock.AcquireReaderAsync(cancellationToken))
        {
            string foo = handle.State[0];
            // `handle.State.Add("baz");` is not possible
            // since state is IReadOnlyList<string>
        }

        // try to acquire exclusive writer access without waiting
        // returns false if the lock is already held
        if (rwLock.TryAcquireWriter(out var tryWriterHandle))
        {
            using (tryWriterHandle)
            {
                tryWriterHandle.State.Add("qux");
            }
        }

        // try to acquire reader access without waiting
        // returns false if the lock is already held by a writer
        // or if a writer is waiting
        if (rwLock.TryAcquireReader(out var tryReaderHandle))
        {
            using (tryReaderHandle)
            {
                string bar = tryReaderHandle.State[1];
            }
        }
    }
}
