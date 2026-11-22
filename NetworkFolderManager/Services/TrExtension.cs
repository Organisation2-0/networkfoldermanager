using System.Windows.Markup;

namespace NetworkFolderManager.Services;

/// <summary>XAML: <c>Text="{loc:Tr 'Deutscher Text'}"</c></summary>
[MarkupExtensionReturnType(typeof(string))]
public class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
