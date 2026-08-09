using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Rendering;

/// <summary>预裁剪帧：RGBA 像素 + alpha 掩码（hitTest O(1)），切片时一次性生成。</summary>
/// <param name="RectX">帧在源图中的 x（网格线绘制用）。</param>
/// <param name="RectY">帧在源图中的 y。</param>
/// <param name="ContentTop">帧内第一个不透明像素行（px）；气泡按实际可见头顶定位，修正帧内透明边。</param>
public sealed record SpriteFrame(byte[] Rgba, byte[] Mask, int Width, int Height, int RectX = 0, int RectY = 0, int ContentTop = 0);

/// <summary>idle 播放列表配置（对齐 pet.ts 的 ap_idle_clips / ap_idle_interval / ap_idle_mode）。</summary>
public sealed record IdlePlaylistOptions(IReadOnlyList<int> Clips, double IntervalMs, bool Random);

/// <summary>
/// 精灵渲染器：1:1 移植 windows/src/pet.ts 的 Pet 类绘制语义 —— 动画行
/// （overrideRow ?? idleRow ?? moodRow）、行内帧循环、每行最宽帧缩放
/// （per-CLIP 缩放，帧间不抖动）、anchored bottom-center 整数缩放、headroom
/// 与 spriteRect（气泡定位用）。渲染循环由窗口驱动（DispatcherTimer），
/// 帧绘制为 WriteableBitmap 直写。
/// </summary>
public sealed class PetRenderer
{
    private readonly SpriteSheet _sheet;
    private IdlePlaylistOptions? _idle;   // 可变：完整替换（设置页即时生效）
    private int _row;                 // 情绪行（setState）
    private int? _overrideRow;        // 临时动作覆盖行（owner + 优先级仲裁）
    private string? _overrideOwner;
    private int _overridePriority;
    private int? _idleRow;            // idle playlist 当前行
    private int _idleIndex;
    private int _frame;
    private double _fps = 3;
    private int _lastDrawnRow = -1;

    /// <summary>精灵缩放百分比（设置页 70-130%，1.0 = 填满缓冲）。</summary>
    private double _sizePercent = 1.0;
    /// <summary>待机浮动（bob）：独立相位计数器，行切换不跳变。</summary>
    private bool _bobEnabled;
    private int _bobPhase;

    /// <summary>设置精灵尺寸百分比（0.7-1.3，越界钳制）。</summary>
    public void SetSizePercent(double percent) => _sizePercent = Math.Clamp(percent, 0.7, 1.3);

    /// <summary>开关待机上下浮动（设置页 ap_fx）。</summary>
    public void SetBob(bool enabled) => _bobEnabled = enabled;

    /// <summary>动作覆盖优先级（高者持有；拖拽 > 庆祝 > 点击 > 漫游 > 待机）。</summary>
    public const int PriorityRoam = 2;
    public const int PriorityClick = 3;
    public const int PriorityDrag = 4;

    public int ClipCount => _sheet.Clips.Count;

    /// <summary>当前播放列表的轮播间隔（毫秒）；无播放列表 → null（窗口计时的唯一来源）。</summary>
    public double? IdleIntervalMs => _idle?.IntervalMs;

    /// <summary>idle 播放列表激活中（窗口驱动轮播节奏时用）。</summary>
    public bool IsIdleCycling => _idle is { Clips.Count: > 0 } && _idleRow is not null;

    /// <summary>
    /// 完整替换 idle 播放列表（设置页即时生效，无需重建渲染器）：
    /// null/空列表 → 关闭轮播并回 idle 行首帧；从关闭启用 → 立即开始；
    /// clip 集变化 → 校验/去重后重置索引（当前行失效时回第一项）。
    /// </summary>
    public void SetIdlePlaylist(IdlePlaylistOptions? options)
    {
        var clips = options?.Clips
            .Where(clip => clip >= 0 && clip < _sheet.Clips.Count)
            .Distinct()
            .ToList();
        if (clips is not { Count: > 0 })
        {
            var wasActive = _idle is not null;
            _idle = null;
            StopIdleCycling();
            if (wasActive) _frame = 0;
            return;
        }

        _idle = options! with { Clips = clips };
        if (_idleRow is null || !clips.Contains(_idleRow.Value))
        {
            _idleIndex = 0;
            _idleRow = clips[0];
            _frame = 0;
        }
    }

    public double Fps => _fps;
    public int ActiveRow => _overrideRow ?? _idleRow ?? _row;

    /// <summary>
    /// 精灵上方留白比例（气泡锚点）：窗口顶到实际可见头顶的距离占缓冲比例。
    /// 按帧内不透明内容顶部（ContentTopInset）计算，不同宠物帧内透明边不同时
    /// 气泡仍贴实际头顶，不会压头/远离。
    /// </summary>
    public double Headroom { get; private set; }

    /// <summary>帧内不透明内容顶部偏移（物理像素，已含缩放）；气泡锚点用。</summary>
    public int ContentTopInset { get; private set; }

    /// <summary>最近绘制的精灵矩形（buffer 像素）。</summary>
    public (int X, int Y, int W, int H) SpriteRect { get; private set; }

    private double _lastScale = 1;

    public PetRenderer(SpriteSheet sheet, IdlePlaylistOptions? idleOptions = null)
    {
        _sheet = sheet;
        _idle = idleOptions;
    }

    /// <summary>切到状态行 + 帧率；idle 时进入播放列表（对齐 setState）。</summary>
    public void SetState(string state, int? boundRow = null)
    {
        _fps = StateMapping.FpsFor(state);
        var row = StateMapping.RowFor(state, boundRow);
        if (row != _row) _row = row;

        if (state == "idle") StartIdleCycling();
        else StopIdleCycling();
    }

    /// <summary>漫游行覆盖（无 owner 版本 = 最高优先级，兼容现有调用/测试）。</summary>
    public void SetRow(int row) => SetRow(row, owner: null, priority: int.MaxValue);

    /// <summary>
    /// 带 owner 的动作覆盖：低优先级无法覆盖高优先级持有者；
    /// 清除只允许同一 owner（避免点击/拖拽/漫游互相误清）。
    /// </summary>
    public void SetRow(int row, string? owner, int priority)
    {
        if (_overrideOwner is not null && owner is not null && priority < _overridePriority) return;
        if (_overrideRow != row || _overrideOwner != owner)
        {
            _overrideRow = row;
            _overrideOwner = owner;
            _overridePriority = priority;
            _frame = 0;
        }
    }

    public void ClearRow() => ClearRow(owner: null);

    /// <summary>按 owner 清除覆盖；owner 为 null 时清除任意持有者（兼容旧语义）。</summary>
    public void ClearRow(string? owner)
    {
        if (owner is not null && _overrideOwner is not null && _overrideOwner != owner) return;
        if (_overrideRow is not null)
        {
            _overrideRow = null;
            _overrideOwner = null;
            _overridePriority = 0;
            _frame = 0;
        }
    }

    public void AdvanceFrame()
    {
        _frame++;
        _bobPhase++;
    }

    /// <summary>推进 idle playlist（对齐 advanceIdleClip）；无 playlist 时不动。</summary>
    public void AdvanceIdleClip()
    {
        if (_idle is null || _idle.Clips.Count == 0) return;
        _idleIndex = _idle.Random
            ? Random.Shared.Next(_idle.Clips.Count)
            : (_idleIndex + 1) % _idle.Clips.Count;
        _idleRow = _idle.Clips[_idleIndex];
        _frame = 0;
    }

    private void StartIdleCycling()
    {
        if (_idle is null || _idle.Clips.Count == 0) { StopIdleCycling(); return; }
        if (_idleRow is not null && _idle.Clips.Contains(_idleRow.Value)) return; // 已在播放
        _idleIndex = 0;
        _idleRow = _idle.Clips[0];
        _frame = 0;
    }

    private void StopIdleCycling()
    {
        _idleRow = null;
        _idleIndex = 0;
    }

    /// <summary>当前激活帧（clip 内取模，对齐 currentClip + frame % clip.length）。</summary>
    public SpriteFrame? CurrentFrame()
    {
        if (_sheet.IsEmpty) return null;
        var row = Math.Min(ActiveRow, _sheet.Clips.Count - 1);
        var clip = _sheet.Clips[row];
        return clip.Count > 0 ? clip[_frame % clip.Count] : null;
    }

    /// <summary>
    /// Calculates the current frame placement without writing a pixel buffer. Presentation
    /// layers use this to preserve sprite bounds, bubble anchors, and alpha hit testing.
    /// </summary>
    public SpriteFrame? PrepareFrame(int bufferWidth, int bufferHeight)
    {
        var activeRow = ActiveRow;
        if (activeRow != _lastDrawnRow)
        {
            _lastDrawnRow = activeRow;
            _frame = 0;
        }

        var frame = CurrentFrame();
        if (frame is null) return null;

        var rowIndex = Math.Min(activeRow, _sheet.Clips.Count - 1);
        var scaleWidth = _sheet.ClipMaxWidths[rowIndex] > 0 ? _sheet.ClipMaxWidths[rowIndex] : frame.Width;
        // 浮点最近邻缩放：整数化（floor）会把 1.0~1.99 的放大吞成 1，
        // 导致尺寸设置 100%→130% 对贴近缓冲的精灵完全无效（260x320 缓冲下的常见场景）。
        var scale = Math.Min((double)bufferWidth / scaleWidth, (double)bufferHeight / frame.Height) * _sizePercent;
        _lastScale = scale;
        var width = (int)(frame.Width * scale);
        var height = (int)(frame.Height * scale);
        // 待机浮动：以地面为轴向上 0-6px 正弦（不穿底），相位独立于行切换
        var bobY = _bobEnabled ? (int)((Math.Sin(_bobPhase * 0.35) * 0.5 + 0.5) * -6) : 0;
        // 帧内透明边修正：气泡锚定实际可见头顶，而不是帧矩形顶（切片保留帧内透明边）
        var contentTop = _sheet.ClipContentTops is { Count: > 0 } tops ? tops[rowIndex] : 0;
        ContentTopInset = (int)(contentTop * scale);
        Headroom = (bufferHeight - height + ContentTopInset + bobY) / (double)bufferHeight;
        SpriteRect = ((bufferWidth - width) / 2, bufferHeight - height + bobY, width, height);
        return frame;
    }

    /// <summary>
    /// 绘制一帧到帧缓冲：anchored bottom-center + 浮点最近邻缩放；缩放来自该行最宽帧
    /// （per-CLIP，帧间同尺寸，气泡不跳动，对齐 pet.ts draw()）。行切换时帧号归零。
    /// 每个源像素覆盖目标区间 [floor(p*scale), floor((p+1)*scale))：任意分数缩放
    /// 无空洞无重叠（原“整数块 + (int) 步进”在非整数 scale 下会漏行漏列）。
    /// </summary>
    public void DrawFrame(byte[] buffer, int bufferWidth, int bufferHeight)
    {
        var frame = PrepareFrame(bufferWidth, bufferHeight);
        if (frame is null) return;

        var (dx, dy, _, _) = SpriteRect;
        var scale = _lastScale;
        for (var fy = 0; fy < frame.Height; fy++)
        {
            var dstY0 = dy + (int)(fy * scale);
            var dstY1 = dy + (int)((fy + 1) * scale);
            if (dstY1 <= 0 || dstY0 >= bufferHeight) continue; // 整行越界（负 Y 溢出/底部外）
            for (var fx = 0; fx < frame.Width; fx++)
            {
                var src = (fy * frame.Width + fx) * 4;
                if (frame.Rgba[src + 3] == 0) continue;
                var dstX0 = dx + (int)(fx * scale);
                var dstX1 = dx + (int)((fx + 1) * scale);
                if (dstX1 <= 0 || dstX0 >= bufferWidth) continue; // 整列越界
                for (var y = dstY0; y < dstY1; y++)
                {
                    if (y < 0 || y >= bufferHeight) continue;
                    for (var x = dstX0; x < dstX1; x++)
                    {
                        if (x < 0 || x >= bufferWidth) continue;
                        var dst = (y * bufferWidth + x) * 4;
                        buffer[dst] = frame.Rgba[src];
                        buffer[dst + 1] = frame.Rgba[src + 1];
                        buffer[dst + 2] = frame.Rgba[src + 2];
                        buffer[dst + 3] = frame.Rgba[src + 3];
                    }
                }
            }
        }
    }

    /// <summary>alpha hitTest（buffer 像素）：掩码查询 O(1)，对齐 pet.ts hitTest 语义。</summary>
    public bool HitTest(int bufferX, int bufferY)
    {
        var frame = CurrentFrame();
        if (frame is null) return false;
        var (x, y, w, h) = SpriteRect;
        if (bufferX < x || bufferX >= x + w || bufferY < y || bufferY >= y + h) return false;
        var scale = _lastScale;
        var fx = (int)((bufferX - x) / scale);
        var fy = (int)((bufferY - y) / scale);
        if (fx < 0 || fx >= frame.Width || fy < 0 || fy >= frame.Height) return false;
        return frame.Mask[fy * frame.Width + fx] == 1;
    }
}
