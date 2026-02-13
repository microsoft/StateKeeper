namespace Microsoft.StateKeeper.Raw;

/// <summary>
/// Exception thrown by DEBUG-only leak detection when a lock releaser is garbage collected without being disposed.
/// </summary>
public class LeakDetectedException : InvalidOperationException
{
    public LeakDetectedException() : base("A lock releaser was garbage collected without being disposed.")
    {
    }

    public LeakDetectedException(string message) : base(message)
    {
    }

    public LeakDetectedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a LeakDetectedException with a standardized message for lock leak detection.
    /// </summary>
    /// <param name="lockTypeLabel">A label identifying the lock type (e.g., "AsyncLock.Releaser", "AsyncReaderWriterLock.Releaser (write lock)")</param>
    /// <param name="acquisitionStackTrace">The stack trace captured when the lock was acquired</param>
    public LeakDetectedException(string lockTypeLabel, string acquisitionStackTrace)
        : base($"{lockTypeLabel} was garbage collected without being disposed. " +
               $"This indicates a lock that was acquired but never released, which may cause deadlocks.\n\n" +
               $"Lock was acquired at:\n{acquisitionStackTrace}")
    {
    }
}
