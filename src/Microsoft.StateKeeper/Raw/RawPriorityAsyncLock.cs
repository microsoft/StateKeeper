// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult

namespace Microsoft.StateKeeper.Raw;

/// <summary>
/// A mutual-exclusion lock that can be acquired asynchronously. Waiting tasks acquire the lock in priority order.
/// </summary>
/// <typeparam name="TWaiterPriority">Type of argument to determine priority of waiters</typeparam>
public sealed class RawPriorityAsyncLock<TWaiterPriority> : IDisposable
{
    private readonly PriorityQueue<TaskCompletionSource<IDisposable>, TWaiterPriority> waiters;
    private bool isLocked = false;
    private bool isDisposed = false;

    /// <summary>
    /// Initializes a new RawPriorityAsyncLock.
    /// </summary>
    public RawPriorityAsyncLock(IComparer<TWaiterPriority> priorityComparer)
    {
        ArgumentNullException.ThrowIfNull(priorityComparer);
        this.waiters = new PriorityQueue<TaskCompletionSource<IDisposable>, TWaiterPriority>(priorityComparer);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RawPriorityAsyncLock{TWaiterPriority}"/> class using the default priority comparer.
    /// </summary>
    public RawPriorityAsyncLock() : this(Comparer<TWaiterPriority>.Default)
    {
    }

    /// <summary>
    /// Obtains a lock, asynchronously awaiting for the lock if it is not immediately available.
    /// </summary>
    /// <remarks>
    /// The lock can only be acquired once at a time, so attempting to acquire while already holding the lock will result in a deadlock.
    /// </remarks>
    /// <param name="waiterPriority">Information about the task that is acquiring the lock, used to calculate priority when multiple tasks are waiting.</param>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
    /// <returns>A releaser which releases the lock when disposed</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the AsyncLock has been disposed before the lock is obtained</exception>
    public ValueTask<IDisposable> AcquireAsync(TWaiterPriority waiterPriority, CancellationToken cancellationToken)
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
                TaskCompletionSource<IDisposable> taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());
                this.waiters.Enqueue(taskCompletionSource, waiterPriority);
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
            while (this.waiters.Count > 0)
            {
                this.waiters.Dequeue().TrySetException(new ObjectDisposedException(this.GetType().FullName));
            }
        }
    }

    /// <summary>
    /// prevents new tasks from acquiring locks and stops all waiting tasks
    /// </summary>
    private void Release()
    {
        lock (this.waiters)
        {
            while (this.waiters.Count > 0)
            {
                var nextWaiter = this.waiters.Dequeue();

                var releaser = new Releaser(this);

                // try to allow the next waiter to acquire the lock. this returns false if the next waiter was already canceled
                if (nextWaiter.TrySetResult(releaser))
                {
                    return; // successfully transferred the lock to the next waiter
                }
            }

            // if no waiters were completed (either there were no waiters or all waiters were canceled), the lock is now unlocked
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

        private readonly RawPriorityAsyncLock<TWaiterPriority> asyncLock;
        private int isDisposed = NOT_DISPOSED;

        /// <summary>
        /// Releases the lock
        /// </summary>
        internal Releaser(RawPriorityAsyncLock<TWaiterPriority> asyncLock)
        {
            this.asyncLock = asyncLock;
        }

        public void Dispose()
        {
            // ensure the release can only make the lock release once
            // even if the releaser was disposed multiple times
            if (Interlocked.Exchange(ref this.isDisposed, DISPOSED) == NOT_DISPOSED)
            {
                this.asyncLock.Release();
            }
        }
    }
}
