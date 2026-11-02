using M3uPlayer.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace M3uPlayer.App.Views;

public sealed partial class LivePage : Page
{
    private const double NarrowWidth = 900; // below this the category pane gives way to a drop-down
    private readonly Action _onDataChanged;
    private readonly HashSet<ChannelRow> _realized = [];
    private bool _syncing;
    private CancellationTokenSource? _pictureCts;
    private string? _pictureUrl;

    public LiveViewModel Vm { get; }

    public LivePage()
    {
        Vm = new LiveViewModel(DispatcherQueue, App.Services.Player, App.Services.Channels, App.Services.Programmes, App.Services.Db);
        InitializeComponent();
        App.Services.Player.Attach(Video);
        Vm.ChannelsReplaced += SyncChannelSelection;
        Vm.SelectedChannelChanged += () => { if (Vm.SelectedChannel is { } r) ChannelList.ScrollIntoView(r); };
        Vm.GuideLoaded += OnGuideLoaded;
        Vm.PropertyChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(Vm.DetailIconUrl)) _ = LoadPictureAsync();
        };
        // DataChanged fires on a worker thread.
        _onDataChanged = () => DispatcherQueue.TryEnqueue(() => Run(Vm.OnDataChangedAsync));
        Loaded += (_, _) =>
        {
            App.Services.Refresh.DataChanged += _onDataChanged;
            Vm.Start();
            UpdateCategoryPane();
        };
        // The video surface is destroyed with the page: stop, and let the user start again.
        Unloaded += (_, _) =>
        {
            App.Services.Refresh.DataChanged -= _onDataChanged;
            Vm.Pause();
            Vm.StopPlayback();
        };
    }

    private static async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }

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
        var narrow = ActualWidth < NarrowWidth;
        var show = !Vm.CategoriesCollapsed && !narrow;
        CategoryCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = show ? new GridLength(200) : new GridLength(0);
        ChannelColumn.Width = new GridLength(narrow ? 280 : 320);
        CategoryCombo.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        CategoryToggle.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        // A zero-width column still takes a spacing slot; give it back to the video.
        RightColumn.Margin = new Thickness(show ? 0 : -8, 0, 0, 0);
        Layout.ColumnSpacing = 8;
    }

    // ---- channels ----
    private void SyncChannelSelection()
    {
        _realized.Clear();
        _syncing = true;
        try { ChannelList.SelectedItem = Vm.Channels.FirstOrDefault(r => r.Key == Vm.SelectedChannel?.Key); }
        finally { _syncing = false; }
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && ChannelList.SelectedItem is ChannelRow row) Vm.Play(row);
    }

    private void ChannelList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ChannelRow row) return;
        if (args.InRecycleQueue) _realized.Remove(row);
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

    private void RightColumn_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 16:9, but never more than 60% of the column so the guide keeps room.
        VideoBox.Height = Math.Max(120, Math.Min(e.NewSize.Width * 9 / 16, e.NewSize.Height * 0.6));
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
            if (current is not null)
            {
                // From the top first: ScrollIntoView from a stale offset can stop short of the item.
                GuideList.UpdateLayout();
                if (Vm.Guide.Count > 0) GuideList.ScrollIntoView(Vm.Guide[0]);
                GuideList.UpdateLayout();
                GuideList.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
            }
            else if (Vm.Guide.LastOrDefault() is { } last) GuideList.ScrollIntoView(last);
        });
    }

    private void GuideList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && GuideList.SelectedItem is GuideItem g) Vm.SelectProgramme(g);
    }

    private void GuideList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not GuideItem g) return;
        // Day headers are labels, not choices.
        args.ItemContainer.IsHitTestVisible = !g.IsHeader;
        args.ItemContainer.IsTabStop = !g.IsHeader;
    }

    private async Task LoadPictureAsync()
    {
        var url = Vm.DetailIconUrl;
        if (url == _pictureUrl) return; // most notifications are about something else
        _pictureUrl = url;
        _pictureCts?.Cancel();
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
