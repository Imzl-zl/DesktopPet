using System.Text;

namespace DesktopPet.Infra.Diagnostics;

public interface IAppLogger : IDisposable
{
    Exception? LastError { get; }
    void Info(string component, string message);
    void Error(string component, string message);
    void Flush();
}

public sealed class NullAppLogger : IAppLogger
{
    public static NullAppLogger Instance { get; } = new();
    public Exception? LastError => null;
    public void Info(string component, string message) { }
    public void Error(string component, string message) { }
    public void Flush() { }
    public void Dispose() { }
}

public sealed class RollingFileLogger : IAppLogger
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private FileStream? _stream;
    private bool _disposed;

    public RollingFileLogger(
        string directory,
        string baseName,
        long maxBytes = 1024 * 1024,
        int maxFiles = 5)
    {
        if (maxBytes < 128) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        _directory = directory;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(directory);
        _stream = OpenCurrent();
    }

    public Exception? LastError { get; private set; }

    public void Info(string component, string message) => Write("INFO", component, message);
    public void Error(string component, string message) => Write("ERROR", component, message);

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try { _stream?.Flush(flushToDisk: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex;
                System.Diagnostics.Debug.WriteLine($"DesktopPet logger flush failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream?.Flush(flushToDisk: true); }
            finally
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private void Write(string level, string component, string message)
    {
        var safeComponent = SecretRedactor.Redact(component.ReplaceLineEndings(" "));
        var safeMessage = SecretRedactor.Redact(message.ReplaceLineEndings(" "));
        var bytes = EncodeBoundedLine(level, safeComponent, safeMessage);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _stream ??= OpenCurrent();
                if (_stream.Length + bytes.Length > _maxBytes) Rotate_NoLock();
                _stream!.Write(bytes);
                _stream.Flush(flushToDisk: false);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastError = ex;
                System.Diagnostics.Debug.WriteLine($"DesktopPet logger write failed: {ex.Message}");
            }
        }
    }

    private byte[] EncodeBoundedLine(string level, string component, string message)
    {
        var timestamp = DateTimeOffset.Now;
        var prefix = $"{timestamp:O} [{level}] [{component}] ";
        var suffix = Environment.NewLine;
        var line = prefix + message + suffix;
        if (Encoding.UTF8.GetByteCount(line) <= _maxBytes) return Encoding.UTF8.GetBytes(line);

        const string marker = " [TRUNCATED]";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix + marker + suffix);
        if (fixedBytes > _maxBytes)
        {
            prefix = $"{timestamp:O} [{level}] ";
            fixedBytes = Encoding.UTF8.GetByteCount(prefix + marker + suffix);
        }
        var budget = Math.Max(0, _maxBytes - fixedBytes);
        var low = 0;
        var high = message.Length;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(message.AsSpan(0, mid)) <= budget) low = mid;
            else high = mid - 1;
        }
        return Encoding.UTF8.GetBytes(prefix + message[..low] + marker + suffix);
    }

    private void Rotate_NoLock()
    {
        Exception? failure = null;
        try
        {
            _stream?.Flush(flushToDisk: true);
            _stream?.Dispose();
            _stream = null;

            for (var index = _maxFiles - 1; index >= 1; index--)
            {
                var source = index == 1 ? CurrentPath : RotatedPath(index - 1);
                var target = RotatedPath(index);
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(source)) File.Move(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
        }
        finally
        {
            try { _stream = OpenCurrent(); }
            catch (Exception reopenError) when (reopenError is IOException or UnauthorizedAccessException)
            {
                failure = failure is null ? reopenError : CombineRotationFailure(failure, reopenError);
            }
        }
        if (failure is not null) throw failure;
    }

    /// <summary>轮转双失败合并：必须产出 IOException 家族，Write 的捕获过滤才能接住
    /// （AggregateException 会逃逸中断调用方会话）。</summary>
    internal static Exception CombineRotationFailure(Exception moveFailure, Exception reopenFailure)
        => new IOException("日志轮转失败（移动与重开均失败）", reopenFailure);

    private FileStream OpenCurrent()
        => new(
            CurrentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);

    private string CurrentPath => Path.Combine(_directory, _baseName + ".log");
    private string RotatedPath(int index) => Path.Combine(_directory, $"{_baseName}.log.{index}");
}
