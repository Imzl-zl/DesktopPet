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

    [Fact]
    public async Task DisposeAsync_LeakedLease_ForcesDisposeAfterTimeout()
    {
        // 修复回归：租约泄漏（调用方忘记释放）时 DisposeAsync 不得永久挂起——
        // 超时后强制回收底层资源（泄漏租约后续 Release 幂等）。
        var generation = new ProbeGeneration();
        var owner = new AsyncGenerationOwner<ProbeGeneration>();
        await owner.ReplaceAsync(generation);
        var leaked = owner.Acquire();
        Assert.NotNull(leaked);

        await owner.DisposeAsync(TimeSpan.FromMilliseconds(100)).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, generation.DisposeCount); // 资源已被强制释放
        leaked!.Dispose(); // 泄漏租约晚释放：幂等，不二次 Dispose
        Assert.Equal(1, generation.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_NoLeak_CompletesWithoutForcedWait()
    {
        var generation = new ProbeGeneration();
        var owner = new AsyncGenerationOwner<ProbeGeneration>();
        await owner.ReplaceAsync(generation);
        using var lease = owner.Acquire();
        Assert.NotNull(lease);
        lease!.Dispose(); // 正常释放

        await owner.DisposeAsync(TimeSpan.FromMilliseconds(50)).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
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
