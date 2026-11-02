using System.Runtime.InteropServices;

namespace M3uPlayer.App.Player;

/// <summary>Keeps the display on while video plays. The request belongs to the calling thread (always the UI thread here) and ends with it; no system setting changes.</summary>
public static class DisplayAwake
{
    private const uint Continuous = 0x80000000, DisplayRequired = 0x00000002;
    private static bool _on;

    public static void Set(bool on)
    {
        if (on == _on) return;
        _on = on;
        SetThreadExecutionState(on ? Continuous | DisplayRequired : Continuous);
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
