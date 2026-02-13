using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult
#pragma warning disable CA1065 // Leak-detection finalizer intentionally throws in DEBUG builds
#pragma warning disable CA1821 // Finalizer body is intentionally conditional (#if DEBUG) for leak detection

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
                TaskCompletionSource<IDisposable> taskCompletionSource = new TaskCompletionSource<IDisposable>();
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

#if DEBUG
                // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                // so defuse the releaser to prevent false leak detection.
                // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                releaser.Defuse();
#endif
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
        internal Releaser(RawAsyncLock RawAsyncLock)
        {
            this.RawAsyncLock = RawAsyncLock;
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
        /// </summary>
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
                this.RawAsyncLock.Release();
            }
        }
    }
}
