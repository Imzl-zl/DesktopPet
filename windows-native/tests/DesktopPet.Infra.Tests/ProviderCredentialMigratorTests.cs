using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

public sealed class ProviderCredentialMigratorTests
{
    [Fact]
    public void SharedLegacyReference_IsCopiedToUniqueStableReferencesBeforePublish()
    {
        var source = new ProvidersFileModel
        {
            Models =
            [
                Model("model", "model-key", "one"),
                Model("model", "model-key", "two"),
            ],
        };
        var credentials = new InMemoryCredentialStore();
        credentials.Set("model-key", "secret");
        ProvidersFileModel? saved = null;
        var migrator = new ProviderCredentialMigrator(
            () => source,
            value => saved = value,
            credentials);

        var result = migrator.Migrate();

        Assert.True(result.Changed);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Models.Select(model => model.Id).Distinct().Count());
        Assert.Equal(2, saved.Models.Select(model => model.ApiKeyRef).Distinct().Count());
        Assert.All(saved.Models, model =>
        {
            Assert.StartsWith($"provider/model/{model.Id}/", model.ApiKeyRef);
            Assert.Equal("secret", credentials.Get(model.ApiKeyRef));
        });
        Assert.Null(credentials.Get("model-key"));
    }

    [Fact]
    public void LegacyImageCredential_MigratesToDedicatedReference()
    {
        var source = new ProvidersFileModel
        {
            Image = new ImageGenConfig(
                "https://example.com/v1",
                ProviderCredentialRefs.LegacyImage,
                "image-model"),
        };
        var credentials = new InMemoryCredentialStore();
        credentials.Set(ProviderCredentialRefs.LegacyImage, "image-secret");
        ProvidersFileModel? saved = null;

        var result = new ProviderCredentialMigrator(
            () => source, value => saved = value, credentials).Migrate();

        Assert.True(result.Changed);
        Assert.Equal(ProviderCredentialRefs.Image, saved!.Image!.ApiKeyRef);
        Assert.Equal("image-secret", credentials.Get(ProviderCredentialRefs.Image));
        Assert.Null(credentials.Get(ProviderCredentialRefs.LegacyImage));
    }

    [Fact]
    public void ValidImagePlusInvalidLegacyModel_DoesNotPublishOrDeleteCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopPet.ProviderMigration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "providers.json");
            var json = """
                {
                  "models": [{
                    "id": "legacy-model",
                    "name": "recoverable",
                    "baseUrl": "",
                    "apiKeyRef": "model-key",
                    "modelName": "model",
                    "capabilities": "chat",
                    "isDefault": true
                  }],
                  "image": {
                    "baseUrl": "https://example.com/v1",
                    "apiKeyRef": "image-key",
                    "modelName": "image-model",
                    "size": "1024x1024"
                  }
                }
                """;
            File.WriteAllText(path, json);
            var credentials = new InMemoryCredentialStore();
            credentials.Set(ProviderCredentialRefs.LegacyModel, "model-secret");
            credentials.Set(ProviderCredentialRefs.LegacyImage, "image-secret");

            var result = new ProviderCredentialMigrator(
                new FileJsonStore(directory), credentials).Migrate();

            Assert.True(result.SkippedUnsafeSource);
            Assert.False(result.Changed);
            Assert.Equal(json, File.ReadAllText(path));
            Assert.Equal("model-secret", credentials.Get(ProviderCredentialRefs.LegacyModel));
            Assert.Equal("image-secret", credentials.Get(ProviderCredentialRefs.LegacyImage));
            Assert.Null(credentials.Get(ProviderCredentialRefs.Image));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmptyOrUnparseableModel_DoesNotDeleteRecoverableLegacyCredentials()
    {
        var credentials = new InMemoryCredentialStore();
        credentials.Set(ProviderCredentialRefs.LegacyModel, "recoverable");

        var result = new ProviderCredentialMigrator(
            () => new ProvidersFileModel(),
            _ => throw new InvalidOperationException("must not publish"),
            credentials).Migrate();

        Assert.False(result.Changed);
        Assert.Equal("recoverable", credentials.Get(ProviderCredentialRefs.LegacyModel));
    }

    [Fact]
    public void ExistingCopiedCredential_MakesRetryIdempotent()
    {
        var source = new ProvidersFileModel { Models = [Model("stable", "model-key", "one")] };
        var credentials = new InMemoryCredentialStore();
        credentials.Set("model-key", "secret");
        var target = ProviderCredentialRefs.ForModel("stable");
        credentials.Set(target, "secret");
        ProvidersFileModel? saved = null;

        var result = new ProviderCredentialMigrator(
            () => source, value => saved = value, credentials).Migrate();

        Assert.True(result.Changed);
        Assert.Equal(target, saved!.Models[0].ApiKeyRef);
        Assert.Equal("secret", credentials.Get(target));
    }

    [Fact]
    public void ConflictingTargetCredential_DoesNotPublishOrDeleteLegacy()
    {
        var source = new ProvidersFileModel { Models = [Model("stable", "model-key", "one")] };
        var credentials = new InMemoryCredentialStore();
        credentials.Set("model-key", "old-secret");
        credentials.Set(ProviderCredentialRefs.ForModel("stable"), "different-secret");
        var saves = 0;
        var migrator = new ProviderCredentialMigrator(
            () => source, _ => saves++, credentials);

        var ex = Assert.Throws<CredentialMigrationException>(() => migrator.Migrate());

        Assert.Equal("target-conflict", ex.Code);
        Assert.Equal(0, saves);
        Assert.Equal("old-secret", credentials.Get("model-key"));
    }

    private static ProviderConfig Model(string id, string keyRef, string name)
        => new(
            id,
            name,
            "https://example.com/v1",
            keyRef,
            name,
            ModelCapabilities.Chat,
            IsDefault: false);
}
