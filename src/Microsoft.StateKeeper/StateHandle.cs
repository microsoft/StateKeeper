namespace Microsoft.StateKeeper;

/// <summary>
/// A handle that provides managed access to a state object.
/// Disposing the handle relinquishes access, allowing other callers to access the state.
/// </summary>
/// <typeparam name="TState">The type of the state object.</typeparam>
public sealed class StateHandle<TState> : IDisposable
{
    /// <summary>
    /// Gets the state object associated with this handle.
    /// </summary>
    public TState State { get; }

    private readonly IDisposable lockReleaser;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateHandle{TState}"/> class.
    /// </summary>
    /// <param name="state">The state object to associate with this handle.</param>
    /// <param name="lockReleaser">The disposable object that relinquishes access when disposed.</param>
    internal StateHandle(TState state, IDisposable lockReleaser)
    {
        this.State = state;
        this.lockReleaser = lockReleaser;
    }

    /// <summary>
    /// Relinquishes access to the state, allowing other callers to access it.
    /// </summary>
    public void Dispose()
    {
        // lock releaser implementation is responsible for making sure Dispose is idempotent
        this.lockReleaser.Dispose();
    }
}
