using System.Net;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// 共享 HttpClient 工厂（Microsoft Learn「HttpClient guidelines for .NET」推荐形态：
/// 长生命周期单例 + SocketsHttpHandler.PooledConnectionLifetime）。
/// 端点由用户在设置页自定义（云域名 / 本地 Ollama），连接按生命周期轮换以反映
/// DNS / 网络变化；请求级 deadline 由各 Provider / 调度层持有，HttpClient.Timeout 保持 Infinite。
/// </summary>
public static class ProviderHttpClient
{
    /// <summary>连接池复用上限：到期后连接关闭，下次请求重新建连（重新 DNS 解析）。</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = PooledConnectionLifetime,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
