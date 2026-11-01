using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace M3uPlayer.App.Views;

public sealed partial class LivePage : Page
{
    private readonly Action _onDataChanged;

    public LivePage()
    {
        InitializeComponent();
        _onDataChanged = () => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) => App.Services.Refresh.DataChanged += _onDataChanged;
        Unloaded += (_, _) => App.Services.Refresh.DataChanged -= _onDataChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        try
        {
            var count = await Task.Run(App.Services.Channels.CountAll);
            EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CountText.Text = $"Wczytano {count} kanałów.";
        }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }

    private void AddPlaylist_Click(object sender, RoutedEventArgs e) => App.MainWindow!.Navigate(typeof(SettingsPage), "add");
}
