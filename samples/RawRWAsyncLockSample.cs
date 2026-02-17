// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper.Samples;

/// <summary>
/// Demonstrates RawRWAsyncLock: a reader-writer lock without managed state.
/// Allows multiple concurrent readers or one exclusive writer.
/// You manage the protected resource yourself.
/// </summary>
internal static class RawRWAsyncLockSample
{
    internal static async Task Sample(CancellationToken cancellationToken)
    {
        List<string> list = ["foo", "bar"];

        // initialize with FIFO writer ordering (default)
        using RawRWAsyncLock rawRwLock = new(AcquisitionOrder.FIFO);

        // acquire exclusive write lock — readers and other writers have to wait
        using (var releaser = await rawRwLock.AcquireWriteLockAsync(cancellationToken))
        {
            list.Add("baz");
        }

        // acquire read lock — other readers can proceed concurrently, but writers have to wait
        using (var releaser = await rawRwLock.AcquireReadLockAsync(cancellationToken))
        {
            string foo = list[0];
        }

        // try to acquire write lock without waiting
        if (rawRwLock.TryAcquireWriteLock(out var tryWriteReleaser))
        {
            using (tryWriteReleaser)
            {
                list.Add("qux");
            }
        }

        // try to acquire read lock without waiting
        if (rawRwLock.TryAcquireReadLock(out var tryReadReleaser))
        {
            using (tryReadReleaser)
            {
                string bar = list[1];
            }
        }
    }
}
