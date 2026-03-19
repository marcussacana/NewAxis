using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using NewAxis.Services;
using Silk.NET.OpenGL;

namespace NewAxis.Controls;

public partial class VideoPlayerControl
{
    private static readonly HashSet<string> ImageSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle",
        "pgssub",
        "dvd_subtitle",
        "vobsub",
        "dvb_subtitle",
        "xsub"
    };

    private void EnsureMpvInitialized()
    {
        if (_mpvInitialized || _gl == null || _glInterface == null)
        {
            return;
        }

        if (!EnsureNativePlayerDependenciesReady())
        {
            return;
        }

        try
        {
            Log("EnsureMpvInitialized: creating MpvContext.");
            _mpv = new MpvContext();
            _mpv.Initialize("--vo=libmpv", "--hwdec=no", "--wid=0");
            _mpv.FileLoaded += OnMpvFileLoaded;
            Log("EnsureMpvInitialized: MpvContext initialized.");

            _getProcAddressCallback = (nint ctx, string name) => _glInterface.GetProcAddress(name);

            var openglInitParams = new LibMpv.mpv_opengl_init_params
            {
                get_proc_address = _getProcAddressCallback,
                get_proc_address_ctx = IntPtr.Zero,
                extra_exts = IntPtr.Zero
            };

            nint openglInitParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(openglInitParams));
            Marshal.StructureToPtr(openglInitParams, openglInitParamsPtr, fDeleteOld: false);
            nint apiTypePtr = Marshal.StringToHGlobalAnsi("opengl");

            var renderParams = new LibMpv.mpv_render_param[]
            {
                new()
                {
                    type = LibMpv.mpv_render_param_type.API_TYPE,
                    data = apiTypePtr
                },
                new()
                {
                    type = LibMpv.mpv_render_param_type.OPENGL_INIT_PARAMS,
                    data = openglInitParamsPtr
                },
                new()
                {
                    type = LibMpv.mpv_render_param_type.INVALID,
                    data = IntPtr.Zero
                }
            };

            int createResult = CreateMpvRenderContext(renderParams);
            Marshal.FreeHGlobal(apiTypePtr);
            Marshal.FreeHGlobal(openglInitParamsPtr);

            if (createResult < 0)
            {
                throw new Exception($"Failed to create MPV render context: {createResult}");
            }

            Log($"EnsureMpvInitialized: render context created (ptr=0x{((IntPtr)_renderContext).ToInt64():X}).");
            RegisterMpvCallbacks();
            _mpvInitialized = true;
            Log("EnsureMpvInitialized: success.");
            TryDispatchPendingLoad();
        }
        catch (Exception ex)
        {
            CleanupFailedMpvInitialization();
            Console.WriteLine("[VideoPlayerControl] MPV init failed: " + ex.Message);
            Trace.WriteLine($"[VideoPlayerControl] EnsureMpvInitialized failed: {ex}");
            _mpvInitialized = false;
        }
    }

    private int CreateMpvRenderContext(LibMpv.mpv_render_param[] renderParams)
    {
        int renderParamSize = Marshal.SizeOf<LibMpv.mpv_render_param>();
        nint renderParamsPtr = Marshal.AllocHGlobal(renderParamSize * renderParams.Length);

        try
        {
            for (int index = 0; index < renderParams.Length; index++)
            {
                Marshal.StructureToPtr(renderParams[index], IntPtr.Add(renderParamsPtr, index * renderParamSize), fDeleteOld: false);
            }

            return LibMpv.mpv_render_context_create(out _renderContext, _mpv!.Handle, renderParamsPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(renderParamsPtr);
        }
    }

    private void RegisterMpvCallbacks()
    {
        _updateCallback = delegate
        {
            Dispatcher.UIThread.Post(() =>
            {
                _lastMpvUpdateUtc = DateTime.UtcNow;
                _mpvFrameDirty = true;
                RequestNextFrameRendering();
            }, DispatcherPriority.Render);
        };

        LibMpv.mpv_render_context_set_update_callback(_renderContext, _updateCallback, IntPtr.Zero);

        DispatcherTimer.Run(() =>
        {
            try
            {
                _mpv?.PollEvents();
                if (_isPlaying && DateTime.UtcNow - _lastMpvUpdateUtc > TimeSpan.FromMilliseconds(120))
                {
                    _mpvFrameDirty = true;
                    RequestNextFrameRendering();
                }
            }
            catch
            {
            }

            return true;
        }, TimeSpan.FromMilliseconds(50));
    }

    private bool EnsureNativePlayerDependenciesReady()
    {
        string libMpvPath = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        if (File.Exists(libMpvPath))
        {
            return true;
        }

        if (!_dependencyCheckInProgress && DateTime.UtcNow - _lastDependencyRetryUtc > TimeSpan.FromSeconds(1))
        {
            _dependencyCheckInProgress = true;
            _lastDependencyRetryUtc = DateTime.UtcNow;

            Task.Run(() =>
            {
                PlayerDependencyService.DependencyCheckResult result = PlayerDependencyService.EnsureNativeDependencies();
                if (!result.Success)
                {
                    Log("EnsureMpvInitialized: native dependencies unavailable. " + result.Message);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _dependencyCheckInProgress = false;
                    RequestNextFrameRendering();
                });
            });
        }

        return false;
    }

    private void TryDispatchPendingLoad()
    {
        if (!_pendingLoadFile || _mpv == null || string.IsNullOrWhiteSpace(_currentFile))
        {
            return;
        }

        _mpv.LoadFile(_currentFile);
        _pendingLoadFile = false;
        Log("TryDispatchPendingLoad: mpv.LoadFile dispatched after delayed init.");
        DetectSbsLayout();
        UpdateMpvAspectForStereoMode();
        _isPlaying = true;
        MarkFrameDirtyAndRequest();
    }

    private void CleanupFailedMpvInitialization()
    {
        if (_renderContext != IntPtr.Zero)
        {
            LibMpv.mpv_render_context_free(_renderContext);
            _renderContext = IntPtr.Zero;
        }

        if (_mpv != null)
        {
            _mpv.FileLoaded -= OnMpvFileLoaded;
            _mpv.Dispose();
            _mpv = null;
        }
    }

    private void OnMpvFileLoaded()
    {
        Log("OnMpvFileLoaded event received.");
        Dispatcher.UIThread.Post(() =>
        {
            UpdateMpvSourceSize();
            DetectSbsLayout();
            UpdateMpvAspectForStereoMode();
            LogSelectedSubtitleState();
            FileLoaded?.Invoke();
            Log($"OnMpvFileLoaded UI: source={_sourceVideoWidth}x{_sourceVideoHeight} layout={_detectedSbsLayout}");
            RequestNextFrameRendering();
        });
    }

    private IReadOnlyList<MediaTrackInfo> GetMpvTracks(MediaTrackType trackType)
    {
        List<MediaTrackInfo> tracks = new();
        if (_mpv == null)
        {
            return tracks;
        }

        int trackCount = (int)_mpv.GetPropertyDouble("track-list/count");
        string expectedType = trackType == MediaTrackType.Audio ? "audio" : "sub";

        for (int i = 0; i < trackCount; i++)
        {
            string currentTrackType = _mpv.GetPropertyString($"track-list/{i}/type") ?? string.Empty;
            if (!string.Equals(currentTrackType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int trackId = (int)_mpv.GetPropertyDouble($"track-list/{i}/id");
            if (trackId < 0)
            {
                continue;
            }

            string language = _mpv.GetPropertyString($"track-list/{i}/lang") ?? "unk";
            string title = _mpv.GetPropertyString($"track-list/{i}/title") ?? string.Empty;
            bool isSelected = string.Equals(_mpv.GetPropertyString($"track-list/{i}/selected"), "yes", StringComparison.OrdinalIgnoreCase);
            bool isImageSubtitle = trackType == MediaTrackType.Subtitle && IsImageSubtitleTrack(i);
            var loc = LocalizationService.Instance;
            string trackTypeLabel = trackType == MediaTrackType.Audio ? loc["VideoTrackTypeAudio"] : loc["VideoTrackTypeSubtitle"];
            string displayTitle = string.IsNullOrWhiteSpace(title) ? $"{trackTypeLabel} {trackId}" : title;
            if (isImageSubtitle && !displayTitle.Contains("[Image]", StringComparison.OrdinalIgnoreCase))
            {
                displayTitle += " [Image]";
            }

            tracks.Add(new MediaTrackInfo
            {
                Id = trackId,
                Language = language,
                Title = displayTitle,
                IsSelected = isSelected
            });
        }

        return tracks;
    }

    private void UpdateMpvSourceSize()
    {
        if (_mpv == null)
        {
            return;
        }

        int sourceWidth = (int)Math.Round(_mpv.GetPropertyDouble("width"));
        int sourceHeight = (int)Math.Round(_mpv.GetPropertyDouble("height"));
        if (sourceWidth > 0 && sourceHeight > 0)
        {
            _sourceVideoWidth = sourceWidth;
            _sourceVideoHeight = sourceHeight;
        }
        _mpvTextSubtitleTrackSelected = HasSelectedTextSubtitleTrack();
        _mpvImageSubtitleTrackSelected = HasSelectedImageSubtitleTrack();
        if (_lastLoggedMpvTextSubtitleTrackSelected != _mpvTextSubtitleTrackSelected)
        {
            _lastLoggedMpvTextSubtitleTrackSelected = _mpvTextSubtitleTrackSelected;
            if (Program.LogEnabled)
            {
                Log($"MPV text subtitle track selected: {_mpvTextSubtitleTrackSelected}");
            }
        }

        LogSelectedSubtitleState();
    }

    private bool HasSelectedTextSubtitleTrack()
    {
        if (_mpv == null)
        {
            return false;
        }

        int trackCount = (int)_mpv.GetPropertyDouble("track-list/count");
        for (int i = 0; i < trackCount; i++)
        {
            string currentTrackType = _mpv.GetPropertyString($"track-list/{i}/type") ?? string.Empty;
            bool isSelected = string.Equals(_mpv.GetPropertyString($"track-list/{i}/selected"), "yes", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(currentTrackType, "sub", StringComparison.OrdinalIgnoreCase) || !isSelected)
            {
                continue;
            }

            return !IsImageSubtitleTrack(i);
        }

        return false;
    }

    private bool HasSelectedImageSubtitleTrack()
    {
        if (_mpv == null)
        {
            return false;
        }
        int trackCount = (int)_mpv.GetPropertyDouble("track-list/count");
        for (int i = 0; i < trackCount; i++)
        {
            string currentTrackType = _mpv.GetPropertyString($"track-list/{i}/type") ?? string.Empty;
            bool isSelected = string.Equals(_mpv.GetPropertyString($"track-list/{i}/selected"), "yes", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(currentTrackType, "sub", StringComparison.OrdinalIgnoreCase) || !isSelected)
            {
                continue;
            }
            return IsImageSubtitleTrack(i);
        }
        return false;
    }
    private bool IsImageSubtitleTrack(int trackIndex)
    {
        if (_mpv == null)
        {
            return false;
        }

        string image = _mpv.GetPropertyString($"track-list/{trackIndex}/image") ?? "no";
        if (string.Equals(image, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string codec = _mpv.GetPropertyString($"track-list/{trackIndex}/codec") ?? string.Empty;
        return ImageSubtitleCodecs.Contains(codec);
    }

    private void LogSelectedSubtitleState()
    {
        if (_mpv == null || !Program.LogEnabled)
        {
            return;
        }

        string signature = BuildSelectedSubtitleSignature();
        if (string.Equals(_lastLoggedSelectedSubtitleSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedSelectedSubtitleSignature = signature;
        Log(signature);
    }

    private string BuildSelectedSubtitleSignature()
    {
        if (_mpv == null)
        {
            return "Subtitle state: mpv unavailable";
        }

        int trackCount = (int)_mpv.GetPropertyDouble("track-list/count");
        List<string> subtitleEntries = new();
        int selectedSid = (int)_mpv.GetPropertyDouble("sid");

        for (int i = 0; i < trackCount; i++)
        {
            string type = _mpv.GetPropertyString($"track-list/{i}/type") ?? string.Empty;
            if (!string.Equals(type, "sub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int trackId = (int)_mpv.GetPropertyDouble($"track-list/{i}/id");
            string selected = _mpv.GetPropertyString($"track-list/{i}/selected") ?? "no";
            string image = _mpv.GetPropertyString($"track-list/{i}/image") ?? "no";
            string codec = _mpv.GetPropertyString($"track-list/{i}/codec") ?? "<null>";
            string demuxW = _mpv.GetPropertyString($"track-list/{i}/demux-w") ?? "<null>";
            string demuxH = _mpv.GetPropertyString($"track-list/{i}/demux-h") ?? "<null>";
            string lang = _mpv.GetPropertyString($"track-list/{i}/lang") ?? "unk";
            string title = _mpv.GetPropertyString($"track-list/{i}/title") ?? string.Empty;
            string external = _mpv.GetPropertyString($"track-list/{i}/external") ?? "no";
            string defaultTrack = _mpv.GetPropertyString($"track-list/{i}/default") ?? "no";
            string forced = _mpv.GetPropertyString($"track-list/{i}/forced") ?? "no";
            string inferredKind = IsImageSubtitleTrack(i) ? "image" : "text";
            string mainSelection = trackId == selectedSid ? "*" : "";
            subtitleEntries.Add($"id={trackId}{mainSelection} selected={selected} image={image} inferred={inferredKind} codec={codec} demux={demuxW}x{demuxH} lang={lang} external={external} default={defaultTrack} forced={forced} title={title}");
        }

        string subVisibility = _mpv.GetPropertyString("sub-visibility") ?? "<null>";
        string subScale = _mpv.GetPropertyString("sub-scale") ?? "<null>";
        string margins = _mpv.GetPropertyString("sub-use-margins") ?? "<null>";
        string forceMargins = _mpv.GetPropertyString("sub-ass-force-margins") ?? "<null>";
        string assVideoData = _mpv.GetPropertyString("sub-ass-use-video-data") ?? "<null>";
        string blendSubs = _mpv.GetPropertyString("blend-subtitles") ?? "<null>";

        return "Subtitle state: " +
               $"file={_currentFile ?? "<null>"} sid={selectedSid} visibility={subVisibility} textTrack={_mpvTextSubtitleTrackSelected} " +
               $"sub-scale={subScale} sub-use-margins={margins} sub-ass-force-margins={forceMargins} sub-ass-use-video-data={assVideoData} blend-subtitles={blendSubs} " +
               $"tracks=[{string.Join(" | ", subtitleEntries)}]";
    }

    private unsafe void RenderMpvToTexture(int width, int height, uint targetTexture, MpvRenderPass renderPass)
    {
        if (_gl == null || _renderContext == IntPtr.Zero || _mpv == null)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _videoFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, targetTexture, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        LibMpv.mpv_opengl_fbo renderTargetFbo = new()
        {
            fbo = (int)_videoFbo,
            w = width,
            h = height,
            internal_format = 32856
        };

        LibMpv.mpv_render_param* renderParams = stackalloc LibMpv.mpv_render_param[2];
        renderParams[0] = new LibMpv.mpv_render_param
        {
            type = LibMpv.mpv_render_param_type.FORWARD_TARGET,
            data = (IntPtr)(&renderTargetFbo)
        };
        renderParams[1] = new LibMpv.mpv_render_param
        {
            type = LibMpv.mpv_render_param_type.INVALID,
            data = IntPtr.Zero
        };

        if (ShouldLogFrequentRender())
        {
            Log($"mpv render begin: tex={targetTexture} size={width}x{height} pass={renderPass}");
        }

        switch (renderPass)
        {
            case MpvRenderPass.VideoOnly:
                LibMpv.mpv_render_context_render_video_only(_renderContext, (IntPtr)renderParams);
                break;
            case MpvRenderPass.SubtitlesOnly:
                LibMpv.mpv_render_context_render_subtitles(_renderContext, (IntPtr)renderParams);
                break;
            default:
                LibMpv.mpv_render_context_render(_renderContext, (IntPtr)renderParams);
                break;
        }

        if (ShouldLogFrequentRender())
        {
            Log("mpv render end");
        }

        _lastMpvUpdateUtc = DateTime.UtcNow;
    }
}


