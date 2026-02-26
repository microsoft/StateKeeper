// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

/// <summary>
/// Function the grain invokes to let test code assert that execution
/// is still happening on the grain's task scheduler.
/// </summary>
public delegate Task CheckOrleans();

/// <summary>
/// An async test callback that runs inside a grain and receives a
/// <see cref="CheckOrleans"/> to verify scheduler affinity.
/// </summary>
public delegate Task GrainTestFunction(CheckOrleans checkOrleans);
