namespace M3uPlayer.Core.Media;

public enum ShortcutKey { Space, F, M, Up, Down, Left, Right, Escape, Home }

/// <summary>Where the keyboard focus is, as far as the shortcuts care. List: a list or combo box that uses the arrows itself.</summary>
public enum FocusKind { Video, Other, Button, List, Slider, Text }

public enum ShortcutAction { None, TogglePause, ToggleFullscreen, ToggleMute, NextChannel, PreviousChannel, ExitFullscreen, SeekBack, SeekForward, RestartProgramme }

public static class Shortcuts
{
    public static ShortcutAction For(ShortcutKey key, FocusKind focus, bool fullscreen, bool archive)
    {
        if (focus == FocusKind.Text) return ShortcutAction.None; // typing must never be intercepted
        var ownsArrows = focus is FocusKind.List or FocusKind.Slider;
        return key switch
        {
            ShortcutKey.Space => focus is FocusKind.Button or FocusKind.List ? ShortcutAction.None : ShortcutAction.TogglePause, // those activate or select
            ShortcutKey.F => ShortcutAction.ToggleFullscreen,
            ShortcutKey.M => ShortcutAction.ToggleMute,
            ShortcutKey.Up => ownsArrows ? ShortcutAction.None : ShortcutAction.PreviousChannel,
            ShortcutKey.Down => ownsArrows ? ShortcutAction.None : ShortcutAction.NextChannel,
            ShortcutKey.Left => !ownsArrows ? ShortcutAction.SeekBack : ShortcutAction.None, // live: back into the archive of the current programme
            ShortcutKey.Right => archive && !ownsArrows ? ShortcutAction.SeekForward : ShortcutAction.None,
            ShortcutKey.Home => ownsArrows ? ShortcutAction.None : ShortcutAction.RestartProgramme, // lists and sliders use Home themselves
            ShortcutKey.Escape => fullscreen ? ShortcutAction.ExitFullscreen : ShortcutAction.None,
            _ => ShortcutAction.None,
        };
    }
}
