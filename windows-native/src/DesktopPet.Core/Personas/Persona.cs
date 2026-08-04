namespace DesktopPet.Core.Personas;

/// <summary>
/// 人格条目（docs/ai-personas.md §2）。内置不可删、不可直接覆盖；
/// 编辑内置人格时复制为 <see cref="Builtin"/> = false 的自定义条目。
/// </summary>
public sealed record Persona(
    string Id,
    string Name,
    string Description,
    string Prompt,
    bool Builtin);
