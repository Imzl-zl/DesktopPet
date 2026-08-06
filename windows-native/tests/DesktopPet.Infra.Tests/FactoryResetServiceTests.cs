using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

public sealed class FactoryResetServiceTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(), "DesktopPet.Reset.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Reset_DeletesOnlyInjectedRootAndAllNamespacedCredentials()
    {
        var root = Path.Combine(_sandbox, "DesktopPet");
        var sibling = Path.Combine(_sandbox, "keep");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(root, "providers.json"), "secret reference");
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var credentials = new InMemoryCredentialStore();
        credentials.Set("provider/a", "secret-a");
        credentials.Set("provider/b", "secret-b");

        var result = new FactoryResetService(root, credentials).Reset();

        Assert.True(result.DataDirectoryExisted);
        Assert.Equal(2, result.CredentialsDeleted);
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.Null(credentials.Get("provider/a"));
    }

    [Fact]
    public void Reset_IsIdempotentWhenDataAndCredentialsAreAlreadyGone()
    {
        var root = Path.Combine(_sandbox, "DesktopPet");
        var credentials = new InMemoryCredentialStore();
        var service = new FactoryResetService(root, credentials);

        var first = service.Reset();
        var second = service.Reset();

        Assert.False(first.DataDirectoryExisted);
        Assert.False(second.DataDirectoryExisted);
        Assert.Equal(0, second.CredentialsDeleted);
    }

    [Fact]
    public void CredentialFailure_RestoresStagedData()
    {
        var root = Path.Combine(_sandbox, "DesktopPet");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "preserve");
        var service = new FactoryResetService(root, new FailingCleaner());

        var error = Assert.Throws<FactoryResetException>(() => service.Reset());

        Assert.Equal("delete-credentials", error.Stage);
        Assert.False(error.RollbackComplete);
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Empty(Directory.GetDirectories(_sandbox, "DesktopPet.reset-*"));
    }

    [Fact]
    public void Constructor_RejectsFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_sandbox))!;
        Assert.Throws<ArgumentException>(() =>
            new FactoryResetService(root, new InMemoryCredentialStore()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
    }

    private sealed class FailingCleaner : ICredentialNamespaceCleaner
    {
        public int DeleteAll() => throw new CredentialStoreException("删除", 5);
    }
}
