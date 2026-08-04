using DesktopPet.Core.Personas;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5a：人格系统（对齐 docs/ai-personas.md）。
/// 验收锚点：7 种内置人格切换后回复风格明显区分（每人格用固定测试句验证）。
/// </summary>
public class PersonaTests
{
    // ---- 内置人格库 ----

    [Fact]
    public void BuiltinPersonas_ExposesExactlySeven()
    {
        var all = BuiltinPersonas.GetAll();
        Assert.Equal(7, all.Count);
    }

    [Fact]
    public void BuiltinPersonas_HasExpectedIds()
    {
        var ids = BuiltinPersonas.GetAll().Select(p => p.Id).OrderBy(x => x).ToArray();
        Assert.Equal(
            ["big-sis", "cold-goddess", "cold-idol", "green-tea", "puppy", "warm-guy", "wolf-cub"],
            ids);
    }

    [Fact]
    public void BuiltinPersonas_AllMarkedBuiltinWithNonEmptyPrompts()
    {
        foreach (var p in BuiltinPersonas.GetAll())
        {
            Assert.True(p.Builtin, $"{p.Id} should be builtin");
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"{p.Id} needs a name");
            Assert.False(string.IsNullOrWhiteSpace(p.Prompt), $"{p.Id} needs a prompt");
        }
    }

    [Fact]
    public void BuiltinPersonas_PromptsArePairwiseDistinct()
    {
        var prompts = BuiltinPersonas.GetAll().Select(p => p.Prompt).ToArray();
        for (var i = 0; i < prompts.Length; i++)
        {
            for (var j = i + 1; j < prompts.Length; j++)
            {
                Assert.NotEqual(prompts[i], prompts[j]);
            }
        }
    }

    /// <summary>验收锚点：用固定测试句验证 7 人格提示词风格可区分
    /// （每人格的 system prompt 含各自身份/称呼特征，拼接后互不相同）。</summary>
    [Fact]
    public void FixedTestSentence_YieldsDistinctSystemPromptsPerPersona()
    {
        const string testSentence = "我下班了";
        var built = BuiltinPersonas.GetAll()
            .ToDictionary(p => p.Id, p => PersonaEngine.BuildSystemPrompt(p) + "\n用户说：" + testSentence);

        // 7 个人格拼接结果两两不同
        var values = built.Values.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = i + 1; j < values.Length; j++)
            {
                Assert.NotEqual(values[i], values[j]);
            }
        }

        // 每个内置人格包含其独特称呼/口头禅特征（docs/ai-personas.md §3）
        Assert.Contains("宝贝", built["warm-guy"]);
        Assert.Contains("惜字如金", built["cold-idol"]);
        Assert.Contains("宣示主权", built["wolf-cub"]);
        Assert.Contains("姐姐", built["puppy"]);
        Assert.Contains("看心情", built["cold-goddess"]);
        Assert.Contains("哥哥", built["green-tea"]);
        Assert.Contains("小笨蛋", built["big-sis"]);
    }

    // ---- Prompt 拼接 ----

    [Fact]
    public void BuildSystemPrompt_StartsWithBasePrompt()
    {
        var persona = BuiltinPersonas.GetAll()[0];
        var built = PersonaEngine.BuildSystemPrompt(persona);
        Assert.StartsWith(PersonaEngine.BasePrompt, built);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsPersonaPromptAfterBase()
    {
        var persona = BuiltinPersonas.GetAll()[0];
        var built = PersonaEngine.BuildSystemPrompt(persona);
        Assert.Contains(persona.Prompt, built);
        // 拼接顺序：Base 在前、人格在后
        Assert.True(built.IndexOf(PersonaEngine.BasePrompt, StringComparison.Ordinal)
            < built.IndexOf(persona.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void BasePrompt_ContainsSceneConstraints()
    {
        Assert.Contains("不超过 50 字", PersonaEngine.BasePrompt);
        Assert.Contains("人格", PersonaEngine.BasePrompt);
    }

    [Fact]
    public void SamplingParameters_MatchAiPersonasDoc()
    {
        Assert.Equal(0.7, PersonaEngine.Temperature);
        Assert.Equal(120, PersonaEngine.MaxTokens);
    }

    // ---- personas.json 存储模型（只存 selectedId + 自定义）----

    [Fact]
    public void PersonasFile_DefaultsToWarmGuy()
    {
        var file = new PersonasFileModel();
        Assert.Equal("warm-guy", file.SelectedId);
        Assert.Empty(file.CustomPersonas);
        Assert.Equal(1, file.Version);
    }

    [Fact]
    public void PersonasFile_Normalize_FixesSelectedIdWhenUnknown()
    {
        var file = new PersonasFileModel { SelectedId = "no-such-persona", CustomPersonas = [] };
        var normalized = PersonasFileModel.Normalize(file);
        Assert.Equal("warm-guy", normalized.SelectedId);
    }

    [Fact]
    public void PersonasFile_Normalize_KeepsKnownCustomSelection()
    {
        var file = new PersonasFileModel
        {
            SelectedId = "custom-1",
            CustomPersonas = [new Persona("custom-1", "我的", "测试", "你是我的专属宠物。", Builtin: false)],
        };
        var normalized = PersonasFileModel.Normalize(file);
        Assert.Equal("custom-1", normalized.SelectedId);
    }

    [Fact]
    public void PersonasFile_MergeWithBuiltins_ReturnsBuiltinsPlusCustoms()
    {
        var file = new PersonasFileModel
        {
            SelectedId = "warm-guy",
            CustomPersonas = [new Persona("custom-1", "我的", "测试", "你是我的专属宠物。", Builtin: false)],
        };
        var merged = file.MergeWithBuiltins();
        Assert.Equal(8, merged.Count);
        Assert.Equal(7, merged.Count(p => p.Builtin));
        Assert.Single(merged.Where(p => p.Id == "custom-1" && !p.Builtin));
    }

    [Fact]
    public void PersonasFile_EditingBuiltin_CopiesToCustomEntry()
    {
        // 内置人格不可直接覆盖：编辑生成 builtin:false 副本（ai-personas.md §6）
        var builtin = BuiltinPersonas.GetById("warm-guy")!;
        var copy = PersonasFileModel.EditBuiltinAsCustom(builtin, newPrompt: "你是暖男，但更活泼。");
        Assert.False(copy.Builtin);
        Assert.NotEqual(builtin.Id, copy.Id);
        Assert.StartsWith("custom-", copy.Id);
        Assert.Equal("暖男", copy.Name);           // 名称保留
        Assert.Equal("你是暖男，但更活泼。", copy.Prompt);
    }

    [Fact]
    public void PersonasFile_Resolve_ReturnsBuiltinByDefault()
    {
        var file = new PersonasFileModel();
        var resolved = file.ResolveSelected();
        Assert.Equal("warm-guy", resolved.Id);
        Assert.True(resolved.Builtin);
    }

    [Fact]
    public void PersonasFile_Resolve_ReturnsCustomWhenSelected()
    {
        var custom = new Persona("custom-9", "我的", "测试", "你是我的专属宠物。", Builtin: false);
        var file = new PersonasFileModel { SelectedId = "custom-9", CustomPersonas = [custom] };
        Assert.Equal("custom-9", file.ResolveSelected().Id);
    }

    [Fact]
    public void PersonasFile_Resolve_FallsBackToWarmGuyForUnknownId()
    {
        var file = new PersonasFileModel { SelectedId = "ghost", CustomPersonas = [] };
        Assert.Equal("warm-guy", file.ResolveSelected().Id);
    }

    [Fact]
    public void PersonasFile_Normalize_DropsInvalidCustomEntries()
    {
        var file = new PersonasFileModel
        {
            SelectedId = "warm-guy",
            CustomPersonas =
            [
                new Persona("custom-ok", "好的", "", "有 prompt", Builtin: false),
                new Persona("", "无 id", "", "坏条目", Builtin: false),
                new Persona("custom-empty", "", "", "", Builtin: false),
            ],
        };
        var normalized = PersonasFileModel.Normalize(file);
        Assert.Single(normalized.CustomPersonas);
        Assert.Equal("custom-ok", normalized.CustomPersonas[0].Id);
    }

    // ---- 自定义人格约束 ----

    [Fact]
    public void CustomPersona_StillBoundedByBasePrompt()
    {
        // 自定义人格同样受 Base Prompt 约束（ai-personas.md §4：拼接顺序固定 Base 在前）
        var custom = new Persona("custom-x", "话痨", "", "你可以回复很长很长。", Builtin: false);
        var built = PersonaEngine.BuildSystemPrompt(custom);
        Assert.StartsWith(PersonaEngine.BasePrompt, built);
        // Base 的 50 字约束在人格 prompt 之前，覆盖"回复可以很长"这类指令
        Assert.True(built.IndexOf("不超过 50 字", StringComparison.Ordinal)
            < built.IndexOf("回复很长", StringComparison.Ordinal));
    }
}
