using System.Runtime.InteropServices;
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

    private static readonly CancellationTokenSource Stop = new();
    private static Task? _dialogClosed;
    private MainWindow? _window;

    public static MainWindow? MainWindow => ((App)Current)._window;

    /// <summary>Cancelled when the window closes; every background refresh should honour it.</summary>
    public static CancellationToken Shutdown => Stop.Token;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            if (_window is null) { Fatal(e.Exception); return; } // nothing visible to keep alive
            e.Handled = true;
            _ = ReportAsync(e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Start(); }
        catch (Exception ex) { Fatal(ex); }
    }

    private void Start()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M3uPlayer");
        Directory.CreateDirectory(dir);
        Services.Log = new FileLog(Path.Combine(dir, "app.log"));
        Services.Db = new Database(Path.Combine(dir, "m3u.db"));
        Services.Sources = new SourceStore(Services.Db);
        Services.Channels = new ChannelStore(Services.Db);
        Services.Programmes = new ProgrammeStore(Services.Db);
        var tmp = Path.Combine(dir, "tmp");
        var fetcher = new SourceFetcher(new HttpClient(), [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)],
            Task.Delay, tmp);
        Services.Refresh = new RefreshService(Services.Db, fetcher, TimeProvider.System, Services.Log);
        DeleteLeftovers(tmp);

        _window = new MainWindow();
        // RunAsync parses and imports synchronously between awaits, so keep it off the UI thread.
        var loop = Task.Run(async () =>
        {
            try { await Services.Refresh.RunAsync(Stop.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SafeLog("Refresh loop ended: " + ex.Message); }
        });
        _window.Closed += (_, _) =>
        {
            Stop.Cancel();
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        };
        _window.Activate();
    }

    // Only the files directly inside the app's own tmp folder: nothing a crashed run left behind is worth keeping.
    private static void DeleteLeftovers(string tmp)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(tmp))
                try { File.Delete(file); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    /// <summary>Startup failed or crashed with no window: say so in plain words, then exit instead of lingering invisibly.</summary>
    private static void Fatal(Exception ex)
    {
        SafeLog(ex.ToString());
        MessageBoxW(0, "Nie udało się uruchomić aplikacji. Szczegóły zapisano w dzienniku w folderze M3uPlayer w danych lokalnych. Zamknij inne kopie aplikacji i spróbuj ponownie.",
            "M3U Player", 0x10);
        Environment.Exit(1);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    /// <summary>Shows a dialog; one already open is waited for, because WinUI allows only one at a time.</summary>
    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        while (_dialogClosed is { } open) await open;
        var done = new TaskCompletionSource();
        _dialogClosed = done.Task;
        try { return await dialog.ShowAsync(); }
        finally { _dialogClosed = null; done.SetResult(); }
    }

    /// <summary>Logs the exception and, if a window is up, tells the user in plain words (after any open dialog). Never throws.</summary>
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
                    await ShowDialogAsync(new ContentDialog
                    {
                        Title = "Coś poszło nie tak",
                        Content = "Aplikacja napotkała nieoczekiwany problem. Szczegóły zapisano w dzienniku. Spróbuj powtórzyć czynność; jeśli problem wraca, uruchom aplikację ponownie.",
                        CloseButtonText = "Zamknij komunikat",
                        XamlRoot = window.Content.XamlRoot,
                    });
                }
                catch (Exception) { }
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
