namespace DesktopPet.Agent.Capture;

/// <summary>
/// Creates a real capture source only while screen analysis is enabled and
/// releases it as soon as analysis is disabled.
/// </summary>
public interface IActivatableScreenCaptureSource
{
    void SetEnabled(bool enabled);
}

public sealed class SwitchableScreenCaptureSource : IScreenCaptureSource, IActivatableScreenCaptureSource, IDisposable
{
    private readonly Func<IScreenCaptureSource> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IScreenCaptureSource? _activeSource;
    private bool _enabled;
    private bool _disposed;

    public SwitchableScreenCaptureSource(Func<IScreenCaptureSource> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void SetEnabled(bool enabled)
    {
        IScreenCaptureSource? sourceToDispose = null;
        _gate.Wait();
        try
        {
            if (_disposed || _enabled == enabled) return;

            _enabled = enabled;
            if (!enabled)
            {
                sourceToDispose = _activeSource;
                _activeSource = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        DisposeSource(sourceToDispose);
    }

    public async Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_enabled) return null;

            _activeSource ??= _factory()
                ?? throw new InvalidOperationException("The screen capture factory returned null.");
            return await _activeSource.CaptureAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        IScreenCaptureSource? sourceToDispose = null;
        _gate.Wait();
        try
        {
            if (_disposed) return;

            _disposed = true;
            _enabled = false;
            sourceToDispose = _activeSource;
            _activeSource = null;
        }
        finally
        {
            _gate.Release();
        }

        DisposeSource(sourceToDispose);
    }

    private static void DisposeSource(IScreenCaptureSource? source)
    {
        if (source is IDisposable disposable) disposable.Dispose();
    }
}
