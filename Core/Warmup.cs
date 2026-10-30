using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClawControl.Core;

/// <summary>
/// Compiles the panel's own code once, in the background a little after startup, so the first
/// time something is used (the app switcher growing, a screen opening) .NET doesn't compile it
/// right then and drop frames. Precompiled (ReadyToRun) builds measured no better at startup and
/// add 31 MB; this costs a moment of one low-priority thread, once.
/// </summary>
static class Warmup
{
    static readonly string[] Namespaces = ["ClawControl.UI", "ClawControl.Features", "ClawControl.Input", "ClawControl.Hardware.RunningApps"];

    public static void Start(TimeSpan delay) => new Thread(() =>
    {
        Thread.Sleep(delay);
        Run();
    })
    { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Warm-up" }.Start();

    static void Run()
    {
        var watch = Stopwatch.StartNew();
        int count = 0;
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            foreach (var type in typeof(Warmup).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters || type.FullName is not { } name || !Namespaces.Any(name.StartsWith)) continue;
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        count++;
                    }
                    catch { }  // e.g. a method the runtime can't prepare on its own
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Warming up", ex);
        }
        Log.Info($"Warm-up: {count:N0} methods compiled ahead in {watch.ElapsedMilliseconds:N0} ms");
    }
}
