using ClawControl.Core;
using HidSharp;

namespace ClawControl.Hardware;

/// <summary>
/// Talks to the Claw's built-in controller chip directly, the way MSI's own controller library
/// does (protocol in docs/HARDWARE.md, "Controller chip"): its settings live in one 1478-byte block
/// ("profile") on the chip, and the chip applies them itself, so nothing of ours runs while
/// you play. We read the block, change only the bytes for the setting you touched, write just
/// those bytes, and ask the chip to save a moment after the last change.
///
/// All I/O runs on its own worker. A reader thread sits blocked on the device waiting for
/// replies (no polling, no CPU while idle) and also hears mode changes made by MSI.
///
/// While the panel is open a temporary mapping can sit on top of the settings (SetOverlay): it
/// goes to the chip's working memory only and is never saved; saving waits until it's off. A
/// restore file names the bytes it covered, so if Claw Control stops with it on, the next start
/// puts them back.
/// </summary>
public sealed class ClawPad : IDisposable
{
    public const int ProfileLength = 1478;
    const int Vid = 0x0DB0;
    const byte ReportId = 0x0F;
    const int ChunkLength = 55;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1000);
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);
    static readonly string RestorePath = Path.Combine(Log.Dir, "controller-restore.bin");

    const byte CmdReadProfile = 4, CmdReadProfileReply = 5, CmdWriteProfile = 0x21, CmdSyncToRom = 34,
        CmdSwitchMode = 36, CmdReadMode = 38, CmdModeReply = 39, CmdDisableDevice = 45, CmdRgbControl = 224,
        CmdMotionReply = 48;

    readonly HardwareWorker _worker = new();
    readonly object _gate = new();
    readonly List<(Func<byte[], bool> Match, TaskCompletionSource<byte[]> Reply)> _waiters = new();
    readonly Timer _saveTimer, _reapplyTimer;
    HidStream? _stream;
    byte[]? _chip;      // what the chip has (as far as we know)
    byte[]? _desired;   // what we want it to have; differences get written
    bool _flushQueued;
    Func<byte[], byte[]>? _overlay;        // on the chip now: the wanted block -> what the chip gets
    Func<byte[], byte[]>? _overlayWanted;  // asked for (applied in order on the worker)
    bool _saveAfterOverlay;                // a save is due once the overlay is off

    /// <summary>Raw mode from the chip (0 off, 1 Xbox, 2 PlayStation, 3 MSI, 4 Desktop), if known.</summary>
    public int? Mode { get; private set; }

    /// <summary>M-keys function byte reported with the mode (kept as is when switching).</summary>
    public int MKeys { get; private set; }

    /// <summary>The settings block as we want it (null until first read). Read-only copy.</summary>
    public byte[]? Profile { get { lock (_gate) return _desired?.ToArray(); } }

    /// <summary>Profile or mode changed (any thread).</summary>
    public event Action? Changed;

    /// <summary>The chip reported a mode change nobody here asked for (e.g. MSI). Any thread.</summary>
    public event Action<int>? ModeReported;

    public ClawPad()
    {
        _saveTimer = new Timer(_ => _worker.Run(() => { SaveToRom(); return 0; }));
        _reapplyTimer = new Timer(_ => _worker.Run(ReapplyOverlay));
        DeviceList.Local.Changed += (_, _) => Close();  // re-find after re-enumeration (mode switch)
        // Claw Control stopped while the panel mapping was on: put the buttons back right away.
        if (File.Exists(RestorePath)) _ = LoadAsync();
    }

    // ---- Reading ----

    /// <summary>Reads mode and the whole settings block (about 30 small messages).</summary>
    public Task<bool> LoadAsync() => _worker.Run(Load);

    bool Load()
    {
        try
        {
            ReadModeCore();
            var block = new byte[ProfileLength];
            for (int offset = 0; offset < ProfileLength; offset += ChunkLength)
            {
                int length = Math.Min(ChunkLength, ProfileLength - offset);
                int off = offset;
                var reply = Request(new byte[] { CmdReadProfile, 0, (byte)(off >> 8), (byte)off, (byte)length },
                    r => r[4] == CmdReadProfileReply && r[6] == (byte)(off >> 8) && r[7] == (byte)off && r[8] == length);
                if (reply == null) throw new IOException($"No reply for settings at {off}");
                Array.Copy(reply, 9, block, off, length);
            }
            bool restore;
            lock (_gate)
            {
                _chip = block;
                // Keep changes the user made before the read finished.
                _desired ??= block.ToArray();
                restore = _overlay == null && ApplyRestorePoint();
            }
            Log.Info($"Controller settings read (mode {Mode})");
            if (restore)
            {
                Log.Info("Claw Control had stopped with the panel open: putting the controller's buttons back");
                if (Flush(save: false)) SaveToRom();
                DeleteRestorePoint();
            }
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Reading controller settings", ex);
            return false;
        }
    }

    public Task<int?> ReadModeAsync() => _worker.Run(() =>
    {
        try { ReadModeCore(); }
        catch (Exception ex) { Log.Error("Reading controller mode", ex); }
        return Mode;
    });

    void ReadModeCore()
    {
        var reply = Request(new byte[] { CmdReadMode }, r => r[4] == CmdModeReply);
        if (reply == null) return;
        Mode = reply[5];
        MKeys = reply[6];
    }

    // ---- Writing ----

    /// <summary>
    /// Changes bytes of the settings block. Applied to the chip at once (in the background, only
    /// what differs) and saved shortly after the last change.
    /// </summary>
    public void Patch(int offset, params byte[] bytes)
    {
        lock (_gate)
        {
            if (_desired == null) return;  // not read yet: nothing safe to write against
            Array.Copy(bytes, 0, _desired, offset, bytes.Length);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        Changed?.Invoke();
        _worker.Run(() => { Flush(); return 0; });
    }

    /// <summary>
    /// Sends a range again even though we think the chip has it (someone else, i.e. MSI, may have
    /// overwritten it).
    /// </summary>
    public void Rewrite(int offset, int length)
    {
        lock (_gate)
        {
            if (Target() is not { } target || _chip == null) return;
            for (int i = offset; i < offset + length; i++) _chip[i] = (byte)~target[i];
            if (_flushQueued) return;
            _flushQueued = true;
        }
        _worker.Run(() => { Flush(); return 0; });
    }

    /// <summary>What the chip should have: the wanted block, with the overlay on top while it's on. Call with _gate held.</summary>
    byte[]? Target() => _desired == null ? null : _overlay?.Invoke(_desired) ?? _desired;

    /// <summary>Writes what differs; <paramref name="save"/>: save it shortly after (or once the overlay is off).</summary>
    bool Flush(bool save = true)
    {
        List<(int Start, byte[] Data)> runs;
        lock (_gate)
        {
            _flushQueued = false;
            if (Target() is not { } target || _chip == null) return false;
            // Nearby changes go as one write (up to a message's worth of bytes in between): the
            // panel mapping takes 4 messages instead of 18.
            runs = Differences(_chip, target, ChunkLength - 1);
        }
        if (runs.Count == 0) return true;
        try
        {
            foreach (var (start, data) in runs)
            {
                for (int i = 0; i < data.Length; i += ChunkLength)
                {
                    int length = Math.Min(ChunkLength, data.Length - i);
                    int offset = start + i;
                    var message = new byte[5 + length];
                    message[0] = CmdWriteProfile;
                    message[1] = 1;  // profile number, as MSI writes it
                    message[2] = (byte)(offset >> 8);
                    message[3] = (byte)offset;
                    message[4] = (byte)length;
                    Array.Copy(data, i, message, 5, length);
                    if (Request(message, IsAck) == null) throw new IOException($"Controller didn't confirm a write at {offset}");
                }
                lock (_gate) Array.Copy(data, 0, _chip!, start, data.Length);
            }
            if (save)
                lock (_gate)
                {
                    if (_overlay != null) _saveAfterOverlay = true;
                    else _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
                }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Writing controller settings", ex);
            return false;
        }
    }

    /// <summary>Contiguous runs where the two blocks differ (gaps up to <paramref name="maxGap"/> merged), with the bytes of <paramref name="to"/>.</summary>
    static List<(int, byte[])> Differences(byte[] from, byte[] to, int maxGap = 4)
    {
        var runs = new List<(int, byte[])>();
        int i = 0;
        while (i < to.Length)
        {
            if (from[i] == to[i]) { i++; continue; }
            int start = i, end = i;
            for (int j = i; j < to.Length && j - end <= maxGap; j++)
                if (from[j] != to[j]) end = j;
            runs.Add((start, to[start..(end + 1)]));
            i = end + 1;
        }
        return runs;
    }

    void SaveToRom()
    {
        // Saving copies the chip's whole working memory, overlay included: wait until it's off.
        lock (_gate)
            if (_overlay != null)
            {
                _saveAfterOverlay = true;
                return;
            }
        try
        {
            if (Request(new byte[] { CmdSyncToRom }, IsAck) != null) Log.Info("Controller settings saved");
        }
        catch (Exception ex)
        {
            Log.Error("Saving controller settings", ex);
        }
    }

    // ---- Temporary mapping while the panel is open ----

    /// <summary>
    /// Puts <paramref name="overlay"/> (the wanted block -> what the chip gets) on the chip, or
    /// with null takes it off and writes the wanted settings back. Changes made meanwhile go to
    /// the wanted settings as usual; where the overlay covers them they reach the chip when it's
    /// off. The task ends when the chip has it.
    /// </summary>
    public Task SetOverlay(Func<byte[], byte[]>? overlay)
    {
        lock (_gate) _overlayWanted = overlay;
        return _worker.Run(ApplyOverlay);
    }

    int ApplyOverlay()
    {
        Func<byte[], byte[]>? wanted;
        lock (_gate)
        {
            wanted = _overlayWanted;
            if (wanted == _overlay) return 0;
        }
        if (wanted != null) BeginOverlay(wanted);
        else EndOverlay();
        return 0;
    }

    /// <summary>Puts an overlay on, or swaps it for another (writing only what differs).</summary>
    void BeginOverlay(Func<byte[], byte[]> overlay)
    {
        if (Profile == null && !Load()) return;  // panel opened before the first read
        var clock = System.Diagnostics.Stopwatch.StartNew();
        byte[] undo;
        lock (_gate)
        {
            _overlay = overlay;
            undo = RestorePoint(_desired!, overlay(_desired!));
        }
        try { File.WriteAllBytes(RestorePath, undo); }
        catch (Exception ex) { Log.Error("Saving the controller's restore point", ex); }
        if (Flush(save: false)) Log.Detail($"Panel mapping on ({clock.ElapsedMilliseconds} ms)");
    }

    void EndOverlay()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate) _overlay = null;
        if (!Flush(save: false))
        {
            Thread.Sleep(300);
            if (!Flush(save: false)) return;  // the restore point stays for the next start
        }
        Log.Detail($"Panel mapping off ({clock.ElapsedMilliseconds} ms)");
        bool save;
        lock (_gate)
        {
            save = _saveAfterOverlay;
            _saveAfterOverlay = false;
        }
        if (save) SaveToRom();
        DeleteRestorePoint();
    }

    /// <summary>MSI changed the mode while the overlay was on: send the overlay's bytes again in case the chip dropped them.</summary>
    int ReapplyOverlay()
    {
        lock (_gate)
        {
            if (_overlay == null || Target() is not { } target || _chip == null) return 0;
            for (int i = 0; i < target.Length; i++)
                if (target[i] != _desired![i]) _chip[i] = (byte)~target[i];
        }
        Flush(save: false);
        return 0;
    }

    /// <summary>The wanted bytes wherever the overlay differs: [offset (2), length (2), bytes] per run.</summary>
    static byte[] RestorePoint(byte[] wanted, byte[] overlaid)
    {
        var record = new List<byte>();
        foreach (var (start, data) in Differences(overlaid, wanted))
        {
            record.AddRange(new[] { (byte)(start >> 8), (byte)start, (byte)(data.Length >> 8), (byte)data.Length });
            record.AddRange(data);
        }
        return record.ToArray();
    }

    /// <summary>Copies a restore point left by a stop with the overlay on into the wanted block. Call with _gate held.</summary>
    bool ApplyRestorePoint()
    {
        if (_desired == null || !File.Exists(RestorePath)) return false;
        try
        {
            var record = File.ReadAllBytes(RestorePath);
            for (int i = 0; i + 4 <= record.Length;)
            {
                int start = record[i] << 8 | record[i + 1], length = record[i + 2] << 8 | record[i + 3];
                if (i + 4 + length > record.Length || start + length > ProfileLength) break;
                Array.Copy(record, i + 4, _desired, start, length);
                i += 4 + length;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the controller's restore point", ex);
            return false;
        }
    }

    static void DeleteRestorePoint()
    {
        try { File.Delete(RestorePath); }
        catch (Exception ex) { Log.Error("Removing the controller's restore point", ex); }
    }

    /// <summary>Restarts the light animation so new light settings show at once.</summary>
    public void PlayLights() => _worker.Run(() => { Send(new byte[] { CmdRgbControl, 1, 0 }); return 0; });

    /// <summary>Xbox 1, PlayStation 2, Desktop 4. The chip re-appears as a new device after this.</summary>
    public Task SwitchModeAsync(int mode) => _worker.Run(() =>
    {
        try
        {
            Log.Info($"Switching controller to mode {mode}");
            Send(new byte[] { CmdSwitchMode, (byte)mode, (byte)MKeys });
            Thread.Sleep(150);
            ReadModeCore();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Switching controller mode", ex);
        }
        return 0;
    });

    /// <summary>Turns the built-in controller off (no input at all) or back on.</summary>
    public Task SetDisabledAsync(bool off) => _worker.Run(() =>
    {
        try
        {
            Log.Info(off ? "Turning the controller off" : "Turning the controller on");
            Send(new byte[] { CmdDisableDevice, (byte)(off ? 1 : 0) });
        }
        catch (Exception ex)
        {
            Log.Error("Turning the controller on/off", ex);
        }
        return 0;
    });

    // ---- Transport ----

    static bool IsAck(byte[] reply) => reply[4] is not (CmdReadProfileReply or 10 or CmdModeReply or 48 or 244);

    /// <summary>Sends and waits for the matching reply; retries twice. Worker thread only.</summary>
    byte[]? Request(byte[] command, Func<byte[], bool> match)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters) _waiters.Add((match, reply));
            try
            {
                Send(command);
                if (reply.Task.Wait(ReplyTimeout)) return reply.Task.Result;
            }
            finally
            {
                lock (_waiters) _waiters.RemoveAll(w => w.Reply == reply);
            }
        }
        return null;
    }

    void Send(byte[] command)
    {
        var stream = Open();
        var report = new byte[64];
        report[0] = ReportId;
        report[3] = 0x3C;
        Array.Copy(command, 0, report, 4, Math.Min(command.Length, 60));
        try
        {
            stream.Write(report);
        }
        catch (IOException)
        {
            Close();
            Open().Write(report);  // the device was re-created (mode switch): one retry
        }
    }

    HidStream Open()
    {
        lock (_gate)
        {
            if (_stream != null) return _stream;
            var device = DeviceList.Local.GetHidDevices(Vid).FirstOrDefault(IsSettingsChannel)
                ?? throw new IOException("Controller settings channel not found");
            if (!device.TryOpen(out var stream)) throw new IOException("Couldn't open the controller settings channel");
            stream.ReadTimeout = Timeout.Infinite;
            _stream = stream;
            new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "Controller replies" }.Start();
            return stream;
        }
    }

    static bool IsSettingsChannel(HidDevice device)
    {
        try { return device.GetMaxOutputReportLength() == 64 && device.GetReportDescriptor().OutputReports.Any(r => r.ReportID == ReportId); }
        catch { return false; }
    }

    void Close()
    {
        lock (_gate)
        {
            _stream?.Dispose();  // ends the reader thread
            _stream = null;
        }
    }

    /// <summary>Blocked in the driver until a reply arrives; costs nothing while idle.</summary>
    void ReadLoop(HidStream stream)
    {
        var buffer = new byte[64];
        while (true)
        {
            int length;
            try { length = stream.Read(buffer, 0, buffer.Length); }
            catch { break; }  // closed or device gone
            if (length < 9) continue;
            var reply = buffer[..length].ToArray();
            bool claimed = false;
            lock (_waiters)
            {
                foreach (var waiter in _waiters)
                    if (waiter.Match(reply) && waiter.Reply.TrySetResult(reply)) { claimed = true; break; }
            }
            if (claimed) continue;
            if (reply[4] != CmdMotionReply)
                lock (_gate)
                    // Someone else (MSI) wrote to the chip; MSI saves after its writes, which would
                    // save the overlay too, so save the real settings again once it's off.
                    if (_overlay != null) _saveAfterOverlay = true;
            if (reply[4] == CmdModeReply)
            {
                Mode = reply[5];
                MKeys = reply[6];
                ModeReported?.Invoke(reply[5]);
                bool overlaid;
                lock (_gate) overlaid = _overlay != null;
                if (overlaid) _reapplyTimer.Change(300, Timeout.Infinite);
            }
        }
    }

    public void Dispose()
    {
        // Never leave the panel mapping on the chip.
        SetOverlay(null).Wait(TimeSpan.FromSeconds(2));
        _saveTimer.Dispose();
        _reapplyTimer.Dispose();
        Close();
        _worker.Dispose();
    }
}
