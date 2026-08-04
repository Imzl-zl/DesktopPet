using System.IO;
using System.Windows;
using DesktopPet.App.Interop;
using DesktopPet.App.Rendering;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;

namespace DesktopPet.App.Windows;

/// <summary>
/// 多实例窗口管理器：1:1 继承 Rust sync_desktop_pet_windows 语义 —— 每宠物一
/// 窗口（label 语义 = pet-{id}）、创建缺失/关闭多余/独立显隐；全局显隐对齐
/// set_desktop_pets_visible（pets-visible 文件 + 全窗口 show/hide）。位置：
/// 已保存（物理像素）优先，否则右下角默认排布。
/// </summary>
public sealed class PetWindowManager
{
    private readonly Dictionary<string, PetWindow> _windows = new();
    private readonly Dictionary<string, PetPosition> _positions;
    private readonly IJsonStore _store;
    private readonly SpriteLoader _spriteLoader;
    private bool _globallyVisible = true;
    private FloatingBallWindow? _floatingBall;

    /// <summary>Phase 2 内置预设池（Phase 4 设置页可编辑，对齐 ap_quick_bubbles）。</summary>
    public string PresetPoolJson { get; set; } =
        "[\"辛苦了~\",\"摸摸头\",\"加油！\",\"休息一下吧\",\"盯——\",\"(*´∀`*)\"]";

    public bool GloballyVisible => _globallyVisible;

    public event Action<bool>? GlobalVisibilityChanged;

    public PetWindowManager(IJsonStore store, SpriteLoader spriteLoader)
    {
        _store = store;
        _spriteLoader = spriteLoader;
        _positions = store.LoadPositions();
        _globallyVisible = store.LoadGlobalVisibility();
    }

    /// <summary>reconcile：wanted 集合 = 当前 store 实例；多退少补 + 显隐同步。</summary>
    public void Reconcile(PetStore store, bool globallyVisible)
    {
        _globallyVisible = globallyVisible;
        var wantedIds = new HashSet<string>(store.Instances.Select(i => i.Id));

        foreach (var (id, window) in _windows.ToList())
        {
            if (!wantedIds.Contains(id))
            {
                window.Close();
                _windows.Remove(id);
            }
        }

        var index = 0;
        foreach (var instance in store.Instances)
        {
            if (!_windows.TryGetValue(instance.Id, out var window))
            {
                window = new PetWindow(instance, _spriteLoader, OnDragFinished);
                window.SetImportHandler(ImportSprite);
                window.SetBroadcastQuickBubble(BroadcastQuickBubble);
                window.SetClickAction("none"); // Phase 4 设置页配置 LEFT_CLICK_KEY
                window.SetQuickPresetPool(PresetPoolJson);
                _windows[instance.Id] = window;
                PositionAndShow(window, instance, index);
            }
            else
            {
                var visible = instance.Visible && _globallyVisible;
                if (visible != window.IsVisible) window.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
            index++;
        }
    }

    private void PositionAndShow(PetWindow window, PetInstance instance, int index)
    {
        var position = _positions.TryGetValue(instance.Id, out var saved)
            ? (saved.X, saved.Y)
            : DefaultPosition(index);
        window.ShowAt(position.X, position.Y);
        if (!(instance.Visible && _globallyVisible)) window.Hide();
    }

    /// <summary>默认排布：主屏右下角依次向左上堆叠（对齐 Rust default_pet_position）。</summary>
    private static (int X, int Y) DefaultPosition(int index)
    {
        var (width, height) = NativeMethods.PrimaryWorkAreaSize();
        var (x, y) = WindowPlacement.DefaultPetPosition(width, height, index);
        return ((int)x, (int)y);
    }

    /// <summary>全局显隐（托盘）：写 pets-visible 文件 + 全部宠物窗口 show/hide。
    /// 隐藏后恢复位置不漂移（窗口坐标不变，只切 Visibility）。</summary>
    public void SetGlobalVisible(bool visible)
    {
        _globallyVisible = visible;
        _store.SaveGlobalVisibility(visible);
        foreach (var window in _windows.Values)
        {
            window.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        }
        GlobalVisibilityChanged?.Invoke(visible);
    }

    /// <summary>bench 模式用：摆单只宠物到指定物理坐标并返回窗口。</summary>
    public PetWindow ShowBenchPet(int physicalX, int physicalY)
    {
        var instance = new PetInstance
        {
            Id = PetStoreModel.NewPetInstanceId(),
            Name = "Bench Pet",
            SpriteSlug = "placeholder",
            Visible = true,
            Size = 100,
            RoamEnabled = false,
            RoamMode = RoamMode.Stay,
            RoamSpeed = 5,
            WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
            WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
            ReactsToActivity = false,
        };
        var window = new PetWindow(instance, _spriteLoader, OnDragFinished);
        window.SetImportHandler(ImportSprite);
        window.SetBroadcastQuickBubble(BroadcastQuickBubble);
        window.SetClickAction("none");
        window.SetQuickPresetPool(PresetPoolJson);
        _windows[instance.Id] = window;
        window.ShowAt(physicalX, physicalY);
        return window;
    }

    public IReadOnlyList<PetWindow> VisibleWindows => _windows.Values.ToList();

    private void OnDragFinished(PetWindow window, int x, int y)
    {
        var position = new PetPosition(x, y);
        _positions[window.PetId] = position;
        _store.SavePositions(PetPositionsFile.Update(_positions, window.PetId, position));
    }

    /// <summary>
    /// 导入精灵：保存本地缓存 + 创建宠物实例 + 重建窗口（拖拽文件到宠物窗口触发）。
    /// </summary>
    public void ImportSprite(byte[] bytes, string suggestedName)
    {
        var id = PetStoreModel.NewPetInstanceId();
        _spriteLoader.SaveLocal(id, bytes);
        var instance = new PetInstance
        {
            Id = id,
            Name = suggestedName.Length > 0 ? suggestedName[..Math.Min(40, suggestedName.Length)] : "New Pet",
            SpriteSlug = id,
            Visible = true,
            Size = 100,
            RoamEnabled = true,
            RoamMode = RoamMode.Wander,
            RoamSpeed = 5,
            WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
            WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
            ReactsToActivity = true,
        };
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        store = PetStoreModel.CreatePetInstance(store, instance);
        _store.SavePetStore(store);
        Reconcile(store, _globallyVisible);
    }

    /// <summary>快速气泡广播：浮球发送 → 全员同时说（对齐 emit(&quot;quick-bubble&quot; target all）。</summary>
    public void BroadcastQuickBubble(string text)
    {
        foreach (var window in _windows.Values)
        {
            window.ShowBroadcastQuickBubble(text);
        }
    }

    /// <summary>创建浮球窗口（球内活体宠物 = 选中实例精灵）。</summary>
    public void CreateFloatingBall(string dataDirectory)
    {
        if (_floatingBall is not null) return;
        _floatingBall = new FloatingBallWindow(
            BroadcastQuickBubble,
            () => PresetPoolJson,
            SelectedSpriteBytes,
            dataDirectory);
        _floatingBall.Show();
    }

    /// <summary>选中实例的精灵文件路径（浮球内活体宠物）。</summary>
    private string? SelectedSpriteBytes()
    {
        var store = _store.LoadPetStore();
        var selected = store is null ? null : PetStoreModel.SelectedPetInstance(store);
        if (selected is null && store is { Instances.Count: > 0 }) selected = store.Instances[0];
        if (selected is null) return null;
        var path = Path.Combine(_spriteLoader.SpritesDirectory, $"{selected.SpriteSlug}.png");
        return File.Exists(path) ? path : null;
    }

    public void Shutdown()
    {
        _floatingBall?.Close();
        _floatingBall = null;
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
        _windows.Clear();
    }
}
