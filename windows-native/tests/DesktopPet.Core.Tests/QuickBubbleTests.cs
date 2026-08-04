using DesktopPet.Core.Interaction;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>1:1 移植自 windows/src/quick-bubble.test.ts。</summary>
public class QuickBubbleTests
{
    private sealed class FakeBubbleClock : IQuickBubbleClock
    {
        private sealed class Timer(Action callback, bool[] cancelled) : IDisposable
        {
            public Action Callback { get; } = callback;
            public bool[] Cancelled { get; } = cancelled;
            public void Dispose() => Cancelled[0] = true;
        }

        public long Now { get; set; }
        public List<IDisposable> Timers { get; } = [];
        private readonly List<bool[]> _cancelled = [];

        long IQuickBubbleClock.Now() => Now;

        public IDisposable Schedule(Action callback, long delayMs)
        {
            var flag = new[] { false };
            _cancelled.Add(flag);
            var timer = new Timer(callback, flag);
            Timers.Add(timer);
            return timer;
        }

        public bool IsCancelled(int index) => _cancelled[index][0];

        public void Fire(int index) => ((Timer)Timers[index]).Callback();
    }

    [Fact]
    public void ExpiresOnlyTheNewestMessage_AndRequestsNormalRenderOnce()
    {
        var clock = new FakeBubbleClock();
        var expireCount = 0;
        var bubble = new QuickBubbleController(clock, () => expireCount++);

        bubble.Show("first", 4000);
        bubble.Show("second", 6000);

        Assert.True(clock.IsCancelled(0)); // 旧消息定时器被取消

        clock.Now = 4000;
        clock.Fire(0); // 已取消的旧定时器回调不生效
        Assert.Equal("second", bubble.Current());
        Assert.Equal(0, expireCount);

        clock.Now = 6000;
        clock.Fire(1);
        Assert.Null(bubble.Current());
        Assert.Equal(1, expireCount);
    }

    [Fact]
    public void NormalizesPersistedDisplayDurationIntoSupportedRange()
    {
        Assert.Equal(4, QuickBubbleDuration.NormalizeSeconds(null));
        Assert.Equal(4, QuickBubbleDuration.NormalizeSeconds(0));
        Assert.Equal(4.4, QuickBubbleDuration.NormalizeSeconds(4.4));
        Assert.Equal(99, QuickBubbleDuration.NormalizeSeconds(99));
    }
}
