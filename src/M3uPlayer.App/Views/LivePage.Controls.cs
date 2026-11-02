using M3uPlayer.App.Player;
using M3uPlayer.Core.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace M3uPlayer.App.Views;

/// <summary>Fullscreen, the controls bar (auto-hide, tracks, archive timeline) and the keyboard shortcuts.</summary>
public sealed partial class LivePage
{
    private DispatcherQueueTimer _hideTimer = null!, _seekTimer = null!, _trackTimer = null!;
    private bool _fullscreen, _menuOpen, _syncSlider, _refocusing, _dragging, _dirty;
    private (DateTimeOffset, DateTimeOffset) _timelineKey;
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    private void InitControls()
    {
        _hideTimer = Timer(TimeSpan.FromSeconds(3), false, HideControls);
        _seekTimer = Timer(TimeSpan.FromMilliseconds(300), false, CommitSeek);
        _trackTimer = Timer(TimeSpan.FromSeconds(1), true, RefreshTrackButtons);
        KeyDown += Page_KeyDown;
        Vm.Archive.PropertyChanged += (_, _) => Archive_Changed();
        // The slider marks its pointer events handled, so only a handledEventsToo handler sees the release.
        Timeline.AddHandler(PointerPressedEvent, new PointerEventHandler(Timeline_Pressed), true);
        foreach (var e in new[] { UIElement.PointerReleasedEvent, UIElement.PointerCaptureLostEvent, UIElement.PointerCanceledEvent })
            Timeline.AddHandler(e, new PointerEventHandler(Timeline_Released), true);
    }

    // The page is gone from the screen: nothing may keep ticking, and the pointer must come back.
    private void StopControls()
    {
        _hideTimer.Stop();
        _seekTimer.Stop();
        _trackTimer.Stop();
        _dragging = _dirty = false;
        App.MainWindow?.HideCursor(false);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e) => Vm.FlushVolume();

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
        // A click leaves focus on a control that is about to disappear, and the shortcuts would die with it.
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f && IsWithin(f, ControlsBar))
        {
            _refocusing = true;
            try { VideoHost.Focus(FocusState.Programmatic); }
            finally { _refocusing = false; }
        }
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
    private void VideoHost_GotFocus(object sender, RoutedEventArgs e) { if (!_refocusing) ShowControls(); }

    // A click only brings the bar up; it does not pause or toggle anything.
    private void Video_Tapped(object sender, TappedRoutedEventArgs e)
    {
        VideoHost.Focus(FocusState.Programmatic);
        ShowControls();
    }

    private void Video_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && (IsWithin(d, ControlsBar) || IsWithin(d, OverlayButton))) return; // those have their own meaning
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
        if (_dragging || _seekTimer.IsRunning) return; // the viewer is moving it right now
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

    private void Archive_Changed()
    {
        // A pending seek belongs to the programme it was made on; it must not land on the next one.
        var key = Vm.Archive.Timeline is { } t ? (t.Start, t.Stop) : default;
        if (key != _timelineKey) { _timelineKey = key; _seekTimer.Stop(); _dirty = false; }
        SyncTimeline();
    }

    private void Timeline_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncSlider) return;
        _dirty = true;
        if (_dragging) return; // a drag seeks once, when the pointer is released
        _seekTimer.Stop();
        _seekTimer.Start(); // keys: a run of presses seeks once, when it rests
    }

    private void Timeline_Pressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _seekTimer.Stop();
    }

    private void Timeline_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_dirty) CommitSeek(); else SyncTimeline();
    }

    private void CommitSeek()
    {
        _seekTimer.Stop();
        _dirty = false;
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
}
