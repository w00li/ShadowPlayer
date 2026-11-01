using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Refresh;
using M3uPlayer.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace M3uPlayer.App;

public partial class App : Application
{
    /// <summary>The composition root: set once in OnLaunched, before any window exists.</summary>
    public static class Services
    {
        public static Database Db { get; internal set; } = null!;
        public static SourceStore Sources { get; internal set; } = null!;
        public static ChannelStore Channels { get; internal set; } = null!;
        public static ProgrammeStore Programmes { get; internal set; } = null!;
        public static RefreshService Refresh { get; internal set; } = null!;
        public static FileLog Log { get; internal set; } = null!;
    }

    private readonly CancellationTokenSource _stop = new();
    private MainWindow? _window;

    public static MainWindow? MainWindow => ((App)Current)._window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            _ = ReportAsync(e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M3uPlayer");
        Directory.CreateDirectory(dir);
        Services.Log = new FileLog(Path.Combine(dir, "app.log"));
        Services.Db = new Database(Path.Combine(dir, "m3u.db"));
        Services.Sources = new SourceStore(Services.Db);
        Services.Channels = new ChannelStore(Services.Db);
        Services.Programmes = new ProgrammeStore(Services.Db);
        var fetcher = new SourceFetcher(new HttpClient(), [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)],
            Task.Delay, Path.Combine(dir, "tmp"));
        Services.Refresh = new RefreshService(Services.Db, fetcher, TimeProvider.System, Services.Log);

        _window = new MainWindow();
        _window.Closed += (_, _) => _stop.Cancel();
        _window.Activate();
        // RunAsync parses and imports synchronously between awaits, so keep it off the UI thread.
        _ = Task.Run(async () =>
        {
            try { await Services.Refresh.RunAsync(_stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SafeLog("Refresh loop ended: " + ex.Message); }
        });
    }

    /// <summary>Logs the exception and, if a window is up, tells the user in plain words. Never throws.</summary>
    public static async Task ReportAsync(Exception ex)
    {
        SafeLog(ex.ToString());
        try
        {
            var window = MainWindow;
            if (window is null) return;
            var tcs = new TaskCompletionSource();
            if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await new ContentDialog
                    {
                        Title = "Coś poszło nie tak",
                        Content = "Aplikacja napotkała nieoczekiwany problem. Szczegóły zapisano w dzienniku. Spróbuj powtórzyć czynność; jeśli problem wraca, uruchom aplikację ponownie.",
                        CloseButtonText = "Zamknij komunikat",
                        XamlRoot = window.Content.XamlRoot,
                    }.ShowAsync();
                }
                catch (Exception) { } // e.g. another dialog is already open
                finally { tcs.TrySetResult(); }
            })) return;
            await tcs.Task;
        }
        catch (Exception) { }
    }

    private static void SafeLog(string message)
    {
        try { Services.Log?.Write(message); }
        catch (Exception) { }
    }
}
