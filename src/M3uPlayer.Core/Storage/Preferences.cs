using System.Globalization;

namespace M3uPlayer.Core.Storage;

/// <summary>
/// The viewer's preferences, kept in the settings table. A value missing, unreadable or outside its choices (say, edited by hand)
/// reads as the default, and the defaults are how the app behaved before these options existed.
/// </summary>
public sealed class Preferences(Database db)
{
    public const string DefaultIntervalKey = "sources.defaultInterval";

    public static readonly IReadOnlyList<string> ThemeChoices = ["system", "light", "dark"];
    public static readonly IReadOnlyList<int> BufferChoices = [1000, 1500, 3000, 5000, 10000];
    public static readonly IReadOnlyList<int> SeekChoices = [5, 10, 15, 30, 60];
    public static readonly IReadOnlyList<int> HideControlsChoices = [2, 3, 5, 10];
    public static readonly IReadOnlyList<int> IntervalChoices = [60, 180, 360, 720, 1440, 2880, 10080];

    public string Theme { get => Text("ui.theme", ThemeChoices, "system"); set => Put("ui.theme", value, ThemeChoices); }
    public bool Autostart { get => Bool("playback.autostart", false); set => db.SetSetting("playback.autostart", value ? "1" : "0"); }
    public bool StartFullscreen { get => Bool("playback.fullscreen", false); set => db.SetSetting("playback.fullscreen", value ? "1" : "0"); }
    public bool HwDecoding { get => Bool("playback.hwDecoding", true); set => db.SetSetting("playback.hwDecoding", value ? "1" : "0"); }
    public int BufferMs { get => Int("playback.bufferMs", BufferChoices, 1500); set => Put("playback.bufferMs", value, BufferChoices); }
    public int SeekSeconds { get => Int("playback.seekSeconds", SeekChoices, 10); set => Put("playback.seekSeconds", value, SeekChoices); }
    public int HideControlsSeconds { get => Int("playback.hideControlsSeconds", HideControlsChoices, 3); set => Put("playback.hideControlsSeconds", value, HideControlsChoices); }
    public int DefaultIntervalMinutes { get => Int(DefaultIntervalKey, IntervalChoices, 720); set => Put(DefaultIntervalKey, value, IntervalChoices); }

    private string Text(string key, IReadOnlyList<string> choices, string fallback) =>
        db.GetSetting(key) is { } v && choices.Contains(v) ? v : fallback;

    private bool Bool(string key, bool fallback) => db.GetSetting(key) switch { "1" => true, "0" => false, _ => fallback };

    private int Int(string key, IReadOnlyList<int> choices, int fallback) =>
        int.TryParse(db.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && choices.Contains(v) ? v : fallback;

    private void Put<T>(string key, T value, IReadOnlyList<T> choices) where T : IFormattable
    {
        if (!choices.Contains(value)) throw new ArgumentOutOfRangeException(key, value, "Not one of the offered choices.");
        db.SetSetting(key, value.ToString(null, CultureInfo.InvariantCulture));
    }

    private void Put(string key, string value, IReadOnlyList<string> choices)
    {
        if (!choices.Contains(value)) throw new ArgumentOutOfRangeException(key, value, "Not one of the offered choices.");
        db.SetSetting(key, value);
    }
}
