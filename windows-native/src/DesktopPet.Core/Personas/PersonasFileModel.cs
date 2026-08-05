namespace DesktopPet.Core.Personas;

/// <summary>
/// personas.json 存储模型（docs/ai-personas.md §2）：
/// 只存 version / selectedId / 自定义人格；内置人格硬编码在程序里，永不落盘。
/// 归一化与解析在 Core（可单测），持久化由 IJsonStore 负责。
/// </summary>
public sealed class PersonasFileModel
{
    public int Version { get; set; } = 1;

    public string SelectedId { get; set; } = "warm-guy";

    public List<Persona> CustomPersonas { get; set; } = [];

    /// <summary>
    /// 归一化：丢弃无效自定义条目（id/name/prompt 空白）；
    /// selectedId 未知时回退内置默认（暖男）。示例对话去空行保留。
    /// </summary>
    public static PersonasFileModel Normalize(PersonasFileModel raw)
    {
        var customs = (raw.CustomPersonas ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Id)
                        && !string.IsNullOrWhiteSpace(p.Name)
                        && !string.IsNullOrWhiteSpace(p.Prompt))
            .Select(p => p with
            {
                ExampleDialogs = (p.ExampleDialogs ?? [])
                    .Select(d => d.Trim())
                    .Where(d => d.Length > 0)
                    .Take(10)   // 上限 10 段，防滥用
                    .ToArray(),
            })
            .ToList();

        var selected = raw.SelectedId;
        var known = BuiltinPersonas.GetById(selected) is not null
            || customs.Any(p => p.Id == selected);
        if (!known) selected = "warm-guy";

        return new PersonasFileModel
        {
            Version = 1,
            SelectedId = selected,
            CustomPersonas = customs,
        };
    }

    /// <summary>完整人格列表：内置（前） + 自定义（后）。</summary>
    public IReadOnlyList<Persona> MergeWithBuiltins()
    {
        var list = new List<Persona>(BuiltinPersonas.GetAll());
        list.AddRange(CustomPersonas);
        return list;
    }

    /// <summary>复制并切换选中人格（class 不可用 with，提供显式复制）。</summary>
    public PersonasFileModel Select(string personaId)
        => new()
        {
            Version = Version,
            SelectedId = personaId,
            CustomPersonas = new List<Persona>(CustomPersonas),
        };

    /// <summary>解析当前选中人格：内置 / 自定义；未知 id 回退暖男。</summary>
    public Persona ResolveSelected()
    {
        var builtin = BuiltinPersonas.GetById(SelectedId);
        if (builtin is not null) return builtin;
        var custom = CustomPersonas.FirstOrDefault(p => p.Id == SelectedId);
        if (custom is not null) return custom;
        return BuiltinPersonas.GetById("warm-guy")!;
    }

    /// <summary>编辑内置人格：复制为自定义条目（builtin:false，新 id，保留名称/描述）。</summary>
    public static Persona EditBuiltinAsCustom(Persona builtin, string newPrompt)
        => new(
            Id: "custom-" + Guid.NewGuid().ToString("N")[..8],
            Name: builtin.Name,
            Description: builtin.Description,
            Prompt: newPrompt,
            Builtin: false);
}
