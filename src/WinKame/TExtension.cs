using System.Windows.Markup;

namespace WinKame;

/// <summary>XAML: Text="{l:T 'Geral'}" shows the text in the interface language.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Core.L.T(Text);
}
