using System.ComponentModel;
using System.Diagnostics;
using M3uPlayer.Core.Diagnostics;
using M3uPlayer.Core.Localization;
using M3uPlayer.Core.Updates;

namespace M3uPlayer.App.ViewModels;

/// <summary>
/// The app's one update flow, shared by the Live bar and Settings. Call from the UI thread: progress and results come back on it.
/// </summary>
public sealed class UpdateModel(UpdateChecker checker, string dir, Version current, FileLog log) : Observable
{
    private CancellationTokenSource? _download;

    public UpdateInfo? Available { get; private set; }
    public string Status { get; private set; } = "";
    public double Percent { get; private set; }
    public bool IsChecking { get; private set; }
    public bool IsDownloading => _download is not null;
    public bool IsBarOpen { get; private set; }

    public bool CanCheck => !IsChecking && !IsDownloading;
    public bool CanUpdate => Available is not null && !IsDownloading;
    public string CurrentVersion => current.ToString(3);
    public Uri? ReleasePage => Available?.ReleasePage;

    /// <summary>At start nothing failing is shown, only logged; when the viewer asked, a failure is said.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (!CanCheck) return;
        IsChecking = true;
        if (userAsked) Status = Strings.Update_Checking;
        Raise();
        try
        {
            Available = await checker.CheckAsync(CancellationToken.None);
            Status = Available is { } u ? string.Format(Strings.Update_Available, u.Version.ToString(3)) : string.Format(Strings.Update_Latest, CurrentVersion);
            IsBarOpen = Available is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.Write("Update check failed: " + ex.Message);
            Status = userAsked ? Strings.Update_CheckFailed : "";
        }
        finally
        {
            IsChecking = false;
            Raise();
        }
    }

    /// <summary>Downloads, verifies, starts the installer and closes the app. Ignored while a download runs.</summary>
    public async Task UpdateAsync()
    {
        if (Available is not { } u || IsDownloading) return;
        _download = new CancellationTokenSource();
        Show(string.Format(Strings.Update_Downloading, 0), 0);
        var progress = new Progress<double>(p => Show(string.Format(Strings.Update_Downloading, (int)(p * 100)), p * 100));
        try
        {
            var path = await checker.DownloadAsync(u, dir, progress, _download.Token);
            Process.Start(new ProcessStartInfo(path, "/SILENT /SP- /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            App.CloseForUpdate();
            return;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Status = Strings.Update_Cancelled; } // UAC declined; the file stays for the next try
        catch (OperationCanceledException) { Status = string.Format(Strings.Update_Available, u.Version.ToString(3)); }
        catch (InvalidDataException) { Status = Strings.Update_Damaged; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            log.Write("Update download failed: " + ex.Message);
            Status = Strings.Update_DownloadFailed;
        }
        _download.Dispose();
        _download = null;
        Percent = 0;
        IsBarOpen = true;
        Raise();
    }

    public void Cancel() => _download?.Cancel();

    public void Later()
    {
        IsBarOpen = false;
        Raise();
    }

    private void Show(string status, double percent)
    {
        (Status, Percent, IsBarOpen) = (status, percent, true);
        Raise();
    }
}
