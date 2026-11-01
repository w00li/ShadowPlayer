using M3uPlayer.App.ViewModels;
using M3uPlayer.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace M3uPlayer.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = new();
    private readonly Action _onDataChanged;

    public SettingsPage()
    {
        InitializeComponent();
        PlaylistList.ItemsSource = _vm.Playlists;
        EpgList.ItemsSource = _vm.Epgs;
        _vm.Playlists.CollectionChanged += (_, _) => NoPlaylists.Visibility = _vm.Playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _vm.Epgs.CollectionChanged += (_, _) => NoEpgs.Visibility = _vm.Epgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // DataChanged fires on a worker thread.
        _onDataChanged = () => DispatcherQueue.TryEnqueue(async () => await Guard(_vm.ReloadAsync));
        Loaded += (_, _) => App.Services.Refresh.DataChanged += _onDataChanged;
        Unloaded += (_, _) => App.Services.Refresh.DataChanged -= _onDataChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await Guard(_vm.ReloadAsync);
        if (e.Parameter as string == "add") await Guard(() => AddAsync(SourceKind.Playlist));
    }

    private static async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }

    private static SourceRow Row(object sender) => (SourceRow)((FrameworkElement)sender).Tag;

    private async Task AddAsync(SourceKind kind)
    {
        var dialog = new AddSourceDialog(kind, file => _vm.SuggestName(kind, file)) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _vm.AddAsync(kind, dialog.Location, dialog.SourceName);
    }

    private async void AddPlaylist_Click(object sender, RoutedEventArgs e) => await Guard(() => AddAsync(SourceKind.Playlist));
    private async void AddEpg_Click(object sender, RoutedEventArgs e) => await Guard(() => AddAsync(SourceKind.Epg));
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Guard(() => _vm.RefreshAsync(Row(sender).Id));
    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await Guard(() => _vm.MoveAsync(Row(sender), -1));
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await Guard(() => _vm.MoveAsync(Row(sender), 1));

    private async void Remove_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var row = Row(sender);
        var consequence = row.IsEpg
            ? "Kanały z list zostają."
            : "Kanały z tej listy znikną z aplikacji, a źródło EPG dodane automatycznie z tej listy też zostanie usunięte. Ulubione zostają i wrócą, gdy dodasz tę samą listę ponownie.";
        var dialog = new ContentDialog
        {
            Title = $"Usunąć „{row.Name}”?",
            Content = consequence,
            PrimaryButtonText = row.RemoveLabel,
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _vm.RemoveAsync(row.Id);
    });

    private async void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (box.Tag is not SourceRow row || box.SelectedIndex < 0 || box.SelectedIndex == row.IntervalIndex) return;
        row.IntervalIndex = box.SelectedIndex;
        await Guard(() => _vm.SetScheduleAsync(row, IntervalChoice.All[row.IntervalIndex].Minutes, row.AutoRefresh));
    }

    private async void Auto_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.Tag is not SourceRow row || toggle.IsOn == row.AutoRefresh) return;
        row.AutoRefresh = toggle.IsOn;
        await Guard(() => _vm.SetScheduleAsync(row, IntervalChoice.All[row.IntervalIndex].Minutes, row.AutoRefresh));
    }
}
