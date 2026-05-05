// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable CA2000 // Releaser ownership is transferred to callers via return value or TrySetResult
#pragma warning disable CA1065 // Leak-detection finalizer intentionally throws in DEBUG builds
#pragma warning disable CA1821 // Finalizer body is intentionally conditional (#if DEBUG) for leak detection

namespace Microsoft.StateKeeper.Raw;

/// <summary>
/// A reader-writer lock for synchronizing asynchronous tasks that read or write a shared resource.
/// Allows multiple concurrent readers or one writer at a time.
/// </summary>
public sealed class RawRWAsyncLock : IDisposable
{
    private readonly object syncObject = new();

    private readonly LinkedList<TaskCompletionSource<IDisposable>> WriteWaiters = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> ReadWaiters = new();

    private int activeReaders = 0;
    private bool activeWriter = false;
    private bool isDisposed = false;

    private readonly AcquisitionOrder writerAcquisitionOrder;

    /// <summary>
    /// Initializes a new RawRWAsyncLock
    /// </summary>
    /// <param name="writerAcquisitionOrder">The order in which waiting writers acquire the lock (FIFO or LIFO)</param>
    public RawRWAsyncLock(AcquisitionOrder writerAcquisitionOrder = AcquisitionOrder.FIFO)
    {
        this.writerAcquisitionOrder = writerAcquisitionOrder;
    }

    /// <summary>
    /// Obtains a write lock, asynchronously awaiting for the lock if it is not immediately available.
    /// </summary>
    /// <remarks>
    /// The write lock can only be acquired once at a time, so attempting to acquire while already holding the lock will result in a deadlock.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
    /// <returns>A releaser which releases the lock when disposed</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RawRWAsyncLock has been disposed before the lock is obtained</exception>
    /// <exception cref="TaskCanceledException">Thrown if the provided cancellation token is canceled before the lock is obtained</exception>
    public ValueTask<IDisposable> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        lock (this.syncObject)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<IDisposable>(cancellationToken);
            }

            if (this.activeReaders == 0 && !this.activeWriter)
            {
                this.activeWriter = true;
                return new ValueTask<IDisposable>(new Releaser(this, isWriterLock: true));
            }
            else
            {
                /// RunContinuationsAsynchronously: prevents awaiter continuations from running synchronously
                // inside TrySetResult while we hold the internal lock, which would risk reentrancy bugs and deadlocks.
                TaskCompletionSource<IDisposable> taskCompletionSource = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() =>
                {
                    lock (this.syncObject)
                    {
                        if (taskCompletionSource.TrySetCanceled())
                        {
                            // cancelling this writer may unblock readers if there are no other active or waiting writers
                            // if there are waiting readers but no active writer and no non-canceled writers,
                            // let the readers proceed
                            if (this.ReadWaiters.Count != 0 && !this.activeWriter && this.WriteWaiters.All(w => w == taskCompletionSource || w.Task.IsCanceled))
                            {
                                foreach (var reader in this.ReadWaiters)
                                {
                                    var releaser = new Releaser(this, isWriterLock: false);

                                    if (reader.TrySetResult(releaser))
                                    {
                                        this.activeReaders++;
                                    }
#if DEBUG
                                    else
                                    {
                                        // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                                        // so defuse the releaser to prevent false leak detection.
                                        // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                                        releaser.Defuse();
                                    }
#endif
                                }
                                this.ReadWaiters.Clear();
                            }
                        }
                    }
                });

                if (this.writerAcquisitionOrder == AcquisitionOrder.FIFO)
                {
                    this.WriteWaiters.AddLast(taskCompletionSource);
                }
                else
                {
                    this.WriteWaiters.AddFirst(taskCompletionSource);
                }

                return new ValueTask<IDisposable>(taskCompletionSource.Task);
            }
        }
    }

    /// <summary>
    /// Obtains a read lock, asynchronously awaiting for the lock if it is not immediately available.
    /// </summary>
    /// <remarks>
    /// Although multiple readers may hold the read lock concurrently, attempting to acquire the read lock while already holding it will result in a deadlock
    /// if there are waiting writers.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining the lock.</param>
    /// <returns>A releaser which releases the lock when disposed</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RawRWAsyncLock has been disposed before the lock is obtained</exception>
    /// <exception cref="TaskCanceledException">Thrown if the provided cancellation token is canceled before the lock is obtained</exception>
    public ValueTask<IDisposable> AcquireReadLockAsync(CancellationToken cancellationToken)
    {
        lock (this.syncObject)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<IDisposable>(cancellationToken);
            }

            // Although canceled writers lingering in WriteWaiters could cause this check
            // to send the reader to ReadWaiters unnecessarily, this is not a bug: when the
            // lock holder releases, the release path iterates WriteWaiters, removes canceled
            // entries, and then unblocks all ReadWaiters. So the reader will always be granted.
            // Canceled writers lingering in WriteWaiters must not block new readers.
            // Unlike TryAcquireReadLock (where this is the only chance to acquire),
            // this path also has a fallback: the reader queues into ReadWaiters and gets
            // unblocked when the lock holder releases and cleans up canceled writers.
            // However, skipping canceled writers here avoids unnecessary latency —
            // without this check, a new reader would be queued until a current reader releases
            // even though it could acquire immediately alongside existing active readers.
            if (!this.activeWriter && this.WriteWaiters.All(w => w.Task.IsCanceled))
            {
                this.activeReaders++;
                return new ValueTask<IDisposable>(new Releaser(this, isWriterLock: false));
            }
            else
            {
                // RunContinuationsAsynchronously: prevents awaiter continuations from running synchronously
                // inside TrySetResult while we hold the internal lock, which would risk reentrancy bugs and deadlocks.
                TaskCompletionSource<IDisposable> taskCompletionSource = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());
                this.ReadWaiters.AddLast(taskCompletionSource);
                return new ValueTask<IDisposable>(taskCompletionSource.Task);
            }
        }
    }

    /// <summary>
    /// Attempt to immediately acquire the write lock without waiting.
    /// </summary>
    /// <param name="releaser">releaser which releases the lock when disposed, or null if lock is not acquired</param>
    /// <returns>true if lock is acquired, false otherwise</returns>
    public bool TryAcquireWriteLock([NotNullWhen(true)] out IDisposable? releaser)
    {
        lock (this.syncObject)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            if (this.activeReaders == 0 && !this.activeWriter)
            {
                this.activeWriter = true;
                releaser = new Releaser(this, isWriterLock: true);
                return true;
            }
            else
            {
                releaser = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Attempt to immediately acquire the read lock without waiting.
    /// </summary>
    /// <param name="releaser">releaser which releases the lock when disposed, or null if lock is not acquired</param>
    /// <returns>true if lock is acquired, false otherwise</returns>
    public bool TryAcquireReadLock([NotNullWhen(true)] out IDisposable? releaser)
    {
        lock (this.syncObject)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);

            // We check specifically for non-cancelled waiting writers because
            // canceled writers that linger in WriteWaiters must not block new readers.
            // Unlike AcquireReadLockAsync (where the reader queues into ReadWaiters and will
            // be unblocked when the lock holder releases and cleans up canceled writers),
            // TryAcquireReadLock has no fallback — it returns false immediately. So we must
            // skip canceled writers here to avoid incorrectly rejecting the caller.
            if (!this.activeWriter && this.WriteWaiters.All(w => w.Task.IsCanceled))
            {
                this.activeReaders++;
                releaser = new Releaser(this, isWriterLock: false);
                return true;
            }
            else
            {
                releaser = null;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (this.syncObject)
        {
            this.isDisposed = true;
            foreach (var waiter in this.WriteWaiters)
            {
                waiter.TrySetException(new ObjectDisposedException(this.GetType().FullName));
            }
            foreach (var waiter in this.ReadWaiters)
            {
                waiter.TrySetException(new ObjectDisposedException(this.GetType().FullName));
            }
        }
    }

    private void ReleaseWriterLock()
    {
        lock (this.syncObject)
        {
            if (this.isDisposed)
            {
                return; // release nothing
            }

            // in LIFO mode, we may have canceled waiters at the end of the queue (the bottom of the stack)
            // which might otherwise never be cleaned up from the queue if there are always new writers coming in
            // so we have to check for and remove entries for these cancelled waiters before we move on
            if (this.writerAcquisitionOrder == AcquisitionOrder.LIFO)
            {
                while (this.WriteWaiters.Last?.Value.Task.IsCanceled == true)
                {
                    this.WriteWaiters.RemoveLast();
                }
            }

            while (this.WriteWaiters.Count != 0)
            {
                var nextWriter = this.WriteWaiters.First!.Value;
                this.WriteWaiters.RemoveFirst();

                var releaser = new Releaser(this, isWriterLock: true);

                // false if this waiter was already cancelled
                if (nextWriter.TrySetResult(releaser))
                {
                    return; // successfully transferred the lock to the next waiting writer
                }

#if DEBUG
                // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                // so defuse the releaser to prevent false leak detection.
                // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                releaser.Defuse();
#endif
            }

            this.activeWriter = false; // no active writer now

            if (this.ReadWaiters.Count != 0)
            {
                foreach (var reader in this.ReadWaiters)
                {
                    var releaser = new Releaser(this, isWriterLock: false);

                    if (reader.TrySetResult(releaser))
                    {
                        // successfully transferred the lock to a waiting reader
                        this.activeReaders++;
                    }
#if DEBUG
                    else
                    {
                        // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                        // so defuse the releaser to prevent false leak detection.
                        // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                        releaser.Defuse();
                    }
#endif
                }
                this.ReadWaiters.Clear();
            }
        }
    }

    private void ReleaseReaderLock()
    {
        lock (this.syncObject)
        {
            if (this.isDisposed)
            {
                return; // release nothing
            }
            this.activeReaders--;
            if (this.activeReaders == 0)
            {
                while (this.WriteWaiters.Count != 0)
                {
                    var nextWriter = this.WriteWaiters.First!.Value;
                    this.WriteWaiters.RemoveFirst();

                    var releaser = new Releaser(this, isWriterLock: true);

                    if (nextWriter.TrySetResult(releaser))
                    {
                        // successfully transferred the lock to the next waiting writer
                        this.activeWriter = true;
                        return;
                    }

#if DEBUG
                    // The waiter was already completed (e.g., canceled) and did not acquire the lock,
                    // so defuse the releaser to prevent false leak detection.
                    // DEBUG-only: The leak detection finalizer only exists in DEBUG builds.
                    releaser.Defuse();
#endif
                }
            }
        }
    }

    /// <summary>
    /// An object whose disposal releases a held lock
    /// </summary>
    private sealed class Releaser : IDisposable
    {
        private const int DISPOSED = 1;
        private const int NOT_DISPOSED = 0;

        private readonly RawRWAsyncLock RawRWAsyncLock;
        private readonly bool isWriterLock;
        private int isDisposed = NOT_DISPOSED;

#if DEBUG
        /// <summary>
        /// Captures where the lock was acquired for leak detection diagnostics.
        /// DEBUG-only: Capturing stack traces has performance overhead that we don't need in production.
        /// </summary>
        private readonly string acquisitionStackTrace;
#endif

        internal Releaser(RawRWAsyncLock RawRWAsyncLock, bool isWriterLock)
        {
            this.RawRWAsyncLock = RawRWAsyncLock;
            this.isWriterLock = isWriterLock;
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
            var lockType = this.isWriterLock ? "write" : "read";
            throw new LeakDetectedException($"{this.GetType().FullName} ({lockType} lock)", this.acquisitionStackTrace);
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
                if (this.isWriterLock)
                {
                    this.RawRWAsyncLock.ReleaseWriterLock();
                }
                else
                {
                    this.RawRWAsyncLock.ReleaseReaderLock();
                }
            }
        }
    }
}
