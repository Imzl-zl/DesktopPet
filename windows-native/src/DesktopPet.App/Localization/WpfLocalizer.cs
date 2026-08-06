using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopPet.Core.I18n;

namespace DesktopPet.App.Localization;

/// <summary>
/// Tracks static WPF strings on first discovery, then refreshes only those tracked slots.
/// Dynamic user/model content created later is never discovered during a language refresh.
/// </summary>
public static class WpfLocalizer
{
    public sealed record LocalizedArgument(string Key);

    private sealed class Entry
    {
        public string? Title;
        public string? Text;
        public object?[]? TextArguments;
        public string? Content;
        public object?[]? ContentArguments;
        public string? Header;
        public string? ToolTip;
        public object?[]? ToolTipArguments;
        public string? AutomationName;
        public object?[]? AutomationArguments;
    }

    private static readonly ConditionalWeakTable<DependencyObject, Entry> Entries = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> Exclusions = new();

    public static void ApplyNew(DependencyObject root, I18nService i18n)
        => Apply(root, i18n, discover: true, new HashSet<DependencyObject>());

    public static void RefreshTracked(DependencyObject root, I18nService i18n)
        => Apply(root, i18n, discover: false, new HashSet<DependencyObject>());

    public static LocalizedArgument Localize(string key) => new(key);

    public static void Exclude(DependencyObject element)
    {
        Entries.Remove(element);
        _ = Exclusions.GetValue(element, _ => new object());
    }

    public static void SetDynamicText(TextBlock block, string text)
    {
        Exclude(block);
        block.Text = text;
    }

    public static void SetDynamicContent(ContentControl control, object? content)
    {
        Exclude(control);
        control.Content = content;
    }

    public static void SetText(TextBlock block, string key, I18nService i18n)
    {
        var entry = Entries.GetOrCreateValue(block);
        entry.Text = key;
        entry.TextArguments = null;
        block.Text = i18n.T(key);
    }

    public static void SetFormattedText(
        TextBlock block,
        string template,
        I18nService i18n,
        params object?[] args)
    {
        var entry = Entries.GetOrCreateValue(block);
        entry.Text = template;
        entry.TextArguments = args.ToArray();
        block.Text = i18n.Format(template, ResolveArguments(i18n, args));
    }

    public static void SetFormattedContent(
        ContentControl control,
        string template,
        I18nService i18n,
        params object?[] args)
    {
        var entry = Entries.GetOrCreateValue(control);
        entry.Content = template;
        entry.ContentArguments = args.ToArray();
        control.Content = i18n.Format(template, ResolveArguments(i18n, args));
    }

    public static void SetFormattedToolTip(
        FrameworkElement element,
        string template,
        I18nService i18n,
        params object?[] args)
    {
        var entry = Entries.GetOrCreateValue(element);
        entry.ToolTip = template;
        entry.ToolTipArguments = args.ToArray();
        element.ToolTip = i18n.Format(template, ResolveArguments(i18n, args));
    }

    public static void SetFormattedAutomationName(
        DependencyObject element,
        string template,
        I18nService i18n,
        params object?[] args)
    {
        var entry = Entries.GetOrCreateValue(element);
        entry.AutomationName = template;
        entry.AutomationArguments = args.ToArray();
        AutomationProperties.SetName(element, i18n.Format(template, ResolveArguments(i18n, args)));
    }

    private static void Apply(
        DependencyObject current,
        I18nService i18n,
        bool discover,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current)) return;
        if (discover) Discover(current);
        if (Entries.TryGetValue(current, out var entry)) Translate(current, entry, i18n);

        foreach (var child in LogicalTreeHelper.GetChildren(current))
        {
            if (child is DependencyObject dependency)
                Apply(dependency, i18n, discover, visited);
        }
    }

    private static void Discover(DependencyObject current)
    {
        if (Exclusions.TryGetValue(current, out _)) return;
        Entry? entry = null;
        if (current is Window window && IsCatalogText(window.Title))
            (entry ??= Entries.GetOrCreateValue(current)).Title = window.Title;
        if (current is TextBlock textBlock && IsCatalogText(textBlock.Text))
            (entry ??= Entries.GetOrCreateValue(current)).Text = textBlock.Text;
        if (current is ContentControl contentControl
            && contentControl.Content is string content
            && IsCatalogText(content))
        {
            (entry ??= Entries.GetOrCreateValue(current)).Content = content;
        }
        if (current is HeaderedItemsControl headered
            && headered.Header is string header
            && IsCatalogText(header))
        {
            (entry ??= Entries.GetOrCreateValue(current)).Header = header;
        }
        if (current is FrameworkElement automationElement
            && AutomationProperties.GetName(automationElement) is { Length: > 0 } automationName
            && IsCatalogText(automationName))
        {
            (entry ??= Entries.GetOrCreateValue(current)).AutomationName = automationName;
        }
        if (current is FrameworkElement element
            && element.ToolTip is string toolTip
            && IsCatalogText(toolTip))
        {
            (entry ??= Entries.GetOrCreateValue(current)).ToolTip = toolTip;
        }
    }

    private static void Translate(DependencyObject current, Entry entry, I18nService i18n)
    {
        if (current is Window window && entry.Title is not null)
            window.Title = i18n.T(entry.Title);
        if (current is TextBlock textBlock && entry.Text is not null)
            textBlock.Text = entry.TextArguments is { } textArgs
                ? i18n.Format(entry.Text, ResolveArguments(i18n, textArgs))
                : i18n.T(entry.Text);
        if (current is ContentControl contentControl && entry.Content is not null)
            contentControl.Content = entry.ContentArguments is { } contentArgs
                ? i18n.Format(entry.Content, ResolveArguments(i18n, contentArgs))
                : i18n.T(entry.Content);
        if (current is HeaderedItemsControl headered && entry.Header is not null)
            headered.Header = i18n.T(entry.Header);
        if (current is FrameworkElement element && entry.ToolTip is not null)
            element.ToolTip = entry.ToolTipArguments is { } toolTipArgs
                ? i18n.Format(entry.ToolTip, ResolveArguments(i18n, toolTipArgs))
                : i18n.T(entry.ToolTip);
        if (current is DependencyObject automationElement && entry.AutomationName is not null)
            AutomationProperties.SetName(
                automationElement,
                entry.AutomationArguments is { } automationArgs
                    ? i18n.Format(entry.AutomationName, ResolveArguments(i18n, automationArgs))
                    : i18n.T(entry.AutomationName));
    }

    private static object?[] ResolveArguments(I18nService i18n, object?[] args)
        => args.Select(argument => argument is LocalizedArgument localized
            ? i18n.T(localized.Key)
            : argument).ToArray();

    private static bool IsCatalogText(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && I18nService.HasTranslation(AppLang.En, text);
}
