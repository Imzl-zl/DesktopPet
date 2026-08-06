using DesktopPet.Core.Ai;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.App.Ai;

/// <summary>Immutable provider/pipeline generation published by <see cref="AiCoordinator"/>.</summary>
internal sealed class AiRuntimeGeneration(
    ModelRequestScheduler? scheduler,
    ChatPipeline? pipeline,
    IImageProvider? imageProvider) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public ModelRequestScheduler? Scheduler { get; } = scheduler;
    public ChatPipeline? Pipeline { get; } = pipeline;
    public IImageProvider? ImageProvider { get; } = imageProvider;
    public CancellationToken LifetimeToken => _lifetime.Token;

    public void RequestStop() => _lifetime.Cancel();

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (Scheduler is not null) await Scheduler.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
