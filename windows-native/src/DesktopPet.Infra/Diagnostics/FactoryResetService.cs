using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Diagnostics;

public sealed class FactoryResetException : IOException
{
    public FactoryResetException(
        string stage,
        string? residualPath,
        bool rollbackComplete,
        Exception innerException)
        : base($"Factory reset failed during {stage}", innerException)
    {
        Stage = stage;
        ResidualPath = residualPath;
        RollbackComplete = rollbackComplete;
    }

    public string Stage { get; }
    public string? ResidualPath { get; }
    public bool RollbackComplete { get; }
}

public sealed record FactoryResetResult(bool DataDirectoryExisted, int CredentialsDeleted);

public sealed class FactoryResetService
{
    private readonly string _root;
    private readonly ICredentialNamespaceCleaner _credentials;

    public FactoryResetService(string root, ICredentialNamespaceCleaner credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(_root, Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase)
            || Directory.GetParent(_root) is null)
        {
            throw new ArgumentException("The reset root must be a non-root directory", nameof(root));
        }
        _credentials = credentials;
    }

    public FactoryResetResult Reset()
    {
        RejectReparsePoint();
        var existed = Directory.Exists(_root);
        string? staged = null;
        if (existed)
        {
            staged = _root + ".reset-" + Guid.NewGuid().ToString("N");
            try { Directory.Move(_root, staged); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FactoryResetException("stage-data", _root, rollbackComplete: true, ex);
            }
        }

        int deleted;
        try
        {
            deleted = _credentials.DeleteAll();
        }
        catch (IOException ex)
        {
            var dataRestored = RestoreStagedData(staged);
            throw new FactoryResetException(
                "delete-credentials",
                dataRestored ? null : staged,
                rollbackComplete: false,
                ex);
        }

        if (staged is not null)
        {
            try { Directory.Delete(staged, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FactoryResetException(
                    "delete-data",
                    staged,
                    rollbackComplete: false,
                    ex);
            }
        }
        DeletePriorResiduals();
        return new FactoryResetResult(existed, deleted);
    }

    private bool RestoreStagedData(string? staged)
    {
        if (staged is null) return true;
        try
        {
            if (Directory.Exists(_root)) return false;
            Directory.Move(staged, _root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RejectReparsePoint()
    {
        if (!Directory.Exists(_root)) return;
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The reset root cannot be a reparse point", nameof(_root));
    }

    private void DeletePriorResiduals()
    {
        var parent = Directory.GetParent(_root)!.FullName;
        if (!Directory.Exists(parent)) return;
        var name = Path.GetFileName(_root);
        foreach (var residual in Directory.EnumerateDirectories(parent, name + ".reset-*"))
        {
            if ((File.GetAttributes(residual) & FileAttributes.ReparsePoint) != 0)
            {
                throw new FactoryResetException(
                    "delete-residual-data",
                    residual,
                    rollbackComplete: false,
                    new IOException("Refusing to delete a reset residual reparse point"));
            }
            try { Directory.Delete(residual, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FactoryResetException(
                    "delete-residual-data",
                    residual,
                    rollbackComplete: false,
                    ex);
            }
        }
    }
}
