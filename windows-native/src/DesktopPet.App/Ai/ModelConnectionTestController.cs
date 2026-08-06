using DesktopPet.Infra.Providers;

namespace DesktopPet.App.Ai;

/// <summary>Owns one latest-only connection test generation for a model editor.</summary>
public sealed class ModelConnectionTestController : IDisposable
{
    private readonly object _sync = new();
    private readonly IModelConnectionTester _tester;
    private CancellationTokenSource? _active;
    private long _generation;
    private bool _disposed;

    public ModelConnectionTestController(IModelConnectionTester tester)
    {
        _tester = tester;
    }

    public async Task<ModelConnectionTestResult?> TestLatestAsync(
        ModelConnectionDraft draft,
        CancellationToken ct = default)
    {
        CancellationTokenSource owner;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            _active?.Dispose();
            owner = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _active = owner;
            generation = ++_generation;
        }

        ModelConnectionTestResult result;
        try
        {
            result = await _tester.TestAsync(draft, owner.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && owner.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, owner))
                {
                    _active = null;
                    owner.Dispose();
                }
            }
        }

        lock (_sync)
            return !_disposed && generation == _generation ? result : null;
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _generation++;
            _active?.Cancel();
            _active?.Dispose();
            _active = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _active?.Cancel();
            _active?.Dispose();
            _active = null;
        }
    }
}
