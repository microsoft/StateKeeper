// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper;

namespace Microsoft.StateKeeper.Samples;

/// <summary>
/// Demonstrates PriorityAsyncLock: exclusive access with priority ordering.
/// Waiters acquire access in ascending priority order.
/// </summary>
internal static class PriorityAsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        // initialize with the mutable state to protect
        // optionally pass a custom comparer to control priority ordering.
        // waiters acquire access in ascending order.
        using PriorityAsyncLock<List<string>, int> priorityLock = new(
            state: ["foo", "bar"],
            priorityComparer: Comparer<int>.Create((a, b) => a.CompareTo(b)));

        // acquire exclusive access with a given priority
        using (var handle = await priorityLock.AcquireAsync(waiterPriority: 123, cancellationToken))
        {
            handle.State.Add("baz");
            string foo = handle.State[0];
        }

        // try to acquire without waiting — returns false if the lock is already held
        if (priorityLock.TryAcquire(out var tryHandle))
        {
            using (tryHandle)
            {
                tryHandle.State.Add("qux");
            }
        }
    }
}
