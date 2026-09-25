// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult

namespace Microsoft.StateKeeper.Raw;

/// <summary>
/// A mutual-exclusion lock that can be acquired asynchronously. Supports either FIFO or LIFO ordering for waiting tasks.
/// </summary>
public sealed class RawAsyncLock : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<IDisposable>> waiters = new();
    private bool isLocked = false;
    private bool isDisposed = false;

    private readonly AcquisitionOrder acquisitionOrder;

    /// <summary>
    /// Initializes a new RawAsyncLock
    /// </summary>
    /// <param name="acquisitionOrder">Selects whether waiting tasks obtain the lock in First-In-First-Out or Last-In-First-Out order</param>
    public RawAsyncLock(AcquisitionOrder acquisitionOrder = AcquisitionOrder.FIFO)
    {
        this.acquisitionOrder = acquisitionOrder;
    }

    /// <summary>
    /// Obtains a lock, asynchronously awaiting for the lock if it is not immediately available.
    /// </summary>
    /// <remarks>
    /// The lock can only be acquired once at a time, so attempting to acquire while already holding the lock will result in a deadlock.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
    /// <returns>A releaser which releases the lock when disposed</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RawAsyncLock has been disposed before the lock is obtained</exception>
    public ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (this.waiters)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<IDisposable>(cancellationToken);
            }

            if (this.isLocked)
            {
                // RunContinuationsAsynchronously: prevents awaiter continuations from running synchronously
                // inside TrySetResult while we hold the internal lock, which would risk reentrancy bugs and deadlocks.
                TaskCompletionSource<IDisposable> taskCompletionSource = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());
                if (this.acquisitionOrder == AcquisitionOrder.FIFO)
                {
                    this.waiters.AddLast(taskCompletionSource);
                }
                else
                {
                    this.waiters.AddFirst(taskCompletionSource);
                }
                return new ValueTask<IDisposable>(taskCompletionSource.Task);
            }
            else
            {
                this.isLocked = true;
                return ValueTask.FromResult<IDisposable>(new Releaser(this));
            }
        }
    }

    /// <summary>
    /// Attempt to immediately acquire the lock without waiting.
    /// </summary>
    /// <param name="releaser">releaser which releases the lock when disposed, or null if lock is not acquired</param>
    /// <returns>true if lock is acquired, false otherwise</returns>
    public bool TryAcquire([NotNullWhen(true)] out IDisposable? releaser)
    {
        lock (this.waiters)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            if (this.isLocked)
            {
                releaser = null;
                return false;
            }
            else
            {
                this.isLocked = true;
                releaser = new Releaser(this);
                return true;
            }
        }
    }

    /// <summary>
    /// prevents new tasks from acquiring locks and stops all waiting tasks
    /// </summary>
    public void Dispose()
    {
        lock (this.waiters)
        {
            this.isDisposed = true;
            foreach (var waiter in this.waiters)
            {
                waiter.TrySetException(new ObjectDisposedException(this.GetType().FullName));
            }
            this.waiters.Clear();
        }
    }

    /// <summary>
    /// prevents new tasks from acquiring locks and stops all waiting tasks
    /// </summary>
    private void Release()
    {
        lock (this.waiters)
        {
            // in LIFO mode, we may have canceled waiters at the end of the queue which might otherwise
            // never be cleaned up from the queue if there are always new waiters coming in
            // so we have to check for and remove entries for these cancelled waiters before we move on
            if (this.acquisitionOrder == AcquisitionOrder.LIFO)
            {
                while (this.waiters.LastOrDefault<TaskCompletionSource<IDisposable>?>()?.Task.IsCanceled == true)
                {
                    this.waiters.RemoveLast();
                }
            }

            // iterate through the waiters in order, completing the first waiter that is not canceled
            while (this.waiters.Count != 0)
            {
                var nextWaiter = this.waiters.First!.Value;
                this.waiters.RemoveFirst();

                var releaser = new Releaser(this);

                // try to allow the next waiter to acquire the lock. this returns false if the next waiter was already canceled
                if (nextWaiter.TrySetResult(releaser))
                {
                    return; // successfully transferred the lock to the next waiter
                }
            }

            // if no waiters were completed (either there were no waiters or all waiters were canceled),
            // then the lock is now unlocked
            this.isLocked = false;
        }
    }

    /// <summary>
    /// An object whose disposal releases a held lock
    /// </summary>
    private sealed class Releaser : IDisposable
    {
        private const int DISPOSED = 1;
        private const int NOT_DISPOSED = 0;

        private readonly RawAsyncLock RawAsyncLock;
        private int isDisposed = NOT_DISPOSED;

        /// <summary>
        /// Releases the lock
        /// </summary>
        internal Releaser(RawAsyncLock RawAsyncLock)
        {
            this.RawAsyncLock = RawAsyncLock;
        }

        public void Dispose()
        {
            // ensure the release can only make the lock release once
            // even if the releaser was disposed multiple times
            if (Interlocked.Exchange(ref this.isDisposed, DISPOSED) == NOT_DISPOSED)
            {
                this.RawAsyncLock.Release();
            }
        }
    }
}
