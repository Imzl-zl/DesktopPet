using System.IO;
using System.Reflection;
using DesktopPet.App.Rendering;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Tests;

public sealed class SpriteLoaderTests
{
    [Fact]
    public async Task LoadLocalAsync_LoadsSavedSpriteAndPopulatesTheSharedCache()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "single-row.png");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "DesktopPet.App.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var loader = new SpriteLoader(dataDirectory);
            loader.SaveLocal("preview", await File.ReadAllBytesAsync(fixture));

            var loadLocalAsync = typeof(SpriteLoader).GetMethod(
                "LoadLocalAsync", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(loadLocalAsync);

            var task = Assert.IsAssignableFrom<Task<SpriteSheet?>>(loadLocalAsync.Invoke(
                loader, ["preview", CancellationToken.None]));
            var sheet = await task;

            Assert.NotNull(sheet);
            Assert.Same(sheet, loader.TryGetCached("preview"));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
