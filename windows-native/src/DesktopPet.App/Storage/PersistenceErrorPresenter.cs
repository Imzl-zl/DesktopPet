using System.Diagnostics;
using System.Windows;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Storage;

namespace DesktopPet.App;

/// <summary>统一把持久化故障呈现给用户；存储层仍负责抛出，不在 UI 层静默吞掉。</summary>
internal static class PersistenceErrorPresenter
{
    private static int _dialogOpen;
    private static I18nService _i18n = new();

    public static void Configure(I18nService i18n) => _i18n = i18n;

    public static void Report(JsonStoreException exception, Window? owner = null)
    {
        Debug.WriteLine($"JSON persistence failed ({exception.Operation}): {exception.FilePath}: {exception.InnerException?.Message}");
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        void Show()
        {
            if (Interlocked.Exchange(ref _dialogOpen, 1) != 0) return;
            try
            {
                var i18n = _i18n;
                MessageBox.Show(
                    owner,
                    i18n.Format(
                        "无法{0}桌宠数据。请检查数据目录权限或磁盘空间。\n\n{1}",
                        i18n.T(exception.Operation),
                        exception.FilePath),
                    "DesktopPet",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Volatile.Write(ref _dialogOpen, 0);
            }
        }

        if (app.Dispatcher.CheckAccess())
        {
            Show();
        }
        else
        {
            _ = app.Dispatcher.BeginInvoke(Show);
        }
    }
}
