// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper.Samples;

/// <summary>
/// Demonstrates RawPriorityAsyncLock: a mutual-exclusion lock without managed state
/// where waiting tasks acquire in ascending priority order.
/// You manage the protected resource yourself.
/// </summary>
internal static class RawPriorityAsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        List<string> list = ["foo", "bar"];

        // initialize — uses the default comparer for the priority type
        using RawPriorityAsyncLock<int> rawPriorityLock = new();

        // acquire the lock with a given priority
        // when multiple tasks are waiting, they acquire in ascending order
        using (var releaser = await rawPriorityLock.AcquireAsync(waiterPriority: 1, cancellationToken))
        {
            // access the shared resource while holding the lock
            // but nothing guarantees that other tasks won't also access that state
            list.Add("baz");
        }

        // try to acquire without waiting — returns false if the lock is held
        if (rawPriorityLock.TryAcquire(out var tryReleaser))
        {
            using (tryReleaser)
            {
                list.Add("qux");
            }
        }
    }
}
