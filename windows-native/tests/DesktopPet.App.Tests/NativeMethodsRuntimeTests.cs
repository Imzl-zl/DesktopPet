using System.Threading;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.App.Interop;

namespace DesktopPet.App.Tests;

/// <summary>
/// NativeMethods 的运行时验证（不 mock）：直接对真实 HWND 调用，证明
/// 1) x64 user32.dll 的入口点命名（GetWindowLongPtrW/SetWindowLongPtrW 带 W 后缀）能在运行时解析——
///    无后缀的 GetWindowLongPtr 不是 x64 导出（本机 GetProcAddress 实测为 0，旧代码是潜在
///    EntryPointNotFoundException）；x86 请勿改回无后缀名。
/// 2) LibraryImport + Span&lt;char&gt; 的 GetWindowTextW marshalling 端到端可读回窗口标题。
/// </summary>
public sealed class NativeMethodsRuntimeTests
{
    private const int GwlExStyle = -20;
    private const string ProbeTitle = "DesktopPetNativeProbe";

    [Fact]
    public void GetSetWindowLongPtr_ResolveOnRealHwnd_RoundTripsStyle()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                Title = ProbeTitle,
                Width = 100,
                Height = 100,
                ShowInTaskbar = false,
            };
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(nint.Zero, hwnd);

            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, GwlExStyle);
            Assert.NotEqual(nint.Zero, exStyle); // 顶层窗口至少带 WS_EX_WINDOWEDGE 等位

            var previous = NativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
            Assert.Equal(exStyle, previous); // 设置相同值时返回旧值

            window.Close();
        });
    }

    [Fact]
    public void EnumerateVisibleWindows_ReadsWindowTitleThroughSpanCharMarshalling()
    {
        RunSta(() =>
        {
            var window = new Window
            {
                Title = ProbeTitle,
                Width = 100,
                Height = 100,
                ShowInTaskbar = false,
            };
            window.Show();

            // excludeProcessId 传 uint.MaxValue = 不过滤任何窗口，探针窗口应出现在枚举结果里；
            // 标题经 GetWindowTextW（LibraryImport + stackalloc Span<char>）读回。
            var windows = NativeMethods.EnumerateVisibleWindows(uint.MaxValue);
            Assert.Contains(windows, w => w.Title == ProbeTitle);

            window.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
