namespace DesktopPet.Core.Care;

/// <summary>
/// 养成状态存储操作（对齐 care.ts 的 stateFor/mutate/migrateLegacyCareState，
/// 字典语义，持久化由 IJsonStore 负责）。
/// </summary>
public static class CareStoreModel
{
    /// <summary>读取实例状态；缺失返回空状态。</summary>
    public static CareState StateFor(IReadOnlyDictionary<string, CareState> store, string instanceId, DateTime now)
        => store.TryGetValue(instanceId, out var state) ? state : CareEngine.EmptyState(now);

    /// <summary>应用变更并保存。</summary>
    public static Dictionary<string, CareState> Mutate(
        IReadOnlyDictionary<string, CareState> store, string instanceId,
        Action<CareState> change, DateTime now)
    {
        var next = new Dictionary<string, CareState>(store);
        var state = StateFor(store, instanceId, now);
        change(state);
        next[instanceId] = state;
        return next;
    }

    /// <summary>
    /// 把旧 sprite-keyed 养成记录一次性迁移到实例 id，不覆盖已有进度
    /// （对齐 migrateLegacyCareState）。
    /// </summary>
    public static Dictionary<string, CareState> MigrateLegacyCareState(
        IReadOnlyDictionary<string, CareState> store, string legacySlug, string instanceId)
    {
        if (string.IsNullOrEmpty(legacySlug) || string.IsNullOrEmpty(instanceId) || legacySlug == instanceId)
        {
            return new Dictionary<string, CareState>(store);
        }
        var next = new Dictionary<string, CareState>(store);
        if (!next.TryGetValue(legacySlug, out var legacy) || legacy is null) return next;
        if (!next.ContainsKey(instanceId)) next[instanceId] = legacy;
        next.Remove(legacySlug);
        return next;
    }
}
