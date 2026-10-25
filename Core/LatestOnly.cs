namespace ClawControl.Core;

/// <summary>
/// Sends values to slow hardware on a worker, skipping values that were replaced before they
/// went out: holding the D-pad on a slider sends the newest value, not every step in between.
/// </summary>
public sealed class LatestOnly<T> where T : struct
{
    readonly HardwareWorker _worker;
    readonly Action<T> _send;
    readonly string _what;
    readonly object _lock = new();
    T? _pending;

    public LatestOnly(HardwareWorker worker, string what, Action<T> send)
    {
        _worker = worker;
        _what = what;
        _send = send;
    }

    public void Post(T value)
    {
        lock (_lock)
        {
            bool scheduled = _pending != null;
            _pending = value;
            if (scheduled) return;
        }
        _worker.Run(() =>
        {
            T next;
            lock (_lock)
            {
                next = _pending!.Value;
                _pending = null;
            }
            try { _send(next); }
            catch (Exception ex) { Log.Error(_what, ex); }
            return 0;
        });
    }
}
