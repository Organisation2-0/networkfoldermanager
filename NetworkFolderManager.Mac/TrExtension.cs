using Avalonia.Markup.Xaml;
using NetworkFolderManager.Services;

namespace NetworkFolderManager;

/// <summary>AXAML: <c>Text="{loc:Tr 'Deutscher Text'}"</c> – übersetzt über <see cref="Loc"/>.</summary>
public class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
