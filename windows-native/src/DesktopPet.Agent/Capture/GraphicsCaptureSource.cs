using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

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
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private readonly object _gate = new();
    private readonly CaptureCadenceGate _copyCadence;
    private GraphicsCaptureItem? _item;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;
    private IDirect3DDevice? _device;
    private SoftwareBitmap? _latest;
    private GraphicsCaptureState _state;
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

            _device = CreateD3DDevice();
            _item = CreateCaptureItemForMonitor(MonitorFromPoint(0, 0, 1 /* MONITOR_DEFAULTTONEAREST */));
            _item.Closed += OnItemClosed;
            _framePool = Direct3D11CaptureFramePool.Create(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();
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

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hmon)
    {
        var factory = GetActivationFactory(typeof(GraphicsCaptureItem));
        try
        {
            var interop = (IGraphicsCaptureItemInterop)factory;
            var iid = IID_GraphicsCaptureItem; // 固定 IID（typeof().GUID 随投影版本漂移）
            var itemAbi = IntPtr.Zero;
            var hr = interop.CreateForMonitor(hmon, ref iid, out itemAbi);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
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
        if (framePool is not null) framePool.FrameArrived -= OnFrameArrived;
        session?.Dispose();
        framePool?.Dispose();
        device?.Dispose();
        latest?.Dispose();
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
