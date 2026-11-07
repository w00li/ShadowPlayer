using M3uPlayer.Core.Localization;
using M3uPlayer.Core.Sources;
using M3uPlayer.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace M3uPlayer.App.Views;

public sealed partial class AddSourceDialog : ContentDialog
{
    private readonly Func<string?, string> _suggestName;
    private string? _file;
    private bool _nameEdited, _settingName;

    public string Location { get; private set; } = "";
    public string SourceName { get; private set; } = "";

    public AddSourceDialog(SourceKind kind, Func<string?, string> suggestName)
    {
        InitializeComponent();
        _suggestName = suggestName;
        var title = kind == SourceKind.Epg ? Strings.AddSource_AddEpgTitle : Strings.AddSource_AddListTitle;
        Title = title;
        PrimaryButtonText = title;
        SetName(suggestName(null));
    }

    private void SetName(string name) { _settingName = true; NameBox.Text = name; _settingName = false; }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UrlError.Visibility = Visibility.Collapsed;
        if (UrlBox.Text.Length > 0 && _file is not null) { _file = null; FileText.Text = ""; }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NameError.Visibility = Visibility.Collapsed;
        if (!_settingName) _nameEdited = true;
    }

    private void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (UrlBox.Text.Length > 0) ValidateUrl();
    }

    private bool ValidateUrl()
    {
        if (_file is not null) return true;
        var error = SourceInput.ValidateUrl(UrlBox.Text);
        UrlError.Text = error ?? "";
        UrlError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
    }

    private void ShowFileError(string message)
    {
        FileError.Text = message;
        FileError.Visibility = Visibility.Visible;
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        FileError.Visibility = Visibility.Collapsed;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add("*");
            if (await picker.PickSingleFileAsync() is not { } file) return;
            if (string.IsNullOrEmpty(file.Path))
            {
                ShowFileError(Strings.AddSource_FileNotLocal);
                return;
            }
            UrlBox.Text = "";
            _file = file.Path;
            UrlError.Visibility = Visibility.Collapsed;
            FileText.Text = SourceInput.DisplayLocation(file.Path);
            if (!_nameEdited) SetName(_suggestName(file.Path));
        }
        catch (Exception ex)
        {
            App.Services.Log.Write("File picker failed: " + ex);
            ShowFileError(Strings.AddSource_PickerFailed);
        }
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var ok = ValidateUrl();
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameError.Text = Strings.AddSource_NameRequired;
            NameError.Visibility = Visibility.Visible;
            ok = false;
        }
        if (!ok) { args.Cancel = true; return; }
        Location = _file ?? UrlBox.Text.Trim();
        SourceName = SourceInput.SanitizeName(name, _suggestName(_file));
    }
}
