namespace M3uPlayer.App.ViewModels;

public static class Safe
{
    /// <summary>Fire-and-forget for UI handlers: cancellation is normal, anything else is reported to the user.</summary>
    public static async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await App.ReportAsync(ex); }
    }
}
