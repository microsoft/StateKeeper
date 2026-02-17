// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma warning disable CA2000 // StateHandle takes ownership of the lock releaser

using System.Diagnostics.CodeAnalysis;
using Microsoft.StateKeeper.Raw;

namespace Microsoft.StateKeeper;

/// <summary>
/// Manages exclusive access to a state object. Waiting callers acquire access in priority order.
/// </summary>
/// <remarks><para>Waiters acquire access in ascending priority order.</para>
/// <para>Releasing the acquired handle allows other callers to acquire access.</para></remarks>
/// <typeparam name="TState">The type of the state object.</typeparam>
/// <typeparam name="TWaiterPriority">Type of argument to determine priority of callers.</typeparam>
public sealed class PriorityAsyncLock<TState, TWaiterPriority> : IDisposable where TState : class
{
    private readonly TState state;
    private readonly RawPriorityAsyncLock<TWaiterPriority> asyncLock;

    /// <summary>
    /// Initializes a new PriorityAsyncLock.
    /// </summary>
    /// <param name="state">The state object to manage access to.</param>
    /// <param name="priorityComparer">Comparer used to determine the order in which waiting callers acquire access.</param>
    public PriorityAsyncLock(TState state, IComparer<TWaiterPriority> priorityComparer)
    {
        this.state = state;
        this.asyncLock = new RawPriorityAsyncLock<TWaiterPriority>(priorityComparer);
    }

    /// <summary>
    /// Initializes a new PriorityAsyncLock using the default comparer for <typeparamref name="TWaiterPriority"/>.
    /// </summary>
    /// <param name="state">The state object to manage access to.</param>
    public PriorityAsyncLock(TState state)
        : this(state, Comparer<TWaiterPriority>.Default)
    {
    }

    /// <summary>
    /// Acquires exclusive access to the state, asynchronously awaiting if access is not immediately available.
    /// </summary>
    /// <remarks>
    /// Access can only be acquired once at a time, so attempting to acquire while already holding access will result in a deadlock.
    /// </remarks>
    /// <param name="waiterPriority">Information about the caller, used to calculate priority when multiple callers are waiting.</param>
    /// <param name="cancellationToken">A token whose cancellation indicates lost interest in obtaining access.</param>
    /// <returns>A handle which provides access to the state and relinquishes access when disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the PriorityAsyncLock has been disposed before access is obtained.</exception>
    public ValueTask<StateHandle<TState>> AcquireAsync(TWaiterPriority waiterPriority, CancellationToken cancellationToken)
    {
        var task = this.asyncLock.AcquireAsync(waiterPriority, cancellationToken);
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
        var releaser = await task.ConfigureAwait(false);
        return new StateHandle<TState>(this.state, releaser);
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
