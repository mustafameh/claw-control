using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawControl.Core;
using NAudio.Wave;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinRT;

namespace ClawControl.Hardware;

/// <summary>How recordings are made (the Capture screen).</summary>
public sealed record RecordOptions(int Fps, bool High, bool GameSound);

/// <summary>
/// Screenshots and recordings of the main screen, made here (Windows' own Game Bar capture is
/// often switched off, and only records one app). Windows.Graphics.Capture hands over each new
/// picture on the GPU, full-screen games included; recordings go to the GPU's video encoder
/// (Media Foundation, H.264 in MP4) with the game's sound from the speakers' loopback. Files go
/// to Videos\Captures, where Windows' own captures go. Nothing runs except while recording.
/// </summary>
public sealed class ScreenCapture
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    Recorder? _recorder;

    /// <summary>When the recording running now started, or null.</summary>
    public DateTime? RecordingSince => _recorder?.Started;

    /// <summary>A recording started or stopped (any thread).</summary>
    public event Action? Changed;

    /// <summary>Saves what the main screen shows now as a PNG; its path, or null if it failed.</summary>
    public Task<string?> ScreenshotAsync(string name) => Task.Run(async () =>
    {
        try
        {
            var item = Direct3D.MainScreen();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Direct3D.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);
            Direct3D.Quiet(session);
            var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (p, _) =>
            {
                var frame = p.TryGetNextFrame();
                if (frame != null && !arrived.TrySetResult(frame)) frame.Dispose();
            };
            session.StartCapture();
            using var shot = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var copy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(shot.Surface, BitmapAlphaMode.Premultiplied);
            // Games often leave the alpha channel empty: saved as plain colour, not see-through.
            using var opaque = SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var file = await NewFile(name, ".png");
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(opaque);
                await encoder.FlushAsync();
            }
            Log.Info($"Screenshot saved: {file.Path}");
            return file.Path;
        }
        catch (Exception ex)
        {
            Log.Error("Taking a screenshot", ex);
            return null;
        }
    });

    /// <summary>Starts recording the main screen; false if it couldn't start.</summary>
    public async Task<bool> StartRecordingAsync(string name, RecordOptions options)
    {
        if (_recorder != null) return true;
        try
        {
            var file = await NewFile(name, ".mp4");
            var recorder = new Recorder(file, options);
            _recorder = recorder;
            recorder.Finished += () =>
            {
                if (_recorder == recorder) _recorder = null;
                Changed?.Invoke();
            };
            await recorder.StartAsync();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Starting a recording", ex);
            _recorder = null;
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Stops the recording (the file is finished in the background).</summary>
    public void StopRecording() => _recorder?.Stop();

    /// <summary>Claw Control is closing: finish the file (at most <paramref name="wait"/>).</summary>
    public void Shutdown(TimeSpan wait)
    {
        var recorder = _recorder;
        recorder?.Stop();
        recorder?.Done.Wait(wait);
    }

    static async Task<StorageFile> NewFile(string name, string extension)
    {
        Directory.CreateDirectory(Folder);
        var folder = await StorageFolder.GetFolderFromPathAsync(Folder);
        string clean = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (clean.Length == 0) clean = "Screen";
        return await folder.CreateFileAsync($"{clean} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{extension}", CreationCollisionOption.GenerateUniqueName);
    }

    /// <summary>
    /// One recording: new pictures from the capture and the speakers' sound go into a media
    /// stream that Media Foundation encodes to the file as fast as they come.
    /// </summary>
    sealed class Recorder
    {
        readonly StorageFile _file;
        readonly RecordOptions _options;
        readonly BlockingCollection<Direct3D11CaptureFrame> _frames = new(boundedCapacity: 2);
        readonly ConcurrentQueue<byte[]> _sound = new();
        readonly AutoResetEvent _soundArrived = new(false);
        readonly Stopwatch _clock = new();
        readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Direct3D11CaptureFramePool? _pool;
        GraphicsCaptureSession? _session;
        WasapiLoopbackCapture? _loopback;
        TimeSpan? _firstFrame, _lastKept;
        long _soundBytes;
        int _rate = 48000, _channels = 2;
        volatile bool _stopping;

        public DateTime Started { get; } = DateTime.Now;
        public Task Done => _done.Task;
        public event Action? Finished;

        public Recorder(StorageFile file, RecordOptions options)
        {
            _file = file;
            _options = options;
        }

        public async Task StartAsync()
        {
            var item = Direct3D.MainScreen();
            int width = item.Size.Width & ~1, height = item.Size.Height & ~1;  // the encoder wants even sizes

            var video = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height));
            AudioStreamDescriptor? audio = null;
            if (_options.GameSound)
            {
                _loopback = new WasapiLoopbackCapture();
                _rate = _loopback.WaveFormat.SampleRate;
                _channels = Math.Min(2, _loopback.WaveFormat.Channels);
                audio = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)_rate, (uint)_channels, 16));
            }
            var source = audio != null ? new MediaStreamSource(video, audio) : new MediaStreamSource(video);
            source.BufferTime = TimeSpan.Zero;
            source.CanSeek = false;
            source.SampleRequested += OnSampleRequested;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)_options.Fps;
            profile.Video.FrameRate.Denominator = 1;
            // Bits per second: ~0.09 / 0.15 bits per pixel per frame (standard / high).
            profile.Video.Bitrate = (uint)(width * height * _options.Fps * (_options.High ? 0.15 : 0.09));
            profile.Audio = audio != null ? AudioEncodingProperties.CreateAac((uint)_rate, (uint)_channels, 192_000) : null;

            var stream = await _file.OpenAsync(FileAccessMode.ReadWrite);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prepared.CanTranscode) throw new InvalidOperationException($"Can't record: {prepared.FailureReason}");

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Direct3D.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
            _session = _pool.CreateCaptureSession(item);
            Direct3D.Quiet(_session, _options.Fps);
            _pool.FrameArrived += OnFrameArrived;
            if (_loopback != null)
            {
                _loopback.DataAvailable += OnSound;
                _loopback.StartRecording();
            }
            _clock.Start();
            _session.StartCapture();
            Log.Info($"Recording to {_file.Path} ({width}x{height}, {_options.Fps} FPS, {profile.Video.Bitrate / 1_000_000.0:0.#} Mbit/s, sound {_options.GameSound})");

            _ = Run(prepared, stream);
        }

        async Task Run(PrepareTranscodeResult prepared, IDisposable stream)
        {
            try
            {
                await prepared.TranscodeAsync();
                Log.Info($"Recording saved: {_file.Path}");
            }
            catch (Exception ex)
            {
                Log.Error("Recording", ex);
            }
            finally
            {
                Cleanup();
                stream.Dispose();
                _done.TrySetResult();
                Finished?.Invoke();
            }
        }

        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;
            _frames.CompleteAdding();
            _soundArrived.Set();
        }

        void Cleanup()
        {
            try { _loopback?.StopRecording(); } catch { }
            _loopback?.Dispose();
            _session?.Dispose();
            _pool?.Dispose();
            while (_frames.TryTake(out var frame)) frame.Dispose();
        }

        /// <summary>A new picture: kept at the chosen frame rate (the screen may update faster).</summary>
        void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
        {
            var frame = pool.TryGetNextFrame();
            if (frame == null) return;
            var time = frame.SystemRelativeTime;
            bool keep = !_stopping && (_lastKept is not { } last || (time - last).TotalSeconds >= 0.9 / _options.Fps);
            if (keep && _frames.TryAdd(frame))
            {
                _lastKept = time;
                return;
            }
            frame.Dispose();  // too soon, or the encoder is behind
        }

        void OnSound(object? sender, WaveInEventArgs e)
        {
            if (_stopping || e.BytesRecorded == 0 || _loopback == null) return;
            _sound.Enqueue(ToPcm16(e.Buffer, e.BytesRecorded, _loopback.WaveFormat));
            _soundArrived.Set();
        }

        /// <summary>The speakers' mix (usually 32-bit float, maybe more than two channels) as
        /// 16-bit stereo, what the AAC encoder takes.</summary>
        byte[] ToPcm16(byte[] buffer, int length, WaveFormat format)
        {
            int inChannels = format.Channels, bytesPerSample = format.BitsPerSample / 8;
            int frames = length / (bytesPerSample * inChannels);
            var pcm = new byte[frames * _channels * 2];
            bool floats = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < _channels; c++)
                {
                    int at = (f * inChannels + c) * bytesPerSample;
                    short value = floats
                        ? (short)Math.Clamp(BitConverter.ToSingle(buffer, at) * 32767f, -32768f, 32767f)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(buffer, at) : (short)(BitConverter.ToInt32(buffer, at) >> 16);
                    BitConverter.TryWriteBytes(pcm.AsSpan((f * _channels + c) * 2), value);
                }
            return pcm;
        }

        void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            try
            {
                if (args.Request.StreamDescriptor is VideoStreamDescriptor) NextPicture(args.Request);
                else NextSound(args.Request);
            }
            catch (Exception ex)
            {
                Log.Error("Recording a sample", ex);
                args.Request.Sample = null;
            }
        }

        void NextPicture(MediaStreamSourceSampleRequest request)
        {
            // Waits for the next new picture; none (stopped): the end of the video.
            Direct3D11CaptureFrame? frame = null;
            try { frame = _frames.Take(); }
            catch (InvalidOperationException) { }
            if (frame == null)
            {
                request.Sample = null;
                return;
            }
            _firstFrame ??= frame.SystemRelativeTime;
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, frame.SystemRelativeTime - _firstFrame.Value);
            sample.Duration = TimeSpan.FromSeconds(1.0 / _options.Fps);
            sample.Processed += (_, _) => frame.Dispose();
            request.Sample = sample;
        }

        /// <summary>The next stretch of sound; while nothing plays (the loopback sends nothing
        /// then) silence keeps the sound in step with the clock.</summary>
        void NextSound(MediaStreamSourceSampleRequest request)
        {
            int bytesPerSecond = _rate * _channels * 2;
            while (true)
            {
                if (_sound.TryDequeue(out var pcm))
                {
                    request.Sample = Sound(pcm, bytesPerSecond);
                    return;
                }
                if (_stopping)
                {
                    request.Sample = null;
                    return;
                }
                _soundArrived.WaitOne(20);
                if (!_sound.IsEmpty) continue;
                // Behind the clock by more than a moment: fill with silence up to now (≤ 100 ms at a time).
                long due = (long)(_clock.Elapsed.TotalSeconds * bytesPerSecond) - _soundBytes;
                if (due > bytesPerSecond / 25)
                {
                    long length = Math.Min(due, bytesPerSecond / 10) / (_channels * 2) * (_channels * 2);
                    request.Sample = Sound(new byte[length], bytesPerSecond);
                    return;
                }
            }
        }

        MediaStreamSample Sound(byte[] pcm, int bytesPerSecond)
        {
            var time = TimeSpan.FromSeconds(_soundBytes / (double)bytesPerSecond);
            _soundBytes += pcm.Length;
            var sample = MediaStreamSample.CreateFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pcm), time);
            sample.Duration = TimeSpan.FromSeconds(pcm.Length / (double)bytesPerSecond);
            return sample;
        }
    }

    /// <summary>The GPU device the captures use, and the main screen as a capture item.</summary>
    static class Direct3D
    {
        static IDirect3DDevice? _device;

        public static IDirect3DDevice Device => _device ??= Create();

        static IDirect3DDevice Create()
        {
            const uint BgraSupport = 0x20, SdkVersion = 7;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out IntPtr device, out _, out IntPtr context));
            Marshal.Release(context);
            try
            {
                var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in dxgiId, out IntPtr dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr inspectable));
                    try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally { Marshal.Release(device); }
        }

        public static GraphicsCaptureItem MainScreen()
        {
            var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            IntPtr item = interop.CreateForMonitor(MonitorFromPoint(default, 1), ref itemId);
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }

        /// <summary>No mouse pointer and (Windows 11) no yellow border around the screen; at most
        /// <paramref name="fps"/> pictures a second when given. The SDK this builds against
        /// predates the last two, so they're set through their interfaces (Windows' metadata).</summary>
        public static void Quiet(GraphicsCaptureSession session, int fps = 0)
        {
            try { session.IsCursorCaptureEnabled = false; } catch { }
            Call(session, "f2cdd966-22ae-5ea1-9596-3a289344c3be", self => SetBool(self, false));  // IsBorderRequired
            if (fps > 0)
                Call(session, "67c0ea62-1f85-5061-925a-239be0ac09cb", self => SetTime(self, TimeSpan.FromSeconds(0.95 / fps).Ticks));  // MinUpdateInterval
        }

        static void Call(GraphicsCaptureSession session, string interfaceId, Func<IntPtr, int> set)
        {
            try
            {
                var id = new Guid(interfaceId);
                if (Marshal.QueryInterface(((IWinRTObject)session).NativeObject.ThisPtr, in id, out IntPtr self) != 0) return;
                try { set(self); }
                finally { Marshal.Release(self); }
            }
            catch (Exception ex)
            {
                Log.Error("Setting up the screen capture", ex);
            }
        }

        // After IInspectable's six methods: get (6), put (7).
        static unsafe int SetBool(IntPtr self, bool value) => ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)(*(IntPtr**)self)[7])(self, (byte)(value ? 1 : 0));
        static unsafe int SetTime(IntPtr self, long ticks) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)(*(IntPtr**)self)[7])(self, ticks);

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount,
            uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
        [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }
}
