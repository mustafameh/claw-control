using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>An app the Start menu lists: its name and its shell id (what opens it).</summary>
public sealed record StartMenuApp(string Name, string AppId, string? Program);

/// <summary>
/// The apps in the Start menu's All apps list (Windows' AppsFolder: desktop programs and Store
/// apps alike), read only when asked. Uninstallers and documents are left out.
/// </summary>
public static class StartMenuApps
{
    public static List<StartMenuApp> List()
    {
        var apps = new List<StartMenuApp>();
        try
        {
            var itemId = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref itemId, out IShellItem folder) != 0) return apps;
            var handler = BhidEnumItems;
            var enumId = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref handler, ref enumId, out IntPtr pointer) != 0) return apps;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            while (items.Next(1, out IShellItem item, out uint fetched) == 0 && fetched == 1)
            {
                string? name = Text(item, NormalDisplay), id = Text(item, ParentRelativeParsing);
                Marshal.ReleaseComObject(item);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(id) || Skipped(name, id)) continue;
                apps.Add(new StartMenuApp(name.Trim(), id, ProgramOf(id)));
            }
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
        catch (Exception ex)
        {
            Log.Error("Listing the Start menu's apps", ex);
        }
        return apps.DistinctBy(a => a.AppId).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool Skipped(string name, string id) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || id.Contains("://")  // web links
        || Path.GetExtension(id).ToLowerInvariant() is ".url" or ".txt" or ".chm" or ".pdf" or ".htm" or ".html" or ".rtf" or ".ini";

    /// <summary>A desktop program's file from its shell id ("{known folder}\path\app.exe"), if it is one.</summary>
    static string? ProgramOf(string id)
    {
        try
        {
            string? path = null;
            if (id.StartsWith('{') && id.IndexOf('}') is int end and > 0 && Guid.TryParse(id[..(end + 1)], out var folder)
                && SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out IntPtr text) == 0)
            {
                path = Marshal.PtrToStringUni(text) + id[(end + 1)..];
                Marshal.FreeCoTaskMem(text);
            }
            else if (id.Length > 2 && id[1] == ':') path = id;
            return path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    static string? Text(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out IntPtr text) != 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    const uint NormalDisplay = 0, ParentRelativeParsing = 0x80018001;
    static readonly Guid BhidEnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid id, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint form, out IntPtr name);
    }

    [ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
