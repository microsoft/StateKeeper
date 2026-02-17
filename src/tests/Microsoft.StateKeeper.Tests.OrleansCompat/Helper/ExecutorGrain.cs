// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

/// <summary>
/// Grain that executes test callbacks on the Orleans task scheduler.
/// </summary>
public class ExecutorGrain : Grain, IExecutorGrain
{
    /// <summary>
    /// The task scheduler captured on the first call to <see cref="Execute"/>,
    /// used by <see cref="CheckOrleans"/> to verify we haven't escaped the grain context.
    /// </summary>
    private TaskScheduler? _expectedScheduler;

    /// <inheritdoc />
    public async Task Execute(string callbackId)
    {
        try
        {
            this._expectedScheduler ??= TaskScheduler.Current;
            var callback = IExecutorGrain.GetCallback(callbackId);
            await callback(this.CheckOrleans);
        }
        finally
        {
            this.DeactivateOnIdle();
        }
    }

    /// <inheritdoc />
    public Task Ping()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asserts that we are still running on the grain's original task scheduler,
    /// then exercises a grain-to-grain call by pinging a disposable sibling grain.
    /// Passed to test callbacks as the <see cref="CheckOrleans"/> delegate.
    /// </summary>
    private async Task CheckOrleans()
    {
        // directly verify we're still in the expected task scheduler
        var scheduler = TaskScheduler.Current;
        if (scheduler != this._expectedScheduler)
        {
            throw new InvalidOperationException(
                $"Expected to be running on the grain's task scheduler ({this._expectedScheduler}), " +
                $"but running on {scheduler} instead.");
        }

        // verify orleans works (if we were in the wrong scheduler it would throw)
        var pingTargetId = Guid.NewGuid().ToString();
        var pingTarget = this.GrainFactory.GetGrain<IExecutorGrain>(pingTargetId);
        await pingTarget.Ping();
    }
}
