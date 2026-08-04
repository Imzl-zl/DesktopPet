using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace DesktopPet.Agent.Capture;

/// <summary>
/// 真实屏幕捕获（迁移计划 §6.4）：IGraphicsCaptureItemInterop.CreateForMonitor
/// （微软官方 Win32 互操作路径，Windows.UI.Composition-Win32-Samples 同款）
/// + Direct3D11CaptureFramePool → 320×180 灰度缩略图。
/// 注意：必须在 STA + 消息泵线程创建（FrameArrived 事件依赖消息循环；
/// AgentHost 用 WPF Dispatcher.Run 提供泵）。不可用 → 抛 NotSupportedException。
/// </summary>
public sealed class GraphicsCaptureSource : IScreenCaptureSource, IDisposable
{
    public const int ThumbWidth = 320;
    public const int ThumbHeight = 180;

    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private readonly object _gate = new();
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _framePool;
    private IDirect3DDevice? _device;
    private SoftwareBitmap? _latest;
    private bool _hasFrame;
    private bool _disposed;

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    public GraphicsCaptureSource()
    {
        if (!IsSupported())
        {
            throw new NotSupportedException("当前设备不支持 Windows.Graphics.Capture");
        }

        _device = CreateD3DDevice();
        var item = CreateCaptureItemForMonitor(MonitorFromPoint(0, 0, 1 /* MONITOR_DEFAULTTONEAREST */));
        _framePool = Direct3D11CaptureFramePool.Create(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();
    }

    public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
    {
        SoftwareBitmap? bitmap;
        lock (_gate)
        {
            if (!_hasFrame) return Task.FromResult<CapturedFrame?>(null);
            bitmap = _latest;
            _latest = null;
            _hasFrame = false;
        }

        try
        {
            using (bitmap)
            {
                var gray = ConvertToGrayThumbnail(bitmap!); // lock 内已保证非 null（_hasFrame=true）
                return Task.FromResult<CapturedFrame?>(new CapturedFrame(ThumbWidth, ThumbHeight, gray));
            }
        }
        catch
        {
            return Task.FromResult<CapturedFrame?>(null); // 单帧转换失败：下一拍重试
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
            lock (_gate)
            {
                _latest?.Dispose();
                _latest = bitmap;
                _hasFrame = true;
            }
        }
        catch
        {
            // 单帧捕获失败忽略（下一帧再试）
        }
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
        var interop = (IGraphicsCaptureItemInterop)factory;
        var iid = IID_GraphicsCaptureItem; // 固定 IID（typeof().GUID 随投影版本漂移）
        var hr = interop.CreateForMonitor(hmon, ref iid, out var itemAbi);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        try
        {
            return GraphicsCaptureItem.FromAbi(itemAbi);
        }
        finally
        {
            Marshal.Release(itemAbi);
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
            // WARP 软件回退（虚拟机/无 GPU 环境）
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out d3dDevice, out _, out _);
            if (hr != 0) throw new InvalidOperationException($"D3D11CreateDevice 失败 hr=0x{hr:X8}");
        }

        var iidDxgi = IID_IDXGIDevice;
        Marshal.QueryInterface(d3dDevice, ref iidDxgi, out var dxgiDevice);
        Marshal.Release(d3dDevice);
        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDevice);
            if (hr < 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 hr=0x{hr:X8}");
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _framePool?.Dispose();
        _device?.Dispose();
        lock (_gate) _latest?.Dispose();
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
