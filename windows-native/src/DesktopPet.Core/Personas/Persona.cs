namespace DesktopPet.Core.Personas;

/// <summary>
/// 人格条目（docs/ai-personas.md §2）。内置不可删、不可直接覆盖；
/// 编辑内置人格时复制为 <see cref="Builtin"/> = false 的自定义条目。
/// Phase 6e：ExampleDialogs 自定义示例对话（2-5 段，"示例 > 描述"，C.AI 经验）。
/// </summary>
public sealed record Persona(
    string Id,
    string Name,
    string Description,
    string Prompt,
    bool Builtin,
    string[]? ExampleDialogs = null);
