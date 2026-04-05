using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using NewAxis.Services;

namespace NewAxis.Controls;

public partial class VideoPlayerControl
{
    private void AttachWindowHooks(Window? window)
    {
        if (_hostWindow == window)
        {
            return;
        }

        DetachWindowHooks();
        _hostWindow = window;
        if (_hostWindow == null)
        {
            _windowEligibleFor3D = false;
            return;
        }

        _hostWindow.Activated += OnHostWindowActivationChanged;
        _hostWindow.Deactivated += OnHostWindowActivationChanged;
        _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        UpdateWindowEligibility();
    }

    private void DetachWindowHooks()
    {
        if (_hostWindow == null)
        {
            return;
        }

        _hostWindow.Activated -= OnHostWindowActivationChanged;
        _hostWindow.Deactivated -= OnHostWindowActivationChanged;
        _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
        _hostWindow = null;
        _windowEligibleFor3D = false;
    }

    private void OnHostWindowActivationChanged(object? sender, EventArgs e)
    {
        UpdateWindowEligibility();
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateWindowEligibility();
        }
    }

    private void UpdateWindowEligibility()
    {
        bool isEligibleFor3D = _hostWindow != null && _hostWindow.IsActive && _hostWindow.WindowState == WindowState.FullScreen;
        if (_windowEligibleFor3D == isEligibleFor3D)
        {
            return;
        }

        _windowEligibleFor3D = isEligibleFor3D;
        if (!isEligibleFor3D)
        {
            ApplyLensHint(enabled: false);
        }

        RequestNextFrameRendering();
    }

    private void ApplyLensHint(bool enabled)
    {
        SRWeaverClient? srWeaver = _srWeaver;
        if (srWeaver == null || !srWeaver.IsInitialized)
        {
            _lensHintRequested = false;
            return;
        }

        if (_lensHintRequested != enabled)
        {
            srWeaver.SetLensHintEnabled(enabled);
            _lensHintRequested = enabled;
        }
    }

    private void TryInitializeSrWeaver(bool force)
    {
        if (!_sbs3DEnabled || _gl == null || _renderContext == IntPtr.Zero)
        {
            return;
        }

        SRWeaverClient? srWeaver = _srWeaver;
        if ((srWeaver != null && srWeaver.IsInitialized) || (!force && DateTime.UtcNow - _lastWeaverAttemptUtc < TimeSpan.FromSeconds(2)))
        {
            return;
        }

        _lastWeaverAttemptUtc = DateTime.UtcNow;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        AttachWindowHooks(topLevel as Window);
        nint windowHandle = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        _srWeaver?.Dispose();
        _srWeaver = new SRWeaverClient();
        if (!_srWeaver.Initialize(3840u, 2160u, windowHandle))
        {
            _srWeaver.Dispose();
            _srWeaver = null;
            ApplyLensHint(enabled: false);
        }
    }

    private void DisableSrWeaver()
    {
        if (_srWeaver == null)
        {
            return;
        }

        try
        {
            _srWeaver.SetLensHintEnabled(enable: false);
            _srWeaver.SetTrackingEnabledStatus(enable: false);
        }
        catch
        {
        }

        _srWeaver.Dispose();
        _srWeaver = null;
        _lastWeaverAttemptUtc = DateTime.MinValue;
        _lastContextRecoveryAttemptUtc = DateTime.MinValue;
        _lensHintRequested = false;
    }

    private void DetectSbsLayout()
    {
        if (_sbs3DEnabled)
        {
            _detectedSbsLayout = IsCurrentSourceLikelyFullSbs() ? SbsLayout.Full : SbsLayout.Half;
        }
    }

    private void UpdateMpvAspectForStereoMode()
    {
        if (_mpv == null)
        {
            return;
        }

        bool isFullSbs = _sbs3DEnabled && IsCurrentSourceLikelyFullSbs();
        bool aspectOverrideChanged = _mpvFullSbsAspectOverrideEnabled != isFullSbs;
        if (_mpvKeepAspectDisabled != false)
        {
            _mpvKeepAspectDisabled = false;
            try
            {
                _mpv.SetProperty("keepaspect", "yes");
                Log($"MPV keepaspect=yes (sbs={_sbs3DEnabled}, layout={_detectedSbsLayout})");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VideoPlayerControl] Failed to set MPV keepaspect: {ex}");
            }
        }

        if (_mpvFullSbsAspectOverrideEnabled == isFullSbs)
        {
            return;
        }

        _mpvFullSbsAspectOverrideEnabled = isFullSbs;
        if (aspectOverrideChanged)
        {
            MarkSubtitleTextureDirty();
        }
        try
        {
            _mpv.SetProperty("video-aspect-override", isFullSbs ? "1.7777778" : "no");
            Log($"MPV video-aspect-override={(isFullSbs ? "1.7777778" : "no")} (sbs={_sbs3DEnabled}, layout={_detectedSbsLayout})");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoPlayerControl] Failed to set MPV video-aspect-override: {ex}");
        }
    }

    private float ComputeStereoAspectZoomY(int width, int height, bool isFullSbs)
    {
        return 1f;
    }

    private bool IsCurrentSourceLikelyFullSbs()
    {
        return DetectSbsLayoutFromName(_currentFile) switch
        {
            SbsLayout.Full => true,
            SbsLayout.Half => false,
            _ => IsSourceDimensionLikelyFullSbs()
        };
    }

    private bool IsSourceDimensionLikelyFullSbs()
    {
        if (_sourceVideoWidth <= 0 || _sourceVideoHeight <= 0)
        {
            return false;
        }

        double aspectRatio = (double)_sourceVideoWidth / _sourceVideoHeight;
        return _sourceVideoWidth >= 3000 || aspectRatio >= 2.4;
    }

    private void LogStereoRenderState(bool isFullSbs, float aspectZoomY, int width, int height)
    {
        if (!_sbs3DEnabled || !Program.LogEnabled)
        {
            _lastLoggedFullSbs = null;
            _lastLoggedAspectZoomY = null;
            return;
        }

        bool stateChanged = _lastLoggedFullSbs != isFullSbs
            || !_lastLoggedAspectZoomY.HasValue
            || Math.Abs(_lastLoggedAspectZoomY.Value - aspectZoomY) > 0.01f;
        bool isPeriodicLog = _renderCallCount <= 8 || _renderCallCount % 240 == 0;
        if (stateChanged || isPeriodicLog)
        {
            _lastLoggedFullSbs = isFullSbs;
            _lastLoggedAspectZoomY = aspectZoomY;
            Log($"Stereo state: fullSbs={isFullSbs} zoomY={aspectZoomY:0.###} source={_sourceVideoWidth}x{_sourceVideoHeight} target={width}x{height}");
        }
    }

    private static SbsLayout DetectSbsLayoutFromName(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return SbsLayout.Unknown;
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        if (fileName.Contains("full-sbs") || fileName.Contains("fullsbs") || fileName.Contains("fsbs") || fileName.Contains("sbs-full") || fileName.Contains("sbsfull"))
        {
            return SbsLayout.Full;
        }

        if (fileName.Contains("half-sbs") || fileName.Contains("halfsbs") || fileName.Contains("hsbs"))
        {
            return SbsLayout.Half;
        }

        return SbsLayout.Unknown;
    }
}


