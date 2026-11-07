using System.Globalization;
using M3uPlayer.App.ViewModels;
using M3uPlayer.Core.Localization;
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
        var current = Languages.Resolve(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, CultureInfo.CurrentUICulture);
        LanguageBox.ItemsSource = Languages.All.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Languages.All.ToList().FindIndex(l => l.Code == current);
        // Attached after the first selection, so showing the current language does not restart the app.
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedIndex >= 0 && Languages.All[LanguageBox.SelectedIndex].Code is var code && code != current) App.RestartForLanguage(code);
        };
        InitPreferences();
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
        if (e.Parameter as string == "add")
        {
            Tabs.SelectedItem = SourcesItem;
            await Guard(() => AddAsync(SourceKind.Playlist));
        }
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        GeneralTab.Visibility = Shown(GeneralItem);
        PlaybackTab.Visibility = Shown(PlaybackItem);
        SourcesTab.Visibility = Shown(SourcesItem);
        DataTab.Visibility = Shown(DataItem);
    }

    private Visibility Shown(SelectorBarItem item) => Tabs.SelectedItem == item ? Visibility.Visible : Visibility.Collapsed;

    private static readonly Dictionary<int, string> IntervalLabels = new()
    {
        [60] = Strings.Interval_1h, [180] = Strings.Interval_3h, [360] = Strings.Interval_6h, [720] = Strings.Interval_12h,
        [1440] = Strings.Interval_24h, [2880] = Strings.Interval_48h, [10080] = Strings.Interval_7d,
    };

    private static string Seconds(double s) => string.Format(Strings.Settings_Seconds, s.ToString("0.#", CultureInfo.CurrentCulture));

    /// <summary>Fills each control with its saved value first and only then listens, so showing a value never saves it.</summary>
    private void InitPreferences()
    {
        var p = App.Services.Prefs;
        Choice(ThemeBox, Preferences.ThemeChoices, [Strings.Settings_ThemeSystem, Strings.Settings_ThemeLight, Strings.Settings_ThemeDark], p.Theme,
            v => { p.Theme = v; App.MainWindow?.ApplyTheme(v); });
        Choice(BufferBox, Preferences.BufferChoices, [.. Preferences.BufferChoices.Select(ms => Seconds(ms / 1000.0))], p.BufferMs, v => p.BufferMs = v);
        Choice(SeekBox, Preferences.SeekChoices, [.. Preferences.SeekChoices.Select(s => Seconds(s))], p.SeekSeconds, v => p.SeekSeconds = v);
        Choice(HideBox, Preferences.HideControlsChoices, [.. Preferences.HideControlsChoices.Select(s => Seconds(s))], p.HideControlsSeconds, v => p.HideControlsSeconds = v);
        Choice(DefaultIntervalBox, Preferences.IntervalChoices, [.. Preferences.IntervalChoices.Select(m => IntervalLabels[m])], p.DefaultIntervalMinutes,
            v => p.DefaultIntervalMinutes = v);
        Toggle(AutostartSwitch, p.Autostart, v => p.Autostart = v);
        Toggle(FullscreenSwitch, p.StartFullscreen, v => p.StartFullscreen = v);
        Toggle(HwSwitch, p.HwDecoding, v => p.HwDecoding = v);
    }

    private static void Choice<T>(ComboBox box, IReadOnlyList<T> values, IReadOnlyList<string> labels, T current, Action<T> save)
    {
        box.ItemsSource = labels;
        box.SelectedIndex = values.ToList().IndexOf(current);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Save(() => save(values[box.SelectedIndex])); };
    }

    private static void Toggle(ToggleSwitch toggle, bool current, Action<bool> save)
    {
        toggle.IsOn = current;
        toggle.Toggled += (_, _) => Save(() => save(toggle.IsOn));
    }

    private static void Save(Action save)
    {
        try { save(); }
        catch (Exception ex) { _ = App.ReportAsync(ex); }
    }

    private async void ClearLogos_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        LogosCleared.Visibility = Visibility.Collapsed;
        await Task.Run(App.Services.Logos.Clear);
        LogosCleared.Visibility = Visibility.Visible;
    });

    private async void ClearAll_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Settings_ClearAllQuestion,
            Content = Strings.Settings_ClearAllBody,
            PrimaryButtonText = Strings.Settings_ClearAllConfirm,
            CloseButtonText = Strings.AddSource_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = DialogRoot,
        };
        if (await App.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        DataReset.Request(App.DataDir);
        App.Restart();
    });

    private static async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }

    // The page's own XamlRoot is null until it is in the tree, and the first "Dodaj listę" opens this page and its dialog in one go.
    private static XamlRoot DialogRoot => App.MainWindow!.Content.XamlRoot;

    private static SourceRow Row(object sender) => (SourceRow)((FrameworkElement)sender).Tag;

    private async Task AddAsync(SourceKind kind)
    {
        var dialog = new AddSourceDialog(kind, file => _vm.SuggestName(kind, file)) { XamlRoot = DialogRoot };
        if (await App.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
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
            ? Strings.Settings_RemoveEpgConsequence
            : Strings.Settings_RemoveListConsequence;
        var dialog = new ContentDialog
        {
            Title = string.Format(Strings.Settings_RemoveQuestion, row.Name),
            Content = consequence,
            PrimaryButtonText = row.RemoveLabel,
            CloseButtonText = Strings.AddSource_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = DialogRoot,
        };
        if (await App.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await _vm.RemoveAsync(row.Id);
        // The focused button is gone with its row; keep keyboard users in place.
        (row.IsEpg ? AddEpgButton : AddPlaylistButton).Focus(FocusState.Programmatic);
    });

    private async void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (box.Tag is not SourceRow row || box.SelectedIndex < 0 || box.SelectedIndex == row.IntervalIndex) return;
        await Guard(() => _vm.SetScheduleAsync(row, box.SelectedIndex, row.AutoRefresh));
    }

    private async void Auto_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.Tag is not SourceRow row || toggle.IsOn == row.AutoRefresh) return;
        await Guard(() => _vm.SetScheduleAsync(row, row.IntervalIndex, toggle.IsOn));
    }
}
