using System.Net.Http.Json;
using System.Text.Json;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Tts;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// OpenAI 兼容 TTS Provider（windows-tts-design.md §6.3）：
/// POST {baseUrl}/audio/speech（model/input/voice/response_format/speed）→ 音频字节流；
/// 音色列表 GET {baseUrl}/audio/voices（SiliconFlow / Fish Audio / Neiroha 均支持；404 返回空）。
/// 通吃云端（SiliconFlow/Fish/OpenAI）与本地 GPT-SoVITS 等兼容端点。
/// 错误分级对齐 ProviderException：auth / timeout / network / invalid-response。
/// </summary>
public sealed class OpenAiCompatibleTtsProvider : ITtsProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TtsEndpointConfig _config;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    public OpenAiCompatibleTtsProvider(
        TtsEndpointConfig config,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? DefaultTimeout;
    }

    public string Id => "openai";
    public bool RequiresNetwork => true;

    public async Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct)
    {
        var trimmed = request.Text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("语音文本不能为空", nameof(request.Text));

        var apiKey = _credentials.Get(_config.ApiKeyRef);
        var voiceId = !string.IsNullOrEmpty(request.VoiceId) ? request.VoiceId : _config.Voice;
        var body = new Dictionary<string, object>
        {
            ["model"] = _config.ModelName,
            ["input"] = trimmed,
            ["voice"] = voiceId,
            ["response_format"] = "mp3",
            ["speed"] = Math.Clamp(request.SpeedPercent, 50, 200) / 100.0,
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderEndpointPolicy.BuildRequestUri(
                _config.BaseUrl,
                "audio/speech",
                !string.IsNullOrEmpty(apiKey)));
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (httpRequest.Headers.UserAgent.Count == 0)
            httpRequest.Headers.UserAgent.ParseAdd("DesktopPet/1.0");
        httpRequest.Content = JsonContent.Create(body);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ProviderException("timeout", "TTS 请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("network", $"TTS 网络错误: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new ProviderException("auth", "TTS 鉴权失败：请检查 API Key");
            }
            if (!response.IsSuccessStatusCode)
            {
                // 400 等：透传端点原文（voice/model 配置错误排查关键信息）
                var detail = await SafeReadErrorAsync(response, deadline.Token);
                throw new ProviderException("invalid-response", $"TTS 端点错误 ({(int)response.StatusCode}): {detail}");
            }
            var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, deadline.Token).ConfigureAwait(false);
            ms.Position = 0;
            return ms;
        }
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct)
    {
        var apiKey = _credentials.Get(_config.ApiKeyRef);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            ProviderEndpointPolicy.BuildRequestUri(
                _config.BaseUrl,
                "audio/voices",
                !string.IsNullOrEmpty(apiKey)));
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (httpRequest.Headers.UserAgent.Count == 0)
            httpRequest.Headers.UserAgent.ParseAdd("DesktopPet/1.0");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, deadline.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return []; // 列表失败不致命：设置页可手动输入音色
        }

        using (response)
        {
            // 401/403 = 鉴权失败必须显式报错（否则设置页测试连接假成功）；404 = 端点不支持列表 → 空
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new ProviderException("auth", "TTS 鉴权失败：请检查 API Key");
            }
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode) return []; // 其他失败（500 等）不致命：可手动输入音色
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                var root = doc.RootElement;
                if (!root.TryGetProperty("voices", out var voices)) return [];
                var list = new List<TtsVoiceInfo>();
                foreach (var v in voices.EnumerateArray())
                {
                    list.Add(new TtsVoiceInfo(
                        Id: v.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        DisplayName: v.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        Language: v.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "",
                        Gender: v.TryGetProperty("gender", out var g) ? g.GetString() ?? "" : ""));
                }
                return list.Where(v => v.Id.Length > 0).ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 300 ? text[..300] : text; // 防超长响应刷日志
        }
        catch
        {
            return "(无法读取响应体)";
        }
    }
}
