using System.Net.WebSockets;
using System.Text;

namespace DesktopPet.Infra.Tts;

/// <summary>TTS 音色（Edge TTS 名称，如 zh-CN-XiaoxiaoNeural）。</summary>
public sealed record TtsVoice(string Name, string Language);

/// <summary>
/// TTS Provider 契约（架构文档 §3.2）。实现：EdgeTtsProvider（默认，免费）。
/// 语音开关默认关（AI 助手页）；弹幕模式不朗读（App 层控制）。
/// </summary>
public interface ITtsProvider
{
    /// <summary>合成语音，返回音频流（调用方负责 Dispose；格式因实现而异：
    /// SAPI=WAV / Edge=MP3）。</summary>
    Task<Stream> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct);
}

/// <summary>Edge TTS 底层 socket 抽象（测试注入内存实现）。</summary>
public interface IEdgeSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string message, CancellationToken ct);
    Task<byte[]> ReceiveAsync(CancellationToken ct);
}

/// <summary>
/// 真实 WebSocket 实现（Edge TTS 端点）。
/// 自研最小 RFC 6455 客户端：WinHTTP 的 ClientWebSocket 对微软端点握手失败
/// （HTTP/2 ALPN / 扩展协商差异 → 400），裸 TCP + TLS + 手工帧已验证可用。
/// 不协商 permessage-deflate（服务端允许，帧即明文）。
/// </summary>
public sealed class EdgeTtsSocket : IEdgeSocket
{
    private readonly System.Net.Sockets.TcpClient _tcp = new();
    private System.Net.Security.SslStream? _ssl;
    private Stream? _stream;
    private readonly byte[] _headerBuf = new byte[2];
    private readonly byte[] _lenBuf = new byte[8];

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        await _tcp.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
        _ssl = new System.Net.Security.SslStream(_tcp.GetStream());
        // 带 ct 的重载（.NET 8 支持）：TLS 握手期间取消同样生效
        await _ssl.AuthenticateAsClientAsync(
            new System.Net.Security.SslClientAuthenticationOptions { TargetHost = uri.Host },
            ct).ConfigureAwait(false);
        _stream = _ssl;

        var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var request =
            $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
            $"Host: {uri.Host}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Origin: chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold\r\n" +
            "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0\r\n" +
            "Pragma: no-cache\r\n" +
            "Cache-Control: no-cache\r\n" +
            $"Cookie: muid={EdgeTtsProtocol.GenerateMuid()};\r\n" +
            "\r\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(request);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);

        // 读响应头（直到 \r\n\r\n）
        var response = new System.Text.StringBuilder();
        var buf = new byte[1];
        while (!response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await _stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("握手响应中断");
            response.Append((char)buf[0]);
            if (response.Length > 8192) throw new IOException("握手响应过长");
        }
        var responseText = response.ToString();
        System.Diagnostics.Debug.WriteLine("[tts-handshake] " + responseText);
        if (!responseText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new IOException("WebSocket 握手失败: " + response.ToString().Split('\r')[0]);
    }

    /// <summary>发送文本帧（FIN + opcode=1 + mask）。</summary>
    public async Task SendAsync(string message, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("未连接");
        var payload = System.Text.Encoding.UTF8.GetBytes(message);
        using var frame = new MemoryStream();
        frame.WriteByte(0x81); // FIN | text
        var mask = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(mask);
        if (payload.Length <= 125)
        {
            frame.WriteByte((byte)(0x80 | payload.Length));
        }
        else if (payload.Length <= 0xFFFF)
        {
            frame.WriteByte(0x80 | 126);
            frame.WriteByte((byte)(payload.Length >> 8));
            frame.WriteByte((byte)payload.Length);
        }
        else
        {
            frame.WriteByte(0x80 | 127);
            var len = (ulong)payload.Length;
            for (var i = 7; i >= 0; i--) frame.WriteByte((byte)(len >> (8 * i)));
        }
        frame.Write(mask, 0, 4);
        for (var i = 0; i < payload.Length; i++) frame.WriteByte((byte)(payload[i] ^ mask[i % 4]));
        await _stream.WriteAsync(frame.ToArray(), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        System.Diagnostics.Debug.WriteLine($"[tts-send] {payload.Length} bytes FULL:");
        System.Diagnostics.Debug.WriteLine(message);
    }

    /// <summary>接收一帧（聚合 FIN；服务端帧无 mask）。返回帧原始字节（含 Path 头）。</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("未连接");
        await ReadExactlyAsync(_headerBuf, ct).ConfigureAwait(false);
        var fin = (_headerBuf[0] & 0x80) != 0;
        var opcode = _headerBuf[0] & 0x0F;
        var masked = (_headerBuf[1] & 0x80) != 0;
        var len7 = _headerBuf[1] & 0x7F;

        long length;
        if (len7 == 126)
        {
            await ReadExactlyAsync(_lenBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
            length = (_lenBuf[0] << 8) | _lenBuf[1];
        }
        else if (len7 == 127)
        {
            await ReadExactlyAsync(_lenBuf, ct).ConfigureAwait(false);
            length = 0;
            for (var i = 0; i < 8; i++) length = (length << 8) | _lenBuf[i];
        }
        else
        {
            length = len7;
        }
        if (length > 16 * 1024 * 1024) throw new IOException($"帧过长: {length}");

        byte[] maskKey = [];
        if (masked)
        {
            maskKey = new byte[4];
            await ReadExactlyAsync(maskKey, ct).ConfigureAwait(false);
        }

        var payload = new byte[length];
        if (length > 0) await ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i % 4];
        }

        // 控制帧处理：ping → pong；close → 抛（带服务端错误详情）
        if (opcode is 0x8)
        {
            var reason = System.Text.Encoding.UTF8.GetString(payload);
            throw new EndOfStreamException(reason.Length > 0 ? "对端关闭: " + reason : "对端关闭连接");
        }
        if (opcode is 0x9)
        {
            await SendPongAsync(payload, ct).ConfigureAwait(false);
            return await ReceiveAsync(ct).ConfigureAwait(false);
        }
        if (opcode is 0xA) return await ReceiveAsync(ct).ConfigureAwait(false); // pong 忽略
        if (opcode is not (0x1 or 0x2)) return await ReceiveAsync(ct).ConfigureAwait(false); // 非数据帧跳过

        // 分片聚合（微软一帧一消息；FIN 缺失时循环读续片）
        if (!fin)
        {
            using var ms = new MemoryStream();
            ms.Write(payload, 0, payload.Length);
            while (true)
            {
                var (nextFin, nextPayload) = await ReadFrameCoreAsync(ct).ConfigureAwait(false);
                ms.Write(nextPayload, 0, nextPayload.Length);
                if (nextFin) break;
            }
            return ms.ToArray();
        }
        return payload;
    }

    private async Task<(bool Fin, byte[] Payload)> ReadFrameCoreAsync(CancellationToken ct)
    {
        await ReadExactlyAsync(_headerBuf, ct).ConfigureAwait(false);
        var fin = (_headerBuf[0] & 0x80) != 0;
        var masked = (_headerBuf[1] & 0x80) != 0;
        var len7 = _headerBuf[1] & 0x7F;
        long length;
        if (len7 == 126)
        {
            await ReadExactlyAsync(_lenBuf.AsMemory(0, 2), ct).ConfigureAwait(false);
            length = (_lenBuf[0] << 8) | _lenBuf[1];
        }
        else if (len7 == 127)
        {
            await ReadExactlyAsync(_lenBuf, ct).ConfigureAwait(false);
            length = 0;
            for (var i = 0; i < 8; i++) length = (length << 8) | _lenBuf[i];
        }
        else
        {
            length = len7;
        }
        if (length > 16 * 1024 * 1024) throw new IOException($"帧过长: {length}");
        byte[] maskKey = [];
        if (masked)
        {
            maskKey = new byte[4];
            await ReadExactlyAsync(maskKey, ct).ConfigureAwait(false);
        }
        var payload = new byte[length];
        if (length > 0) await ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++) payload[i] ^= maskKey[i % 4];
        }
        return (fin, payload);
    }

    private async Task SendPongAsync(byte[] payload, CancellationToken ct)
    {
        if (_stream is null) return;
        using var frame = new MemoryStream();
        frame.WriteByte(0x8A);
        frame.WriteByte((byte)payload.Length);
        frame.Write(payload, 0, payload.Length);
        await _stream.WriteAsync(frame.ToArray(), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream!.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("连接中断");
            offset += read;
        }
    }

    public void Dispose()
    {
        _ssl?.Dispose();
        _tcp.Dispose();
    }
}

/// <summary>Edge TTS 免费协议（speech.platform.bing.com readaloud）的纯逻辑：消息构建 + 帧解析。</summary>
public static class EdgeTtsProtocol
{
    /// <summary>Edge 免费端点（TrustedClientToken 为公开固定值；Sec-MS-GEC 每次握手生成）。</summary>
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string SecMsGecVersion = "1-143.0.3650.75";

    /// <summary>
    /// Sec-MS-GEC 签名（edge-tts issue #290 算法）：Windows FILETIME（1601 起 100ns）
    /// 对齐 5 分钟窗口 → 字符串拼接 TrustedClientToken → SHA256 大写 hex。
    /// </summary>
    public static string GenerateSecMsGec(DateTime utcNow)
    {
        var ticks = utcNow.ToFileTimeUtc();
        ticks -= ticks % 3_000_000_000; // 5 分钟对齐
        var str = ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(str)));
    }

    /// <summary>随机 MUID（edge-tts headers_with_muid：Cookie 里带 muid）。</summary>
    public static string GenerateMuid()
    {
        var bytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>构建本次握手的端点（带 Sec-MS-GEC / ConnectionId）。</summary>
    public static Uri BuildEndpoint(DateTime utcNow)
    {
        var gec = GenerateSecMsGec(utcNow);
        var connectionId = Guid.NewGuid().ToString("N");
        return new Uri(
            $"wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
            $"?TrustedClientToken={TrustedClientToken}" +
            $"&Sec-MS-GEC={gec}" +
            $"&Sec-MS-GEC-Version={SecMsGecVersion}" +
            $"&ConnectionId={connectionId}");
    }

    /// <summary>
    /// JS 风格日期（微软端点校验 X-Timestamp 格式；edge-tts 的 date_to_string 同款）。
    /// </summary>
    public static string DateToString(DateTime utcNow)
        => utcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);

    public static string BuildSpeechConfigMessage(
        DateTime? utcNow = null,
        string outputFormat = "audio-24khz-48kbitrate-mono-mp3")
    {
        var body = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":" +
                   "{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"}," +
                   $"\"outputFormat\":\"{outputFormat}\"}}}}";
        return $"X-Timestamp:{DateToString(utcNow ?? DateTime.UtcNow)}\r\n" +
               "Content-Type:application/json; charset=utf-8\r\n" +
               "Path:speech.config\r\n\r\n" + body;
    }

    public static string BuildSsmlMessage(string text, TtsVoice voice, string requestId, DateTime? utcNow = null)
    {
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var ssml =
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{voice.Name}'>" +
            "<prosody pitch='+0Hz' rate='+0%' volume='+0%'>" +
            escaped +
            "</prosody></voice></speak>";
        return $"X-RequestId:{requestId}\r\n" +
               "Content-Type:application/ssml+xml\r\n" +
               $"X-Timestamp:{DateToString(utcNow ?? DateTime.UtcNow)}Z\r\n" + // 微软 Edge bug：SSML 时间戳必须带 Z
               "Path:ssml\r\n\r\n" + ssml;
    }

    /// <summary>解析 Edge 帧："头字段...Path:xxx\r\n\r\n" + payload（audio 帧为二进制）。</summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> data, out string path, out ReadOnlyMemory<byte> payload)
    {
        path = "";
        payload = default;
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return false;

        var header = Encoding.UTF8.GetString(data[..headerEnd]);
        const string pathMarker = "Path:";
        var pathIdx = header.LastIndexOf(pathMarker, StringComparison.Ordinal);
        if (pathIdx < 0) return false;
        path = header[(pathIdx + pathMarker.Length)..].Trim();
        payload = data[(headerEnd + 4)..].ToArray();
        return path.Length > 0;
    }
}

/// <summary>
/// Edge TTS Provider（免费协议）：连接 → speech.config → SSML → 收集 audio 帧直到 turn.end。
/// 输出 MP3 字节流；弹幕模式不调用（App 层）。
/// </summary>
public sealed class EdgeTtsProvider : ITtsProvider
{
    /// <summary>整体合成 deadline：调用方可能传 CancellationToken.None（App 层），
    /// Provider 必须兜底防止端点挂起时无限等待。</summary>
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<IEdgeSocket> _socketFactory;
    private readonly TimeSpan _overallTimeout;

    public EdgeTtsProvider(Func<IEdgeSocket>? socketFactory = null, TimeSpan? overallTimeout = null)
    {
        _socketFactory = socketFactory ?? (() => new EdgeTtsSocket());
        _overallTimeout = overallTimeout ?? DefaultOverallTimeout;
    }

    public async Task<Stream> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("语音文本不能为空", nameof(text));
        if (trimmed.Length > 500) trimmed = trimmed[..500] + "…"; // Edge 单次请求上限防护

        // 整体 deadline：连接/握手/发帧/收帧全程受此约束
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_overallTimeout);

        using var socket = _socketFactory();
        var utcNow = DateTime.UtcNow;
        await socket.ConnectAsync(EdgeTtsProtocol.BuildEndpoint(utcNow), deadline.Token);
        await socket.SendAsync(EdgeTtsProtocol.BuildSpeechConfigMessage(utcNow), deadline.Token);
        await socket.SendAsync(EdgeTtsProtocol.BuildSsmlMessage(trimmed, voice, Guid.NewGuid().ToString("N"), utcNow), deadline.Token);

        using var audio = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(deadline.Token);
            if (!EdgeTtsProtocol.TryParseFrame(frame, out var path, out var payload))
                throw new EndOfStreamException("TTS 帧解析失败");
            if (path == "audio") audio.Write(payload.Span);
            else if (path == "turn.end") break;
        }

        var result = new MemoryStream(audio.ToArray());
        return result;
    }
}
