# StateKeeper

Async locks for **safely** managing concurrent access to shared state in .NET.

StateKeeper provides type-safe compile-time guarantees about how a mutable state can be used. With `AsyncLock`, the protected state is accessed through a handle which is received when the lock is acquired and disposed to release the lock. In `RWAsyncLock`, holding a writer lock allows access to the mutable state while holding a reader lock only provides an immutable copy of the state.


# Installation

This project is currently experimental. A Nuget package is coming soon.


## Usage

AsyncLock allows exclusive access to the protected state in either first-in-first-out or last-in-first-out order.

```cs
// initialize with the mutable state to protect
using AsyncLock<List<string>> asyncLock = new(
    state: ["foo", "bar"],
    acquisitionOrder: AcquisitionOrder.FIFO);

// acquire exclusive access to the state
// other callers have to wait until the handle is disposed
using (var handle = await asyncLock.AcquireAsync(cancellationToken))
{
    handle.State.Add("baz");
    string foo = handle.State[0];
}

// try to acquire without waiting — returns false if the lock is already held
if (asyncLock.TryAcquire(out var tryHandle))
{
    using (tryHandle)
    {
        tryHandle.State.Add("qux");
    }
}
```

RWAsyncLock is a readers-writer lock that allows multiple concurrent readers or one exclusive writer.

```cs
// initialize with the mutable state accessible by writers
// and a function to map to the read-only state accessible by readers
using RWAsyncLock<List<string>, IReadOnlyList<string>> rwLock = new (
    mutableState: ["foo", "bar"],
    toReadOnlyState: list => list.AsReadOnly());

// get exclusive writer access to the mutable state
// other writers and readers have to wait
using (var handle = await rwLock.AcquireWriterAsync(cancellationToken))
{
    handle.State.Add("baz"); // state is List<string>
}

// get reader access to the read-only state
// other readers can acquire access concurrently, but writers have to wait
using (var handle = await rwLock.AcquireReaderAsync(cancellationToken))
{
    string foo = handle.State[0]; // state is IReadOnlyList<string>
    // `handle.State.Add("baz");` is not possible
}
```

PriorityAsyncLock allows exclusive access to the protected state, where waiters have an associated priority that determines the order that they acquire access.

```cs
// initialize with the mutable state to protect
// optionally pass a custom comparer to control priority ordering.
// waiters acquire access in ascending order.
using PriorityAsyncLock<List<string>, int> priorityLock = new(
    state: ["foo", "bar"],
    priorityComparer: Comparer<int>.Create((a, b) => a.CompareTo(b)));

// acquire exclusive access with a given priority
using (var handle = await priorityLock.AcquireAsync(waiterPriority: 123, cancellationToken))
{
    handle.State.Add("baz");
    string foo = handle.State[0];
}

// try to acquire without waiting — returns false if the lock is already held
if (priorityLock.TryAcquire(out var tryHandle))
{
    using (tryHandle)
    {
        tryHandle.State.Add("qux");
    }
}
```

`Microsoft.StateKeeper.Raw` contains variants that do not hold an associated state.

```cs
List<string> list = ["foo", "bar"];

using RawAsyncLock rawLock = new(AcquisitionOrder.FIFO);

// acquire the lock — returns a releaser that unlocks when disposed
using (var releaser = await rawLock.AcquireAsync(cancellationToken))
{
    // access the shared resource while holding the lock
    // but nothing guarantees that other tasks won't also access that state
    list.Add("baz");
}
```

### Automated Leak Detection

Neglecting to dispose a handle means the lock will not be able to be acquired again, so this should always be avoided through a `using` statement or `finally` block. **In debug mode only**, StateKeeper has automated leak detection which will terminate the process if a handle is garbage-collected before being disposed.

## Building

Build with `dotnet build` or run tests with `dotnet test`

[Requires .NET SDK 8 or later.](https://dotnet.microsoft.com/en-us/download)

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit [Contributor License Agreements](https://cla.opensource.microsoft.com).

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
