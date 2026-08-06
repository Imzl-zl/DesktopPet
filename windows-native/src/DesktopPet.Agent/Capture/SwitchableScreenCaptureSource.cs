using System.IO;

namespace DesktopPet.Agent.Capture;

/// <summary>
/// Creates a real capture source only while screen analysis is enabled and
/// releases it as soon as analysis is disabled.
/// </summary>
public interface IActivatableScreenCaptureSource
{
    void SetEnabled(bool enabled);
}

/// <summary>Controls the cadence of the expensive surface-to-bitmap copy.</summary>
public interface ICaptureCadenceSource
{
    void SetCaptureInterval(TimeSpan interval);
}

/// <summary>Reports a terminal capture/source fault to the lifecycle owner.</summary>
public interface ICaptureFaultSource
{
    event Action<Exception>? Faulted;
}

public sealed class CaptureSourceUnavailableException : IOException
{
    public CaptureSourceUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class SwitchableScreenCaptureSource :
    IScreenCaptureSource,
    IActivatableScreenCaptureSource,
    ICaptureCadenceSource,
    ICaptureFaultSource,
    IDisposable
{
    private static readonly TimeSpan DefaultCaptureInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<IScreenCaptureSource> _factory;
    private readonly Action<IScreenCaptureSource> _disposeSource;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private IScreenCaptureSource? _activeSource;
    private IScreenCaptureSource? _retiredSource;
    private TimeSpan _captureInterval = DefaultCaptureInterval;
    private long _retryNotBefore;
    private int _failureCount;
    private bool _enabled;
    private bool _disposed;
    private Action<Exception>? _activeFaultHandler;

    public SwitchableScreenCaptureSource(
        Func<IScreenCaptureSource> factory,
        Action<IScreenCaptureSource>? disposeSource = null,
        TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _disposeSource = disposeSource ?? DisposeDefault;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<Exception>? Faulted;

    public void SetEnabled(bool enabled)
    {
        IScreenCaptureSource? activeToDispose = null;
        IScreenCaptureSource? retiredToDispose = null;
        _operationGate.Wait();
        try
        {
            lock (_stateLock)
            {
                if (_disposed || _enabled == enabled) return;
                _enabled = enabled;
                if (!enabled)
                {
                    activeToDispose = DetachActiveSource_NoLock();
                    retiredToDispose = DetachRetiredSource_NoLock();
                    ResetRetry_NoLock();
                }
                else
                {
                    ResetRetry_NoLock();
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }

        DisposeSource(activeToDispose);
        DisposeSource(retiredToDispose);
    }

    public void SetCaptureInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        _operationGate.Wait();
        try
        {
            IScreenCaptureSource? active;
            lock (_stateLock)
            {
                if (_disposed) return;
                _captureInterval = interval;
                active = _activeSource;
            }
            if (active is ICaptureCadenceSource cadence)
                cadence.SetCaptureInterval(interval);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        IScreenCaptureSource? retiredToDispose = null;
        CaptureSourceUnavailableException? unavailable = null;
        CapturedFrame? result = null;
        try
        {
            IScreenCaptureSource? source;
            TimeSpan interval;
            lock (_stateLock)
            {
                if (_disposed || !_enabled) return null;
                retiredToDispose = DetachRetiredSource_NoLock();
                source = _activeSource;
                interval = _captureInterval;
                if (source is null && !RetryAllowed_NoLock())
                {
                    unavailable = CreateUnavailable_NoLock("capture source retry backoff is active");
                }
            }

            if (unavailable is null && source is null)
            {
                try
                {
                    source = _factory() ?? throw new InvalidOperationException(
                        "The screen capture factory returned null.");
                    if (source is ICaptureCadenceSource cadence)
                        cadence.SetCaptureInterval(interval);
                    lock (_stateLock)
                    {
                        if (_disposed || !_enabled)
                        {
                            retiredToDispose = source;
                            source = null;
                        }
                        else
                        {
                            AttachSource_NoLock(source);
                            _failureCount = 0;
                            _retryNotBefore = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (source is not null)
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_activeSource, source))
                                _activeSource = null;
                        }
                        retiredToDispose = source;
                    }
                    lock (_stateLock)
                    {
                        RegisterFailure_NoLock(ex);
                        unavailable = CreateUnavailable_NoLock("capture source creation failed", ex);
                    }
                }
            }

            if (unavailable is null && source is not null)
                result = await source.CaptureAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }

        DisposeSource(retiredToDispose);
        if (unavailable is not null)
        {
            NotifyFault(unavailable);
            throw unavailable;
        }
        return result;
    }

    public void Dispose()
    {
        IScreenCaptureSource? activeToDispose;
        IScreenCaptureSource? retiredToDispose;
        _operationGate.Wait();
        try
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _enabled = false;
                activeToDispose = DetachActiveSource_NoLock();
                retiredToDispose = DetachRetiredSource_NoLock();
            }
        }
        finally
        {
            _operationGate.Release();
        }

        DisposeSource(activeToDispose);
        DisposeSource(retiredToDispose);
        // The gate is intentionally retained: a waiter may acquire it after disposal,
        // observe _disposed, and release it. The source lifetime is bounded by its owner.
    }

    private void OnSourceFaulted(IScreenCaptureSource source, Exception exception)
    {
        var shouldNotify = false;
        lock (_stateLock)
        {
            if (_disposed || !ReferenceEquals(_activeSource, source)) return;
            DetachActiveSource_NoLock();
            _retiredSource = source;
            RegisterFailure_NoLock(exception);
            shouldNotify = true;
        }
        if (shouldNotify) NotifyFault(exception);
    }

    private void AttachSource_NoLock(IScreenCaptureSource source)
    {
        _activeSource = source;
        if (source is ICaptureFaultSource faultSource)
        {
            _activeFaultHandler = exception => OnSourceFaulted(source, exception);
            faultSource.Faulted += _activeFaultHandler;
        }
    }

    private IScreenCaptureSource? DetachActiveSource_NoLock()
    {
        var source = _activeSource;
        if (source is ICaptureFaultSource faultSource && _activeFaultHandler is not null)
            faultSource.Faulted -= _activeFaultHandler;
        _activeFaultHandler = null;
        _activeSource = null;
        return source;
    }

    private IScreenCaptureSource? DetachRetiredSource_NoLock()
    {
        var source = _retiredSource;
        _retiredSource = null;
        return source;
    }

    private void RegisterFailure_NoLock(Exception exception)
    {
        _failureCount = Math.Min(_failureCount + 1, 6);
        var seconds = Math.Min(
            MaxRetryBackoff.TotalSeconds,
            Math.Pow(2, Math.Max(0, _failureCount - 1)));
        var delay = TimeSpan.FromSeconds(seconds);
        _retryNotBefore = _timeProvider.GetTimestamp() +
            (long)(delay.TotalSeconds * _timeProvider.TimestampFrequency);
    }

    private void ResetRetry_NoLock()
    {
        _failureCount = 0;
        _retryNotBefore = 0;
    }

    private bool RetryAllowed_NoLock()
        => _retryNotBefore == 0
           || _timeProvider.GetElapsedTime(
               _retryNotBefore,
               _timeProvider.GetTimestamp()) >= TimeSpan.Zero;

    private CaptureSourceUnavailableException CreateUnavailable_NoLock(
        string message,
        Exception? inner = null)
        => new($"{message}; retry backoff={_failureCount}", inner);

    private void NotifyFault(Exception exception)
    {
        Faulted?.Invoke(exception);
    }

    private static void DisposeDefault(IScreenCaptureSource source)
    {
        if (source is IDisposable disposable) disposable.Dispose();
    }

    private void DisposeSource(IScreenCaptureSource? source)
    {
        if (source is null) return;
        _disposeSource(source);
    }
}
