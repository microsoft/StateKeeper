// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.StateKeeper;

public enum AcquisitionOrder
{
    /// <summary>
    /// first-in-first-out order for waiting tasks to acquire a lock
    /// </summary>
    FIFO,
    /// <summary>
    /// last-in-first-out order for waiting tasks to acquire a lock
    /// </summary>
    LIFO
}
