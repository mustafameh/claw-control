using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClawControl.Features.OptiScaler;

// Game and launcher folders are controlled by the player. Access them with the
// desktop user's normal rights, including when the panel itself is elevated.
internal static class GameFiles
{
    [DllImport("user32.dll")]
    static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint id);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    internal static T AsPlayer<T>(Func<T> operation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return operation();
        // The UAC linked token can allow identification only. The interactive shell's
        // primary token supports file I/O at the player's normal access level.
        if (GetWindowThreadProcessId(GetShellWindow(), out var shellId) == 0)
            throw new IOException("Windows Explorer is unavailable. Restart Explorer and try again.");
        const uint queryLimitedInformation = 0x1000, tokenQueryAndDuplicate = 0x000A;
        using var shell = OpenProcess(queryLimitedInformation, false, shellId);
        if (shell.IsInvalid || !OpenProcessToken(shell, tokenQueryAndDuplicate, out var shellToken))
            throw new UnauthorizedAccessException("Unable to access the Windows desktop session.", new Win32Exception());
        using var token = shellToken;
        return WindowsIdentity.RunImpersonated(token, () =>
        {
            using var limited = WindowsIdentity.GetCurrent();
            if (identity.User == null || limited.User is not { } player || !identity.User.Equals(player) ||
                new WindowsPrincipal(limited).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("A standard Windows user token is required.");
            return operation();
        });
    }

    internal static void WriteText(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(contents);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
