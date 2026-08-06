namespace DesktopPet.Infra.Lifecycle;

/// <summary>Serializes ownership transfer for a disposable resource without disposing it implicitly.</summary>
public sealed class OwnedResourceSlot<T> where T : class
{
    private readonly object _sync = new();
    private T? _current;

    public T? Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    public bool TryPublish(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_sync)
        {
            if (_current is not null) return false;
            _current = resource;
            return true;
        }
    }

    public T? Take()
    {
        lock (_sync)
        {
            var resource = _current;
            _current = null;
            return resource;
        }
    }

    public bool TryTake(T expected, out T? resource)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_sync)
        {
            if (!ReferenceEquals(_current, expected))
            {
                resource = null;
                return false;
            }
            resource = _current;
            _current = null;
            return true;
        }
    }
}
