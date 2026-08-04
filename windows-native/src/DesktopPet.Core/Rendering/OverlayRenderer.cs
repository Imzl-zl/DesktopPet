using DesktopPet.Core.Care;

namespace DesktopPet.Core.Rendering;

/// <summary>
/// 成长表现叠加层（迁移计划 §3.7）：与宠物渲染器同帧绘制到同一 buffer。
/// 阶段视觉：脚下光晕（mint/sky/gold）、轮廓辉光（掩码外扩染色）、头顶皇冠、
/// idle 星点粒子。像素纯函数，可单测。
/// </summary>
public static class OverlayRenderer
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> GlowColors = new()
    {
        ["mint"] = (0x4E, 0xCB, 0xA5),
        ["sky"] = (0x5B, 0x9D, 0xF0),
        ["gold"] = (0xFF, 0xD7, 0x00),
    };

    /// <summary>
    /// 在宠物帧上叠加阶段表现。
    /// </summary>
    /// <param name="buffer">RGBA 帧缓冲（已画精灵）。</param>
    /// <param name="frame">当前精灵帧（掩码定位用）。</param>
    /// <param name="appearance">阶段外观。</param>
    /// <param name="spriteX/spriteY/scale">精灵绘制位置与缩放（同 PetRenderer.SpriteRect）。</param>
    /// <param name="timeMs">时间（星点动画）。</param>
    public static void Apply(byte[] buffer, int bufferWidth, int bufferHeight, SpriteFrame frame,
        StageAppearance appearance, int spriteX, int spriteY, int scale, double timeMs)
    {
        if (appearance.GlowColor is null) return;

        var (r, g, b) = GlowColors[appearance.GlowColor];
        var dw = frame.Width * scale;
        var dh = frame.Height * scale;

        // 脚下光晕：精灵底部椭圆（Companion+）
        if (appearance.GlowUnder)
        {
            DrawGlowUnder(buffer, bufferWidth, bufferHeight, r, g, b,
                spriteX + dw / 2, spriteY + dh, dw / 2, timeMs);
        }

        // 轮廓辉光：掩码外扩染色（Scout+）
        if (appearance.GlowOutline)
        {
            DrawOutline(buffer, bufferWidth, bufferHeight, frame, spriteX, spriteY, scale, r, g, b);
        }

        // 皇冠：精灵头顶（Legend）
        if (appearance.Crown)
        {
            DrawCrown(buffer, bufferWidth, spriteX + dw / 2 - 4 * scale, spriteY - 6 * scale, scale, timeMs);
        }

        // idle 星点：偶飘 1-2 颗（Hero+）
        if (appearance.StarParticles)
        {
            DrawStarParticles(buffer, bufferWidth, bufferHeight, spriteX, spriteY, dw, dh, r, g, b, timeMs);
        }
    }

    private static void DrawGlowUnder(byte[] buffer, int w, int h, byte r, byte g, byte b,
        int centerX, int bottomY, int radiusX, double timeMs)
    {
        // 呼吸光晕：半径随 sin 轻微变化（对齐"柔和光晕"）
        var breathe = 1 + 0.08 * Math.Sin(timeMs / 800.0);
        var rx = (int)(radiusX * breathe);
        var ry = Math.Max(2, rx / 3);
        for (var y = bottomY - ry; y < bottomY; y++)
        {
            if (y < 0 || y >= h) continue;
            for (var x = centerX - rx; x < centerX + rx; x++)
            {
                if (x < 0 || x >= w) continue;
                var nx = (x - centerX) / (double)rx;
                var ny = (y - (bottomY - ry)) / (double)ry;
                if (nx * nx + ny * ny > 1) continue;
                var alpha = (byte)(70 * (1 - Math.Sqrt(nx * nx + ny * ny)) * 0.6);
                BlendPixel(buffer, w, x, y, r, g, b, alpha);
            }
        }
    }

    private static void DrawOutline(byte[] buffer, int w, int h, SpriteFrame frame,
        int spriteX, int spriteY, int scale, byte r, byte g, byte b)
    {
        // 精灵四条边外扩 1px 染色（轮廓辉光）
        var dw = frame.Width * scale;
        var dh = frame.Height * scale;

        // 左边缘（fx == 0）外扩到 spriteX-1
        for (var fy = 0; fy < frame.Height; fy++)
        {
            if (frame.Mask[fy * frame.Width] != 1) continue;
            for (var sy = 0; sy < scale; sy++)
            {
                var y = spriteY + fy * scale + sy;
                if (y < 0 || y >= h) continue;
                var x = spriteX - 1;
                if (x >= 0) BlendPixel(buffer, w, x, y, r, g, b, 90);
            }
        }
        // 右边缘外扩
        for (var fy = 0; fy < frame.Height; fy++)
        {
            if (frame.Mask[fy * frame.Width + frame.Width - 1] != 1) continue;
            for (var sy = 0; sy < scale; sy++)
            {
                var y = spriteY + fy * scale + sy;
                if (y < 0 || y >= h) continue;
                var x = spriteX + dw;
                if (x < w) BlendPixel(buffer, w, x, y, r, g, b, 90);
            }
        }
        // 顶边缘外扩
        for (var fx = 0; fx < frame.Width; fx++)
        {
            if (frame.Mask[fx] != 1) continue;
            for (var sx = 0; sx < scale; sx++)
            {
                var x = spriteX + fx * scale + sx;
                if (x < 0 || x >= w) continue;
                var y = spriteY - 1;
                if (y >= 0) BlendPixel(buffer, w, x, y, r, g, b, 90);
            }
        }
        // 底边缘外扩
        for (var fx = 0; fx < frame.Width; fx++)
        {
            if (frame.Mask[(frame.Height - 1) * frame.Width + fx] != 1) continue;
            for (var sx = 0; sx < scale; sx++)
            {
                var x = spriteX + fx * scale + sx;
                if (x < 0 || x >= w) continue;
                var y = spriteY + dh;
                if (y < h) BlendPixel(buffer, w, x, y, r, g, b, 90);
            }
        }
    }

    /// <summary>程序生成的像素皇冠（金色 + 宝石点）。</summary>
    private static void DrawCrown(byte[] buffer, int w, int crownCenterX, int crownTopY, int scale, double timeMs)
    {
        // 8x5 皇冠模板：1 = 金色，2 = 宝石（红）
        var template = new[,]
        {
            { 0, 1, 0, 1, 0, 1, 0, 0 },
            { 1, 0, 1, 0, 1, 0, 1, 1 },
            { 1, 1, 1, 1, 1, 1, 1, 1 },
            { 1, 2, 1, 2, 1, 2, 1, 1 },
            { 0, 1, 1, 1, 1, 1, 1, 0 },
        };
        var blink = (byte)(timeMs % 500 < 250 ? 200 : 120); // 宝石闪烁
        for (var ty = 0; ty < 5; ty++)
        {
            for (var tx = 0; tx < 8; tx++)
            {
                if (template[ty, tx] == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                {
                    var y = crownTopY + ty * scale + sy;
                    if (y < 0) continue;
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var x = crownCenterX + tx * scale + sx;
                        if (x < 0 || x >= w) continue;
                        var alpha = template[ty, tx] == 2 ? blink : (byte)230;
                        BlendPixel(buffer, w, x, y, 0xFF, 0xD7, 0x00, alpha);
                    }
                }
            }
        }
    }

    /// <summary>idle 星点：2 颗沿椭圆轨迹缓慢飘动 + 闪烁。</summary>
    private static void DrawStarParticles(byte[] buffer, int w, int h,
        int spriteX, int spriteY, int dw, int dh, byte r, byte g, byte b, double timeMs)
    {
        for (var i = 0; i < 2; i++)
        {
            var phase = timeMs / 3000.0 + i * Math.PI;
            var x = spriteX + dw / 2 + (int)(Math.Sin(phase) * dw * 0.35);
            var y = spriteY + (int)(Math.Abs(Math.Cos(phase)) * dh * 0.35);
            if (x < 0 || x >= w || y < 0 || y >= h) continue;
            var twinkle = (byte)(80 + 120 * Math.Abs(Math.Sin(timeMs / 300.0 + i)));
            BlendPixel(buffer, w, x, y, r, g, b, twinkle);
            if (x + 1 < w) BlendPixel(buffer, w, x + 1, y, r, g, b, (byte)(twinkle / 2));
        }
    }

    private static void BlendPixel(byte[] buffer, int w, int x, int y, byte r, byte g, byte b, byte alpha)
    {
        var off = (y * w + x) * 4;
        var dstA = buffer[off + 3];
        var srcA = alpha;
        // 简单 over 合成（前景半透明 → 背景）
        var outA = srcA + dstA * (255 - srcA) / 255;
        if (outA <= 0) return;
        buffer[off] = (byte)((r * srcA + buffer[off] * (255 - srcA)) / 255);
        buffer[off + 1] = (byte)((g * srcA + buffer[off + 1] * (255 - srcA)) / 255);
        buffer[off + 2] = (byte)((b * srcA + buffer[off + 2] * (255 - srcA)) / 255);
        buffer[off + 3] = (byte)outA;
    }
}
