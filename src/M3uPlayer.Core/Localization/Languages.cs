using System.Globalization;

namespace M3uPlayer.Core.Localization;

/// <summary>The languages the app speaks. A new one needs Strings.&lt;code&gt;.resx and a line here.</summary>
public static class Languages
{
    /// <summary>Each name is written in its own language, so a viewer can find their way back from one they cannot read.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All = [("pl", "Polski"), ("en", "English")];

    /// <summary>The saved choice while it is still offered, else the system language when offered, else English.</summary>
    public static string Resolve(string? saved, CultureInfo system) =>
        Find(saved) ?? Find(system.TwoLetterISOLanguageName) ?? "en";

    /// <summary>Sets the language for text and for dates and numbers, for this thread and every thread started later.</summary>
    public static void Apply(string code)
    {
        var c = new CultureInfo(code);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = c;
    }

    private static string? Find(string? code) => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).Code;
}
