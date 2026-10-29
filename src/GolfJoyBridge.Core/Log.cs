using System.Text;

namespace GolfJoyBridge;

/// <summary>Very small thread-safe logger: file + optional console + event for the UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static event Action<string>? LineWritten;

    public static string? FilePath => _path;

    /// <summary>Starts logging into folder/connector.log (truncated when it grows beyond ~2 MB).</summary>
    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "connector.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                File.WriteAllText(_path, "");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Gate)
        {
            try { if (_path != null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
            try { Console.WriteLine(line); } catch { }
        }
        try { LineWritten?.Invoke(line); } catch { }
    }
}
