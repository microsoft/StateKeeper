using StateKeeper;
using StateKeeper.Raw;

namespace StateKeeper.Samples;

/// <summary>
/// Demonstrates RawAsyncLock: a mutual-exclusion lock without managed state.
/// You manage the protected resource yourself.
/// </summary>
internal static class RawAsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        List<string> list = ["foo", "bar"];

        // initialize with FIFO ordering (default) or LIFO
        using RawAsyncLock rawLock = new(AcquisitionOrder.FIFO);

        // acquire the lock — returns a releaser that unlocks when disposed
        using (var releaser = await rawLock.AcquireAsync(cancellationToken))
        {
            // safely access the shared resource while holding the lock
            list.Add("baz");
        }

        // try to acquire without waiting — returns false if the lock is held
        if (rawLock.TryAcquire(out var tryReleaser))
        {
            using (tryReleaser)
            {
                list.Add("qux");
            }
        }
    }
}
