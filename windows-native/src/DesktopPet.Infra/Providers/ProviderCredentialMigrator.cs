using System.Security.Cryptography;
using System.Text;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Storage;
using DesktopPet.Core.Storage;

namespace DesktopPet.Infra.Providers;

public static class ProviderCredentialRefs
{
    public const string LegacyModel = "model-key";
    public const string LegacyImage = "image-key";
    public const string Image = "provider/image/default/api-key";
    public const string Tts = "provider/tts/default/api-key";

    public static string NewConnectionId() => Guid.NewGuid().ToString("N");

    public static string ForModel(string connectionId)
        => $"provider/model/{connectionId}/api-key";

    /// <summary>生图连接凭据引用（每连接独立，对齐 ForModel；windows-imagegen-design.md §6）。</summary>
    public static string ForImage(string connectionId)
        => $"provider/image/{connectionId}/api-key";
}

public sealed class CredentialMigrationException : IOException
{
    public CredentialMigrationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ProviderCredentialMigrationResult(
    ProvidersFileModel Providers,
    bool Changed,
    IReadOnlyList<CredentialStoreException> CleanupErrors,
    bool SkippedUnsafeSource = false);

/// <summary>
/// Copies legacy credentials to connection-scoped targets, publishes providers.json once,
/// then removes unreferenced legacy targets. A crash before publish leaves only safe orphan copies.
/// </summary>
public sealed class ProviderCredentialMigrator
{
    private readonly Func<ProvidersFileMigrationSource?> _load;
    private readonly Action<ProvidersFileModel> _save;
    private readonly ICredentialStore _credentials;

    public ProviderCredentialMigrator(FileJsonStore store, ICredentialStore credentials)
        : this(store.LoadProvidersFileForMigration, store.SaveProvidersFile, credentials)
    {
    }

    public ProviderCredentialMigrator(
        Func<ProvidersFileModel?> load,
        Action<ProvidersFileModel> save,
        ICredentialStore credentials)
        : this(
            () => new ProvidersFileMigrationSource(
                ProvidersFileModel.Normalize(load() ?? new ProvidersFileModel()),
                true),
            save,
            credentials)
    {
    }

    private ProviderCredentialMigrator(
        Func<ProvidersFileMigrationSource?> load,
        Action<ProvidersFileModel> save,
        ICredentialStore credentials)
    {
        _load = load;
        _save = save;
        _credentials = credentials;
    }

    public ProviderCredentialMigrationResult Migrate()
    {
        var inspected = _load();
        if (inspected is null)
            return new ProviderCredentialMigrationResult(new ProvidersFileModel(), false, []);
        if (!inspected.IsLossless)
            return new ProviderCredentialMigrationResult(inspected.Providers, false, [], SkippedUnsafeSource: true);
        var source = inspected.Providers;
        if (source.Models.Count == 0 && source.Image is null)
            return new ProviderCredentialMigrationResult(source, false, []);

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var migrated = new List<ProviderConfig>(source.Models.Count);
        var oldRefs = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;

        for (var index = 0; index < source.Models.Count; index++)
        {
            var model = source.Models[index];
            var id = model.Id.Trim();
            if (id.Length == 0 || !usedIds.Add(id))
            {
                id = StableLegacyId(model, index);
                while (!usedIds.Add(id)) id += "x";
                changed = true;
            }

            var next = model with { Id = id };
            if (!string.IsNullOrEmpty(model.ApiKeyRef))
            {
                var targetRef = ProviderCredentialRefs.ForModel(id);
                if (!string.Equals(model.ApiKeyRef, targetRef, StringComparison.Ordinal))
                {
                    var sourceSecret = _credentials.Get(model.ApiKeyRef)
                        ?? throw new CredentialMigrationException(
                            "source-missing",
                            "模型连接引用的旧凭据不存在，未修改配置");
                    var targetSecret = _credentials.Get(targetRef);
                    if (targetSecret is null)
                    {
                        _credentials.Set(targetRef, sourceSecret);
                        targetSecret = _credentials.Get(targetRef);
                    }
                    if (!string.Equals(targetSecret, sourceSecret, StringComparison.Ordinal))
                    {
                        throw new CredentialMigrationException(
                            "target-conflict",
                            "模型连接的目标凭据已存在且内容不同，未修改配置");
                    }

                    oldRefs.Add(model.ApiKeyRef);
                    next = next with { ApiKeyRef = targetRef };
                    changed = true;
                }
            }
            migrated.Add(next);
        }

        // 先 Normalize：旧平铺格式（Legacy*）转连接列表后再逐连接迁移凭据
        var image = ImageConnectionsConfig.Normalize(source.Image);
        if (image is not null)
        {
            for (var i = 0; i < image.Connections.Count; i++)
            {
                var conn = image.Connections[i];
                if (string.IsNullOrEmpty(conn.ApiKeyRef)) continue;
                var targetRef = ProviderCredentialRefs.ForImage(conn.Id);
                if (string.Equals(conn.ApiKeyRef, targetRef, StringComparison.Ordinal)) continue;

                var sourceSecret = _credentials.Get(conn.ApiKeyRef)
                    ?? throw new CredentialMigrationException(
                        "source-missing",
                        "生图连接引用的旧凭据不存在，未修改配置");
                var targetSecret = _credentials.Get(targetRef);
                if (targetSecret is null)
                {
                    _credentials.Set(targetRef, sourceSecret);
                    targetSecret = _credentials.Get(targetRef);
                }
                if (!string.Equals(targetSecret, sourceSecret, StringComparison.Ordinal))
                {
                    throw new CredentialMigrationException(
                        "target-conflict",
                        "生图连接的目标凭据已存在且内容不同，未修改配置");
                }
                oldRefs.Add(conn.ApiKeyRef);
                image.Connections[i] = conn with { ApiKeyRef = targetRef };
                changed = true;
            }
        }

        var committed = new ProvidersFileModel { Models = migrated, Image = image };
        if (changed) _save(committed);

        var referenced = migrated
            .Select(model => model.ApiKeyRef)
            .Where(reference => !string.IsNullOrEmpty(reference))
            .ToHashSet(StringComparer.Ordinal);
        if (image is not null)
            foreach (var conn in image.Connections)
                if (!string.IsNullOrEmpty(conn.ApiKeyRef))
                    referenced.Add(conn.ApiKeyRef);
        if (!referenced.Contains(ProviderCredentialRefs.LegacyModel))
            oldRefs.Add(ProviderCredentialRefs.LegacyModel);
        if (!referenced.Contains(ProviderCredentialRefs.LegacyImage))
            oldRefs.Add(ProviderCredentialRefs.LegacyImage);

        var cleanupErrors = new List<CredentialStoreException>();
        foreach (var oldRef in oldRefs)
        {
            if (referenced.Contains(oldRef)) continue;
            try { _credentials.Delete(oldRef); }
            catch (CredentialStoreException ex) { cleanupErrors.Add(ex); }
        }

        return new ProviderCredentialMigrationResult(committed, changed, cleanupErrors);
    }

    private static string StableLegacyId(ProviderConfig model, int index)
    {
        var identity = $"{model.Id}\n{model.BaseUrl}\n{model.ModelName}\n{index}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return "legacy-" + hash[..16];
    }
}
