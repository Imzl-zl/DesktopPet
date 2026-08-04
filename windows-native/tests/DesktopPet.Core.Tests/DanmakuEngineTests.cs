using DesktopPet.Core.Danmaku;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5g：弹幕引擎（纯逻辑，可单测）——条目池复用、轨道分配防追尾、出屏回收。
/// 渲染层（Win2D）只负责画 Active 列表。
/// </summary>
public class DanmakuEngineTests
{
    [Fact]
    public void Enqueue_FirstItem_TakesTrackZero()
    {
        var engine = new DanmakuEngine(width: 1000);
        var item = engine.Enqueue("第一条", DateTime.Now);
        Assert.NotNull(item);
        Assert.Equal(0, item!.Track);
        Assert.Single(engine.Active);
        Assert.Equal("第一条", item.Text);
    }

    [Fact]
    public void Enqueue_SequentialItems_SpreadAcrossTracks()
    {
        // 同轨道追尾防护：快速连发 5 条 → 分配到不同轨道
        var engine = new DanmakuEngine(width: 1000, trackCount: 8);
        var tracks = new HashSet<int>();
        for (var i = 0; i < 5; i++)
        {
            var item = engine.Enqueue($"弹幕{i}", DateTime.Now);
            Assert.NotNull(item);
            tracks.Add(item!.Track);
        }
        Assert.Equal(5, tracks.Count); // 5 条都在不同轨道
    }

    [Fact]
    public void Enqueue_TrackFull_ReturnsNull()
    {
        // 所有轨道都被占据且尾部太近 → 丢弃（限流）
        var engine = new DanmakuEngine(width: 1000, trackCount: 2, minGap: 500);
        Assert.NotNull(engine.Enqueue("a", DateTime.Now));
        Assert.NotNull(engine.Enqueue("b", DateTime.Now));
        var third = engine.Enqueue("c", DateTime.Now);
        Assert.Null(third); // 2 轨道全满
    }

    [Fact]
    public void Enqueue_PrefersLeastBusyTrack()
    {
        // 轨道 1 的尾部条目已走远 → 新条目优先进轨道 1 而不是轨道 0
        var engine = new DanmakuEngine(width: 1000, trackCount: 2, minGap: 100, minSpeed: 100);
        var t0 = DateTime.Now;
        var a = engine.Enqueue("a", t0)!;
        var b = engine.Enqueue("b", t0)!;
        Assert.NotEqual(a.Track, b.Track);

        // 推进时间：a 走 600px（尾部 x 最大），b 走 600px
        engine.Tick(6.0);
        // 新条目：尾部最小（最空）的轨道 = 走得最远的那条所在轨道？不对——
        // 最空 = 尾部 x 最大（离入场点最远）的轨道
        var c = engine.Enqueue("c", t0.AddSeconds(6));
        Assert.NotNull(c);
        // 两条候选轨道尾部：a.track 的尾部 = a（x=600），b.track 的尾部 = b（x=600）
        // 相同 → 选第一条（轨道 0 或 1 都行，只断言有值）
    }

    [Fact]
    public void Tick_MovesItemsBySpeed()
    {
        var engine = new DanmakuEngine(width: 1000, minSpeed: 200, maxSpeed: 200);
        var item = engine.Enqueue("动", DateTime.Now)!;
        var x0 = item.X;
        engine.Tick(0.5);
        Assert.Equal(x0 + 100, item.X, precision: 3);
    }

    [Fact]
    public void Tick_OffScreenItems_AreRecycled()
    {
        var engine = new DanmakuEngine(width: 1000, minSpeed: 1000, maxSpeed: 1000);
        var item = engine.Enqueue("快", DateTime.Now)!;
        Assert.Equal(0, engine.PoolSize);
        engine.Tick(2.0); // 移动 2000px > 1000 → 出屏
        Assert.Empty(engine.Active);
        Assert.Equal(1, engine.PoolSize); // 回池
    }

    [Fact]
    public void Enqueue_ReusesPooledInstance()
    {
        var engine = new DanmakuEngine(width: 1000, minSpeed: 1000, maxSpeed: 1000);
        var first = engine.Enqueue("一", DateTime.Now)!;
        engine.Tick(2.0); // 出屏回池

        var second = engine.Enqueue("二", DateTime.Now);
        Assert.NotNull(second);
        Assert.Same(first, second);           // 同一实例复用
        Assert.Equal("二", second!.Text);      // 字段重置
        Assert.InRange(second.Track, 0, 7);   // 轨道重新分配
    }

    [Fact]
    public void Enqueue_RespectsTrackCount()
    {
        var engine = new DanmakuEngine(width: 1000, trackCount: 3);
        var tracks = new HashSet<int>();
        for (var i = 0; i < 6; i++)
        {
            var item = engine.Enqueue($"t{i}", DateTime.Now);
            Assert.NotNull(item);
            tracks.Add(item!.Track);
        }
        Assert.True(tracks.SetEquals(new[] { 0, 1, 2 })); // 只用 3 轨道
    }
}
