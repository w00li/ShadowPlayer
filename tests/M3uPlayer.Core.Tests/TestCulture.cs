using System.Globalization;
using System.Runtime.CompilerServices;

namespace M3uPlayer.Core.Tests;

internal static class TestCulture
{
    // The tests assert Polish text, whatever the machine's language.
    [ModuleInitializer]
    internal static void Init() => CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("pl-PL");

    /// <summary>Switches this test's culture only: CurrentCulture flows with the test, unlike the process-wide defaults other tests share.</summary>
    public static IDisposable Use(string code)
    {
        var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(code);
        return new Restore(() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, ui));
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
}
