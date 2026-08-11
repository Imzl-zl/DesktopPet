using DesktopPet.Core.Ai;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;

namespace DesktopPet.App.Ai;

/// <summary>Immutable provider/pipeline generation published by <see cref="AiCoordinator"/>.
/// Signature = 决定 runtime 形态的关键输入（Enabled/ProviderId/OutputMode/连接与人格引用）；
/// 相同签名 = 无需重建（修复：原实现每次设置保存都重建 scheduler/worker 池）。</summary>
internal sealed class AiRuntimeGeneration(
    ModelRequestScheduler? scheduler,
    ChatPipeline? pipeline,
    IImageProvider? imageProvider,
    string signature) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public ModelRequestScheduler? Scheduler { get; } = scheduler;
    public ChatPipeline? Pipeline { get; } = pipeline;
    public IImageProvider? ImageProvider { get; } = imageProvider;
    public CancellationToken LifetimeToken => _lifetime.Token;
    public string Signature { get; } = signature;

    public void RequestStop() => _lifetime.Cancel();

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (Scheduler is not null) await Scheduler.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>运行时形态签名：关键输入引用变化才需要重建。
    /// 注意：OutputMode 只影响 Agent 启停（reconcile 后半段处理），不影响 runtime 本体——
    /// danmaku/chat/bubble 共用同一 provider 运行时。</summary>
    public static string SignatureOf(
        AppSettings settings,
        ProvidersFileModel providers,
        PersonasFileModel personas)
        => $"{settings.Ai.Enabled}|{settings.Ai.ProviderId}|{ReferenceEquality(providers)}|{ReferenceEquality(personas)}";

    private static string ReferenceEquality(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString();
}
