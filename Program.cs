using ClawControl.Core;

namespace ClawControl;

public static class Program
{
    /// <summary>Named mutex held by the running copy; also used by the single-instance check in App.</summary>
    public const string InstanceMutexName = @"Local\ClawControl.Instance";

    [STAThread]
    public static void Main(string[] args)
    {
        // The startup task runs "--background" at login and after a crash. If Claw Control already
        // runs (or was quit on purpose), decide before WPF loads.
        if (args.Contains("--background") && (AlreadyRunning() || QuitFlag.IsSetThisBoot()))
            return;

        // Numbers, dates and parsing stay culture-free (as with invariant globalization), but
        // Windows' language data is there: WPF needs it for fonts with names in many languages
        // (the Library's poster face; without it the font cache fails at startup).
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    static bool AlreadyRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(InstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
