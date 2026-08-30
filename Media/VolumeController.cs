using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Slate.Media;

/// <summary>
/// System output volume, and the volume of the app that is playing.
/// </summary>
/// <remarks>
/// This is the second place Windows is simply better than the Mac. macOS has no way to
/// set another application's output level — SoundSource and Background Music each ship a
/// virtual audio driver for it, and the macOS build of Slate settles for asking Spotify
/// and browser tabs to turn *themselves* down. Windows has a real per-process mixer in
/// the OS: every app that plays audio gets a session, and <c>ISimpleAudioVolume</c> sets
/// it. That works for Discord and Premiere too, not just the players Slate knows about.
/// </remarks>
public sealed class VolumeController : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioEndpointVolumeNotificationDelegate? _notification;

    private double _systemLevel = 0.5;
    private bool _systemMuted;
    private bool _isAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double SystemLevel
    {
        get => _systemLevel;
        private set { if (Math.Abs(_systemLevel - value) > 0.001) { _systemLevel = value; Raise(); } }
    }

    public bool SystemMuted
    {
        get => _systemMuted;
        private set { if (_systemMuted != value) { _systemMuted = value; Raise(); } }
    }

    /// <summary>False when the default output has no software volume — rare, but HDMI
    /// and some external DACs do it. The control hides itself rather than lying.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set { if (_isAvailable != value) { _isAvailable = value; Raise(); } }
    }

    public VolumeController()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Attach();
    }

    // MARK: - System output

    private void Attach()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            IsAvailable = true;

            // Keeps the slider honest when the volume keys are used, or the output
            // switches to a headset.
            _notification = data => _dispatcher.BeginInvoke(() =>
            {
                SystemLevel = data.MasterVolume;
                SystemMuted = data.Muted;
            });
            _device.AudioEndpointVolume.OnVolumeNotification += _notification;

            SystemLevel = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            SystemMuted = _device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Support.Diagnostics.Log($"volume: no endpoint ({ex.Message})");
        }
    }

    public void SetSystem(double level)
    {
        if (_device is null) return;
        var clamped = (float)Math.Clamp(level, 0, 1);
        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
            // Nudging a slider off zero should lift a mute, the way every other volume
            // control on the machine does.
            if (clamped > 0 && _device.AudioEndpointVolume.Mute)
                _device.AudioEndpointVolume.Mute = false;
            SystemLevel = clamped;
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"volume: system write failed ({ex.Message})");
        }
    }

    public void ToggleSystemMute()
    {
        if (_device is null) return;
        try
        {
            _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
            SystemMuted = _device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"volume: mute failed ({ex.Message})");
        }
    }

    // MARK: - Per-app

    /// <summary>Reads the mixer level for whichever process owns <paramref name="sourceId"/>,
    /// or null when that app has no audio session right now.</summary>
    public double? GetApp(string sourceId)
    {
        foreach (var session in SessionsFor(sourceId))
        {
            try { return session.SimpleAudioVolume.Volume; }
            catch { /* the session can vanish between enumerating and reading */ }
        }
        return null;
    }

    public void SetApp(string sourceId, double level)
    {
        var clamped = (float)Math.Clamp(level, 0, 1);
        foreach (var session in SessionsFor(sourceId))
        {
            try
            {
                session.SimpleAudioVolume.Volume = clamped;
                if (clamped > 0) session.SimpleAudioVolume.Mute = false;
            }
            catch (Exception ex)
            {
                Support.Diagnostics.Log($"volume: app write failed ({ex.Message})");
            }
        }
    }

    /// <summary>
    /// Every audio session belonging to the app behind a media session id.
    /// </summary>
    /// <remarks>
    /// Collected into a list rather than yielded: an iterator cannot `yield` from inside
    /// a `try`/`catch`, and every step here can throw as processes come and go.
    ///
    /// A browser gets one session per renderer that is making noise, so this deliberately
    /// returns all of them rather than the first — turning "Chrome" down should turn
    /// Chrome down, not one of its tabs.
    /// </remarks>
    private List<AudioSessionControl> SessionsFor(string sourceId)
    {
        var found = new List<AudioSessionControl>();
        if (_device is null || string.IsNullOrWhiteSpace(sourceId)) return found;

        var wanted = ProcessNamesFor(sourceId);
        if (wanted.Count == 0) return found;

        SessionCollection sessions;
        try
        {
            sessions = _device.AudioSessionManager.Sessions;
        }
        catch (Exception ex)
        {
            Support.Diagnostics.Log($"volume: session enumeration failed ({ex.Message})");
            return found;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                var pid = (int)session.GetProcessID;
                if (pid <= 0) continue;
                using var process = Process.GetProcessById(pid);
                if (wanted.Contains(process.ProcessName)) found.Add(session);
            }
            catch
            {
                // The process exited between enumerating and inspecting it.
            }
        }
        return found;
    }

    /// <summary>
    /// Media session ids arrive either as an executable ("Spotify.exe") or as a packaged
    /// app id ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"). Reduce both to
    /// the process names a mixer session would carry.
    /// </summary>
    private static HashSet<string> ProcessNamesFor(string sourceId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = sourceId;

        var bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);

        // A packaged id's last dotted component is usually the executable's name.
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1) set.Add(name[(dot + 1)..]);

        return set;
    }

    private void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        try
        {
            if (_device is not null && _notification is not null)
                _device.AudioEndpointVolume.OnVolumeNotification -= _notification;
            _device?.Dispose();
            _enumerator?.Dispose();
        }
        catch { /* teardown races with device removal; nothing useful to do */ }
    }
}
