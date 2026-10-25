using System.Collections.Concurrent;

namespace ClawControl.Core;

/// <summary>
/// One background thread that owns every slow hardware call (firmware/WMI, power APIs),
/// so the UI thread never waits on them. Work runs in the order it was submitted.
/// </summary>
public sealed class HardwareWorker : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();

    public HardwareWorker()
    {
        new Thread(Loop) { IsBackground = true, Name = "Hardware" }.Start();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
            work();
    }

    public void Dispose() => _queue.CompleteAdding();
}
