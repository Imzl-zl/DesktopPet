using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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

/// <summary>命名管道服务端（Agent 侧）。每次断开后可创建新实例重新接受客户端。</summary>
public sealed class PipeRpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private bool _disposed;

    public PipeRpcServer(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("管道名不能为空", nameof(pipeName));
        _pipeName = pipeName;
    }

    public async Task WaitForConnectionAsync(CancellationToken ct)
    {
        NamedPipeServerStream pipe;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pipe?.IsConnected == true) return;
            _pipe?.Dispose();
            pipe = CreatePipe();
            _pipe = pipe;
        }

        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            var ownsPipe = false;
            lock (_sync)
            {
                if (ReferenceEquals(_pipe, pipe))
                {
                    _pipe = null;
                    ownsPipe = true;
                }
            }
            if (ownsPipe) await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public int GetConnectedClientProcessId()
    {
        var pipe = ConnectedPipe();
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取命名管道客户端 PID");
        return checked((int)processId);
    }

    public async Task DisconnectAsync()
    {
        NamedPipeServerStream? pipe;
        lock (_sync)
        {
            pipe = _pipe;
            _pipe = null;
        }
        if (pipe is null) return;

        pipe.Dispose(); // 先关闭句柄，打断可能占住 write lock 的异步写。
        await _writeLock.WaitAsync().ConfigureAwait(false);
        _writeLock.Release();
    }

    public async Task SendAsync(RpcMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RpcFraming.WriteAsync(ConnectedPipe(), message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<RpcMessage> ReceiveAsync(CancellationToken ct)
        => RpcFraming.ReadAsync(ConnectedPipe(), ct);

    public async ValueTask DisposeAsync()
    {
        NamedPipeServerStream? pipe;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            pipe = _pipe;
            _pipe = null;
        }

        if (pipe is not null) pipe.Dispose(); // 先取消在途 IO，再等待写锁归还。
        await _writeLock.WaitAsync().ConfigureAwait(false);
        _writeLock.Release();
        _writeLock.Dispose();
    }

    private NamedPipeServerStream ConnectedPipe()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _pipe is { IsConnected: true } pipe
                ? pipe
                : throw new IOException("命名管道尚未连接");
        }
    }

    private NamedPipeServerStream CreatePipe()
        => new(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipeRpcServer));
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
            out uint clientProcessId);
    }
}

/// <summary>命名管道客户端（App 侧）。</summary>
public sealed class PipeRpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _connected;
    private int _disposed;

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pipe.Dispose(); // 先打断在途 connect/read/write，避免等待 write lock 无界挂起。
        await _writeLock.WaitAsync().ConfigureAwait(false);
        _writeLock.Release();
        _writeLock.Dispose();
    }
}
