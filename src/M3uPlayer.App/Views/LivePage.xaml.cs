using M3uPlayer.App.Player;
using M3uPlayer.App.ViewModels;
using M3uPlayer.Core.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using VirtualKey = Windows.System.VirtualKey;

namespace M3uPlayer.App.Views;

public sealed partial class LivePage : Page
{
    private const double NarrowWidth = 1050, TightWidth = 900; // below these the category pane gives way to a drop-down, then the channel column shrinks
    private readonly Action _onDataChanged;
    private readonly HashSet<ChannelRow> _realized = [];
    private bool _syncing;
    private CancellationTokenSource? _pictureCts;
    private string? _pictureUrl;
    private readonly DispatcherQueueTimer _hideTimer, _seekTimer, _trackTimer;
    private bool _fullscreen, _menuOpen, _syncSlider;
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    public LiveViewModel Vm { get; }

    public LivePage()
    {
        Vm = new LiveViewModel(DispatcherQueue, App.Services.Player, App.Services.Channels, App.Services.Programmes, App.Services.Db);
        InitializeComponent();
        App.Services.Player.Attach(Video);
        Vm.ChannelsReplaced += () => { _realized.Clear(); SyncChannelSelection(); }; // new containers must be realized again
        Vm.SelectedChannelChanged += SyncChannelSelection;
        Vm.Guide.Loaded += OnGuideLoaded;
        Vm.Guide.PropertyChanged += (_, _) => _ = LoadPictureAsync();
        Vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(Vm.CategoriesCollapsed)) UpdateCategoryPane(); };
        Vm.Archive.PropertyChanged += (_, _) => SyncTimeline();
        _hideTimer = Timer(TimeSpan.FromSeconds(3), false, HideControls);
        _seekTimer = Timer(TimeSpan.FromMilliseconds(300), false, CommitSeek); // a drag or a run of key presses seeks once, when it rests
        _trackTimer = Timer(TimeSpan.FromSeconds(1), true, RefreshTrackButtons);
        KeyDown += Page_KeyDown;
        // DataChanged fires on a worker thread.
        _onDataChanged = () => DispatcherQueue.TryEnqueue(() => Safe.Run(Vm.OnDataChangedAsync));
        Loaded += (_, _) =>
        {
            App.Services.Refresh.DataChanged += _onDataChanged;
            if (App.MainWindow is { } w) w.FullscreenChanged += OnFullscreenChanged;
            Vm.Start();
            UpdateCategoryPane();
            Vm.ReturnToScreen();
            VideoHost.Focus(FocusState.Programmatic); // the shortcuts need focus somewhere on the page
            ShowControls();
        };
        Unloaded += (_, _) =>
        {
            App.Services.Refresh.DataChanged -= _onDataChanged;
            if (App.MainWindow is { } w) w.FullscreenChanged -= OnFullscreenChanged;
        };
    }

    private DispatcherQueueTimer Timer(TimeSpan interval, bool repeat, Action tick)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = interval;
        t.IsRepeating = repeat;
        t.Tick += (_, _) => { if (!repeat) t.Stop(); tick(); };
        return t;
    }

    // Leaving the page destroys the video surface: stop here, and resume in Loaded when the user comes back.
    protected override void OnNavigatedFrom(NavigationEventArgs e) => Vm.LeaveScreen();

    // ---- categories ----
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryItem c) Vm.SelectCategory(c);
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryCombo.SelectedItem is CategoryItem c) Vm.SelectCategory(c);
    }

    private void CategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        Vm.CategoriesCollapsed = !Vm.CategoriesCollapsed;
        UpdateCategoryPane();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCategoryPane();

    private void UpdateCategoryPane()
    {
        if (_fullscreen) return; // the video owns the window; leaving fullscreen runs this again
        var narrow = ActualWidth < NarrowWidth;
        var show = !Vm.CategoriesCollapsed && !narrow;
        CategoryCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = show ? new GridLength(200) : new GridLength(0);
        ChannelColumn.Width = new GridLength(ActualWidth < TightWidth ? 280 : 320);
        CategoryCombo.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        CategoryToggle.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible; // below the limit the drop-down is the only way
        // A zero-width column still takes a spacing slot; give it back to the video.
        RightColumn.Margin = new Thickness(show ? 0 : -8, 0, 0, 0);
        Layout.ColumnSpacing = 8;
    }

    // ---- channels ----
    private void SyncChannelSelection()
    {
        var row = Vm.Channels.FirstOrDefault(r => r.Key == Vm.SelectedChannel?.Key);
        if (!ReferenceEquals(ChannelList.SelectedItem, row)) ChannelList.SelectedItem = row;
        if (row is not null) ChannelList.ScrollIntoView(row);
    }

    // Selection changes caused by clicks only select; playing is driven by ItemClick so a click on the selected row works too.
    private void ChannelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChannelRow row) Vm.Play(row);
    }

    private void ChannelList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ChannelRow row) return;
        if (args.InRecycleQueue)
        {
            _realized.Remove(row);
            if (row.Hover || row.StarFocus) { row.Hover = row.StarFocus = false; row.Refresh(); } // pointer-exit is not delivered to a recycled container
        }
        else _realized.Add(row);
        Vm.ReportVisible(_realized);
    }

    private static ChannelRow? RowOf(object? sender) =>
        (sender as FrameworkElement)?.Tag as ChannelRow ?? (sender as FrameworkElement)?.DataContext as ChannelRow;

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(sender, true);
    private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => SetHover(sender, false);

    private static void SetHover(object sender, bool on)
    {
        if (RowOf(sender) is not { } row) return;
        row.Hover = on;
        row.Refresh();
    }

    private void Star_GotFocus(object sender, RoutedEventArgs e) => SetStarFocus(sender, true);
    private void Star_LostFocus(object sender, RoutedEventArgs e) => SetStarFocus(sender, false);

    private static void SetStarFocus(object sender, bool on)
    {
        if (RowOf(sender) is not { } row) return;
        row.StarFocus = on;
        row.Refresh();
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Vm.ToggleFavorite(row.Key);
    }

    private void RowMenu_Opening(object sender, object e)
    {
        var flyout = (MenuFlyout)sender;
        if (RowOf(flyout.Target) is not { } row) return;
        var item = (MenuFlyoutItem)flyout.Items[0];
        item.Text = row.StarTip;
        item.Tag = row;
    }

    private void RowMenu_Favorite(object sender, RoutedEventArgs e)
    {
        if (((MenuFlyoutItem)sender).Tag is ChannelRow row) Vm.ToggleFavorite(row.Key);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => Vm.ClearSearch();

    // ---- video ----
    private void Retry_Click(object sender, RoutedEventArgs e) => Vm.Retry();

    private void RightColumn_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVideoHeight();

    private void UpdateVideoHeight()
    {
        if (_fullscreen) return;
        // 16:9, but never more than 60% of the column so the guide keeps room.
        VideoBox.Height = Math.Max(120, Math.Min(RightColumn.ActualWidth * 9 / 16, RightColumn.ActualHeight * 0.6));
    }

    // ---- fullscreen ----
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (App.MainWindow is { } w) w.SetFullscreen(!w.IsFullscreen);
    }

    // The window is the single source of truth: the page follows it, whoever changed the presenter.
    private void OnFullscreenChanged(bool on)
    {
        _fullscreen = on;
        var show = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            CategoryColumn.Width = ChannelColumn.Width = new GridLength(0);
            Layout.ColumnSpacing = 0;
            Layout.Padding = new Thickness(0);
            RightColumn.Margin = new Thickness(0);
            RightColumn.RowSpacing = 0;
            VideoRow.Height = new GridLength(1, GridUnitType.Star);
            GuideRow.Height = new GridLength(0);
            VideoHost.VerticalAlignment = VerticalAlignment.Stretch;
            VideoBox.Height = double.NaN;
            VideoBox.CornerRadius = new CornerRadius(0);
            CategoryCard.Visibility = ChannelCard.Visibility = GuideCard.Visibility = show;
        }
        else
        {
            Layout.Padding = new Thickness(16, 4, 16, 16);
            RightColumn.RowSpacing = 8;
            VideoRow.Height = GridLength.Auto;
            GuideRow.Height = new GridLength(1, GridUnitType.Star);
            VideoHost.VerticalAlignment = VerticalAlignment.Top;
            VideoBox.CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
            ChannelCard.Visibility = GuideCard.Visibility = show;
            UpdateCategoryPane();
            UpdateVideoHeight();
        }
        FullscreenIcon.Glyph = on ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullscreenButton, on ? "Wyjdź z pełnego ekranu (F, Esc)" : "Pełny ekran (F)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FullscreenButton, on ? "Wyjdź z pełnego ekranu" : "Pełny ekran");
        VideoHost.Focus(FocusState.Programmatic); // whatever had focus may be hidden now
        ShowControls();
    }

    // ---- controls bar ----
    private void ShowControls()
    {
        ControlsBar.Visibility = Visibility.Visible;
        App.MainWindow?.HideCursor(false);
        RefreshTrackButtons();
        _trackTimer.Start();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControls()
    {
        if (_menuOpen || FocusIsIn(ControlsBar)) { _hideTimer.Start(); return; } // check again later
        ControlsBar.Visibility = Visibility.Collapsed;
        _trackTimer.Stop();
        if (_fullscreen) App.MainWindow?.HideCursor(true);
    }

    // Only keyboard focus keeps the bar up: after a click the pointer's own idle timeout should win.
    private bool FocusIsIn(DependencyObject root) =>
        FocusManager.GetFocusedElement(XamlRoot) is UIElement { FocusState: FocusState.Keyboard } f && IsWithin(f, root);

    private static bool IsWithin(DependencyObject? d, DependencyObject root)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d)) if (ReferenceEquals(d, root)) return true;
        return false;
    }

    private void Video_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();
    private void VideoHost_GotFocus(object sender, RoutedEventArgs e) => ShowControls();

    // A click only brings the bar up; it does not pause or toggle anything.
    private void Video_Tapped(object sender, TappedRoutedEventArgs e)
    {
        VideoHost.Focus(FocusState.Programmatic);
        ShowControls();
    }

    private void Video_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsWithin(d, ControlsBar)) return;
        ToggleFullscreen();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Vm.TogglePause();
    private void Mute_Click(object sender, RoutedEventArgs e) => Vm.ToggleMute();
    private void GoLive_Click(object sender, RoutedEventArgs e) => Vm.GoLive();
    private void Menu_Opened(object sender, object e) => _menuOpen = true;

    private void Menu_Closed(object sender, object e)
    {
        _menuOpen = false;
        ShowControls();
    }

    // The player learns its tracks a moment after playing starts and gives no event for it, so the buttons look at the lists while the bar is up.
    private void RefreshTrackButtons()
    {
        AudioButton.IsEnabled = App.Services.Player.AudioTracks.Count > 1;
        SubtitleButton.IsEnabled = App.Services.Player.SubtitleTracks.Count > 1; // one entry is only "Wyłączone"
    }

    private void AudioMenu_Opening(object sender, object e) =>
        FillTracks(AudioMenu, App.Services.Player.AudioTracks, App.Services.Player.SelectAudio);

    private void SubtitleMenu_Opening(object sender, object e) =>
        FillTracks(SubtitleMenu, App.Services.Player.SubtitleTracks, App.Services.Player.SelectSubtitle);

    // Ids come from this fresh read: a list read earlier may belong to the previous stream.
    private static void FillTracks(MenuFlyout menu, IReadOnlyList<TrackInfo> tracks, Action<int> select)
    {
        menu.Items.Clear();
        foreach (var t in tracks)
        {
            var id = t.Id;
            var item = new MenuFlyoutItem { Text = t.Name.Length > 0 ? t.Name : $"Ścieżka {id}" };
            item.Click += (_, _) => select(id);
            menu.Items.Add(item);
        }
    }

    // ---- archive timeline ----
    private void SyncTimeline()
    {
        if (_seekTimer.IsRunning) return; // the viewer is moving it right now
        var a = Vm.Archive;
        _syncSlider = true;
        try
        {
            Timeline.Maximum = a.Maximum;
            Timeline.Value = a.Value;
        }
        finally { _syncSlider = false; }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Timeline, a.AutomationName);
    }

    private void Timeline_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncSlider) return;
        _seekTimer.Stop();
        _seekTimer.Start();
    }

    private void CommitSeek()
    {
        Vm.SeekTo(Timeline.Value);
        SyncTimeline(); // the seek may have been clamped
    }

    // ---- keyboard ----
    private FocusKind FocusKindNow()
    {
        var kind = FocusKind.Other;
        for (var d = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case TextBox or PasswordBox or RichEditBox or AutoSuggestBox: return FocusKind.Text;
                case Slider: return FocusKind.Slider;
                case ListViewBase or ComboBox: return FocusKind.List; // wins over a button inside a row: the list keeps its arrows
                case ButtonBase when kind == FocusKind.Other: kind = FocusKind.Button; break;
            }
            if (ReferenceEquals(d, VideoHost) && kind == FocusKind.Other) kind = FocusKind.Video;
        }
        return kind;
    }

    private static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return;
        ShortcutKey? key = e.Key switch
        {
            VirtualKey.Space => ShortcutKey.Space,
            VirtualKey.F => ShortcutKey.F,
            VirtualKey.M => ShortcutKey.M,
            VirtualKey.Up => ShortcutKey.Up,
            VirtualKey.Down => ShortcutKey.Down,
            VirtualKey.Left => ShortcutKey.Left,
            VirtualKey.Right => ShortcutKey.Right,
            VirtualKey.Escape => ShortcutKey.Escape,
            _ => null,
        };
        if (key is not { } k) return;
        var action = Shortcuts.For(k, FocusKindNow(), _fullscreen, Vm.Archive.Active);
        if (action == ShortcutAction.None) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutAction.TogglePause: Vm.TogglePause(); break;
            case ShortcutAction.ToggleFullscreen: ToggleFullscreen(); break;
            case ShortcutAction.ExitFullscreen: App.MainWindow?.SetFullscreen(false); break;
            case ShortcutAction.ToggleMute: Vm.ToggleMute(); break;
            case ShortcutAction.NextChannel: Vm.SelectNextChannel(); break;
            case ShortcutAction.PreviousChannel: Vm.SelectPreviousChannel(); break;
            case ShortcutAction.SeekBack: Vm.SeekBy(-SeekStep); break;
            case ShortcutAction.SeekForward: Vm.SeekBy(SeekStep); break;
        }
        ShowControls();
    }

    // ---- guide ----
    private void OnGuideLoaded(GuideItem? current)
    {
        _syncing = true;
        try { GuideList.SelectedItem = current; }
        finally { _syncing = false; }
        // Items are laid out lazily; wait one turn so ScrollIntoView finds the container.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var items = Vm.Guide.Items;
            if (items.Count == 0) return;
            // Two programmes of the past stay in view above the current one.
            var at = current is null ? items.Count - 1 : Math.Max(0, items.IndexOf(current) - 2);
            // From the top first: ScrollIntoView from a stale offset can stop short of the item.
            GuideList.UpdateLayout();
            GuideList.ScrollIntoView(items[0]);
            GuideList.UpdateLayout();
            GuideList.ScrollIntoView(items[at], ScrollIntoViewAlignment.Leading);
        });
    }

    // Selecting a past row only selects it; double-click (or the button in the details) plays it.
    private void GuideList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is GuideItem { IsPast: true } g) Vm.PlayProgramme(g);
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Guide.SelectedProgramme is { } g) Vm.PlayProgramme(g);
    }

    private void GuideList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && GuideList.SelectedItem is GuideItem g) Vm.Guide.Select(g);
    }

    private void GuideList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not GuideItem g) return;
        // Day headers are labels, not choices.
        // Disabled containers are skipped by arrow keys; the header text keeps its own colour.
        args.ItemContainer.IsEnabled = !g.IsHeader;
        args.ItemContainer.IsHitTestVisible = !g.IsHeader;
    }

    private async Task LoadPictureAsync()
    {
        var url = Vm.Guide.DetailIconUrl;
        if (url == _pictureUrl) return; // most notifications are about something else
        _pictureUrl = url;
        _pictureCts?.Cancel();
        _pictureCts?.Dispose();
        DetailPicture.Source = null;
        DetailPicture.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(url)) return;
        var cts = _pictureCts = new CancellationTokenSource();
        try
        {
            var path = await App.Services.Logos.GetAsync(url, cts.Token);
            if (path is null || cts.IsCancellationRequested) return;
            DetailPicture.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 240 };
            DetailPicture.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Services.Log.Write("Programme picture failed: " + ex.GetType().Name); }
    }

    private void DetailPicture_Failed(object sender, ExceptionRoutedEventArgs e) => DetailPicture.Visibility = Visibility.Collapsed;

    private void AddPlaylist_Click(object sender, RoutedEventArgs e) => App.MainWindow!.Navigate(typeof(SettingsPage), "add");
}
