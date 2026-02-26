// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

/// <summary>
/// A grain that executes arbitrary async work inside the grain's task scheduler,
/// allowing tests to verify library behavior under Orleans single-threaded execution.
/// </summary>
public interface IExecutorGrain : IGrainWithStringKey
{
    private static readonly ConcurrentDictionary<string, GrainTestFunction> RegisteredCallbacks = new();

    static void RegisterCallback(string id, GrainTestFunction callback) =>
        RegisteredCallbacks[id] = callback;

    static GrainTestFunction GetCallback(string id) =>
        RegisteredCallbacks[id];

    /// <summary>
    /// runs the function registered under the given id, inside the grain
    /// </summary>
    /// <param name="callbackId"></param>
    /// <returns></returns>
    [Alias("Execute")]
    Task Execute(string callbackId);

    /// <summary>
    /// a no-op grain method to exercise the grain factory
    /// </summary>
    /// <returns></returns>
    [Alias("Ping")]
    Task Ping();
}
