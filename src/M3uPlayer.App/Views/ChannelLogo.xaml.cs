using M3uPlayer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace M3uPlayer.App.Views;

/// <summary>A channel's logo on a neutral plate, or its initials. Recycled list containers rebind Row: the old picture goes at once.</summary>
public sealed partial class ChannelLogo : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(ChannelRow), typeof(ChannelLogo), new PropertyMetadata(null, (d, _) => ((ChannelLogo)d).OnRow()));

    private CancellationTokenSource? _cts;

    public ChannelLogo() => InitializeComponent();

    public double Side { get; } = 40;

    public ChannelRow? Row
    {
        get => (ChannelRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private async void OnRow()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        Picture.Source = null;
        InitialsText.Text = Row?.Initials ?? "";
        InitialsText.Visibility = Visibility.Visible;
        if (Row is not { LogoUrl: { Length: > 0 } url } row) return;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var path = await App.Services.Logos.GetAsync(url, ct);
            if (path is null || ct.IsCancellationRequested) return;
            Picture.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 80 };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Services.Log.Write("Logo failed: " + ex.GetType().Name); }
    }

    private void Picture_Opened(object sender, RoutedEventArgs e) => InitialsText.Visibility = Visibility.Collapsed;

    private void Picture_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        Picture.Source = null;
        InitialsText.Visibility = Visibility.Visible;
    }
}
