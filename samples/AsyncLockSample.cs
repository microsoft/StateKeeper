using StateKeeper;
namespace StateKeeper.Samples;

/// <summary>
/// Demonstrates AsyncLock: exclusive access to a shared state object.
/// </summary>
internal static class AsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        // initialize with the mutable state to protect
        using AsyncLock<List<string>> asyncLock = new(
            state: ["foo", "bar"],
            acquisitionOrder: AcquisitionOrder.FIFO);

        // acquire exclusive access to the state
        // other callers have to wait until the handle is disposed
        using (var handle = await asyncLock.AcquireAsync(cancellationToken))
        {
            handle.State.Add("baz");
            string foo = handle.State[0];
        }

        // try to acquire without waiting — returns false if the lock is already held
        if (asyncLock.TryAcquire(out var tryHandle))
        {
            using (tryHandle)
            {
                tryHandle.State.Add("qux");
            }
        }
    }
}
