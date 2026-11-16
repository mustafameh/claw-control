using System.Runtime.InteropServices;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>An app the Start menu lists: its name and its shell id (what opens it). For a program
/// only on the desktop, the id is its shortcut's path.</summary>
public sealed record StartMenuApp(string Name, string AppId, string? Program)
{
    /// <summary>A desktop shortcut rather than a Start menu entry.</summary>
    public bool IsShortcut => Path.IsPathRooted(AppId);

    /// <summary>What opens it: its shortcut, or its shell id.</summary>
    public string Launch => IsShortcut ? AppId : $@"shell:AppsFolder\{AppId}";
}

/// <summary>
/// The apps in the Start menu's All apps list (Windows' AppsFolder: desktop programs and Store
/// apps alike), plus programs that only put a shortcut on the desktop (repacks and other
/// installers often skip the Start menu); read only when asked. Uninstallers and documents are
/// left out.
/// </summary>
public static class StartMenuApps
{
    public static List<StartMenuApp> List()
    {
        var apps = StartMenu();
        try
        {
            apps.AddRange(DesktopShortcuts(apps));
        }
        catch (Exception ex)
        {
            Log.Error("Listing the desktop's shortcuts", ex);
        }
        return apps.DistinctBy(a => a.AppId).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Program shortcuts on the desktop (the user's and everyone's) that the Start menu
    /// doesn't already have (same program or same name).</summary>
    static List<StartMenuApp> DesktopShortcuts(List<StartMenuApp> known)
    {
        var programs = new HashSet<string>(known.Select(a => a.Program).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(known.Select(a => a.Name), StringComparer.CurrentCultureIgnoreCase);
        var found = new List<StartMenuApp>();
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            string dir = Environment.GetFolderPath(folder);
            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0 || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || names.Contains(name)) continue;
                string? program = ShortcutTarget(file);
                if (program == null || !programs.Add(program)) continue;
                names.Add(name);
                found.Add(new StartMenuApp(name, file, program));
            }
        }
        return found;
    }

    /// <summary>The program a shortcut opens, if it's an .exe that exists.</summary>
    static string? ShortcutTarget(string shortcut)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)link!).Load(shortcut, 0);
            var target = new char[1024];
            if (((IShellLinkW)link).GetPath(target, target.Length, IntPtr.Zero, 0) != 0) return null;
            string path = Environment.ExpandEnvironmentVariables(new string(target).TrimEnd('\0'));
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    static List<StartMenuApp> StartMenu()
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
        return apps;
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

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int size, IntPtr findData, uint flags);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
