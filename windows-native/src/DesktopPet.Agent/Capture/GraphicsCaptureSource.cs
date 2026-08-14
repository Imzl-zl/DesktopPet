using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace DesktopPet.Agent.Capture;

public enum GraphicsCaptureState
{
    Running,
    Faulted,
    Disposed,
}

/// <summary>
/// 真实屏幕捕获（迁移计划 §6.4）：IGraphicsCaptureItemInterop.CreateForMonitor
/// （微软官方 Win32 互操作路径，Windows.UI.Composition-Win32-Samples 同款）
/// + Direct3D11CaptureFramePool → 320×180 灰度缩略图。
/// 注意：必须在 STA + 消息泵线程创建（FrameArrived 事件依赖消息循环；
/// AgentHost 用 WPF Dispatcher.Run 提供泵）。不可用 → 抛 NotSupportedException。
/// </summary>
public sealed class GraphicsCaptureSource :
    IScreenCaptureSource,
    ICaptureCadenceSource,
    ICaptureFaultSource,
    IDisposable
{
    public const int ThumbWidth = 320;
    public const int ThumbHeight = 180;

    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const int MonitorDefaultToPrimary = 1; // MONITOR_DEFAULTTOPRIMARY：固定捕获主屏
    private const int MonitorDefaultToNearest = 2; // MONITOR_DEFAULTTONEAREST
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private readonly object _gate = new();
    private readonly CaptureCadenceGate _copyCadence;
    private GraphicsCaptureItem? _item;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;
    private IDirect3DDevice? _device;
    private SoftwareBitmap? _latest;
    private Windows.Graphics.SizeInt32 _lastSize; // 上次帧池尺寸（分辨率变化检测）
    private GraphicsCaptureState _state;
    private AppCapabilityAccessStatus _captureAccess; // Programmatic 捕获权限（24H2+ 前置请求）
    private bool _hasFrame;
    private int _consecutiveFailures;

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    public GraphicsCaptureSource(
        TimeSpan? captureInterval = null,
        TimeProvider? timeProvider = null)
    {
        _copyCadence = new CaptureCadenceGate(
            captureInterval ?? TimeSpan.FromSeconds(1),
            timeProvider);
        try
        {
            if (!IsSupported())
                throw new NotSupportedException("当前设备不支持 Windows.Graphics.Capture");

            // 24H2/25H2+：程序化捕获（无 UI 的 CreateForMonitor/TryCreateFromDisplayId）
            // 必须先 RequestAccessAsync(Programmatic)（官方文档 TryCreateFrom* 前置要求；
            // 截图工具是打包应用自带 capability，unpackaged 应用需显式请求，
            // 权限状态记录在「设置 > 隐私和安全性 > 截图和屏幕录制」开关）。
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _captureAccess = GraphicsCaptureAccess.RequestAccessAsync(
                        GraphicsCaptureAccessKind.Programmatic)
                    .AsTask(cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception accessEx)
            {
                throw new InvalidOperationException(
                    $"GraphicsCaptureAccess.RequestAccessAsync failed: {accessEx.GetType().Name}: {accessEx.Message}",
                    accessEx);
            }

            _device = CreateD3DDevice();
            // 主屏（MONITOR_DEFAULTTOPRIMARY）：桌宠分析以用户主工作屏为准。
            // 原注释误标 TONEAREST(2)；行为一直是主屏，此处显式命名。
            try
            {
                _item = CreateCaptureItemForMonitor(MonitorFromPoint(0, 0, MonitorDefaultToPrimary), _captureAccess);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("GraphicsCapture init failed at CreateForMonitor", ex);
            }
            _item.Closed += OnItemClosed;
            try
            {
                _framePool = Direct3D11CaptureFramePool.Create(
                    _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("GraphicsCapture init failed at CreateFramePool", ex);
            }
            _lastSize = _item.Size; // 分辨率变化基准（FrameArrived 内比较）
            _framePool.FrameArrived += OnFrameArrived;
            try
            {
                _session = _framePool.CreateCaptureSession(_item);
                _session.StartCapture();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("GraphicsCapture init failed at StartCapture", ex);
            }
            lock (_gate) _state = GraphicsCaptureState.Running;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public GraphicsCaptureState State
    {
        get { lock (_gate) return _state; }
    }

    public event Action<Exception>? Faulted;

    public void SetCaptureInterval(TimeSpan interval) => _copyCadence.UpdateInterval(interval);

    public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SoftwareBitmap? bitmap;
        lock (_gate)
        {
            if (_state == GraphicsCaptureState.Disposed)
                throw new ObjectDisposedException(nameof(GraphicsCaptureSource));
            if (_state == GraphicsCaptureState.Faulted)
                throw new CaptureSourceUnavailableException("GraphicsCapture source is faulted");
            if (!_hasFrame) return Task.FromResult<CapturedFrame?>(null);
            bitmap = _latest;
            _latest = null;
            _hasFrame = false;
        }

        try
        {
            using (bitmap)
            {
                var gray = ConvertToGrayThumbnail(bitmap!);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return Task.FromResult<CapturedFrame?>(
                    new CapturedFrame(ThumbWidth, ThumbHeight, gray));
            }
        }
        catch (Exception ex)
        {
            RegisterFailure(ex);
            return Task.FromResult<CapturedFrame?>(null);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (_gate)
            {
                if (_state != GraphicsCaptureState.Running) return;
            }

            // 分辨率/DPI/热插拔变化：item.Size 与帧池尺寸不一致时用新尺寸重建帧池。
            // GraphicsCaptureItem 只有 Closed 事件（无 SizeChanged），官方示例即在此处
            // 比较尺寸后 Recreate（原实现缺失 → 分辨率变化后帧内容缩放/裁剪错误）。
            var device = _device;
            var item = _item;
            if (device is not null && item is not null && item.Size != _lastSize)
            {
                _framePool?.Recreate(
                    device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);
                _lastSize = item.Size;
            }

            if (!_copyCadence.TryAcquire())
            {
                // 节流窗口内的帧：立即取出并释放，不能滞留池中——
                // 缓冲=1 时滞留帧会占满 FramePool，后续新帧被丢弃且 FrameArrived 不再触发
                // （永久死锁：分析引擎永远拿不到新帧）。
                using var dropped = sender.TryGetNextFrame();
                return;
            }
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            _ = CopyFrameAsync(frame);
        }
        catch (Exception ex)
        {
            RegisterFailure(ex);
        }
    }

    /// <summary>
    /// 异步拷贝：CreateCopyFromSurfaceAsync 是异步 API，在消息泵线程上 await 不阻塞；
    /// 原实现 .GetAwaiter().GetResult() 同步等待会卡住同线程的 capture 生命周期操作
    /// （创建/销毁 capture 源）。_copyCadence 节流保证同时最多一个拷贝在飞。
    /// </summary>
    private async Task CopyFrameAsync(Direct3D11CaptureFrame frame)
    {
        try
        {
            using (frame)
            {
                var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                SoftwareBitmap? previous;
                lock (_gate)
                {
                    if (_state != GraphicsCaptureState.Running)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    previous = _latest;
                    _latest = bitmap;
                    _hasFrame = true;
                }
                previous?.Dispose();
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
        }
        catch (Exception ex)
        {
            RegisterFailure(ex);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
        => MarkFault(new InvalidOperationException("GraphicsCaptureItem closed"));

    private void RegisterFailure(Exception exception)
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= 3)
            MarkFault(exception);
    }

    private void MarkFault(Exception exception)
    {
        var notify = false;
        lock (_gate)
        {
            if (_state is GraphicsCaptureState.Disposed or GraphicsCaptureState.Faulted) return;
            _state = GraphicsCaptureState.Faulted;
            notify = true;
        }
        if (notify) Faulted?.Invoke(exception);
    }

    private static byte[] ConvertToGrayThumbnail(SoftwareBitmap source)
    {
        // BGRA8 → 320×180 灰度：CopyToBuffer 取紧凑像素（无 stride 填充），
        // 最近邻缩放 + 亮度权重（Rec.601）。
        var pixelCount = source.PixelWidth * source.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(pixelCount * 4));
        source.CopyToBuffer(buffer);
        var bytes = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        var gray = new byte[ThumbWidth * ThumbHeight];
        for (var y = 0; y < ThumbHeight; y++)
        {
            var srcY = y * source.PixelHeight / ThumbHeight;
            for (var x = 0; x < ThumbWidth; x++)
            {
                var srcX = x * source.PixelWidth / ThumbWidth;
                var p = (srcY * source.PixelWidth + srcX) * 4;
                var (b, g, r) = (bytes[p], bytes[p + 1], bytes[p + 2]);
                gray[y * ThumbWidth + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            }
        }
        return gray;
    }

    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hmon, AppCapabilityAccessStatus captureAccess)
    {
        // 首选官方推荐路径：TryCreateFromDisplayId（微软 Q&A 26100.7623+ 推荐替代 CreateForMonitor；
        // 官方文档：调用前必须先 RequestAccessAsync(Programmatic)，此处 captureAccess 已请求）。
        try
        {
            var mi = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(hmon);
            // Microsoft.UI.DisplayId 与 Windows.Graphics.DisplayId 二进制兼容（均为 uint64 opaque token）
            var wd = new Windows.Graphics.DisplayId { Value = mi.Value };
            var viaDisplayId = GraphicsCaptureItem.TryCreateFromDisplayId(wd);
            if (viaDisplayId is not null)
            {
                return viaDisplayId;
            }
            // 返回 null：继续回退 interop 路径
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"TryCreateFromDisplayId unavailable hmon=0x{hmon.ToInt64():X} captureAccess={captureAccess}", ex);
        }

        var factory = GetActivationFactory(typeof(GraphicsCaptureItem));
        try
        {
            var interop = (IGraphicsCaptureItemInterop)factory;
            var iid = IID_GraphicsCaptureItem; // 固定 IID（typeof().GUID 随投影版本漂移）
            var itemAbi = IntPtr.Zero;
            var hr = interop.CreateForMonitor(hmon, ref iid, out itemAbi);
            if (hr < 0)
            {
                // GetExceptionForHR 对未知 HRESULT 可能返回 null（throw null → NRE 丢真实原因），
                // 先把 HRESULT 原文打进异常链。
                var ex = Marshal.GetExceptionForHR(hr);
                throw new InvalidOperationException($"CreateForMonitor failed hr=0x{hr:X8} hmon=0x{hmon.ToInt64():X}", ex);
            }
            if (itemAbi == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateForMonitor returned S_OK but empty item hmon=0x{hmon.ToInt64():X} captureAccess={captureAccess}");
            }
            try
            {
                return GraphicsCaptureItem.FromAbi(itemAbi);
            }
            finally
            {
                if (itemAbi != IntPtr.Zero) Marshal.Release(itemAbi);
            }
        }
        finally
        {
            if (Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static object GetActivationFactory(Type type)
    {
        // IActivationFactory IID（CsWinRT 投影中无此类型，用常量）
        var iid = new Guid("00000035-0000-0000-c000-000000000046");
        var hrCreate = WindowsCreateString(type.FullName!, (uint)type.FullName!.Length, out var hstring);
        if (hrCreate < 0) throw Marshal.GetExceptionForHR(hrCreate)!;
        try
        {
            var hr = RoGetActivationFactory(hstring, ref iid, out var factory);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            var obj = Marshal.GetObjectForIUnknown(factory); // RCW AddRef
            Marshal.Release(factory);                        // 平衡原始引用
            return obj;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static IDirect3DDevice CreateD3DDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out var d3dDevice, out _, out _);
        if (hr != 0)
        {
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
            // WARP 软件回退（虚拟机/无 GPU 环境）
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out d3dDevice, out _, out _);
            if (hr != 0)
            {
                if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
                throw new InvalidOperationException($"D3D11CreateDevice 失败 hr=0x{hr:X8}");
            }
        }

        var iidDxgi = IID_IDXGIDevice;
        var queryHr = Marshal.QueryInterface(d3dDevice, ref iidDxgi, out var dxgiDevice);
        Marshal.Release(d3dDevice);
        if (queryHr < 0 || dxgiDevice == IntPtr.Zero)
        {
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
            throw new InvalidOperationException($"QueryInterface IDXGIDevice 失败 hr=0x{queryHr:X8}");
        }

        var graphicsDevice = IntPtr.Zero;
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice);
            if (hr < 0 || graphicsDevice == IntPtr.Zero)
                throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 hr=0x{hr:X8}");
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            if (graphicsDevice != IntPtr.Zero) Marshal.Release(graphicsDevice);
            Marshal.Release(dxgiDevice);
        }
    }

    public void Dispose()
    {
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? framePool;
        GraphicsCaptureItem? item;
        IDirect3DDevice? device;
        SoftwareBitmap? latest;
        lock (_gate)
        {
            if (_state == GraphicsCaptureState.Disposed) return;
            _state = GraphicsCaptureState.Disposed;
            session = _session;
            framePool = _framePool;
            item = _item;
            device = _device;
            latest = _latest;
            _session = null;
            _framePool = null;
            _item = null;
            _device = null;
            _latest = null;
            _hasFrame = false;
        }

        if (item is not null) item.Closed -= OnItemClosed;
        if (item is not null) item.Closed -= OnItemClosed;
        if (framePool is not null) framePool.FrameArrived -= OnFrameArrived;
        session?.Dispose();
        framePool?.Dispose();
        device?.Dispose();
        latest?.Dispose();
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr value);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr value);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint numFeatureLevels, uint sdkVersion,
        out IntPtr ppDevice, out IntPtr pFeatureLevel, out IntPtr ppImmediateContext);

    // 微软官方文档：函数导出自 D3D11.dll（windows.graphics.directx.direct3d11.interop.h）
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, int flags);
}
