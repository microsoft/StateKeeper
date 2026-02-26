// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma warning disable CA2000 // StateHandle takes ownership of the lock releaser

using System.Diagnostics.CodeAnalysis;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper;


/// <summary>
/// Manages exclusive access to a state object.
/// Waiting callers acquire access in First-In-First-Out or Last-In-First-Out order.
/// </summary>
/// <remarks>Releasing the acquired handle allows other callers to acquire access.</remarks>
/// <typeparam name="TState">The type of the state object.</typeparam>
public sealed class AsyncLock<TState> : IDisposable where TState : class
{
    private readonly TState state;
    private readonly RawAsyncLock asyncLock;

    /// <summary>
    /// Initializes a new AsyncLock.
    /// </summary>
    /// <param name="state">The state object to manage access to.</param>
    /// <param name="acquisitionOrder">Selects whether waiting callers obtain access in First-In-First-Out or Last-In-First-Out order.</param>
    public AsyncLock(TState state, AcquisitionOrder acquisitionOrder = AcquisitionOrder.FIFO)
    {
        this.state = state;
        this.asyncLock = new RawAsyncLock(acquisitionOrder);
    }

    /// <summary>
    /// Acquires exclusive access to the state, asynchronously awaiting if access is not immediately available.
    /// </summary>
    /// <remarks>
    /// Access can only be acquired once at a time, so attempting to acquire while already holding access will result in a deadlock.
    /// </remarks>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining access.</param>
    /// <returns>A handle which provides access to the state and relinquishes access when disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the AsyncLock has been disposed before access is obtained.</exception>
    public ValueTask<StateHandle<TState>> AcquireAsync(CancellationToken cancellationToken)
    {
        var task = this.asyncLock.AcquireAsync(cancellationToken);
        return task.IsCompletedSuccessfully
            ? ValueTask.FromResult(new StateHandle<TState>(this.state, task.Result))
            : this.AwaitAcquire(task);
    }

    /// <summary>
    /// Async slow path for <see cref="AcquireAsync"/>. Separated to avoid allocating an async state machine
    /// when the lock is acquired synchronously (uncontested).
    /// </summary>
    private async ValueTask<StateHandle<TState>> AwaitAcquire(ValueTask<IDisposable> task)
    {
        return new StateHandle<TState>(this.state, await task);
    }

    /// <summary>
    /// Attempts to immediately acquire exclusive access to the state without waiting.
    /// </summary>
    /// <param name="handle">A handle which provides access to the state and relinquishes access when disposed, or null if access is not acquired.</param>
    /// <returns>true if access is acquired, false otherwise.</returns>
    public bool TryAcquire([NotNullWhen(true)] out StateHandle<TState>? handle)
    {
        if (this.asyncLock.TryAcquire(out var lockReleaser))
        {
            handle = new StateHandle<TState>(this.state, lockReleaser);
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
        this.asyncLock.Dispose();
    }
}
