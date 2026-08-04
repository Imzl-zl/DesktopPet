using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Infra.PipeRpc;

public enum RpcType
{
    Hello,        // client → server：握手（带 app 版本）
    Config,       // client → server：AgentConfig 下发
    ScreenEvent,  // server → client：屏幕事件推送
    Ping,
    Pong,
    Shutdown,     // client → server：优雅退出
}

/// <summary>管道消息：类型 + 可选 JSON payload（null 表示无 payload）。</summary>
public sealed record RpcMessage(RpcType Type, JsonElement? Payload);

/// <summary>
/// 帧协议：4 字节大端长度前缀 + UTF-8 JSON（RpcMessage 序列化）。
/// </summary>
public static class RpcFraming
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task WriteAsync(Stream stream, RpcMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<RpcMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > 64 * 1024 * 1024)
            throw new IOException($"非法帧长度: {length}");

        var body = new byte[length];
        await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RpcMessage>(body, Options)
            ?? throw new IOException("帧反序列化失败");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("对端关闭连接");
            offset += read;
        }
    }
}

/// <summary>命名管道服务端（Agent 侧）。单连接模型：接受第一个客户端。</summary>
public sealed class PipeRpcServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // 并发写防串帧（事件推送 + Ping 响应）
    private bool _connected;

    public PipeRpcServer(string pipeName)
    {
        _pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public async Task WaitForConnectionAsync(CancellationToken ct)
    {
        if (_connected) return;
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _connected = true;
    }

    public async Task SendAsync(RpcMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RpcFraming.WriteAsync(_pipe, message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<RpcMessage> ReceiveAsync(CancellationToken ct)
        => RpcFraming.ReadAsync(_pipe, ct);

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_connected) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>命名管道客户端（App 侧）。</summary>
public sealed class PipeRpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _connected;

    public PipeRpcClient(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return;
        await _pipe.ConnectAsync(ct).ConfigureAwait(false);
        _connected = true;
    }

    public async Task SendAsync(RpcMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RpcFraming.WriteAsync(_pipe, message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<RpcMessage> ReceiveAsync(CancellationToken ct)
        => RpcFraming.ReadAsync(_pipe, ct);

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_connected) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
