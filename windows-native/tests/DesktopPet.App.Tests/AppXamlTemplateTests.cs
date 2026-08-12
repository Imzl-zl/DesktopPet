using System.IO;
using System.Xml.Linq;

namespace DesktopPet.App.Tests;

public sealed class AppXamlTemplateTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void MenuItemTemplateBindsItsPresenterToTheHeader()
    {
        var template = TemplateFor("MenuItem");
        var presenter = template.Descendants(Presentation + "ContentPresenter").Single();

        Assert.Equal("Header", (string?)presenter.Attribute("ContentSource"));
    }

    [Fact]
    public void ComboBoxTemplatePropagatesHostPaddingToItsSelectionToggle()
    {
        var template = TemplateFor("ComboBox");
        var toggle = template.Descendants(Presentation + "ToggleButton").Single();
        var presenter = toggle.Descendants(Presentation + "ContentPresenter")
            .Single(element => (string?)element.Attribute("Content") is { } content
                && content.Contains("SelectionBoxItem", StringComparison.Ordinal));

        Assert.Equal("{TemplateBinding Padding}", (string?)toggle.Attribute("Padding"));
        Assert.Equal("{TemplateBinding Padding}", (string?)presenter.Attribute("Margin"));
    }

    private static XElement TemplateFor(string targetType)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
        var style = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == targetType);
        var templateSetter = style.Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Template");

        return templateSetter.Element(Presentation + "Setter.Value")!
            .Element(Presentation + "ControlTemplate")!;
    }
}
