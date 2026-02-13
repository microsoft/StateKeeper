#pragma warning disable CA2000 // StateHandle takes ownership of the lock releaser

using System.Diagnostics.CodeAnalysis;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper;

/// <summary>
/// Manages access to a state object with reader-writer semantics.
/// Allows multiple concurrent readers or one writer at a time.
/// </summary>
/// <remarks>Releasing the acquired handle allows other callers to acquire access.</remarks>
/// <typeparam name="TMutableState">The type of the mutable state object provided to writers.</typeparam>
/// <typeparam name="TReadOnlyState">The type of the read-only state object provided to readers.</typeparam>
public sealed class RWAsyncLock<TMutableState, TReadOnlyState> : IDisposable where TMutableState : class
{
    private readonly TMutableState mutableState;
    private readonly Func<TMutableState, TReadOnlyState> toReadOnlyState;
    private readonly RawRWAsyncLock asyncReadersWriterLock;

    /// <summary>
    /// Initializes a new RWAsyncLock.
    /// </summary>
    /// <param name="mutableState">The mutable state object to manage access to.</param>
    /// <param name="toReadOnlyState">A function that converts the mutable state to a read-only representation for readers.</param>
    /// <param name="writerAcquisitionOrder">The order in which waiting writers acquire access (FIFO or LIFO).</param>
    public RWAsyncLock(
        TMutableState mutableState,
        Func<TMutableState, TReadOnlyState> toReadOnlyState,
        AcquisitionOrder writerAcquisitionOrder = AcquisitionOrder.FIFO)
    {
        ArgumentNullException.ThrowIfNull(toReadOnlyState);

        this.mutableState = mutableState;
        this.toReadOnlyState = toReadOnlyState;

        this.asyncReadersWriterLock = new RawRWAsyncLock(writerAcquisitionOrder: writerAcquisitionOrder);
    }

    /// <summary>
    /// Acquires exclusive writer access to the mutable state, asynchronously awaiting if access is not immediately available.
    /// </summary>
    /// <remarks>
    /// Access can only be acquired once at a time, so attempting to acquire while already holding access will result in a deadlock.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining access.</param>
    /// <returns>A handle which provides access to the mutable state and relinquishes access when disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RWAsyncLock has been disposed before access is obtained.</exception>
    /// <exception cref="TaskCanceledException">Thrown if the provided cancellation token is canceled before access is obtained.</exception>
    public ValueTask<StateHandle<TMutableState>> AcquireWriterAsync(CancellationToken cancellationToken)
    {
        var task = this.asyncReadersWriterLock.AcquireWriteLockAsync(cancellationToken);
        return task.IsCompletedSuccessfully
            ? ValueTask.FromResult(new StateHandle<TMutableState>(this.mutableState, task.Result))
            : this.AwaitAcquireWriter(task);
    }

    /// <summary>
    /// Async slow path for <see cref="AcquireWriterAsync"/>. Separated to avoid allocating an async state machine
    /// when the lock is acquired synchronously (uncontested).
    /// </summary>
    private async ValueTask<StateHandle<TMutableState>> AwaitAcquireWriter(ValueTask<IDisposable> task)
    {
        var releaser = await task.ConfigureAwait(false);
        return new StateHandle<TMutableState>(this.mutableState, releaser);
    }

    /// <summary>
    /// Acquires reader access to the read-only state, asynchronously awaiting if access is not immediately available.
    /// </summary>
    /// <remarks>
    /// Although multiple readers may hold access concurrently, attempting to acquire reader access while already holding it
    /// will result in a deadlock if there are waiting writers.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining access.</param>
    /// <returns>A handle which provides access to the read-only state and relinquishes access when disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the RWAsyncLock has been disposed before access is obtained.</exception>
    /// <exception cref="TaskCanceledException">Thrown if the provided cancellation token is canceled before access is obtained.</exception>
    public ValueTask<StateHandle<TReadOnlyState>> AcquireReaderAsync(CancellationToken cancellationToken)
    {
        var task = this.asyncReadersWriterLock.AcquireReadLockAsync(cancellationToken);
        return task.IsCompletedSuccessfully
            ? ValueTask.FromResult(this.CreateReaderHandle(task.Result))
            : this.AwaitAcquireReader(task);
    }

    /// <summary>
    /// Async slow path for <see cref="AcquireReaderAsync"/>. Separated to avoid allocating an async state machine
    /// when the lock is acquired synchronously (uncontested).
    /// </summary>
    private async ValueTask<StateHandle<TReadOnlyState>> AwaitAcquireReader(ValueTask<IDisposable> task)
    {
        var releaser = await task.ConfigureAwait(false);
        return this.CreateReaderHandle(releaser);
    }

    /// <summary>
    /// Creates a reader handle from a lock releaser, disposing the releaser if <see cref="toReadOnlyState"/> throws.
    /// </summary>
    private StateHandle<TReadOnlyState> CreateReaderHandle(IDisposable releaser)
    {
        try
        {
            var readOnlyState = this.toReadOnlyState(this.mutableState);
            return new StateHandle<TReadOnlyState>(readOnlyState, releaser);
        }
        catch (Exception)
        {
            releaser.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to immediately acquire exclusive writer access to the mutable state without waiting.
    /// </summary>
    /// <param name="handle">A handle which provides access to the mutable state and relinquishes access when disposed, or null if access is not acquired.</param>
    /// <returns>true if access is acquired, false otherwise.</returns>
    public bool TryAcquireWriter([NotNullWhen(true)] out StateHandle<TMutableState>? handle)
    {
        if (this.asyncReadersWriterLock.TryAcquireWriteLock(out var lockReleaser))
        {
            handle = new StateHandle<TMutableState>(this.mutableState, lockReleaser);
            return true;
        }
        else
        {
            handle = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to immediately acquire reader access to the read-only state without waiting.
    /// </summary>
    /// <param name="handle">A handle which provides access to the read-only state and relinquishes access when disposed, or null if access is not acquired.</param>
    /// <returns>true if access is acquired, false otherwise.</returns>
    public bool TryAcquireReader([NotNullWhen(true)] out StateHandle<TReadOnlyState>? handle)
    {
        if (this.asyncReadersWriterLock.TryAcquireReadLock(out var lockReleaser))
        {
            handle = this.CreateReaderHandle(lockReleaser);
            return true;
        }
        else
        {
            handle = null;
            return false;
        }
    }

    /// <summary>
    /// Prevents new callers from acquiring access and stops all waiting callers.
    /// </summary>
    public void Dispose()
    {
        this.asyncReadersWriterLock.Dispose();
    }
}
