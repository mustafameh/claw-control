namespace ClawControl.Core;

/// <summary>
/// Remembers that the user chose Quit, so the startup task doesn't start Claw Control again
/// until the next boot.
/// </summary>
public static class QuitFlag
{
    static string FilePath => Path.Combine(Log.Dir, "quit-this-boot.txt");

    static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static void Set()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(FilePath, BootTimeUtc.ToString("o"));
        }
        catch (Exception ex)
        {
            Log.Error("Remembering the quit", ex);
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    public static bool IsSetThisBoot()
    {
        try
        {
            return File.Exists(FilePath)
                && DateTime.TryParse(File.ReadAllText(FilePath), null, System.Globalization.DateTimeStyles.RoundtripKind, out var bootAtQuit)
                && Math.Abs((bootAtQuit - BootTimeUtc).TotalMinutes) < 1;
        }
        catch
        {
            return false;
        }
    }
}
