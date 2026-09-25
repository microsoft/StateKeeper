// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.StateKeeper;

/// <summary>
/// specifies the order in which waiters acquire a lock.
/// </summary>
public enum AcquisitionOrder
{
    /// <summary>
    /// first-in-first-out order for waiters to acquire a lock
    /// </summary>
    FIFO,
    /// <summary>
    /// last-in-first-out order for waiters to acquire a lock
    /// </summary>
    LIFO
}
