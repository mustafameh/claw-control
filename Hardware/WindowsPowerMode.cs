using System.Runtime.InteropServices;

namespace ClawControl.Hardware;

/// <summary>The Windows "Power mode" setting (Settings &gt; Power), which MSI also sets with each preset.</summary>
public static class WindowsPowerMode
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static bool Set(Guid mode) => PowerSetActiveOverlayScheme(mode) == 0;

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
}
