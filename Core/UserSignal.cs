using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClawControl.Core;

/// <summary>
/// A named auto-reset event that this user's normal (non-admin) processes may set, although Claw
/// Control runs elevated. Events made the usual way would only accept elevated callers.
/// </summary>
sealed class UserSignal : WaitHandle
{
    // Full access for SYSTEM, admins and the interactive user; medium integrity may set it.
    const string Sddl = "D:(A;;0x1F0003;;;SY)(A;;0x1F0003;;;BA)(A;;0x1F0003;;;IU)S:(ML;;NW;;;ME)";

    public UserSignal(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out IntPtr descriptor, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            var handle = CreateEventW(ref attributes, false, false, name);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
            SafeWaitHandle = handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
