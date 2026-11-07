using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using M3uPlayer.App.Player;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Localization;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Refresh;
using M3uPlayer.Core.Storage;
using M3uPlayer.Core.Updates;
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
        public static VlcPlayer Player { get; internal set; } = null!;
        public static LogoCache Logos { get; internal set; } = null!;
        public static Preferences Prefs { get; internal set; } = null!;
        public static ViewModels.UpdateModel Updates { get; internal set; } = null!;
    }

    private static readonly CancellationTokenSource Stop = new();
    public const string LanguageSetting = "language";
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M3uPlayer");
    private static bool _restart;
    private static readonly HashSet<string> LoggedStages = [];
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
        Languages.Apply(Languages.Resolve(null, CultureInfo.CurrentUICulture)); // a failure before the saved language is read still speaks the system language
        var dir = DataDir;
        Directory.CreateDirectory(dir);
        Services.Log = new FileLog(Path.Combine(dir, "app.log"));
        try { DataReset.RunIfRequested(dir); } // before anything opens the files it removes; the log stays
        catch (IOException ex)
        {
            SafeLog(ex.ToString());
            MessageBoxW(0, Strings.Reset_Failed, "ShadowPlayer", 0x10); // the marker stays, so the next start tries again
            Environment.Exit(1);
        }
        Services.Db = new Database(Path.Combine(dir, "m3u.db"));
        Services.Prefs = new Preferences(Services.Db);
        Languages.Apply(Languages.Resolve(Services.Db.GetSetting(LanguageSetting), CultureInfo.CurrentUICulture));
        Services.Sources = new SourceStore(Services.Db);
        Services.Channels = new ChannelStore(Services.Db);
        Services.Programmes = new ProgrammeStore(Services.Db);
        var tmp = Path.Combine(dir, "tmp");
        var fetcher = new SourceFetcher(new HttpClient(), [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)],
            Task.Delay, tmp);
        Services.Refresh = new RefreshService(Services.Db, fetcher, TimeProvider.System, Services.Log);
        DeleteLeftovers(tmp);
        Services.Player = new VlcPlayer(Services.Log);
        Services.Logos = new LogoCache(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, Path.Combine(dir, "logos"));

        var updates = Path.Combine(dir, "updates");
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var current = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        UpdateChecker.Clean(updates, current);
        Services.Updates = new ViewModels.UpdateModel(new UpdateChecker(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, current), updates, current, Services.Log);

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
            Services.Updates.Cancel();
            Stop.Cancel();
            DisplayAwake.Set(false);
            Services.Player.Dispose(); // on the UI thread, as the player requires
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            if (_restart) StartAgain();
        };
        _window.ApplyTheme(Services.Prefs.Theme);
        if (Services.Prefs.CheckUpdates)
        {
            // A while after start, so the check never competes with opening the list and the first channel.
            var check = _window.DispatcherQueue.CreateTimer();
            check.Interval = TimeSpan.FromSeconds(10);
            check.IsRepeating = false;
            check.Tick += (_, _) => ViewModels.Safe.Run(() => Services.Updates.CheckAsync(userAsked: false));
            check.Start();
        }
        _window.Activate();
        LogStartupOnce("okno");
    }

    /// <summary>Saves the language and closes the window; the app starts again in it once the player and the refresh loop are stopped.</summary>
    public static void RestartForLanguage(string code)
    {
        Services.Db.SetSetting(LanguageSetting, code);
        Languages.Apply(code); // the failure message below is then already in the chosen language
        Restart();
    }

    /// <summary>The installer is running: close the usual way (session saved, player stopped) and let the installer start the new version.</summary>
    public static void CloseForUpdate() => MainWindow?.Close();

    /// <summary>Closes the window the usual way; once the player and the refresh loop have stopped, the app starts again.</summary>
    public static void Restart()
    {
        _restart = true;
        MainWindow?.Close();
    }

    private static void StartAgain()
    {
        try { Process.Start(Environment.ProcessPath!); }
        catch (Exception ex)
        {
            SafeLog("Restart failed: " + ex.Message);
            MessageBoxW(0, Strings.Restart_Failed, "ShadowPlayer", 0x10);
        }
    }

    /// <summary>How long each stage of the app start took, for a start that feels slow.</summary>
    public static void LogStartupOnce(string stage)
    {
        if (LoggedStages.Add(stage))
            SafeLog($"Start: {stage} po {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms");
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
        MessageBoxW(0, Strings.Startup_Failed,
            "ShadowPlayer", 0x10);
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
        if (MainWindow?.Content is FrameworkElement root) dialog.RequestedTheme = root.RequestedTheme; // a dialog does not inherit the theme from the window
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
                        Title = Strings.Error_Title,
                        Content = Strings.Error_Body,
                        CloseButtonText = Strings.Error_Close,
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
