using System.IO;
using System.Threading;
using System.Windows;
using DesktopPet.App.Bench;
using DesktopPet.App.Rendering;
using DesktopPet.App.Tray;
using DesktopPet.App.Windows;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Storage;

namespace DesktopPet.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\DesktopPet.Native.SingleInstance";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private PetWindowManager? _manager;
    private TrayController? _tray;
    private FileJsonStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例（对齐 Tauri 版 tauri_plugin_single_instance：第二实例直接退出，
        // Phase 4 接设置窗跳转）
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet");
        _store = new FileJsonStore(dataDir);

        var store = InitializeStore();
        _manager = new PetWindowManager(_store, new SpriteLoader(dataDir));
        _manager.Reconcile(store, _store.LoadGlobalVisibility());
        _tray = new TrayController(_manager);

        var args = e.Args;
        var benchIndex = Array.FindIndex(args, a => a.StartsWith("--bench-"));
        if (benchIndex >= 0)
        {
            PetWindow.BenchLogEnabled = true;
            var arg = args[benchIndex];
            var ms = 8000;
            var eq = arg.IndexOf('=');
            if (eq > 0 && int.TryParse(arg[(eq + 1)..], out var parsed)) ms = parsed;
            if (arg.StartsWith("--bench-drag")) BenchMode.RunDrag(_manager, ms);
            else if (arg.StartsWith("--bench-idle")) BenchMode.RunIdle(_manager, ms);
        }
    }

    /// <summary>
    /// 加载/初始化宠物 store。Phase 0：无任何数据时创建一只占位宠物以保证桌面
    /// 可见（Phase 3 迁移工具将接管 Tauri localStorage 数据，此处逻辑届时替换）。
    /// </summary>
    private PetStore InitializeStore()
    {
        var store = _store!.LoadPetStore();
        store = PetStoreModel.MigrateLegacyPetStore(store, legacy: null);
        if (store.Instances.Count == 0)
        {
            store = PetStoreModel.CreatePetInstance(store, new PetInstance
            {
                Id = PetStoreModel.NewPetInstanceId(),
                Name = "Desktop Pet",
                SpriteSlug = "placeholder",
                Visible = true,
                Size = 100,
                RoamEnabled = true,
                RoamMode = RoamMode.Wander,
                RoamSpeed = 5,
                WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
                WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
                ReactsToActivity = true,
            });
            _store.SavePetStore(store);
        }
        return store;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _manager?.Shutdown();
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
