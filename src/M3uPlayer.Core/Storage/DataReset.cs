namespace M3uPlayer.Core.Storage;

/// <summary>
/// "Clear all data": the running app only leaves a marker and restarts; the next start removes the data before it opens the
/// database, so no open file stands in the way. The log stays, for diagnosing whatever made someone start over.
/// </summary>
public static class DataReset
{
    public const string MarkerName = "reset";
    private static readonly string[] Files = ["m3u.db", "m3u.db-wal", "m3u.db-shm"];
    private static readonly string[] Folders = ["logos", "tmp"];

    public static void Request(string dir) => File.WriteAllBytes(Path.Combine(dir, MarkerName), []);

    /// <summary>Throws IOException when something is still locked after <paramref name="wait"/>; the marker then stays for the next start.</summary>
    public static void RunIfRequested(string dir, TimeSpan? wait = null)
    {
        var marker = Path.Combine(dir, MarkerName);
        if (!File.Exists(marker)) return;
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(5)); // the closing instance may still hold the database for a moment
        while (true)
        {
            try
            {
                foreach (var f in Files) File.Delete(Path.Combine(dir, f)); // a missing file is not an error
                foreach (var d in Folders.Select(d => Path.Combine(dir, d)).Where(Directory.Exists)) Directory.Delete(d, true);
                File.Delete(marker);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= until) throw new IOException("The app data could not be removed: " + ex.Message, ex);
                Thread.Sleep(100);
            }
        }
    }
}
