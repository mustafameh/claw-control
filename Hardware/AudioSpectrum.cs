using ClawControl.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClawControl.Hardware;

/// <summary>
/// How loud the sound playing on the Claw is in <see cref="Bands"/> bands, bass first, for the
/// now-playing tile's bars. Uses Windows' loopback copy of the default output (what the speakers
/// or headphones get; never the microphone, never stored). Runs only while the panel shows a
/// playing track: a 2048-sample FFT about 47 times a second on Windows' audio thread, well under
/// a millisecond each.
/// </summary>
public sealed class AudioSpectrum
{
    public const int Bands = 30;
    const int Size = 2048, Hop = 1024;
    const double LowHz = 45, HighHz = 14000;

    readonly object _sync = new();
    readonly float[] _levels = new float[Bands];
    readonly float[] _ring = new float[Size];
    readonly double[] _window = new double[Size];
    readonly double[] _re = new double[Size], _im = new double[Size];
    int _ringAt, _sinceLast;
    double _peak = -30;
    long _lastData;
    WasapiCapture? _capture;
    bool _wanted;
    int _sampleRate = 48000;

    public AudioSpectrum()
    {
        for (int i = 0; i < Size; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
    }

    /// <summary>Starts listening (in the background; the panel never waits for it).</summary>
    public void Start()
    {
        if (_wanted) return;
        _wanted = true;
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (!_wanted || _capture != null) return;
                try
                {
                    var capture = new WasapiLoopbackCapture();
                    _sampleRate = capture.WaveFormat.SampleRate;
                    capture.DataAvailable += OnData;
                    capture.StartRecording();
                    _capture = capture;
                }
                catch (Exception ex)
                {
                    Log.Error("Listening to the sound for the visualizer", ex);
                }
            }
        });
    }

    public void Stop()
    {
        if (!_wanted) return;
        _wanted = false;
        Task.Run(() =>
        {
            lock (_sync)
            {
                if (_wanted || _capture == null) return;
                var capture = _capture;
                _capture = null;
                try
                {
                    capture.DataAvailable -= OnData;
                    capture.StopRecording();
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Stopping the visualizer's listening", ex);
                }
                Array.Clear(_levels);
            }
        });
    }

    /// <summary>Copies the latest levels (0..1). False when nothing has come in lately (paused, silent).</summary>
    public bool Read(float[] into)
    {
        bool fresh = Environment.TickCount64 - Interlocked.Read(ref _lastData) < 150;
        lock (_levels)
        {
            if (fresh) Array.Copy(_levels, into, Bands);
            else Array.Clear(into);
        }
        return fresh;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiCapture capture || e.BytesRecorded == 0) return;
        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is not (2 or 4)) return;
        int frame = channels * bytesPerSample;
        var span = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int at = 0; at + frame <= span.Length; at += frame)
        {
            // Mono: the channels averaged. The shared mix format is 32-bit float in practice.
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var sample = span.Slice(at + c * bytesPerSample, bytesPerSample);
                sum += bytesPerSample == 4 ? BitConverter.ToSingle(sample) : BitConverter.ToInt16(sample) / 32768f;
            }
            _ring[_ringAt] = sum / channels;
            _ringAt = (_ringAt + 1) % Size;
            if (++_sinceLast >= Hop)
            {
                _sinceLast = 0;
                Analyse();
            }
        }
        Interlocked.Exchange(ref _lastData, Environment.TickCount64);
    }

    void Analyse()
    {
        double energy = 0;
        for (int i = 0; i < Size; i++)
        {
            double sample = _ring[(_ringAt + i) % Size];
            energy += sample * sample;
            _re[i] = sample * _window[i];
            _im[i] = 0;
        }
        var levels = new float[Bands];
        if (energy / Size > 1e-9)
        {
            Fft(_re, _im);
            double binHz = (double)_sampleRate / Size;
            double loudest = -120;
            var db = new double[Bands];
            for (int band = 0; band < Bands; band++)
            {
                double from = LowHz * Math.Pow(HighHz / LowHz, (double)band / Bands);
                double to = LowHz * Math.Pow(HighHz / LowHz, (double)(band + 1) / Bands);
                double mid = Math.Sqrt(from * to);
                double power;
                if (to - from < binHz * 2)
                {
                    // Narrower than two bins (the bass): read between the bins at its middle.
                    double bin = mid / binHz;
                    int low = (int)bin;
                    double t = bin - low;
                    power = Power(low) * (1 - t) + Power(low + 1) * t;
                }
                else
                {
                    power = 0;
                    int first = (int)Math.Ceiling(from / binHz), last = Math.Min(Size / 2 - 1, (int)Math.Floor(to / binHz));
                    for (int bin = first; bin <= last; bin++) power = Math.Max(power, Power(bin));
                }
                // Music has less energy up high; tilt it up 3 dB an octave so the bars look even.
                db[band] = 10 * Math.Log10(power + 1e-12) + 3 * Math.Log2(mid / 100);
                loudest = Math.Max(loudest, db[band]);
            }
            // Follows the loudness (volume, quiet songs): jumps up, eases down.
            _peak = loudest > _peak ? loudest : _peak - 0.15;
            const double Range = 42;
            for (int band = 0; band < Bands; band++)
                levels[band] = (float)Math.Clamp((db[band] - (_peak - Range)) / Range, 0, 1);
        }
        lock (_levels) Array.Copy(levels, _levels, Bands);
    }

    double Power(int bin)
    {
        if (bin < 1 || bin >= Size / 2) return 0;
        return _re[bin] * _re[bin] + _im[bin] * _im[bin];
    }

    /// <summary>In-place radix-2 FFT.</summary>
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < length / 2; k++)
                {
                    int a = i + k, b = i + k + length / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
