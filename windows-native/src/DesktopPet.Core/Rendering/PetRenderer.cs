using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Rendering;

/// <summary>预裁剪帧：RGBA 像素 + alpha 掩码（hitTest O(1)），切片时一次性生成。</summary>
/// <param name="RectX">帧在源图中的 x（网格线绘制用）。</param>
/// <param name="RectY">帧在源图中的 y。</param>
public sealed record SpriteFrame(byte[] Rgba, byte[] Mask, int Width, int Height, int RectX = 0, int RectY = 0);

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
    private readonly IdlePlaylistOptions? _idle;
    private int _row;                 // 情绪行（setState）
    private int? _overrideRow;        // 漫游/交互覆盖行（setRow/clearRow）
    private int? _idleRow;            // idle playlist 当前行
    private int _idleIndex;
    private int _frame;
    private double _fps = 3;
    private int _lastDrawnRow = -1;

    public double Fps => _fps;
    public int ActiveRow => _overrideRow ?? _idleRow ?? _row;

    /// <summary>精灵上方留白比例（气泡锚点，对齐 pet.ts headroom）。</summary>
    public double Headroom { get; private set; }

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

    /// <summary>漫游行覆盖（setRow/clearRow，优先级高于 mood 行但低于 idle playlist 之外）。</summary>
    public void SetRow(int row)
    {
        if (_overrideRow != row) { _overrideRow = row; _frame = 0; }
    }

    public void ClearRow()
    {
        if (_overrideRow is not null) { _overrideRow = null; _frame = 0; }
    }

    public void AdvanceFrame() => _frame++;

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
    /// 绘制一帧到帧缓冲：anchored bottom-center + 整数缩放；缩放来自该行最宽帧
    /// （per-CLIP，帧间同尺寸，气泡不跳动，对齐 pet.ts draw()）。行切换时帧号归零。
    /// </summary>
    public void DrawFrame(byte[] buffer, int bufferWidth, int bufferHeight)
    {
        var activeRow = ActiveRow;
        if (activeRow != _lastDrawnRow) { _lastDrawnRow = activeRow; _frame = 0; }

        var frame = CurrentFrame();
        if (frame is null) return;

        var rowIndex = Math.Min(activeRow, _sheet.Clips.Count - 1);
        var scaleW = _sheet.ClipMaxWidths[rowIndex] > 0 ? _sheet.ClipMaxWidths[rowIndex] : frame.Width;

        var fit = Math.Min((double)bufferWidth / scaleW, (double)bufferHeight / frame.Height);
        var scale = fit >= 1 ? Math.Floor(fit) : fit;
        _lastScale = scale;
        var dw = (int)(frame.Width * scale);
        var dh = (int)(frame.Height * scale);
        Headroom = (bufferHeight - dh) / (double)bufferHeight;
        var dx = (bufferWidth - dw) / 2;
        var dy = bufferHeight - dh;
        SpriteRect = (dx, dy, dw, dh);

        for (var fy = 0; fy < frame.Height; fy++)
        {
            for (var fx = 0; fx < frame.Width; fx++)
            {
                var src = (fy * frame.Width + fx) * 4;
                if (frame.Rgba[src + 3] == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                {
                    var y = dy + (int)(fy * scale) + sy;
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var dst = (y * bufferWidth + dx + (int)(fx * scale) + sx) * 4;
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
