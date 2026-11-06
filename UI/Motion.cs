using System.Windows;

namespace ClawControl.UI;

/// <summary>
/// Windows' "Animation effects" switch (Settings › Accessibility › Visual effects), Apple's Reduce
/// Motion: when it's off, the panel's springs and glides jump straight to where they
/// end. Read again each time the panel opens.
/// </summary>
public static class Motion
{
    public static bool Reduced { get; private set; }

    public static void Refresh() => Reduced = !SystemParameters.ClientAreaAnimation;
}
