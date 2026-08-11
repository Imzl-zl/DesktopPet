namespace DesktopPet.Core.ImageGen;

/// <summary>总结图目标（连接 + 模型），由 SummaryImageTargetResolver 解析。</summary>
public sealed record SummaryImageTarget(ImageConnection Connection, string ModelId);

/// <summary>
/// 总结图目标解析（windows-imagegen-design.md §8）：从连接列表 + 用户引用解析出
/// (连接, 模型)。引用格式 "{connectionId}/{modelId}"；空或失效一律回退首连接首模型。
/// </summary>
public static class SummaryImageTargetResolver
{
    /// <summary>解析总结图目标；无有效连接返回 null（调用方跳过生图）。</summary>
    public static SummaryImageTarget? Resolve(
        IReadOnlyList<ImageConnection>? connections,
        string? summaryModelRef)
    {
        var valid = connections?
            .Where(c => !string.IsNullOrWhiteSpace(c.Id)
                        && !string.IsNullOrWhiteSpace(c.BaseUrl)
                        && c.Models.Count > 0)
            .ToList();
        if (valid is null || valid.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(summaryModelRef))
        {
            var parts = summaryModelRef.Split('/', 2);
            if (parts.Length == 2)
            {
                var connId = parts[0].Trim();
                var modelId = parts[1].Trim();
                var conn = valid.FirstOrDefault(c =>
                    string.Equals(c.Id, connId, StringComparison.OrdinalIgnoreCase));
                if (conn is not null)
                {
                    var model = conn.Models.FirstOrDefault(m =>
                        string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase));
                    if (model is not null)
                        return new SummaryImageTarget(conn, model);
                    // 连接匹配但模型不在其白名单（配置变更）→ 回退该连接首模型
                    return new SummaryImageTarget(conn, conn.Models[0]);
                }
            }
        }

        // 空引用 / 引用失效 → 首连接首模型
        var first = valid[0];
        return new SummaryImageTarget(first, first.Models[0]);
    }
}
