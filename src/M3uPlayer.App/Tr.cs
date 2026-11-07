using M3uPlayer.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace M3uPlayer.App;

/// <summary>A translated text in XAML: Text="{l:Tr Key=Settings_Title}". Read once when the control is built; a language change restarts the app.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Strings.ResourceManager.GetString(Key) ?? Key; // a missing key shows itself instead of failing the page
}
