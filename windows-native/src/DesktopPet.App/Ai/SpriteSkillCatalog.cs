using DesktopPet.Core.SpriteSkill;

namespace DesktopPet.App.Ai;

/// <summary>技能目录（P1 先内嵌"动作精灵图"；后续可扩展为资源文件/多技能）。</summary>
public static class SpriteSkillCatalog
{
    public static readonly SpriteSkillDefinition SpritePet = new(
        Id: "sprite-pet",
        Name: "动作精灵图",
        Description: "把宠物照片/描述生成自定义动作精灵图，可直接导入桌宠绑定播放",
        SystemPrompt: """
        你是桌宠动画精灵图生成助手。用户会描述想要的自定义动作，你把它转换成结构化"动作计划"JSON。
        只输出 JSON，不要输出任何其他文字。

        动作计划 JSON 格式：
        {
          "identityDescription": "一句话宠物身份描述（长相/花纹/配色/道具），如：橘猫，圆脸，白手套，蓝眼睛",
          "actions": [
            {
              "id": "动作英文标识，如 idle / jump / dance",
              "frameCount": 帧数（3 到 8 的整数）,
              "durations": [每帧毫秒数数组，长度=frameCount，可省略],
              "loop": true 或 false,
              "rowPrompt": "该行动画的完整生图提示词"
            }
          ]
        }

        rowPrompt 编写规则（关键，直接影响生图成功）：
        1. 必须用英文，简短（不超过 50 词），降低图像模型负担（复杂中文 prompt 易触发生图超时）。
        2. 格式示例："a cute orange cat, 3 separate idle poses of the same cat side by side in one row with clear gaps, each pose in its own cell, flat simple cartoon style, pure green background #00FF00, no shadows no text"
        3. 必须包含：同一只宠物的 N 个分离姿势横向排列、每格一姿势、纯绿背景 #00FF00、无阴影无文字、简单风格。
        4. 不要长句、不要中英混杂、不要多余修饰词。

        其他规则：
        - 动作内容完全由用户描述决定；用户没指定帧数时默认 3 帧（3 帧最稳定）。
        - 用户的参考图描述（如有）优先用于身份锁定。
        """);
}
