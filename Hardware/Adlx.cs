using System.Runtime.InteropServices;

namespace ClawControl.Hardware;

/// <summary>
/// Calls into AMD's driver settings library (ADLX, amdadlx64.dll, installed with the Radeon
/// driver) through the C function tables of AMD's SDK headers
/// (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include). Every interface pointer points to its
/// table; the numbers below are positions in each "...Vtbl" struct. Not thread-safe: use from one
/// thread (AmdGraphics' worker).
/// </summary>
static unsafe class Adlx
{
    // IADLXSystem (not reference counted, released by ADLXTerminate).
    public const int SystemGetGpus = 1, SystemQueryInterface = 2, SystemGetDisplayServices = 3, SystemGet3DSettings = 7;
    // Every other interface starts with IADLXInterface: Acquire, Release, QueryInterface.
    const int Release = 1, QueryInterface = 2;
    // Lists (GPUs, displays, memory options): Size, then the typed At after the IADLXList methods.
    public const int ListSize = 3, ListAt = 11;
    // IADLX3DChill
    public const int ChillSupported = 3, ChillEnabled = 4, ChillRange = 5, ChillMaxFps = 7, ChillSetEnabled = 8, ChillSetMin = 9, ChillSetMax = 10;
    // IADLX3DSettingsServices(1)
    public const int GetChill = 4, GetImageSharpening = 6, GetFrameRateTargetControl = 9, GetRadeonSuperResolution = 14, GetFluidMotionFrames = 17;
    // IADLX3DFrameRateTargetControl
    public const int FrtcSupported = 3, FrtcEnabled = 4, FrtcRange = 5, FrtcFps = 6, FrtcSetEnabled = 7, FrtcSetFps = 8;
    // IADLX3DImageSharpening
    public const int RisSupported = 3, RisEnabled = 4, RisRange = 5, RisSharpness = 6, RisSetEnabled = 7, RisSetSharpness = 8;
    // IADLX3DRadeonSuperResolution (note: a different order from image sharpening)
    public const int RsrSupported = 3, RsrEnabled = 4, RsrSetEnabled = 5, RsrRange = 6, RsrSharpness = 7, RsrSetSharpness = 8;
    // IADLX3DAMDFluidMotionFrames and IADLXDisplayFreeSync
    public const int ToggleSupported = 3, ToggleEnabled = 4, ToggleSetEnabled = 5;
    // IADLXDisplayServices, IADLXDisplay
    public const int GetDisplays = 4, GetFreeSync = 9, DisplayConnectorType = 5, ConnectorEdp = 15;
    // IADLXSystem3, IADLXVariableGraphicsMemory, IADLXVariableGraphicsMemoryOption
    public const int GetVariableGraphicsMemory = 6;
    public const int VgmSupported = 3, VgmGetOption = 5, VgmGetOptions = 6, VgmSetOption = 7;
    public const int OptionMode = 4, OptionCarvedGb = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct IntRange { public int Min, Max, Step; }

    public sealed class Exception(string what, int code) : System.Exception($"{what} failed (ADLX result {code})")
    {
        public int Code { get; } = code;
    }

    [DllImport("amdadlx64.dll", EntryPoint = "ADLXQueryFullVersion", CallingConvention = CallingConvention.Cdecl)]
    static extern int QueryFullVersion(out ulong version);

    // The plain ADLXInitialize treats MSI's OEM Radeon driver as unknown and then lists no GPUs
    // (so no FPS limit, sharpening or FreeSync); MSI's own ADLX helpers use this variant too.
    [DllImport("amdadlx64.dll", EntryPoint = "ADLXInitializeWithIncompatibleDriver", CallingConvention = CallingConvention.Cdecl)]
    static extern int Initialize(ulong version, out IntPtr system);

    [DllImport("amdadlx64.dll", EntryPoint = "ADLXTerminate", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Terminate();

    /// <summary>Starts ADLX at the version the installed driver has; returns IADLXSystem.</summary>
    public static IntPtr Start()
    {
        Check(QueryFullVersion(out ulong version), "ADLXQueryFullVersion");
        Check(Initialize(version, out IntPtr system), "ADLXInitialize");
        return system;
    }

    static void Check(int result, string what)
    {
        // OK, already enabled, already initialized
        if (result is not (0 or 1 or 2)) throw new Exception(what, result);
    }

    static IntPtr Fn(IntPtr obj, int index) => ((IntPtr*)*(IntPtr*)obj)[index];

    public static void Free(IntPtr obj)
    {
        if (obj != 0) ((delegate* unmanaged<IntPtr, int>)Fn(obj, Release))(obj);
    }

    /// <summary>A method returning an interface: (this, out result).</summary>
    public static IntPtr Get(IntPtr obj, int index)
    {
        IntPtr result = 0;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(obj, index))(obj, &result), $"#{index}");
        return result;
    }

    /// <summary>A method taking one pointer or index: (this, arg, out result).</summary>
    public static IntPtr Get(IntPtr obj, int index, IntPtr arg)
    {
        IntPtr result = 0;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Fn(obj, index))(obj, arg, &result), $"#{index}");
        return result;
    }

    /// <summary>Asks an interface for a newer version of itself (e.g. "IADLXSystem3"); 0 if the driver lacks it.</summary>
    public static IntPtr Query(IntPtr obj, string interfaceId, int index = QueryInterface)
    {
        IntPtr result = 0;
        fixed (char* id = interfaceId)
        {
            int code = ((delegate* unmanaged<IntPtr, char*, IntPtr*, int>)Fn(obj, index))(obj, id, &result);
            return code is 0 or 1 or 2 ? result : 0;
        }
    }

    public static uint Size(IntPtr list) => ((delegate* unmanaged<IntPtr, uint>)Fn(list, ListSize))(list);

    public static IntPtr At(IntPtr list, uint position) => Get(list, ListAt, (IntPtr)position);

    public static bool Bool(IntPtr obj, int index)
    {
        byte value = 0;
        Check(((delegate* unmanaged<IntPtr, byte*, int>)Fn(obj, index))(obj, &value), $"#{index}");
        return value != 0;
    }

    /// <summary>For IsSupported: false when the call itself fails.</summary>
    public static bool Supported(IntPtr obj, int index)
    {
        if (obj == 0) return false;
        try { return Bool(obj, index); }
        catch (Exception) { return false; }
    }

    public static void SetBool(IntPtr obj, int index, bool value) =>
        Check(((delegate* unmanaged<IntPtr, byte, int>)Fn(obj, index))(obj, (byte)(value ? 1 : 0)), $"#{index}");

    public static int Int(IntPtr obj, int index)
    {
        int value = 0;
        Check(((delegate* unmanaged<IntPtr, int*, int>)Fn(obj, index))(obj, &value), $"#{index}");
        return value;
    }

    public static void SetInt(IntPtr obj, int index, int value) =>
        Check(((delegate* unmanaged<IntPtr, int, int>)Fn(obj, index))(obj, value), $"#{index}");

    public static double Double(IntPtr obj, int index)
    {
        double value = 0;
        Check(((delegate* unmanaged<IntPtr, double*, int>)Fn(obj, index))(obj, &value), $"#{index}");
        return value;
    }

    public static IntRange Range(IntPtr obj, int index)
    {
        IntRange range = default;
        Check(((delegate* unmanaged<IntPtr, IntRange*, int>)Fn(obj, index))(obj, &range), $"#{index}");
        return range;
    }

    public static void Call(IntPtr obj, int index, IntPtr arg) =>
        Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(obj, index))(obj, arg), $"#{index}");
}
