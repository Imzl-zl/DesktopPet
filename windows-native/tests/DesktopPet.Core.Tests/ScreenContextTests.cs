using DesktopPet.Core.Ai;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5b：屏幕上下文——帧哈希变化检测 + 节流 + 事件队列。
/// 迁移计划 §6.4：缩略图（320×180）灰度哈希，变化超过阈值才送模型；
/// API 成本护栏：默认限频（最多 1 次/5s）。
/// </summary>
public class ScreenContextTests
{
    // ---- FrameHasher：灰度 dHash ----

    private static byte[] SolidGray(byte v, int w = 32, int h = 32)
        => Enumerable.Repeat(v, w * h).ToArray();

    [Fact]
    public void FrameHasher_SolidImage_HashesToZero()
    {
        Assert.Equal(0ul, FrameHasher.HashGrayscale(SolidGray(128), 32, 32));
    }

    [Fact]
    public void FrameHasher_SameImage_SameHash()
    {
        var img = SolidGray(100);
        img[0] = 200; img[1] = 30;
        Assert.Equal(FrameHasher.HashGrayscale(img, 32, 32), FrameHasher.HashGrayscale(img, 32, 32));
    }

    [Fact]
    public void FrameHasher_DifferentImages_DifferentHashes()
    {
        // 水平渐变 vs 垂直渐变：dHash 水平差分模式应显著不同
        var w = 16; var h = 16;
        var horizontal = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                horizontal[y * w + x] = (byte)(x * 255 / (w - 1));
        var vertical = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                vertical[y * w + x] = (byte)(y * 255 / (h - 1));

        var a = FrameHasher.HashGrayscale(horizontal, 16, 16);
        var b = FrameHasher.HashGrayscale(vertical, 16, 16);
        Assert.NotEqual(a, b);
        Assert.True(FrameHasher.HammingDistance(a, b) >= 8);
    }

    [Fact]
    public void FrameHasher_HammingDistance_CountsSetBits()
    {
        Assert.Equal(0, FrameHasher.HammingDistance(0ul, 0ul));
        Assert.Equal(4, FrameHasher.HammingDistance(0x0Ful, 0x00ul));
        Assert.Equal(1, FrameHasher.HammingDistance(0x80ul, 0x00ul));
        // 对称性
        Assert.Equal(FrameHasher.HammingDistance(0xABul, 0xCDul),
            FrameHasher.HammingDistance(0xCDul, 0xABul));
    }

    [Fact]
    public void FrameHasher_SinglePixelChange_ProducesSmallButNonZeroDistance()
    {
        // 1 像素变化 → 哈希差异很小（≤ 2 bits），但图像本身已可被"不同"识别
        var img = SolidGray(128);
        var hash1 = FrameHasher.HashGrayscale(img, 32, 32);
        img[0] = 255;
        var hash2 = FrameHasher.HashGrayscale(img, 32, 32);
        Assert.True(FrameHasher.HammingDistance(hash1, hash2) <= 2);
    }

    [Fact]
    public void FrameHasher_ThrowsOnTinyImage()
    {
        // dHash 需要至少 9×9（8×8 差分窗）
        Assert.Throws<ArgumentException>(() => FrameHasher.HashGrayscale(SolidGray(0, 4, 4), 4, 4));
    }

    // ---- ChangeDetector：阈值 ----

    [Fact]
    public void ChangeDetector_IdenticalFrames_NoChange()
    {
        var detector = new ChangeDetector();
        var img = SolidGray(120);
        var h = FrameHasher.HashGrayscale(img, 32, 32);
        Assert.False(detector.HasChanged(h, h));
    }

    [Fact]
    public void ChangeDetector_SubtleNoise_BelowThreshold_NoChange()
    {
        // 默认阈值 5 bits：轻微噪声（≤5）不算变化
        var detector = new ChangeDetector(thresholdBits: 5);
        var img = SolidGray(128);
        var h1 = FrameHasher.HashGrayscale(img, 32, 32);
        img[3] = 250; img[7] = 10; img[11] = 200;
        var h2 = FrameHasher.HashGrayscale(img, 32, 32);
        Assert.True(FrameHasher.HammingDistance(h1, h2) <= 5);
        Assert.False(detector.HasChanged(h1, h2));
    }

    [Fact]
    public void ChangeDetector_BigLayoutChange_AboveThreshold_Changed()
    {
        var detector = new ChangeDetector(thresholdBits: 5);
        var w = 32; var h = 32;
        var imgA = SolidGray(100, w, h);
        var imgB = new byte[w * h];
        new Random(42).NextBytes(imgB); // 高频随机图案 → 差分位多

        var ha = FrameHasher.HashGrayscale(imgA, w, h);
        var hb = FrameHasher.HashGrayscale(imgB, w, h);
        Assert.True(FrameHasher.HammingDistance(ha, hb) > 5);
        Assert.True(detector.HasChanged(ha, hb));
    }

    // ---- 合成帧序列离线测试（迁移计划 §8：不依赖真实屏幕）----

    [Fact]
    public void RecordedFrameSequence_DetectsOnlyRealChanges()
    {
        // 模拟录制帧序列：静止 → 布局大改 → 静止 → 布局上加轻微噪声 → 切回原画面
        var w = 16; var h = 16;
        var frame1 = SolidGray(128, w, h);
        var layoutChange = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                layoutChange[y * w + x] = (byte)(x < w / 2 ? 0 : 255);
        // 在 layout 画面上加 2 像素噪声（(0,9) 落在 9×8 采样网格内 → ≤2 bits）
        var noise = (byte[])layoutChange.Clone();
        noise[5] = 240; noise[9] = 20;

        var detector = new ChangeDetector(thresholdBits: 5);
        var events = new List<bool>();
        var prev = FrameHasher.HashGrayscale(frame1, w, h);

        var h2 = FrameHasher.HashGrayscale(layoutChange, w, h);
        events.Add(detector.HasChanged(prev, h2));   // 布局大改 → true
        prev = h2;

        events.Add(detector.HasChanged(prev, prev)); // 静止 → false

        var h3 = FrameHasher.HashGrayscale(noise, w, h);
        events.Add(detector.HasChanged(prev, h3));   // 轻微噪声 → false
        prev = h3;

        events.Add(detector.HasChanged(prev, FrameHasher.HashGrayscale(frame1, w, h))); // 切回原画面 → true
        Assert.Equal([true, false, false, true], events);
    }

    // ---- AnalysisThrottle：限频 ≥5s ----

    [Fact]
    public void AnalysisThrottle_FirstCallAllowed()
    {
        var throttle = new AnalysisThrottle(TimeSpan.FromSeconds(5));
        Assert.True(throttle.TryTake(new DateTime(2026, 8, 5, 10, 0, 0)));
    }

    [Fact]
    public void AnalysisThrottle_BlocksCallsWithinInterval()
    {
        var throttle = new AnalysisThrottle(TimeSpan.FromSeconds(5));
        var t0 = new DateTime(2026, 8, 5, 10, 0, 0);
        Assert.True(throttle.TryTake(t0));
        Assert.False(throttle.TryTake(t0.AddSeconds(4.9)));
        // 边界：满 5s 即放行（限频语义 = 最多 1 次/5s）
        Assert.True(throttle.TryTake(t0.AddSeconds(5)));
    }

    [Fact]
    public void AnalysisThrottle_AllowsAfterIntervalElapsed()
    {
        var throttle = new AnalysisThrottle(TimeSpan.FromSeconds(5));
        var t0 = new DateTime(2026, 8, 5, 10, 0, 0);
        Assert.True(throttle.TryTake(t0));
        Assert.True(throttle.TryTake(t0.AddSeconds(5.1)));
    }

    // ---- ScreenEventLog：最近 N 条事件 ----

    [Fact]
    public void ScreenEventLog_KeepsOnlyLatestCapacity()
    {
        var log = new ScreenEventLog(capacity: 3);
        var t = new DateTime(2026, 8, 5, 10, 0, 0);
        log.Add(new ScreenEvent(t, ScreenEventKind.Coding, "写代码"));
        log.Add(new ScreenEvent(t.AddSeconds(1), ScreenEventKind.Idle, "离开"));
        log.Add(new ScreenEvent(t.AddSeconds(2), ScreenEventKind.Browsing, "浏览"));
        log.Add(new ScreenEvent(t.AddSeconds(3), ScreenEventKind.Video, "看视频"));

        var recent = log.Recent();
        Assert.Equal(3, recent.Count);
        Assert.Equal("看视频", recent[^1].Summary);  // 最新在末尾
        Assert.Equal("离开", recent[0].Summary);     // 最旧被挤出
    }

    [Fact]
    public void ScreenEventLog_EmptyByDefault()
    {
        Assert.Empty(new ScreenEventLog().Recent());
    }
}
