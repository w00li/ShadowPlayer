using M3uPlayer.Core.Text;

namespace M3uPlayer.Core.Diagnostics;

public class FileLog(string path)
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:O} {Redactor.Redact(message)}{Environment.NewLine}");
        }
    }
}
