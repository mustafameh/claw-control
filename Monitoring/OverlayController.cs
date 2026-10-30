using ClawControl.Core;

namespace ClawControl.Monitoring;

/// <summary>
/// Turns the performance overlay on and off from the saved settings. Off means nothing exists:
/// no window, no metrics thread, no sensors, no PresentMon. Nothing here blocks the caller.
/// </summary>
public sealed class OverlayController
{
    readonly Settings _settings;
    readonly Func<string> _tdpText;
    readonly Func<AmdLive?> _amd;
    MetricsSampler? _sampler;
    OverlayHud? _hud;

    public OverlayController(Settings settings, Func<string> tdpText, Func<AmdLive?> amd)
    {
        _settings = settings;
        _tdpText = tdpText;
        _amd = amd;
    }

    public bool IsRunning => _sampler != null;

    /// <summary>Call after any overlay setting changes.</summary>
    public void Apply()
    {
        if (!_settings.OverlayEnabled || _settings.OverlayItems.Count == 0)
        {
            Stop();
            return;
        }
        if (_hud == null)
        {
            _hud = new OverlayHud(_settings.OverlayPosition, _settings.OverlaySize);
            _sampler = new MetricsSampler(_settings.OverlayItems, _tdpText, _amd);
            _sampler.Updated += _hud.Update;
            Log.Info($"Performance overlay on: {string.Join(", ", _settings.OverlayItems)}");
        }
        else
        {
            _hud.SetPosition(_settings.OverlayPosition, _settings.OverlaySize);
            _sampler!.SetItems(_settings.OverlayItems);
        }
    }

    void Stop()
    {
        if (_hud == null) return;
        _sampler?.Dispose();
        _hud.Dispose();
        _sampler = null;
        _hud = null;
        Log.Info("Performance overlay off; its sensors and PresentMon are stopped");
    }

    /// <summary>App shutdown: stop, and give the sensors a moment to close cleanly.</summary>
    public void Shutdown(TimeSpan timeout)
    {
        var sampler = _sampler;
        Stop();
        sampler?.WaitForStop(timeout);
    }
}
