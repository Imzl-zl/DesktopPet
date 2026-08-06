using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

public sealed record ModelConnectionDraft(
    string BaseUrl,
    string ModelName,
    string ExistingApiKeyRef,
    string DraftApiKey,
    ModelCapabilities Capabilities,
    string? ReasoningEffort = null,
    int? MaxOutputTokens = null,
    int? ContextWindowTokens = null);

public sealed record ModelConnectionTestResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<ModelInfo> Models);

public interface IModelConnectionTester
{
    Task<ModelConnectionTestResult> TestAsync(ModelConnectionDraft draft, CancellationToken ct);
}

/// <summary>Tests an unsaved draft with an in-memory credential overlay; it never persists the draft.</summary>
public sealed class ModelConnectionTester : IModelConnectionTester
{
    private const string DraftCredentialRef = "__connection-test-draft__";
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ModelConnectionTester(
        ICredentialStore credentials,
        HttpClient http,
        TimeSpan? timeout = null)
    {
        _credentials = credentials;
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<ModelConnectionTestResult> TestAsync(
        ModelConnectionDraft draft,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(draft.BaseUrl))
            return Failure("invalid-url", "请填写模型接口地址");

        var credentialRef = draft.ExistingApiKeyRef;
        ICredentialStore credentials = _credentials;
        if (!string.IsNullOrEmpty(draft.DraftApiKey))
        {
            credentialRef = DraftCredentialRef;
            credentials = new DraftCredentialOverlay(_credentials, draft.DraftApiKey);
        }

        var config = new ProviderConfig(
            Id: "connection-test",
            Name: string.IsNullOrWhiteSpace(draft.ModelName) ? "连接测试" : draft.ModelName.Trim(),
            BaseUrl: draft.BaseUrl.Trim(),
            ApiKeyRef: credentialRef,
            ModelName: string.IsNullOrWhiteSpace(draft.ModelName) ? "connection-test" : draft.ModelName.Trim(),
            Capabilities: draft.Capabilities,
            IsDefault: false,
            ReasoningEffort: draft.ReasoningEffort,
            MaxOutputTokens: draft.MaxOutputTokens,
            ContextWindowTokens: draft.ContextWindowTokens);
        var provider = new OpenAiCompatibleModelProvider(config, credentials, _http, _timeout);
        try
        {
            var models = await provider.ListModelsAsync(ct).ConfigureAwait(false);
            return new ModelConnectionTestResult(
                true,
                "ok",
                models.Count == 0 ? "连接成功，服务未返回模型列表" : $"连接成功，可用模型 {models.Count} 个",
                models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CredentialStoreException)
        {
            return Failure("credential", "无法读取 Windows 凭据");
        }
        catch (ProviderException ex)
        {
            return Failure(ex.Code, DescribeFailure(ex.Code));
        }
    }

    private static ModelConnectionTestResult Failure(string code, string message)
        => new(false, code, message, []);

    private static string DescribeFailure(string code) => code switch
    {
        "invalid-url" => "模型接口地址无效",
        "insecure-transport" => "远程 HTTP 连接不能发送 API Key，请使用 HTTPS",
        "auth" => "鉴权失败，请检查 API Key",
        "timeout" => "连接测试超时",
        "network" => "无法连接模型服务",
        "rate-limit" => "模型服务请求过于频繁",
        "server" => "模型服务暂时不可用",
        "invalid-response" => "模型列表响应格式无效",
        _ => "模型服务拒绝了连接测试",
    };

    private sealed class DraftCredentialOverlay(
        ICredentialStore fallback,
        string draftApiKey) : ICredentialStore
    {
        public string? Get(string key)
            => key == DraftCredentialRef ? draftApiKey : fallback.Get(key);

        public void Set(string key, string value)
            => throw new NotSupportedException("Connection tests cannot persist credentials");

        public bool Delete(string key)
            => throw new NotSupportedException("Connection tests cannot delete credentials");
    }
}
