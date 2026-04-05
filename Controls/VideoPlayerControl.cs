using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using NewAxis.Services;
using Silk.NET.OpenGL;

namespace NewAxis.Controls;

public enum PlaybackBackend
{
    DirectShow,
    Mpv
}

public partial class VideoPlayerControl : OpenGlControlBase
{
    private enum SbsLayout
    {
        Unknown,
        Half,
        Full
    }

    private GL? _gl;
    private GlInterface? _glInterface;
    private uint _program;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private uint _videoTexture;
    private uint _subtitleTexture;
    private uint _compositeTexture;
    private uint _videoFbo;
    private uint _compositeFbo;
    private uint _weaveTexture;
    private uint _weaveFbo;
    private int _videoTextureW;
    private int _videoTextureH;
    private int _subtitleTextureW;
    private int _subtitleTextureH;
    private int _compositeTextureW;
    private int _compositeTextureH;
    private int _weaveTextureW;
    private int _weaveTextureH;
    private int _uVideoTextureLocation = -1;
    private int _uSubtitleTextureLocation = -1;
    private int _uHasSubtitleTextureLocation = -1;
    private int _uFlipYLocation = -1;
    private int _uSbs3DEnabledLocation = -1;
    private int _uSubtitleModeLocation = -1;
    private MpvContext? _mpv;
    private nint _renderContext;
    private LibMpv.mpv_render_update_fn? _updateCallback;
    private LibMpv.get_proc_address_fn? _getProcAddressCallback;
    private bool _mpvInitialized;
    private PlaybackBackend _backend = PlaybackBackend.Mpv;
    private DateTime _lastDependencyRetryUtc = DateTime.MinValue;
    private bool _pendingLoadFile;
    private SRWeaverClient? _srWeaver;
    private DateTime _lastWeaverAttemptUtc = DateTime.MinValue;
    private DateTime _lastContextRecoveryAttemptUtc = DateTime.MinValue;
    private bool _pendingWeaverInit;
    private bool _windowEligibleFor3D;
    private bool _lensHintRequested;
    private Window? _hostWindow;
    private bool _isPlaying;
    private string? _currentFile;
    private bool _sbs3DEnabled;
    private SbsLayout _detectedSbsLayout = SbsLayout.Unknown;
    private int _sourceVideoWidth;
    private int _sourceVideoHeight;
    private bool _mpvTextSubtitleTrackSelected;
    private bool _mpvImageSubtitleTrackSelected;
    private bool? _lastLoggedMpvTextSubtitleTrackSelected;
    private int _renderCallCount;
    private bool? _mpvKeepAspectDisabled;
    private bool? _mpvFullSbsAspectOverrideEnabled;
    private bool? _lastLoggedFullSbs;
    private float? _lastLoggedAspectZoomY;
    private bool _mpvFrameDirty = true;
    private DateTime _lastMpvUpdateUtc = DateTime.MinValue;
    private bool _dependencyCheckInProgress;
    private bool? _subtitleBandLayoutEnabled;
    private int _subtitleBandBottomMargin;
    private int _glMaxTextureSize;
    private int _lastLoggedSubtitleBandHeight;
    private bool _subtitleBandDebugDumpDone;
    private bool _subtitleBandDebugDumpRequested;
    private string? _lastLoggedSelectedSubtitleSignature;

    public bool IsPlaying => _isPlaying;

    public string? CurrentFile => _currentFile;

    public bool IsSbs3DEnabled => _sbs3DEnabled;

    public bool IsMpvBackendActive => _backend == PlaybackBackend.Mpv;

    public event Action? FileLoaded;

    private static void Log(string message)
    {
        Trace.WriteLine("NewAxis", "[VideoPlayerControl] " + message);
    }

    public void SetSbs3DEnabled(bool enabled)
    {
        Log($"SetSbs3DEnabled({enabled}) current={_sbs3DEnabled}");

        bool stateChanged = _sbs3DEnabled != enabled;
        _sbs3DEnabled = enabled;

        if (!enabled)
        {
            if (stateChanged)
            {
                _detectedSbsLayout = SbsLayout.Unknown;
                _pendingWeaverInit = false;
            }

            ApplyLensHint(enabled: false);
            DisableSrWeaver();
        }
        else
        {
            DetectSbsLayout();
            _pendingWeaverInit = true;
        }

        UpdateMpvAspectForStereoMode();
        MarkFrameDirtyAndRequest();
    }

    public void SetPlaybackBackend(PlaybackBackend backend)
    {
        _backend = backend;
        EnsureMpvInitialized();
        MarkFrameDirtyAndRequest();
    }

    public void LoadFile(string filePath)
    {
        Log("LoadFile called: " + filePath);
        ResetPlaybackStateForFile(filePath);

        EnsureMpvInitialized();
        if (_mpv == null)
        {
            Log("LoadFile aborted: _mpv is null after EnsureMpvInitialized.");
            MarkFrameDirtyAndRequest();
            return;
        }

        _mpv.LoadFile(filePath);
        _pendingLoadFile = false;
        Log("mpv.LoadFile dispatched.");
        DetectSbsLayout();
        UpdateMpvAspectForStereoMode();
        _isPlaying = true;
        MarkFrameDirtyAndRequest();
    }

    public void Play()
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("pause", "no");
            _isPlaying = true;
            MarkFrameDirtyAndRequest();
        }
    }

    public void Pause()
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("pause", "yes");
            _isPlaying = false;
        }
    }

    public void Stop()
    {
        if (_mpv != null)
        {
            try
            {
                _mpv.Command("stop");
            }
            catch
            {
            }

            _isPlaying = false;
        }
    }

    public void Seek(double seconds)
    {
        if (_mpv != null)
        {
            _mpv.Command("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute");
            MarkFrameDirtyAndRequest();
        }
    }

    public double GetPosition()
    {
        return _mpv?.GetPropertyDouble("time-pos") ?? 0.0;
    }

    public double GetDuration()
    {
        return _mpv?.GetPropertyDouble("duration") ?? 0.0;
    }

    public double GetSpeed()
    {
        return _mpv?.GetPropertyDouble("speed") ?? 1.0;
    }

    public void SetSpeed(double speed)
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("speed", speed.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    public void SetLoop(bool enabled)
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("loop-file", enabled ? "inf" : "no");
        }
    }

    public void RequestSubtitleDebugDump()
    {
        _subtitleBandDebugDumpDone = false;
        _subtitleBandDebugDumpRequested = true;
        MarkFrameDirtyAndRequest();
    }

    public void SetVolume(double volume)
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("volume", volume.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    public double GetVolume()
    {
        if (_mpv == null)
        {
            return 100;
        }

        try
        {
            return _mpv.GetPropertyDouble("volume");
        }
        catch
        {
            return 100;
        }
    }

    public string GetDetectedSbsLayoutLabel()
    {
        var loc = LocalizationService.Instance;
        return _detectedSbsLayout switch
        {
            SbsLayout.Full => loc["VideoStereoFullSbs"],
            SbsLayout.Half => loc["VideoStereoHalfSbs"],
            _ => loc["VideoStereoAuto"],
        };
    }

    public IReadOnlyList<MediaTrackInfo> GetTracks(MediaTrackType trackType)
    {
        return GetMpvTracks(trackType);
    }

    public bool SelectTrack(MediaTrackType trackType, int trackId)
    {
        if (_mpv == null)
        {
            return false;
        }

        try
        {
            string name = trackType == MediaTrackType.Audio ? "aid" : "sid";
            string value = trackType == MediaTrackType.Subtitle && trackId <= 0 ? "no" : trackId.ToString();
            _mpv.SetProperty(name, value);
            if (trackType == MediaTrackType.Subtitle)
            {
                UpdateMpvSourceSize();
                MarkFrameDirtyAndRequest();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ResetPlaybackStateForFile(string filePath)
    {
        _currentFile = filePath;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        _detectedSbsLayout = SbsLayout.Unknown;
        _mpvTextSubtitleTrackSelected = false;
        _mpvImageSubtitleTrackSelected = false;
        _lastLoggedMpvTextSubtitleTrackSelected = null;
        _subtitleBandDebugDumpDone = false;
        _lastLoggedSelectedSubtitleSignature = null;
        _mpvFrameDirty = true;
        _pendingLoadFile = true;
    }

    private void MarkFrameDirtyAndRequest()
    {
        _mpvFrameDirty = true;
        RequestNextFrameRendering();
    }
}
