// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.StateKeeper.Raw;
using Microsoft.StateKeeper.Tests.OrleansCompat.Helper;

namespace Microsoft.StateKeeper.Tests.OrleansCompat;

[TestClass]
public class RawAsyncLockOrleansTests
{
    [TestMethod]
    public async Task UncontestedAcquire()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var releaser = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task ContestedAcquire()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var releaser1 = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            // Start second acquire — will not complete because lock is held
            var releaser2Task = sut.AcquireAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(releaser2Task.IsCompleted, "second acquire should be waiting");

            releaser1.Dispose();
            await checkOrleans();

            var releaser2 = await releaser2Task;
            await checkOrleans();

            releaser2.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task CancelWaiting()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var releaser = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            // Cancel a waiter via CancellationTokenSource
            using var cts = new CancellationTokenSource();
            var waitTask = sut.AcquireAsync(cts.Token);
            Assert.IsFalse(waitTask.IsCanceled, "should not be canceled before token fires");

            await cts.CancelAsync();
            Assert.IsTrue(waitTask.IsCanceled, "should be canceled after token fires");
            await checkOrleans();

            // Already-canceled token cancels synchronously
            var alreadyCanceled = sut.AcquireAsync(new CancellationToken(canceled: true));
            Assert.IsTrue(alreadyCanceled.IsCanceled);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireSucceeds()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var succeeded = sut.TryAcquire(out var releaser);
            Assert.IsTrue(succeeded);
            Assert.IsNotNull(releaser);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task TryAcquireFails()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var releaser = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            var succeeded = sut.TryAcquire(out var releaser2);
            Assert.IsFalse(succeeded);
            Assert.IsNull(releaser2);
            await checkOrleans();

            releaser.Dispose();
            await checkOrleans();
        });
    }

    [TestMethod]
    public async Task DisposeUnblocksWaitersAndPreventsNewAcquires()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            var sut = new RawAsyncLock();

            var releaser = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            // Start a waiter
            var waiterTask = sut.AcquireAsync(CancellationToken.None).AsTask();

            // Dispose the lock — waiter should get ObjectDisposedException
            sut.Dispose();
            await checkOrleans();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waiterTask);
            await checkOrleans();

            // New acquire after dispose should also throw
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await sut.AcquireAsync(CancellationToken.None));
            await checkOrleans();

            // Clean up the held releaser
            releaser.Dispose();
        });
    }

    [TestMethod]
    public async Task ReleaserDoubleDisposeIsIdempotent()
    {
        await ClusterFixture.ExecuteOnGrain(async checkOrleans =>
        {
            using var sut = new RawAsyncLock();

            var releaser1 = await sut.AcquireAsync(CancellationToken.None);
            await checkOrleans();

            var releaser2Task = sut.AcquireAsync(CancellationToken.None).AsTask();
            var releaser3Task = sut.AcquireAsync(CancellationToken.None).AsTask();

            // First dispose releases lock — releaser2 should acquire
            releaser1.Dispose();
            await checkOrleans();

            var releaser2 = await releaser2Task;
            await checkOrleans();

            // Second dispose of releaser1 should be a no-op
            releaser1.Dispose();
            await checkOrleans();

            Assert.AreEqual(TaskStatus.WaitingForActivation, releaser3Task.Status,
                "third waiter should still be waiting");

            releaser2.Dispose();
            await checkOrleans();

            var releaser3 = await releaser3Task;
            await checkOrleans();

            releaser3.Dispose();
            await checkOrleans();
        });
    }
}
