using System.Windows.Controls;
using DesktopPet.App.Localization;
using DesktopPet.Core.I18n;

namespace DesktopPet.App.Tests;

public sealed class WpfLocalizerTests
{
    [Fact]
    public void RefreshChangesTrackedStaticTextWithoutTranslatingLaterDynamicContent()
        => RunSta(() =>
        {
            var root = new StackPanel();
            var staticText = new TextBlock { Text = "快捷键" };
            root.Children.Add(staticText);
            var english = new I18nService(AppLang.En);
            WpfLocalizer.ApplyNew(root, english);
            Assert.Equal("Hotkeys", staticText.Text);

            var dynamicText = new TextBlock { Text = "快捷键" };
            root.Children.Add(dynamicText);
            var vietnamese = new I18nService(AppLang.Vi);
            WpfLocalizer.RefreshTracked(root, vietnamese);

            Assert.Equal("Phím tắt", staticText.Text);
            Assert.Equal("快捷键", dynamicText.Text);
        });

    [Fact]
    public void ApplyNewTracksButtonContentAndTooltip()
        => RunSta(() =>
        {
            var button = new Button { Content = "应用快捷键", ToolTip = "录入" };

            WpfLocalizer.ApplyNew(button, new I18nService(AppLang.En));

            Assert.Equal("Apply hotkeys", button.Content);
            Assert.Equal("Record", button.ToolTip);
        });

    [Fact]
    public void RefreshKeepsFormattedArgumentsForStaticStatusText()
        => RunSta(() =>
        {
            var root = new StackPanel();
            var text = new TextBlock();
            root.Children.Add(text);
            var service = new I18nService(AppLang.ZhHans);
            WpfLocalizer.SetFormattedText(text, "连接成功，可用模型 {0} 个", service, 3);
            WpfLocalizer.ApplyNew(root, service);

            WpfLocalizer.RefreshTracked(root, new I18nService(AppLang.En));

            Assert.Equal("Connected; 3 models available", text.Text);
        });

    [Fact]
    public void DynamicTextPresentBeforeInitialScanIsNeverTranslated()
        => RunSta(() =>
        {
            var root = new StackPanel();
            var petName = new TextBlock();
            WpfLocalizer.SetDynamicText(petName, "设置");
            root.Children.Add(petName);

            WpfLocalizer.ApplyNew(root, new I18nService(AppLang.En));
            WpfLocalizer.RefreshTracked(root, new I18nService(AppLang.Vi));

            Assert.Equal("设置", petName.Text);
        });

    [Fact]
    public void FormattedLocalizedArgumentsRefreshTooltipsAndAutomationNames()
        => RunSta(() =>
        {
            var button = new Button();
            var service = new I18nService(AppLang.ZhHans);
            WpfLocalizer.SetFormattedToolTip(
                button,
                "录入{0}快捷键",
                service,
                WpfLocalizer.Localize("显示或隐藏宠物"));
            WpfLocalizer.SetFormattedAutomationName(
                button,
                "动作格子 #{0}",
                service,
                4);

            WpfLocalizer.RefreshTracked(button, new I18nService(AppLang.En));

            Assert.Equal("Record hotkey for show or hide pets", button.ToolTip);
            Assert.Equal("Action cell #4", System.Windows.Automation.AutomationProperties.GetName(button));
        });

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
