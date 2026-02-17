// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult
#pragma warning disable CA1065 // Leak-detection finalizer intentionally throws in DEBUG builds
#pragma warning disable CA1821 // Finalizer body is intentionally conditional (#if DEBUG) for leak detection

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
                TaskCompletionSource<IDisposable> taskCompletionSource = new();
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

#if DEBUG
                // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                // so defuse the releaser to prevent false leak detection.
                // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                releaser.Defuse();
#endif
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

#if DEBUG
        /// <summary>
        /// Captures where the lock was acquired for leak detection diagnostics.
        /// DEBUG-only: Capturing stack traces has performance overhead that we don't need in production.
        /// </summary>
        private readonly string acquisitionStackTrace;
#endif

        /// <summary>
        /// Releases the lock
        /// </summary>
        internal Releaser(RawPriorityAsyncLock<TWaiterPriority> asyncLock)
        {
            this.asyncLock = asyncLock;
#if DEBUG
            // Keep track of where the lock was acquired for leak detection diagnostics.
            // In debug mode only, we will log this and panic if the releaser is
            // garbage collected without being disposed
            this.acquisitionStackTrace = Environment.StackTrace;
#endif
        }

#if DEBUG
        /// <summary>
        /// Leak detection finalizer - only runs if neither Dispose() nor Defuse() was called.
        /// This means the releaser handle was returned to user code but was never released.
        /// DEBUG-only: Throwing from a finalizer will crash the process.
        ~Releaser()
        {
            throw new LeakDetectedException(this.GetType().FullName!, this.acquisitionStackTrace);
        }
#endif

#if DEBUG
        /// <summary>
        /// Suppresses the leak detection finalizer for this releaser.
        /// Used when a releaser is created but not handed off to a waiter (e.g., when the waiter was already canceled).
        /// DEBUG-only: The finalizer only exists in DEBUG builds, so this method is also only needed in DEBUG builds.
        /// </summary>
        [SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "Suppressing the leak detection finalizer for releasers that were never handed out")]
        internal void Defuse()
        {
            GC.SuppressFinalize(this);
        }
#endif

        public void Dispose()
        {
            // ensure the release can only make the lock release once
            // even if the releaser was disposed multiple times
            if (Interlocked.Exchange(ref this.isDisposed, DISPOSED) == NOT_DISPOSED)
            {
#if DEBUG
                // Suppress the leak detection finalizer since the lock is being properly released.
                // DEBUG-only: The finalizer only exists in DEBUG builds, so no need to suppress it in release builds.
                GC.SuppressFinalize(this);
#endif
                this.asyncLock.Release();
            }
        }
    }
}
