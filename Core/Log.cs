namespace ClawControl.Core;

/// <summary>Append-only log at %LOCALAPPDATA%\ClawControl\log.txt. Never throws.</summary>
public static class Log
{
    static readonly object Gate = new();

    // CLAWCONTROL_DATA_DIR lets test runs keep their logs and settings out of the real ones.
    public static string Dir { get; } = Environment.GetEnvironmentVariable("CLAWCONTROL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawControl");

    static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Every press, key, command and page switch (settings.json "DetailedLog", off by
    /// default): for chasing a bug, not for everyday use.</summary>
    public static bool Detailed { get; set; }

    public static void Detail(string message)
    {
        if (Detailed) Write("DEBUG", message);
    }

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
