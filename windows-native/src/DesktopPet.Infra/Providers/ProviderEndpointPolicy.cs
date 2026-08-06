using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

public static class ProviderEndpointPolicy
{
    public static Uri BuildRequestUri(string baseUrl, string path, bool hasSecret)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || baseUri.UserInfo.Length > 0
            || baseUri.Query.Length > 0
            || baseUri.Fragment.Length > 0)
        {
            throw new ProviderException("invalid-url", "模型接口地址无效");
        }

        if (hasSecret && baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            throw new ProviderException(
                "insecure-transport",
                "远程 HTTP 连接不能发送 API Key，请使用 HTTPS");
        }

        var endpoint = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var requestUri))
            throw new ProviderException("invalid-url", "模型接口地址无效");
        return requestUri;
    }
}
