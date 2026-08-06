using DesktopPet.Infra.Lifecycle;

namespace DesktopPet.Infra.Tests;

public class AsyncGenerationOwnerTests
{
    [Fact]
    public async Task Replace_PublishesNewGenerationBeforeRetiredLeaseDrains()
    {
        var first = new ProbeGeneration();
        var second = new ProbeGeneration();
        await using var owner = new AsyncGenerationOwner<ProbeGeneration>();
        await owner.ReplaceAsync(first);

        var firstLease = owner.Acquire();
        Assert.NotNull(firstLease);
        var retirement = owner.ReplaceAsync(second);
        using var secondLease = owner.Acquire();

        Assert.NotNull(secondLease);
        Assert.Same(first, firstLease!.Value);
        Assert.Same(second, secondLease!.Value);
        Assert.False(first.IsDisposed);
        Assert.False(retirement.IsCompleted);

        firstLease.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForActiveLeaseAndRejectsNewAcquires()
    {
        var generation = new ProbeGeneration();
        var owner = new AsyncGenerationOwner<ProbeGeneration>();
        await owner.ReplaceAsync(generation);
        var lease = owner.Acquire();
        Assert.NotNull(lease);

        var disposing = owner.DisposeAsync().AsTask();
        Assert.Null(owner.Acquire());
        Assert.False(disposing.IsCompleted);

        lease!.Dispose();
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, generation.DisposeCount);
    }

    private sealed class ProbeGeneration : IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool IsDisposed => DisposeCount > 0;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
