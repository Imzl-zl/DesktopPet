namespace DesktopPet.Core.SpriteSkill;

/// <summary>一个动画动作 = 图集里的一行（对应桌宠的一个可绑定状态/交互）。</summary>
public sealed record ActionSpec(string Id, int FrameCount, IReadOnlyList<double>? Durations = null, bool Loop = true);

/// <summary>精灵图校验结果。</summary>
public sealed record SheetReport(bool Ok, IReadOnlyList<string> Issues)
{
    public static SheetReport Success() => new(true, []);
}

/// <summary>单帧画格尺寸（px）。</summary>
public sealed record CellSpec(int Width, int Height);

/// <summary>确定性流水线错误（帧数不匹配等，调用方据此触发重生成）。</summary>
public sealed class SpritePipelineException : Exception
{
    public SpritePipelineException(string message) : base(message) { }
}
