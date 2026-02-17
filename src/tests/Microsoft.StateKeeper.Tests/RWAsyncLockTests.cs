// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Microsoft.StateKeeper.Tests;

[TestClass]
public class RWAsyncLockTests
{
    public required TestContext TestContext { get; set; }

#if DEBUG
    [TestCleanup]
    public void Cleanup()
    {
        // Force finalizers to run, which will trigger leak detection immediately
        // rather than relying on GC timing
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
#endif

    private sealed class TestState
    {
        public int Value { get; set; }
    }

    [TestMethod]
    public async Task PreventsSimultaneousWriteAccess()
    {
        ConcurrentQueue<string> tags = new();
        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 0 },
            state => state.Value);

        tags.Enqueue("wait1");
        StateHandle<TestState> handle1 = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);

        var otherUser = Task.Run(async () =>
        {
            tags.Enqueue("wait2");
            var handle2 = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);
            Assert.AreEqual(42, handle2.State.Value, "state change from first writer should be visible");
            tags.Enqueue("use2");
            handle2.Dispose();
        })!;

        // make sure that first user does not dispose until background task user has a chance to start
        while (!tags.Contains("wait2"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        tags.Enqueue("use1");
        Assert.AreEqual(0, handle1.State.Value, "initial state should be 0");
        handle1.State.Value = 42;
        handle1.Dispose();

        await otherUser;

        List<string> expectedOrder = new List<string>() { "wait1", "wait2", "use1", "use2" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task AllowsSimultaneousReadAccess()
    {
        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 42 },
            state => state.Value);

        StateHandle<int> handle1 = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);
        StateHandle<int> handle2 = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(42, handle1.State, "first reader should see state");
        Assert.AreEqual(42, handle2.State, "second reader should see state");

        handle1.Dispose();
        handle2.Dispose();
    }

    [TestMethod]
    public async Task PreventsReadDuringWrite()
    {
        ConcurrentQueue<string> tags = new();
        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 0 },
            state => state.Value);

        tags.Enqueue("wait-writer");
        StateHandle<TestState> writerHandle = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);

        var reader = Task.Run(async () =>
        {
            tags.Enqueue("wait-reader");
            var readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);
            Assert.AreEqual(42, readerHandle.State, "reader should see updated state after writer releases");
            tags.Enqueue("use-reader");
            readerHandle.Dispose();
        })!;

        while (!tags.Contains("wait-reader"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        tags.Enqueue("use-writer");
        writerHandle.State.Value = 42;
        writerHandle.Dispose();

        await reader;

        List<string> expectedOrder = new List<string>() { "wait-writer", "wait-reader", "use-writer", "use-reader" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task PreventsWriteDuringRead()
    {
        ConcurrentQueue<string> tags = new();
        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 0 },
            state => state.Value);

        tags.Enqueue("wait-reader");
        StateHandle<int> readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);

        var writer = Task.Run(async () =>
        {
            tags.Enqueue("wait-writer");
            var writerHandle = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);
            tags.Enqueue("use-writer");
            writerHandle.Dispose();
        })!;

        while (!tags.Contains("wait-writer"))
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        tags.Enqueue("use-reader");
        readerHandle.Dispose();

        await writer;

        List<string> expectedOrder = new List<string>() { "wait-reader", "wait-writer", "use-reader", "use-writer" };
        foreach (var i in Enumerable.Range(0, expectedOrder.Count))
        {
            Assert.AreEqual(expectedOrder[i], tags.ToList()[i], $"actual order: {string.Join("-", tags)}");
        }
    }

    [TestMethod]
    public async Task ReaderSeesReadOnlyState()
    {
        var mutableState = new TestState { Value = 42 };
        using var sut = new RWAsyncLock<TestState, int>(
            mutableState,
            state => state.Value);

        StateHandle<int> readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(42, readerHandle.State, "reader should see state value through read-only projection");

        readerHandle.Dispose();
    }

    [TestMethod]
    public async Task WriterSeesMutableState()
    {
        var mutableState = new TestState { Value = 0 };
        using var sut = new RWAsyncLock<TestState, int>(
            mutableState,
            state => state.Value);

        StateHandle<TestState> writerHandle = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(0, writerHandle.State.Value, "writer should see initial state");
        writerHandle.State.Value = 42;
        Assert.AreEqual(42, writerHandle.State.Value, "writer should be able to modify state");

        writerHandle.Dispose();

        // Verify the modification persists
        StateHandle<int> readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);
        Assert.AreEqual(42, readerHandle.State, "reader should see modified state");
        readerHandle.Dispose();
    }

    [TestMethod]
    public void TryAcquireWriter_AcquiresWhenAvailable()
    {
        var state = new TestState { Value = 42 };
        using var sut = new RWAsyncLock<TestState, int>(state, s => s.Value);

        bool acquired1 = sut.TryAcquireWriter(out StateHandle<TestState>? handle1);
        bool acquired2 = sut.TryAcquireWriter(out StateHandle<TestState>? handle2);
        Assert.IsTrue(acquired1, "first TryAcquireWriter should succeed");
        Assert.IsNull(handle2, "second TryAcquireWriter should fail when write lock is held");
        Assert.AreEqual(42, handle1?.State.Value, "handle should provide access to the state");
        handle2?.Dispose();
        handle1?.Dispose();
    }

    [TestMethod]
    public void TryAcquireReader_AcquiresWhenAvailable()
    {
        var state = new TestState { Value = 42 };
        using var sut = new RWAsyncLock<TestState, int>(state, s => s.Value);

        var acquired1 = sut.TryAcquireReader(out StateHandle<int>? handle1);
        var acquired2 = sut.TryAcquireReader(out StateHandle<int>? handle2);
        Assert.IsTrue(acquired1, "first TryAcquireReader should succeed");
        Assert.IsTrue(acquired2, "second TryAcquireReader should succeed (multiple readers allowed)");

        Assert.AreEqual(42, handle1!.State, "first handle should provide access to the state");
        Assert.AreEqual(42, handle2!.State, "second handle should provide access to the state");
        handle1.Dispose();
        handle2.Dispose();
    }

    [TestMethod]
    public void TryAcquireReader_FailsWhenWriterHoldsLock()
    {
        var state = new TestState { Value = 42 };
        using var sut = new RWAsyncLock<TestState, int>(state, s => s.Value);

        var writeAcquired = sut.TryAcquireWriter(out StateHandle<TestState>? writerHandle);
        var readAcquired = sut.TryAcquireReader(out StateHandle<int>? readerHandle);

        Assert.IsTrue(writeAcquired, "TryAcquireWriter should succeed");
        Assert.IsFalse(readAcquired, "TryAcquireReader should fail when write lock is held");
        Assert.IsNull(readerHandle, "reader handle should be null when acquisition fails");
        readerHandle?.Dispose();
        writerHandle?.Dispose();
    }

    [TestMethod]
    public void TryAcquireWriter_FailsWhenReaderHoldsLock()
    {
        var state = new TestState { Value = 42 };
        using var sut = new RWAsyncLock<TestState, int>(state, s => s.Value);

        var readAcquired = sut.TryAcquireReader(out var readerHandle);
        var writeAcquired = sut.TryAcquireWriter(out var writerHandle);

        Assert.IsTrue(readAcquired, "read should be acquired");
        Assert.IsFalse(writeAcquired, "TryAcquireWriter should fail when read lock is held");
        Assert.IsNotNull(readerHandle, "TryAcquireReader should succeed");
        Assert.IsNull(writerHandle, "TryAcquireWriter should fail when read lock is held");
        writerHandle?.Dispose();
        readerHandle?.Dispose();
    }

    [TestMethod]
    public async Task StopsWaitingTasksWhenDisposed()
    {
        var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 0 },
            state => state.Value);

        StateHandle<TestState> handle1 = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);

        bool writerTaskStarted = false;
        bool readerTaskStarted = false;

        Task waitingWriter = Task.Run(async () =>
        {
            writerTaskStarted = true;
            var handle2 = await sut.AcquireWriterAsync(this.TestContext.CancellationToken);
            handle2.Dispose();
        })!;

        Task waitingReader = Task.Run(async () =>
        {
            readerTaskStarted = true;
            var handle3 = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);
            handle3.Dispose();
        })!;

        while (!writerTaskStarted || !readerTaskStarted)
        {
            await Task.Delay(5);
        }

        await Task.Delay(5);

        sut.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waitingWriter);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waitingReader);

        // Dispose handle1 after the test assertion - the RWAsyncLock is already disposed,
        // so this just cleans up to avoid leak detection triggering
        handle1.Dispose();
    }

    [TestMethod]
    public async Task AcquireAsync_HandlesFailureToConvertToReadOnly()
    {
        bool failConversion = true;
        Func<TestState, int> convert = state =>
        {
            if (failConversion)
            {
                throw new InvalidOperationException("Conversion failed");
            }
            return state.Value;
        };


        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 42 },
            convert);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            using var readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken);
        }, "Acquiring reader should fail when conversion fails");

        // Now allow conversion to succeed and verify we can still acquire a reader
        failConversion = false;
        using (var readerHandle = await sut.AcquireReaderAsync(this.TestContext.CancellationToken))
        {
            Assert.AreEqual(42, readerHandle.State, "Reader should see correct state after conversion succeeds");
        }
    }

    [TestMethod]
    public void TryAcquire_HandlesFailureToConvertToReadOnly()
    {
        bool failConversion = true;
        Func<TestState, int> convert = state =>
        {
            if (failConversion)
            {
                throw new InvalidOperationException("Conversion failed");
            }
            return state.Value;
        };

        using var sut = new RWAsyncLock<TestState, int>(
            new TestState { Value = 42 },
            convert);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            var success = sut.TryAcquireReader(out var readerHandle);
            readerHandle?.Dispose(); // just to be safe; we won't get this far
        }, "TryAcquireReader should throw when conversion fails");

        // Now allow conversion to succeed and verify we can still acquire a reader
        failConversion = false;

        var acquired = sut.TryAcquireReader(out var validReaderHandle);
        Assert.IsTrue(acquired, "TryAcquireReader should succeed when conversion works");
        Assert.AreEqual(42, validReaderHandle!.State, "Reader should see correct state after conversion succeeds");
        validReaderHandle.Dispose();
    }
}
