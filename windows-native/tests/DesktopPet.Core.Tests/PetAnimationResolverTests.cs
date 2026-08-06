using DesktopPet.Core.Pets;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 动作解析器测试：UI 与运行时共用的回退语义 ——
/// 未配置默认、越界过滤、空列表回退、开关、模式、间隔钳制、绑定解析。
/// </summary>
public class PetAnimationResolverTests
{
    [Fact]
    public void ResolveDurationMs_DefaultsAndClamps()
    {
        Assert.Equal(2000, PetAnimationResolver.ResolveClickDurationMs(null));
        Assert.Equal(3000, PetAnimationResolver.ResolveCelebrateDurationMs(null));

        var raw = new PetAnimationSettings(
            IdleEnabled: true,
            IdleClips: [0],
            IdleMode: "random",
            IdleIntervalSeconds: 5,
            ClickDurationSeconds: 99,
            CelebrateDurationSeconds: -1,
            Bind: new Dictionary<string, int>());

        // 运行时解析直接钳制（不依赖 Normalize 先行）
        Assert.Equal(10_000, PetAnimationResolver.ResolveClickDurationMs(raw));
        Assert.Equal(1000, PetAnimationResolver.ResolveCelebrateDurationMs(raw));

        var n = PetAnimationResolver.Normalize(raw);
        Assert.Equal(10, n.ClickDurationSeconds);
        Assert.Equal(1, n.CelebrateDurationSeconds);
    }

    [Fact]
    public void ResolveIdle_NullActions_UsesAllClipsRandom5s()
    {
        var idle = PetAnimationResolver.ResolveIdle(null, 9);

        Assert.NotNull(idle);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], idle!.Clips);
        Assert.True(idle.Random);
        Assert.Equal(5000, idle.IntervalMs);
    }

    [Fact]
    public void ResolveIdle_Disabled_ReturnsNull()
    {
        var actions = PetAnimationResolver.Normalize(null) with { IdleEnabled = false };
        Assert.Null(PetAnimationResolver.ResolveIdle(actions, 9));
    }

    [Fact]
    public void ResolveIdle_FiltersOutOfRangeAndDuplicates()
    {
        var actions = PetAnimationResolver.Normalize(null) with
        {
            IdleClips = [0, 1, 1, 2, 99, -3],
            IdleMode = "sequential",
            IdleIntervalSeconds = 10,
        };

        var idle = PetAnimationResolver.ResolveIdle(actions, 9);

        Assert.Equal([0, 1, 2], idle!.Clips);
        Assert.False(idle.Random);
        Assert.Equal(10_000, idle.IntervalMs);
    }

    [Fact]
    public void ResolveIdle_EmptyClips_FallsBackToIdleBindRow()
    {
        var actions = PetAnimationResolver.Normalize(null) with
        {
            IdleClips = [],
            Bind = new Dictionary<string, int> { [PetActionTriggers.Idle] = 6 },
        };

        var idle = PetAnimationResolver.ResolveIdle(actions, 9);

        Assert.Equal([6], idle!.Clips);
    }

    [Fact]
    public void ResolveIdle_AllClipsInvalid_FallsBackToFirstClip()
    {
        var actions = PetAnimationResolver.Normalize(null) with { IdleClips = [99] };
        var idle = PetAnimationResolver.ResolveIdle(actions, 9);

        Assert.Single(idle!.Clips);
        Assert.Equal(0, idle.Clips[0]);
    }

    [Fact]
    public void ResolveIdle_ClampsInterval()
    {
        var actions = PetAnimationResolver.Normalize(null) with { IdleIntervalSeconds = 999 };
        Assert.Equal(60_000, PetAnimationResolver.ResolveIdle(actions, 9)!.IntervalMs);

        var low = PetAnimationResolver.Normalize(null) with { IdleIntervalSeconds = 0 };
        Assert.Equal(1000, PetAnimationResolver.ResolveIdle(low, 9)!.IntervalMs);
    }

    [Fact]
    public void ResolveIdle_NoClips_ReturnsNull()
    {
        Assert.Null(PetAnimationResolver.ResolveIdle(null, 0));
        Assert.Null(PetAnimationResolver.ResolveIdle(PetAnimationResolver.Normalize(null), 0));
    }

    [Fact]
    public void ResolveBind_NullActions_UsesDefaultSemanticRows()
    {
        Assert.Equal(0, PetAnimationResolver.ResolveBind(null, PetActionTriggers.Idle, 9));
        Assert.Equal(3, PetAnimationResolver.ResolveBind(null, PetActionTriggers.Click, 9));
        Assert.Equal(4, PetAnimationResolver.ResolveBind(null, PetActionTriggers.Celebrate, 9));
        Assert.Equal(1, PetAnimationResolver.ResolveBind(null, PetActionTriggers.RoamRight, 9));
        Assert.Equal(2, PetAnimationResolver.ResolveBind(null, PetActionTriggers.RoamLeft, 9));
        Assert.Null(PetAnimationResolver.ResolveBind(null, PetActionTriggers.Drag, 9)); // 拖拽默认无绑定
    }

    [Fact]
    public void ResolveBind_DefaultRowOutOfRange_ReturnsNull()
    {
        Assert.Null(PetAnimationResolver.ResolveBind(null, PetActionTriggers.Celebrate, 3));
        Assert.Null(PetAnimationResolver.ResolveBind(null, PetActionTriggers.RoamRight, 1));
    }

    [Fact]
    public void ResolveBind_ConfiguredRow_RespectsClipCount()
    {
        var actions = PetAnimationResolver.Normalize(null) with
        {
            Bind = new Dictionary<string, int>
            {
                [PetActionTriggers.Click] = 5,
                [PetActionTriggers.Drag] = 7,
                [PetActionTriggers.Celebrate] = 99,
            },
        };

        Assert.Equal(5, PetAnimationResolver.ResolveBind(actions, PetActionTriggers.Click, 9));
        Assert.Equal(7, PetAnimationResolver.ResolveBind(actions, PetActionTriggers.Drag, 9));
        Assert.Null(PetAnimationResolver.ResolveBind(actions, PetActionTriggers.Celebrate, 9)); // 越界
        Assert.Null(PetAnimationResolver.ResolveBind(actions, PetActionTriggers.Idle, 9));     // 未配置
    }

    [Fact]
    public void Normalize_SanitizesNegativeClipsAndMode()
    {
        var raw = new PetAnimationSettings(
            IdleEnabled: true,
            IdleClips: [0, -2, 1, 1],
            IdleMode: "shuffle",
            IdleIntervalSeconds: -10,
            ClickDurationSeconds: 99,
            CelebrateDurationSeconds: -5,
            Bind: new Dictionary<string, int> { [PetActionTriggers.Click] = -1, [PetActionTriggers.Drag] = 4 });

        var n = PetAnimationResolver.Normalize(raw);

        Assert.Equal([0, 1], n.IdleClips);
        Assert.Equal("random", n.IdleMode);
        Assert.Equal(1, n.IdleIntervalSeconds);
        Assert.Equal(10, n.ClickDurationSeconds);
        Assert.Equal(1, n.CelebrateDurationSeconds);
        Assert.False(n.Bind.ContainsKey(PetActionTriggers.Click));
        Assert.Equal(4, n.Bind[PetActionTriggers.Drag]);
    }

    [Fact]
    public void Normalize_Null_GivesDefaults()
    {
        var n = PetAnimationResolver.Normalize(null);

        Assert.True(n.IdleEnabled);
        Assert.Empty(n.IdleClips);
        Assert.Equal("random", n.IdleMode);
        Assert.Equal(5, n.IdleIntervalSeconds);
        Assert.Empty(n.Bind);
    }
}
