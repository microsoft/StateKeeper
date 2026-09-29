// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult

namespace Microsoft.StateKeeper.Raw;

/// <summary>
/// A mutual-exclusion lock that can be acquired asynchronously.
/// </summary>
/// <remarks>Waiters acquire the lock in ascending priority order, as determined by the priority comparer.</remarks>
/// <typeparam name="TWaiterPriority">Type of argument to determine priority of waiters</typeparam>
public sealed class RawPriorityAsyncLock<TWaiterPriority> : IDisposable
{
    private readonly PriorityQueue<TaskCompletionSource<IDisposable>, TWaiterPriority> waiters;
    private bool isLocked = false;
    private bool isDisposed = false;

    /// <summary>
    /// Initializes a new RawPriorityAsyncLock.
    /// </summary>
    /// <param name="priorityComparer">Comparer used to determine the order in which waiters acquire the lock</param>
    public RawPriorityAsyncLock(IComparer<TWaiterPriority> priorityComparer)
    {
        ArgumentNullException.ThrowIfNull(priorityComparer);
        this.waiters = new PriorityQueue<TaskCompletionSource<IDisposable>, TWaiterPriority>(priorityComparer);
    }

    /// <summary>
    /// Initializes a new RawPriorityAsyncLock using the default comparer for <typeparamref name="TWaiterPriority"/>.
    /// </summary>
    public RawPriorityAsyncLock() : this(Comparer<TWaiterPriority>.Default)
    {
    }

    /// <summary>
    /// Obtains the lock, asynchronously waiting for it if it is not immediately available.
    /// </summary>
    /// <remarks>
    /// The lock can only be acquired once at a time, so attempting to acquire while already holding the lock will result in a deadlock.
    /// </remarks>
    /// <param name="waiterPriority">Information about the task that is acquiring the lock, used to calculate priority when there are multiple waiters.</param>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
    /// <returns>A releaser which releases the lock when disposed</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RawPriorityAsyncLock has been disposed before the lock is obtained</exception>
    /// <exception cref="OperationCanceledException">Thrown if the provided cancellation token is canceled before the lock is obtained</exception>
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
    /// Attempts to immediately acquire the lock without waiting.
    /// </summary>
    /// <param name="releaser">releaser which releases the lock when disposed, or null if the lock is not acquired</param>
    /// <returns>true if the lock is acquired, false otherwise</returns>
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
    /// prevents new tasks from acquiring locks and stops all waiters
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
    /// transfers the lock to the next waiter in priority order that has not been canceled, or unlocks it if there is none
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
        /// Initializes a new Releaser for the specified lock
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
