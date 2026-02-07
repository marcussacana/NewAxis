#define TRACE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using NewAxis.Services;
using Silk.NET.OpenGL;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace NewAxis.Controls;

public enum PlaybackBackend
{
    DirectShow,
    Mpv
}

public class VideoPlayerControl : OpenGlControlBase
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

    private bool _mpvHasSubtitleText;

    private bool? _lastLoggedMpvHasSubtitleText;

    private int _renderCallCount;

    private bool? _mpvKeepAspectDisabled;

    private bool? _mpvFullSbsAspectOverrideEnabled;

    private bool? _lastLoggedFullSbs;

    private float? _lastLoggedAspectZoomY;

    private bool _mpvFrameDirty = true;

    private bool? _currentSubVisibility;

    private DateTime _lastMpvUpdateUtc = DateTime.MinValue;

    private bool _dependencyCheckInProgress;

    public bool IsPlaying => _isPlaying;

    public string? CurrentFile => _currentFile;

    public bool IsSbs3DEnabled => _sbs3DEnabled;

    public bool IsMpvBackendActive => _backend == PlaybackBackend.Mpv;

    public event Action? FileLoaded;

    private static void Log(string message)
    {
        Trace.Write("NewAxis", "[VideoPlayerControl] " + message);
    }

    public void SetSbs3DEnabled(bool enabled)
    {
        Log($"SetSbs3DEnabled({enabled}) current={_sbs3DEnabled}");
        if (_sbs3DEnabled == enabled)
        {
            if (!enabled)
            {
                ApplyLensHint(enabled: false);
                DisableSrWeaver();
            }
            if (enabled)
            {
                DetectSbsLayout();
                _pendingWeaverInit = true;
            }
            UpdateMpvAspectForStereoMode();
            MarkFrameDirtyAndRequest();
        }
        else
        {
            _sbs3DEnabled = enabled;
            if (!enabled)
            {
                _detectedSbsLayout = SbsLayout.Unknown;
                _pendingWeaverInit = false;
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
        _currentFile = filePath;
        _sourceVideoWidth = 0;
        _sourceVideoHeight = 0;
        _detectedSbsLayout = SbsLayout.Unknown;
        _mpvHasSubtitleText = false;
        _lastLoggedMpvHasSubtitleText = null;
        _currentSubVisibility = null;
        _mpvFrameDirty = true;
        _pendingLoadFile = true;
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

    public void SetVolume(double volume)
    {
        if (_mpv != null)
        {
            _mpv.SetProperty("volume", volume.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    public double GetVolume()
    {
        if (_mpv == null) return 100;
        try
        {
            return _mpv.GetPropertyDouble("volume");
        }
        catch { }
        return 100;
    }

    public string GetDetectedSbsLayoutLabel()
    {
        SbsLayout detectedSbsLayout = _detectedSbsLayout;
        if (1 == 0)
        {
        }
        string result = detectedSbsLayout switch
        {
            SbsLayout.Full => "Full SBS",
            SbsLayout.Half => "Half SBS",
            _ => "Auto",
        };
        if (1 == 0)
        {
        }
        return result;
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
            string name = ((trackType == MediaTrackType.Audio) ? "aid" : "sid");
            string value = ((trackType == MediaTrackType.Subtitle && trackId <= 0) ? "no" : trackId.ToString());
            _mpv.SetProperty(name, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected unsafe override void OnOpenGlInit(GlInterface gli)
    {
        _glInterface = gli;
        _gl = GL.GetApi(gli.GetProcAddress);
        if (_gl == null)
        {
            Log("OnOpenGlInit: GL.GetApi returned null.");
            return;
        }
        Log("OnOpenGlInit: GL version=" + _gl.GetStringS(StringName.Version) + " renderer=" + _gl.GetStringS(StringName.Renderer));
        string text = "#version 330 core\nlayout (location = 0) in vec3 aPosition;\nlayout (location = 1) in vec2 aTexCoord;\n\nout vec2 vTexCoord;\n\nvoid main()\n{\n    gl_Position = vec4(aPosition, 1.0);\n    vTexCoord = aTexCoord;\n}";
        string text2 = "#version 330 core\n\nin vec2 vTexCoord;\nout vec4 FragColor;\n\nuniform sampler2D uTexture;\nuniform sampler2D uSubTexture;\nuniform int uUseSubTexture;\nuniform int uFlipY;\nuniform int uSbs3DEnabled;\nuniform int uSbsLayout; // 0 = Half SBS, 1 = Full SBS\nuniform int uHasSubText;\nuniform float uAspectZoomY;\nuniform int uStereoToMono;\n\nconst float kSubPopoutPx = 25.0;\n\nfloat luma(vec3 color)\n{\n    return dot(color, vec3(0.299, 0.587, 0.114));\n}\n\nvec4 sampleSubtitle(vec2 uv)\n{\n    return uUseSubTexture == 1 ? texture(uSubTexture, uv) : texture(uTexture, uv);\n}\n\nvec4 sampleSubtitleAA(vec2 uv, vec2 texel)\n{\n    vec2 t = texel * 0.5;\n    vec4 c = sampleSubtitle(uv) * 0.40;\n    c += sampleSubtitle(uv + vec2( t.x, 0.0)) * 0.15;\n    c += sampleSubtitle(uv + vec2(-t.x, 0.0)) * 0.15;\n    c += sampleSubtitle(uv + vec2(0.0,  t.y)) * 0.15;\n    c += sampleSubtitle(uv + vec2(0.0, -t.y)) * 0.15;\n    return c;\n}\n\nfloat subtitleMask(vec3 sampleColor, float baseLum)\n{\n    float lum = luma(sampleColor);\n    float maxc = max(sampleColor.r, max(sampleColor.g, sampleColor.b));\n    float minc = min(sampleColor.r, min(sampleColor.g, sampleColor.b));\n    float chroma = maxc - minc;\n    float brightText = smoothstep(0.62, 0.86, lum) * (1.0 - smoothstep(0.20, 0.45, chroma));\n    float contrast = smoothstep(0.08, 0.30, lum - baseLum);\n    return brightText * contrast;\n}\n\nvoid main()\n{\n    vec2 uv = vTexCoord;\n\n    if (uFlipY == 1)\n    {\n        uv.y = 1.0 - uv.y;\n    }\n\n    if (uAspectZoomY > 1.001)\n    {\n        uv.y = ((uv.y - 0.5) / uAspectZoomY) + 0.5;\n        uv.y = clamp(uv.y, 0.0, 1.0);\n    }\n\n    if (uStereoToMono == 1)\n    {\n        uv.x *= 0.5;\n    }\n\n    vec4 color = texture(uTexture, uv);\n\n    if (uSbs3DEnabled == 1 && uHasSubText == 1 && uv.y > 0.76)\n    {\n        float eyeLocalX = uv.x < 0.5 ? uv.x * 2.0 : (uv.x - 0.5) * 2.0;\n        float srcX = eyeLocalX;\n\n        if (uSbsLayout == 1)\n        {\n            srcX = 0.5 + (eyeLocalX - 0.5) * 0.5;\n        }\n\n        vec2 texel = 1.0 / vec2(textureSize(uTexture, 0));\n        float popout = texel.x * kSubPopoutPx;\n        srcX += (uv.x < 0.5 ? popout : -popout);\n\n        vec2 subUv = vec2(clamp(srcX, 0.0, 1.0), uv.y);\n        vec4 subSample = sampleSubtitleAA(subUv, texel);\n        float baseLum = luma(color.rgb);\n        float fill = subtitleMask(subSample.rgb, baseLum);\n\n        vec2 stepUvNear = texel * 2.6;\n        vec2 stepUvFar = texel * 4.2;\n\n        float ringNear = 0.0;\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x, 0.0), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x, 0.0), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0,  stepUvNear.y), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0, -stepUvNear.y), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x,  stepUvNear.y), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x,  stepUvNear.y), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvNear.x, -stepUvNear.y), texel).rgb, baseLum));\n        ringNear = max(ringNear, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvNear.x, -stepUvNear.y), texel).rgb, baseLum));\n\n        float ringFar = 0.0;\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x, 0.0), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x, 0.0), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0,  stepUvFar.y), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(0.0, -stepUvFar.y), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x,  stepUvFar.y), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x,  stepUvFar.y), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2( stepUvFar.x, -stepUvFar.y), texel).rgb, baseLum));\n        ringFar = max(ringFar, subtitleMask(sampleSubtitleAA(subUv + vec2(-stepUvFar.x, -stepUvFar.y), texel).rgb, baseLum));\n\n        float ring = max(ringNear, ringFar * 1.05);\n\n        float lowerBand = smoothstep(0.76, 0.90, uv.y);\n        float aa = max(fwidth(fill) * 2.0, 0.03);\n        float fillMask = smoothstep(0.20 - aa, 0.46 + aa, fill) * lowerBand;\n        float outlineMask = smoothstep(0.03, 0.22, ring) * lowerBand * (1.0 - fillMask);\n\n        color.rgb = mix(color.rgb, vec3(0.0), outlineMask * 1.0);\n        color.rgb = mix(color.rgb, subSample.rgb, fillMask);\n    }\n\n    FragColor = vec4(color.rgb, 1.0);\n}";
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2))
        {
            throw new Exception("Failed to load video player shaders.");
        }
        _program = CreateShaderProgram(text, text2);
        float[] array = new float[20]
        {
            1f, -1f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f,
            -1f, 1f, 0f, 0f, 1f, -1f, -1f, 0f, 0f, 0f
        };
        ushort[] array2 = new ushort[6] { 0, 1, 2, 2, 3, 0 };
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        _videoTexture = _gl.GenTexture();
        _subtitleTexture = _gl.GenTexture();
        _compositeTexture = _gl.GenTexture();
        _videoFbo = _gl.GenFramebuffer();
        _compositeFbo = _gl.GenFramebuffer();
        _weaveTexture = _gl.GenTexture();
        _weaveFbo = _gl.GenFramebuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* data = array)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(array.Length * 4), data, BufferUsageARB.StaticDraw);
        }
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* data2 = array2)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(array2.Length * 2), data2, BufferUsageARB.StaticDraw);
        }
        _gl.VertexAttribPointer(0u, 3, VertexAttribPointerType.Float, normalized: false, 20u, null);
        _gl.EnableVertexAttribArray(0u);
        _gl.VertexAttribPointer(1u, 2, VertexAttribPointerType.Float, normalized: false, 20u, (void*)12);
        _gl.EnableVertexAttribArray(1u);
        _gl.BindVertexArray(0u);
        _gl.BindTexture(TextureTarget.Texture2D, _videoTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
        _gl.BindTexture(TextureTarget.Texture2D, _subtitleTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
        _gl.BindTexture(TextureTarget.Texture2D, _compositeTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
        _gl.BindTexture(TextureTarget.Texture2D, _weaveTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
        AttachWindowHooks(TopLevel.GetTopLevel(this) as Window);
        EnsureMpvInitialized();
    }

    private void AttachWindowHooks(Window? window)
    {
        if (_hostWindow != window)
        {
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
    }

    private void DetachWindowHooks()
    {
        if (_hostWindow != null)
        {
            _hostWindow.Activated -= OnHostWindowActivationChanged;
            _hostWindow.Deactivated -= OnHostWindowActivationChanged;
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
            _windowEligibleFor3D = false;
        }
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
        bool flag = _hostWindow != null && _hostWindow.IsActive && _hostWindow.WindowState == WindowState.FullScreen;
        if (_windowEligibleFor3D != flag)
        {
            _windowEligibleFor3D = flag;
            if (!flag)
            {
                ApplyLensHint(enabled: false);
            }
            RequestNextFrameRendering();
        }
    }

    private void ApplyLensHint(bool enabled)
    {
        SRWeaverClient? srWeaver = _srWeaver;
        if (srWeaver == null || !srWeaver.IsInitialized)
        {
            _lensHintRequested = false;
        }
        else if (_lensHintRequested != enabled)
        {
            _srWeaver.SetLensHintEnabled(enabled);
            _lensHintRequested = enabled;
        }
    }

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
            LibMpv.mpv_opengl_init_params structure = new LibMpv.mpv_opengl_init_params
            {
                get_proc_address = _getProcAddressCallback,
                get_proc_address_ctx = IntPtr.Zero,
                extra_exts = IntPtr.Zero
            };
            nint num = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
            Marshal.StructureToPtr(structure, num, fDeleteOld: false);
            nint num2 = Marshal.StringToHGlobalAnsi("opengl");
            LibMpv.mpv_render_param[] array = new LibMpv.mpv_render_param[3]
            {
                new LibMpv.mpv_render_param
                {
                    type = LibMpv.mpv_render_param_type.API_TYPE,
                    data = num2
                },
                new LibMpv.mpv_render_param
                {
                    type = LibMpv.mpv_render_param_type.OPENGL_INIT_PARAMS,
                    data = num
                },
                new LibMpv.mpv_render_param
                {
                    type = LibMpv.mpv_render_param_type.INVALID,
                    data = IntPtr.Zero
                }
            };
            int num3 = Marshal.SizeOf<LibMpv.mpv_render_param>();
            nint num4 = Marshal.AllocHGlobal(num3 * array.Length);
            for (int num5 = 0; num5 < array.Length; num5++)
            {
                Marshal.StructureToPtr(array[num5], IntPtr.Add(num4, num5 * num3), fDeleteOld: false);
            }
            int num6 = LibMpv.mpv_render_context_create(out _renderContext, _mpv.Handle, num4);
            Marshal.FreeHGlobal(num4);
            Marshal.FreeHGlobal(num2);
            Marshal.FreeHGlobal(num);
            if (num6 < 0)
            {
                throw new Exception($"Failed to create MPV render context: {num6}");
            }
            Log($"EnsureMpvInitialized: render context created (ptr=0x{((IntPtr)_renderContext).ToInt64():X}).");
            _updateCallback = delegate
            {
                Dispatcher.UIThread.Post(delegate
                {
                    _lastMpvUpdateUtc = DateTime.UtcNow;
                    _mpvFrameDirty = true;
                    RequestNextFrameRendering();
                }, DispatcherPriority.Render);
            };
            LibMpv.mpv_render_context_set_update_callback(_renderContext, _updateCallback, IntPtr.Zero);
            DispatcherTimer.Run(delegate
            {
                try
                {
                    _mpv?.PollEvents();
                    if (_isPlaying && DateTime.UtcNow - _lastMpvUpdateUtc > TimeSpan.FromMilliseconds(120L, 0L))
                    {
                        _mpvFrameDirty = true;
                        RequestNextFrameRendering();
                    }
                }
                catch
                {
                }
                return true;
            }, TimeSpan.FromMilliseconds(33L, 0L));
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

    private bool EnsureNativePlayerDependenciesReady()
    {
        string libMpvPath = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        if (File.Exists(libMpvPath))
        {
            return true;
        }

        if (!_dependencyCheckInProgress && DateTime.UtcNow - _lastDependencyRetryUtc > TimeSpan.FromSeconds(1.0))
        {
            _lastDependencyRetryUtc = DateTime.UtcNow;
            _dependencyCheckInProgress = true;

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
                    _mpvFrameDirty = true;
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
        _updateCallback = null;
        _getProcAddressCallback = null;
        if (_mpv != null)
        {
            try
            {
                _mpv.FileLoaded -= OnMpvFileLoaded;
                _mpv.Dispose();
            }
            catch
            {
            }
            _mpv = null;
        }
    }

    private void OnMpvFileLoaded()
    {
        Log("OnMpvFileLoaded event received.");
        Dispatcher.UIThread.Post(delegate
        {
            UpdateMpvSourceSize();
            DetectSbsLayout();
            _isPlaying = true;
            _mpvFrameDirty = true;
            this.FileLoaded?.Invoke();
            Log($"OnMpvFileLoaded UI: source={_sourceVideoWidth}x{_sourceVideoHeight} layout={_detectedSbsLayout}");
            RequestNextFrameRendering();
        });
    }

    private void TryInitializeSrWeaver(bool force)
    {
        if (!_sbs3DEnabled || _gl == null || _renderContext == IntPtr.Zero)
        {
            return;
        }
        SRWeaverClient? srWeaver = _srWeaver;
        if ((srWeaver != null && srWeaver.IsInitialized) || (!force && DateTime.UtcNow - _lastWeaverAttemptUtc < TimeSpan.FromSeconds(2L)))
        {
            return;
        }
        _lastWeaverAttemptUtc = DateTime.UtcNow;
        TopLevel topLevel = TopLevel.GetTopLevel(this);
        AttachWindowHooks(topLevel as Window);
        nint num = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (num != IntPtr.Zero)
        {
            if (_srWeaver != null)
            {
                _srWeaver.Dispose();
                _srWeaver = null;
            }
            _srWeaver = new SRWeaverClient();
            if (!_srWeaver.Initialize(3840u, 2160u, num))
            {
                _srWeaver.Dispose();
                _srWeaver = null;
                _lensHintRequested = false;
            }
            else
            {
                _lensHintRequested = false;
                ApplyLensHint(enabled: false);
            }
        }
    }

    private void DisableSrWeaver()
    {
        if (_srWeaver != null)
        {
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
    }

    protected override void OnOpenGlRender(GlInterface gli, int fb)
    {
        if (_gl == null)
        {
            return;
        }
        if (_renderContext == IntPtr.Zero)
        {
            EnsureMpvInitialized();
        }
        if (_renderContext == IntPtr.Zero)
        {
            return;
        }
        _renderCallCount++;
        if (_renderCallCount <= 8 || _renderCallCount % 120 == 0)
        {
            Log($"Render call #{_renderCallCount} sbs={_sbs3DEnabled} playing={_isPlaying}");
        }
        if (_hostWindow == null)
        {
            AttachWindowHooks(TopLevel.GetTopLevel(this) as Window);
        }
        UpdateWindowEligibility();
        double num = base.VisualRoot?.RenderScaling ?? 1.0;
        int num2 = (int)Math.Max(1.0, base.Bounds.Width * num);
        int num3 = (int)Math.Max(1.0, base.Bounds.Height * num);
        if (_mpvFrameDirty || _renderCallCount % 20 == 0)
        {
            UpdateMpvSourceSize();
        }
        if (_sbs3DEnabled)
        {
            DetectSbsLayout();
        }
        bool flag = _sbs3DEnabled && IsCurrentSourceLikelyFullSbs();
        UpdateMpvAspectForStereoMode();
        float aspectZoomY = ComputeStereoAspectZoomY(num2, num3, flag);
        LogStereoRenderState(flag, aspectZoomY, num2, num3);
        bool flag2 = _videoTextureW != num2 || _videoTextureH != num3;
        EnsureVideoTextureSize(num2, num3);
        bool flag3 = _mpvFrameDirty || flag2;
        uint num4 = _videoTexture;
        if (_sbs3DEnabled)
        {
            if (_mpvHasSubtitleText)
            {
                bool flag4 = _subtitleTextureW != num2 || _subtitleTextureH != num3;
                bool flag5 = _compositeTextureW != num2 || _compositeTextureH != num3;
                EnsureSubtitleTextureSize(num2, num3);
                EnsureCompositeTextureSize(num2, num3);
                flag3 = flag3 || flag4 || flag5;
                if (flag3)
                {
                    RenderMpvToTexture(num2, num3, _videoTexture, subtitlesVisible: false);
                    RenderMpvToTexture(num2, num3, _subtitleTexture, subtitlesVisible: true);
                    DrawTextureToFramebuffer((int)_compositeFbo, num2, num3, 0, _videoTexture, _subtitleTexture, 1, 1, flag ? 1 : 0, 1, aspectZoomY, 0);
                }
                num4 = _compositeTexture;
            }
            else
            {
                if (flag3)
                {
                    RenderMpvToTexture(num2, num3, _videoTexture, subtitlesVisible: true);
                }
                num4 = _videoTexture;
            }
        }
        else if (flag3)
        {
            RenderMpvToTexture(num2, num3, _videoTexture, subtitlesVisible: true);
        }
        if (flag3)
        {
            _mpvFrameDirty = false;
        }
        if (_sbs3DEnabled)
        {
            if (_windowEligibleFor3D)
            {
                bool pendingWeaverInit = _pendingWeaverInit;
                TryInitializeSrWeaver(pendingWeaverInit);
                _pendingWeaverInit = false;
            }
            bool flag6 = false;
            SRWeaverClient? srWeaver = _srWeaver;
            if (srWeaver != null && srWeaver.IsInitialized)
            {
                SRWeaverClient.RuntimeState runtimeState = _srWeaver.GetRuntimeState();
                if (!runtimeState.ContextValid && DateTime.UtcNow - _lastContextRecoveryAttemptUtc > TimeSpan.FromSeconds(2L))
                {
                    _lastContextRecoveryAttemptUtc = DateTime.UtcNow;
                    if (_srWeaver.TryRecoverContext())
                    {
                        runtimeState = _srWeaver.GetRuntimeState();
                    }
                }
                flag6 = runtimeState.SrAvailable && runtimeState.ContextValid;
            }
            bool flag7 = _windowEligibleFor3D && flag6;
            ApplyLensHint(flag7);
            if (flag7)
            {
                RenderToOutputWithWeaving(fb, num2, num3, num4, flag, aspectZoomY);
            }
            else
            {
                DrawTextureToFramebuffer(fb, num2, num3, 1, num4, num4, 0, 0, 0, 0, aspectZoomY, 1);
            }
        }
        else
        {
            ApplyLensHint(enabled: false);
            DrawTextureToFramebuffer(fb, num2, num3, 1, _videoTexture, _videoTexture, 0, 0, 0, 0, 1f, 0);
        }
    }

    private unsafe void EnsureVideoTextureSize(int width, int height)
    {
        if (_gl != null && (_videoTextureW != width || _videoTextureH != height))
        {
            _videoTextureW = width;
            _videoTextureH = height;
            _gl.BindTexture(TextureTarget.Texture2D, _videoTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, 32856, (uint)width, (uint)height, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
    }

    private unsafe void EnsureSubtitleTextureSize(int width, int height)
    {
        if (_gl != null && (_subtitleTextureW != width || _subtitleTextureH != height))
        {
            _subtitleTextureW = width;
            _subtitleTextureH = height;
            _gl.BindTexture(TextureTarget.Texture2D, _subtitleTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, 32856, (uint)width, (uint)height, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
    }

    private unsafe void EnsureCompositeTextureSize(int width, int height)
    {
        if (_gl != null && (_compositeTextureW != width || _compositeTextureH != height))
        {
            _compositeTextureW = width;
            _compositeTextureH = height;
            _gl.BindTexture(TextureTarget.Texture2D, _compositeTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, 32856, (uint)width, (uint)height, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _compositeFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _compositeTexture, 0);
        }
    }

    private unsafe void EnsureWeaveTextureSize(int width, int height)
    {
        if (_gl != null && (_weaveTextureW != width || _weaveTextureH != height))
        {
            _weaveTextureW = width;
            _weaveTextureH = height;
            _gl.BindTexture(TextureTarget.Texture2D, _weaveTexture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, 32856, (uint)width, (uint)height, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _weaveFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _weaveTexture, 0);
        }
    }

    private void RenderMpvToTexture(int width, int height, uint targetTexture, bool subtitlesVisible)
    {
        if (_gl != null && _renderContext != IntPtr.Zero && _mpv != null)
        {
            if (_currentSubVisibility != subtitlesVisible)
            {
                _mpv.SetProperty("sub-visibility", subtitlesVisible ? "yes" : "no");
                _currentSubVisibility = subtitlesVisible;
            }
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _videoFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, targetTexture, 0);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            LibMpv.mpv_opengl_fbo structure = new LibMpv.mpv_opengl_fbo
            {
                fbo = (int)_videoFbo,
                w = width,
                h = height,
                internal_format = 32856
            };
            nint num = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
            Marshal.StructureToPtr(structure, num, fDeleteOld: false);
            LibMpv.mpv_render_param[] array = new LibMpv.mpv_render_param[2]
            {
                new LibMpv.mpv_render_param
                {
                    type = LibMpv.mpv_render_param_type.FORWARD_TARGET,
                    data = num
                },
                new LibMpv.mpv_render_param
                {
                    type = LibMpv.mpv_render_param_type.INVALID,
                    data = IntPtr.Zero
                }
            };
            int num2 = Marshal.SizeOf<LibMpv.mpv_render_param>();
            nint num3 = Marshal.AllocHGlobal(num2 * array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                Marshal.StructureToPtr(array[i], IntPtr.Add(num3, i * num2), fDeleteOld: false);
            }
            if (_renderCallCount <= 8 || _renderCallCount % 120 == 0)
            {
                Log($"mpv_render_context_render begin: tex={targetTexture} size={width}x{height} subs={subtitlesVisible}");
            }
            LibMpv.mpv_render_context_render(_renderContext, num3);
            if (_renderCallCount <= 8 || _renderCallCount % 120 == 0)
            {
                Log("mpv_render_context_render end");
            }
            _lastMpvUpdateUtc = DateTime.UtcNow;
            Marshal.FreeHGlobal(num3);
            Marshal.FreeHGlobal(num);
        }
    }

    private IReadOnlyList<MediaTrackInfo> GetMpvTracks(MediaTrackType trackType)
    {
        List<MediaTrackInfo> list = new List<MediaTrackInfo>();
        if (_mpv == null)
        {
            return list;
        }
        int num = (int)_mpv.GetPropertyDouble("track-list/count");
        string b = ((trackType == MediaTrackType.Audio) ? "audio" : "sub");
        for (int i = 0; i < num; i++)
        {
            string a = _mpv.GetPropertyString($"track-list/{i}/type") ?? string.Empty;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                int num2 = (int)_mpv.GetPropertyDouble($"track-list/{i}/id");
                if (num2 >= 0 && !string.Equals(_mpv.GetPropertyString($"track-list/{i}/image"), "yes", StringComparison.OrdinalIgnoreCase))
                {
                    string language = _mpv.GetPropertyString($"track-list/{i}/lang") ?? "unk";
                    string text = _mpv.GetPropertyString($"track-list/{i}/title") ?? string.Empty;
                    bool isSelected = string.Equals(_mpv.GetPropertyString($"track-list/{i}/selected"), "yes", StringComparison.OrdinalIgnoreCase);
                    list.Add(new MediaTrackInfo
                    {
                        Id = num2,
                        Language = language,
                        Title = (string.IsNullOrWhiteSpace(text) ? $"{((trackType == MediaTrackType.Audio) ? "Audio" : "Subtitle")} {num2}" : text),
                        IsSelected = isSelected
                    });
                }
            }
        }
        return list;
    }

    private void UpdateMpvSourceSize()
    {
        if (_mpv != null)
        {
            int num = (int)Math.Round(_mpv.GetPropertyDouble("width"));
            int num2 = (int)Math.Round(_mpv.GetPropertyDouble("height"));
            if (num > 0 && num2 > 0)
            {
                _sourceVideoWidth = num;
                _sourceVideoHeight = num2;
            }
            string propertyString = _mpv.GetPropertyString("sub-text");
            _mpvHasSubtitleText = !string.IsNullOrWhiteSpace(propertyString);
            if (_lastLoggedMpvHasSubtitleText != _mpvHasSubtitleText)
            {
                _lastLoggedMpvHasSubtitleText = _mpvHasSubtitleText;
                Console.WriteLine($"[VideoPlayerControl] MPV sub-text active: {_mpvHasSubtitleText}");
            }
        }
    }

    private void DetectSbsLayout()
    {
        if (_sbs3DEnabled)
        {
            _detectedSbsLayout = ((!IsCurrentSourceLikelyFullSbs()) ? SbsLayout.Half : SbsLayout.Full);
        }
    }

    private void UpdateMpvAspectForStereoMode()
    {
        if (_mpv == null)
        {
            return;
        }
        bool flag = _sbs3DEnabled && IsCurrentSourceLikelyFullSbs();
        if (_mpvKeepAspectDisabled != false)
        {
            _mpvKeepAspectDisabled = false;
            try
            {
                _mpv.SetProperty("keepaspect", "yes");
                Log($"MPV keepaspect=yes (sbs={_sbs3DEnabled}, layout={_detectedSbsLayout})");
            }
            catch (Exception value)
            {
                Trace.WriteLine($"[VideoPlayerControl] Failed to set MPV keepaspect: {value}");
            }
        }
        if (_mpvFullSbsAspectOverrideEnabled == flag)
        {
            return;
        }
        _mpvFullSbsAspectOverrideEnabled = flag;
        try
        {
            _mpv.SetProperty("video-aspect-override", flag ? "1.7777778" : "no");
            Log($"MPV video-aspect-override={(flag ? "1.7777778" : "no")} (sbs={_sbs3DEnabled}, layout={_detectedSbsLayout})");
        }
        catch (Exception value2)
        {
            Trace.WriteLine($"[VideoPlayerControl] Failed to set MPV video-aspect-override: {value2}");
        }
    }

    private float ComputeStereoAspectZoomY(int width, int height, bool isFullSbs)
    {
        return 1f;
    }

    private bool IsCurrentSourceLikelyFullSbs()
    {
        switch (DetectSbsLayoutFromName(_currentFile))
        {
            case SbsLayout.Full:
                return true;
            case SbsLayout.Half:
                return false;
            default:
                if (_sourceVideoWidth > 0 && _sourceVideoHeight > 0)
                {
                    double num = (double)_sourceVideoWidth / (double)_sourceVideoHeight;
                    return _sourceVideoWidth >= 3000 || num >= 2.4;
                }
                return false;
        }
    }

    private void LogStereoRenderState(bool isFullSbs, float aspectZoomY, int width, int height)
    {
        if (!_sbs3DEnabled)
        {
            _lastLoggedFullSbs = null;
            _lastLoggedAspectZoomY = null;
            return;
        }
        bool flag = _lastLoggedFullSbs != isFullSbs || !_lastLoggedAspectZoomY.HasValue || Math.Abs(_lastLoggedAspectZoomY.Value - aspectZoomY) > 0.01f;
        bool flag2 = _renderCallCount <= 8 || _renderCallCount % 240 == 0;
        if (flag || flag2)
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
        string text = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        if (text.Contains("full-sbs") || text.Contains("fullsbs") || text.Contains("fsbs") || text.Contains("sbs-full") || text.Contains("sbsfull"))
        {
            return SbsLayout.Full;
        }
        if (text.Contains("half-sbs") || text.Contains("halfsbs") || text.Contains("hsbs"))
        {
            return SbsLayout.Half;
        }
        return SbsLayout.Unknown;
    }

    private void RenderToOutputWithWeaving(int fb, int width, int height, uint sourceTexture, bool isFullSbs, float aspectZoomY)
    {
        if (_gl == null)
        {
            return;
        }
        EnsureWeaveTextureSize(width, height);
        uint sourceFbo = ((sourceTexture == _compositeTexture) ? _compositeFbo : _videoFbo);
        BlitTextureToTextureFlipped(sourceFbo, _weaveFbo, width, height);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        bool flag = false;
        SRWeaverClient? srWeaver = _srWeaver;
        if (srWeaver != null && srWeaver.IsInitialized)
        {
            try
            {
                _srWeaver.Weave(_weaveTexture);
                flag = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[VideoPlayerControl] Leia weaving failed: " + ex.Message);
            }
        }
        if (!flag)
        {
            DrawTextureToFramebuffer(fb, width, height, 1, sourceTexture, sourceTexture, 0, 0, isFullSbs ? 1 : 0, 0, aspectZoomY, 1);
        }
    }

    private void BlitTextureToTextureFlipped(uint sourceFbo, uint targetFbo, int width, int height)
    {
        if (_gl != null)
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sourceFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
            _gl.BlitFramebuffer(0, 0, width, height, 0, height, width, 0, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u);
        }
    }

    private unsafe void DrawTextureToFramebuffer(int fb, int width, int height, int flipY, uint colorTexture, uint subtitleTexture, int useSubtitleTexture, int sbs3DEnabled, int sbsLayout, int hasSubText, float aspectZoomY, int stereoToMono)
    {
        if (_gl != null)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, colorTexture);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, subtitleTexture);
            int uniformLocation = _gl.GetUniformLocation(_program, "uTexture");
            if (uniformLocation != -1)
            {
                _gl.Uniform1(uniformLocation, 0);
            }
            int uniformLocation2 = _gl.GetUniformLocation(_program, "uSubTexture");
            if (uniformLocation2 != -1)
            {
                _gl.Uniform1(uniformLocation2, 1);
            }
            int uniformLocation3 = _gl.GetUniformLocation(_program, "uUseSubTexture");
            if (uniformLocation3 != -1)
            {
                _gl.Uniform1(uniformLocation3, useSubtitleTexture);
            }
            int uniformLocation4 = _gl.GetUniformLocation(_program, "uFlipY");
            if (uniformLocation4 != -1)
            {
                _gl.Uniform1(uniformLocation4, flipY);
            }
            int uniformLocation5 = _gl.GetUniformLocation(_program, "uSbs3DEnabled");
            if (uniformLocation5 != -1)
            {
                _gl.Uniform1(uniformLocation5, sbs3DEnabled);
            }
            int uniformLocation6 = _gl.GetUniformLocation(_program, "uSbsLayout");
            if (uniformLocation6 != -1)
            {
                _gl.Uniform1(uniformLocation6, sbsLayout);
            }
            int uniformLocation7 = _gl.GetUniformLocation(_program, "uHasSubText");
            if (uniformLocation7 != -1)
            {
                _gl.Uniform1(uniformLocation7, hasSubText);
            }
            int uniformLocation8 = _gl.GetUniformLocation(_program, "uAspectZoomY");
            if (uniformLocation8 != -1)
            {
                _gl.Uniform1(uniformLocation8, aspectZoomY);
            }
            int uniformLocation9 = _gl.GetUniformLocation(_program, "uStereoToMono");
            if (uniformLocation9 != -1)
            {
                _gl.Uniform1(uniformLocation9, stereoToMono);
            }
            _gl.DrawElements(PrimitiveType.Triangles, 6u, DrawElementsType.UnsignedShort, null);
            _gl.BindVertexArray(0u);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, 0u);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0u);
        }
    }

    private uint CreateShaderProgram(string vertexSource, string fragmentSource)
    {
        if (_gl == null)
        {
            throw new InvalidOperationException("GL not initialized");
        }
        uint shader = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(shader, vertexSource);
        _gl.CompileShader(shader);
        CheckShaderCompileErrors(shader, "VERTEX");
        uint shader2 = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(shader2, fragmentSource);
        _gl.CompileShader(shader2);
        CheckShaderCompileErrors(shader2, "FRAGMENT");
        uint num = _gl.CreateProgram();
        _gl.AttachShader(num, shader);
        _gl.AttachShader(num, shader2);
        _gl.LinkProgram(num);
        CheckProgramLinkErrors(num);
        _gl.DeleteShader(shader);
        _gl.DeleteShader(shader2);
        return num;
    }

    private void CheckShaderCompileErrors(uint shader, string type)
    {
        if (_gl != null)
        {
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var @params);
            if (@params == 0)
            {
                string shaderInfoLog = _gl.GetShaderInfoLog(shader);
                Console.WriteLine("[VideoPlayerControl] " + type + " shader compile error: " + shaderInfoLog);
            }
        }
    }

    private void CheckProgramLinkErrors(uint program)
    {
        if (_gl != null)
        {
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var @params);
            if (@params == 0)
            {
                string programInfoLog = _gl.GetProgramInfoLog(program);
                Console.WriteLine("[VideoPlayerControl] Program link error: " + programInfoLog);
            }
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gli)
    {
        Log("OnOpenGlDeinit.");
        Stop();
        ApplyLensHint(enabled: false);
        DisableSrWeaver();
        DetachWindowHooks();
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
        if (_gl != null)
        {
            _gl.DeleteProgram(_program);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteBuffer(_ebo);
            _gl.DeleteTexture(_videoTexture);
            _gl.DeleteTexture(_subtitleTexture);
            _gl.DeleteTexture(_compositeTexture);
            _gl.DeleteFramebuffer(_videoFbo);
            _gl.DeleteFramebuffer(_compositeFbo);
            _gl.DeleteTexture(_weaveTexture);
            _gl.DeleteFramebuffer(_weaveFbo);
        }
        base.OnOpenGlDeinit(gli);
    }

    private void MarkFrameDirtyAndRequest()
    {
        _mpvFrameDirty = true;
        RequestNextFrameRendering();
    }
}

