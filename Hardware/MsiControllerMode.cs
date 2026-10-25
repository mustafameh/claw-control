using System.IO.Pipes;
using System.Text;
using ClawControl.Core;

namespace ClawControl.Hardware;

/// <summary>
/// Puts the controller in gamepad (XInput) mode while the panel is open, the way MSI's own Game
/// Bar panel does: by asking MSI's Command Center over its plain-text pipe. In MSI's Desktop mode
/// some buttons (LB / RB) send nothing at all, so this is what makes the panel work the same in
/// every mode. Closing asks Command Center to restore whatever mode was active before.
/// Requests run one at a time in the background; Command Center can take a second to answer.
/// </summary>
public sealed class MsiControllerMode
{
    const string PipeName = "Command_Center_XInput";
    const int ConnectTimeoutMs = 1000;
    // MSI's AI Engine can switch back to Desktop mode while the panel is open; asking again more
    // often than this would just queue up requests.
    static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(1500);

    // MSI's AI Engine switches to Desktop mode 0.2-0.5 s after any non-game window (like our
    // panel) comes to the front, undoing our request; ask again just after that.
    static readonly int[] FollowUpDelaysMs = { 500, 1000, 2000 };

    readonly object _gate = new();
    readonly List<Timer> _followUps = new();
    Task _queue = Task.CompletedTask;
    bool _requested;
    DateTime _lastRequest;

    /// <summary>Panel opened: switch to gamepad mode.</summary>
    public void Enter()
    {
        lock (_gate)
        {
            _requested = true;
            _lastRequest = DateTime.UtcNow;
            Enqueue("xinputmode_set");
            CancelFollowUps();
            foreach (int delay in FollowUpDelaysMs)
                _followUps.Add(new Timer(_ => FollowUp(), null, delay, Timeout.Infinite));
        }
    }

    void FollowUp()
    {
        lock (_gate)
        {
            if (!_requested) return;
            _lastRequest = DateTime.UtcNow;
            Enqueue("xinputmode_set");
        }
    }

    void CancelFollowUps()
    {
        foreach (var timer in _followUps) timer.Dispose();
        _followUps.Clear();
    }

    /// <summary>Desktop-mode input arrived while open: MSI switched back, so ask again.</summary>
    public void Reassert()
    {
        lock (_gate)
        {
            if (!_requested || DateTime.UtcNow - _lastRequest < RetryInterval) return;
            _lastRequest = DateTime.UtcNow;
            Log.Detail("Controller is back in Desktop mode while the panel is open; asking for gamepad mode again");
            Enqueue("xinputmode_set");
        }
    }

    /// <summary>Panel closed: restore the mode that was active before.</summary>
    public void Leave()
    {
        lock (_gate)
        {
            if (!_requested) return;
            _requested = false;
            CancelFollowUps();
            Enqueue("gamebarosd_disappear");
        }
    }

    /// <summary>Waits for queued requests to finish (used when Claw Control exits).</summary>
    public void WaitForPending(TimeSpan timeout)
    {
        Task pending;
        lock (_gate) pending = _queue;
        pending.Wait(timeout);
    }

    void Enqueue(string command) => _queue = _queue.ContinueWith(_ => Send(command), TaskScheduler.Default);

    static void Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(ConnectTimeoutMs);
            var request = Encoding.UTF8.GetBytes(command);
            pipe.Write(request, 0, request.Length);
            pipe.Flush();
            var reply = new byte[4096];
            int length = pipe.Read(reply, 0, reply.Length);
            Log.Detail($"Command Center: {command} -> '{Encoding.UTF8.GetString(reply, 0, length)}'");
        }
        catch (Exception ex)
        {
            Log.Error($"Asking MSI Command Center for '{command}'", ex);
        }
    }
}
