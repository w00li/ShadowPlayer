using System.Runtime.InteropServices;
using M3uPlayer.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace M3uPlayer.App;

public sealed partial class MainWindow : Window
{
    private const int MinWidth = 900, MinHeight = 600; // device-independent pixels
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);
    private readonly SubclassProc _subclass; // kept in a field so the GC cannot free the native callback

    public MainWindow()
    {
        InitializeComponent();
        // The .exe's own icon does not reach the title bar and taskbar of an unpackaged window.
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ShadowPlayer.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // AppWindow sizes are physical pixels: scale the 1400x860 DIP default, but keep it inside the work area.
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(1400 * scale), work.Width), Math.Min((int)(860 * scale), work.Height)));
        AppWindow.Changed += (_, a) =>
        {
            if (!a.DidPresenterChange) return;
            Nav.IsPaneVisible = !IsFullscreen; // the video gets the whole window
            if (!IsFullscreen) HideCursor(false);
            FullscreenChanged?.Invoke(IsFullscreen);
        };
        _subclass = OnMessage;
        SetWindowSubclass(WindowNative.GetWindowHandle(this), _subclass, 1, 0);
        Navigate(typeof(LivePage));
    }

    private bool _syncing;
    private AppWindowPresenter? _windowed;
    private bool _cursorHidden;

    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public void SetFullscreen(bool on)
    {
        if (on == IsFullscreen) return;
        if (on)
        {
            _windowed = AppWindow.Presenter; // the way back keeps maximized or restored as it was
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else AppWindow.SetPresenter(_windowed ?? OverlappedPresenter.Create());
    }

    /// <summary>Hides or shows the mouse pointer; the counter behind ShowCursor needs balanced calls, so only changes count.</summary>
    public void HideCursor(bool hide)
    {
        if (hide == _cursorHidden) return;
        _cursorHidden = hide;
        ShowCursor(!hide);
    }

    /// <param name="parameter">Passed to the page, e.g. "add" to open the Add-list dialog.</param>
    public void Navigate(Type page, object? parameter = null)
    {
        _syncing = true;
        Nav.SelectedItem = page == typeof(SettingsPage) ? SettingsItem : LiveItem;
        _syncing = false;
        Host.Navigate(page, parameter);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string == "settings" ? typeof(SettingsPage) : typeof(LivePage);
        if (Host.CurrentSourcePageType != page) Host.Navigate(page);
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize = new Point { X = (int)(MinWidth * scale), Y = (int)(MinHeight * scale) };
            Marshal.StructureToPtr(info, lParam, false);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
