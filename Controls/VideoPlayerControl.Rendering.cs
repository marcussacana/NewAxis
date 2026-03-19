using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.Platform;
using NewAxis.Graphics;
using NewAxis.Services;
using Silk.NET.OpenGL;
using GLPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace NewAxis.Controls;

public partial class VideoPlayerControl
{
    private const int SubtitleModeNone = 0;
    private const int SubtitleModeStereoMono = 1;

    private enum MpvRenderPass
    {
        VideoOnly,
        VideoWithSubtitles,
        SubtitlesOnly
    }

    private readonly record struct RenderDimensions(int Width, int RenderHeight, bool UseSubtitleOverlay);

    private readonly record struct PreparedFrame(
        uint VideoTexture,
        uint SubtitleTexture,
        int HasSubtitleTexture,
        int SubtitleMode);

    private readonly record struct DrawFrameOptions(
        int FlipY,
        uint VideoTexture,
        uint SubtitleTexture,
        int HasSubtitleTexture,
        int Sbs3DEnabled,
        int SubtitleMode);

    protected unsafe override void OnOpenGlInit(GlInterface gli)
    {
        _glInterface = gli;
        _gl = GL.GetApi(gli.GetProcAddress);
        if (_gl == null)
        {
            Log("OnOpenGlInit: GL.GetApi returned null.");
            return;
        }

        _glMaxTextureSize = _gl.GetInteger((GetPName)3379);
        Log("OnOpenGlInit: GL version=" + _gl.GetStringS(StringName.Version) + " renderer=" + _gl.GetStringS(StringName.Renderer) + " maxTexture=" + _glMaxTextureSize);
        if (string.IsNullOrEmpty(ShaderSources.VideoPlayerVertex) || string.IsNullOrEmpty(ShaderSources.VideoPlayerFragment))
        {
            throw new Exception("Failed to load video player shaders.");
        }

        _program = CreateShaderProgram(ShaderSources.VideoPlayerVertex, ShaderSources.VideoPlayerFragment);
        CacheShaderUniformLocations();
        CreateQuadResources();
        ConfigureManagedTextures();
        AttachWindowHooks(TopLevel.GetTopLevel(this) as Window);
        EnsureMpvInitialized();
    }

    protected override void OnOpenGlRender(GlInterface gli, int fb)
    {
        if (_gl == null)
        {
            return;
        }

        _renderCallCount++;
        if (ShouldLogFrequentRender())
        {
            Log($"Render call #{_renderCallCount} sbs={_sbs3DEnabled} playing={_isPlaying}");
        }

        EnsureWindowHooksAttached();
        UpdateWindowEligibility();

        RenderDimensions dimensions = MeasureRenderDimensions();
        RefreshStereoMetadata();

        bool isFullSbs = _sbs3DEnabled && IsCurrentSourceLikelyFullSbs();
        float aspectZoomY = ComputeStereoAspectZoomY(dimensions.Width, dimensions.RenderHeight, isFullSbs);
        LogStereoRenderState(isFullSbs, aspectZoomY, dimensions.Width, dimensions.RenderHeight);

        PreparedFrame preparedFrame = PrepareOutputTexture(dimensions, isFullSbs, aspectZoomY);
        PresentFrame(fb, dimensions, preparedFrame, isFullSbs, aspectZoomY);
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

    private void EnsureWindowHooksAttached()
    {
        if (_hostWindow == null)
        {
            AttachWindowHooks(TopLevel.GetTopLevel(this) as Window);
        }
    }

    private RenderDimensions MeasureRenderDimensions()
    {
        double renderScale = VisualRoot?.RenderScaling ?? 1.0;
        int width = (int)Math.Max(1.0, Bounds.Width * renderScale);
        int renderHeight = (int)Math.Max(1.0, Bounds.Height * renderScale);
        bool useSubtitleOverlay = _sbs3DEnabled && (_mpvTextSubtitleTrackSelected || _mpvImageSubtitleTrackSelected);
        return new RenderDimensions(width, renderHeight, useSubtitleOverlay);
    }

    private void RefreshStereoMetadata()
    {
        if ((_sourceVideoWidth <= 0 || _sourceVideoHeight <= 0) && (_mpvFrameDirty || _renderCallCount % 120 == 0))
        {
            UpdateMpvSourceSize();
        }

        if (_sbs3DEnabled)
        {
            DetectSbsLayout();
        }

        UpdateMpvAspectForStereoMode();
    }

    private PreparedFrame PrepareOutputTexture(RenderDimensions dimensions, bool isFullSbs, float aspectZoomY)
    {
        bool videoTextureResized = _videoTextureW != dimensions.Width || _videoTextureH != dimensions.RenderHeight;
        EnsureVideoTextureSize(dimensions.Width, dimensions.RenderHeight);

        bool shouldRenderFrame = _mpvFrameDirty || videoTextureResized;
        if (_sbs3DEnabled)
        {
            return PrepareStereoOutputTexture(dimensions, isFullSbs, aspectZoomY, shouldRenderFrame);
        }

        if (shouldRenderFrame)
        {
            RenderMpvToTexture(dimensions.Width, dimensions.RenderHeight, _videoTexture, MpvRenderPass.VideoWithSubtitles);
            _mpvFrameDirty = false;
        }

        return new PreparedFrame(_videoTexture, 0, 0, SubtitleModeNone);
    }

    private PreparedFrame PrepareStereoOutputTexture(RenderDimensions dimensions, bool isFullSbs, float aspectZoomY, bool shouldRenderFrame)
    {
        bool subtitleTextureResized = false;
        if (dimensions.UseSubtitleOverlay)
        {
            subtitleTextureResized = _subtitleTextureW != dimensions.Width || _subtitleTextureH != dimensions.RenderHeight;
            EnsureSubtitleTextureSize(dimensions.Width, dimensions.RenderHeight);
        }

        if (shouldRenderFrame || subtitleTextureResized)
        {
            RenderMpvToTexture(dimensions.Width, dimensions.RenderHeight, _videoTexture, MpvRenderPass.VideoOnly);
            if (dimensions.UseSubtitleOverlay)
            {
                RenderMpvToTexture(dimensions.Width, dimensions.RenderHeight, _subtitleTexture, MpvRenderPass.SubtitlesOnly);
            }
            _mpvFrameDirty = false;
        }

        return new PreparedFrame(
            _videoTexture,
            dimensions.UseSubtitleOverlay ? _subtitleTexture : 0u,
            dimensions.UseSubtitleOverlay ? 1 : 0,
            dimensions.UseSubtitleOverlay ? SubtitleModeStereoMono : SubtitleModeNone);
    }

    private void PresentFrame(int fb, RenderDimensions dimensions, PreparedFrame preparedFrame, bool isFullSbs, float aspectZoomY)
    {
        if (_sbs3DEnabled)
        {
            PresentStereoFrame(fb, dimensions, preparedFrame, isFullSbs, aspectZoomY);
            return;
        }

        ApplyLensHint(enabled: false);
        DrawTextureToFramebuffer(
            fb,
            dimensions.Width,
            dimensions.RenderHeight,
            new DrawFrameOptions(1, preparedFrame.VideoTexture, 0, 0, 0, SubtitleModeNone));
    }

    private void PresentStereoFrame(int fb, RenderDimensions dimensions, PreparedFrame preparedFrame, bool isFullSbs, float aspectZoomY)
    {
        if (_windowEligibleFor3D)
        {
            bool pendingWeaverInit = _pendingWeaverInit;
            TryInitializeSrWeaver(pendingWeaverInit);
            _pendingWeaverInit = false;
        }

        bool canUseWeaving = CanUseStereoWeaving();
        ApplyLensHint(canUseWeaving);

        if (canUseWeaving)
        {
            RenderToOutputWithWeaving(fb, dimensions.Width, dimensions.RenderHeight, preparedFrame, isFullSbs, aspectZoomY);
            return;
        }

        DrawTextureToFramebuffer(
            fb,
            dimensions.Width,
            dimensions.RenderHeight,
            new DrawFrameOptions(
                1,
                preparedFrame.VideoTexture,
                preparedFrame.SubtitleTexture,
                preparedFrame.HasSubtitleTexture,
                1,
                preparedFrame.SubtitleMode));
    }

    private bool CanUseStereoWeaving()
    {
        bool isSrRuntimeReady = false;
        SRWeaverClient? srWeaver = _srWeaver;
        if (srWeaver != null && srWeaver.IsInitialized)
        {
            SRWeaverClient.RuntimeState runtimeState = srWeaver.GetRuntimeState();
            if (!runtimeState.ContextValid && DateTime.UtcNow - _lastContextRecoveryAttemptUtc > TimeSpan.FromSeconds(2))
            {
                _lastContextRecoveryAttemptUtc = DateTime.UtcNow;
                if (srWeaver.TryRecoverContext())
                {
                    runtimeState = srWeaver.GetRuntimeState();
                }
            }

            isSrRuntimeReady = runtimeState.SrAvailable && runtimeState.ContextValid;
        }

        return _windowEligibleFor3D && isSrRuntimeReady;
    }

    private unsafe void CreateQuadResources()
    {
        float[] vertexData =
        {
            1f, -1f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f,
            -1f, 1f, 0f, 0f, 1f, -1f, -1f, 0f, 0f, 0f
        };
        ushort[] indexData = { 0, 1, 2, 2, 3, 0 };

        _vao = _gl!.GenVertexArray();
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
        fixed (float* vertexDataPtr = vertexData)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertexData.Length * sizeof(float)), vertexDataPtr, BufferUsageARB.StaticDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* indexDataPtr = indexData)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indexData.Length * sizeof(ushort)), indexDataPtr, BufferUsageARB.StaticDraw);
        }

        _gl.VertexAttribPointer(0u, 3, VertexAttribPointerType.Float, normalized: false, 20u, null);
        _gl.EnableVertexAttribArray(0u);
        _gl.VertexAttribPointer(1u, 2, VertexAttribPointerType.Float, normalized: false, 20u, (void*)12);
        _gl.EnableVertexAttribArray(1u);
        _gl.BindVertexArray(0u);
    }

    private void ConfigureManagedTextures()
    {
        ConfigureTexture(_videoTexture);
        ConfigureTexture(_subtitleTexture);
        ConfigureTexture(_compositeTexture);
        ConfigureTexture(_weaveTexture);
    }

    private void ConfigureTexture(uint texture)
    {
        _gl!.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, 9729);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, 33071);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, 33071);
    }

    private unsafe void EnsureVideoTextureSize(int width, int height)
    {
        EnsureTextureSize(ref _videoTextureW, ref _videoTextureH, width, height, _videoTexture, 0);
    }

    private unsafe void EnsureSubtitleTextureSize(int width, int height)
    {
        EnsureTextureSize(ref _subtitleTextureW, ref _subtitleTextureH, width, height, _subtitleTexture, 0);
    }

    private unsafe void EnsureCompositeTextureSize(int width, int height)
    {
        EnsureTextureSize(ref _compositeTextureW, ref _compositeTextureH, width, height, _compositeTexture, _compositeFbo);
    }

    private unsafe void EnsureWeaveTextureSize(int width, int height)
    {
        EnsureTextureSize(ref _weaveTextureW, ref _weaveTextureH, width, height, _weaveTexture, _weaveFbo);
    }

    private unsafe void EnsureTextureSize(ref int currentWidth, ref int currentHeight, int width, int height, uint texture, uint framebuffer)
    {
        if (_gl == null || (currentWidth == width && currentHeight == height))
        {
            return;
        }

        currentWidth = width;
        currentHeight = height;
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, 32856, (uint)width, (uint)height, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, null);

        if (framebuffer != 0)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
        }
    }

    private bool ConfigureSubtitleBandLayout(bool enabled, int bottomMargin)
    {
        if (_mpv == null)
        {
            return false;
        }

        int effectiveBottomMargin = enabled ? Math.Max(0, bottomMargin) : 0;
        if (_subtitleBandLayoutEnabled == enabled && (!enabled || _subtitleBandBottomMargin == effectiveBottomMargin))
        {
            return false;
        }

        try
        {
            _mpv.SetProperty("sub-use-margins", enabled ? "yes" : "no");
            _mpv.SetProperty("sub-ass-force-margins", enabled ? "yes" : "no");
            _mpv.SetProperty("stretch-image-subs-to-screen", "no");
            _mpv.SetProperty("video-margin-ratio-bottom", "0");
            _mpv.SetProperty("video-align-y", enabled ? "-1" : "0");

            if (enabled)
            {
                _mpv.Command("vf", "set", $"sub={effectiveBottomMargin}:0");
            }
            else
            {
                _mpv.Command("vf", "clr");
            }

            _subtitleBandLayoutEnabled = enabled;
            _subtitleBandBottomMargin = effectiveBottomMargin;
            Log(enabled ? $"Subtitle band enabled: bottomMargin={effectiveBottomMargin}" : "Subtitle band disabled.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoPlayerControl] Failed to configure subtitle band layout: {ex}");
            return false;
        }
    }

    private int ComputeSubtitleBandHeight(int width, int height)
    {
        int maxTextureSize = _glMaxTextureSize > 0 ? _glMaxTextureSize : 8192;
        if (width <= 0 || height <= 0 || width > maxTextureSize)
        {
            if (_lastLoggedSubtitleBandHeight != 0)
            {
                _lastLoggedSubtitleBandHeight = 0;
                Log($"Subtitle band disabled by texture limit: video={width}x{height} maxTexture={maxTextureSize}");
            }

            return 0;
        }

        int availableBandHeight = Math.Max(0, maxTextureSize - height);
        int subtitleBandHeight = Math.Min(height, availableBandHeight);
        if (_lastLoggedSubtitleBandHeight != subtitleBandHeight)
        {
            _lastLoggedSubtitleBandHeight = subtitleBandHeight;
            Log($"Subtitle band height={subtitleBandHeight} (video={width}x{height}, maxTexture={maxTextureSize})");
        }

        return subtitleBandHeight;
    }

    private void RenderToOutputWithWeaving(int fb, int width, int height, PreparedFrame preparedFrame, bool isFullSbs, float aspectZoomY)
    {
        if (_gl == null)
        {
            return;
        }

        uint sourceFbo = _videoFbo;
        if (preparedFrame.HasSubtitleTexture == 1)
        {
            EnsureCompositeTextureSize(width, height);
            DrawTextureToFramebuffer(
                (int)_compositeFbo,
                width,
                height,
                new DrawFrameOptions(
                    0,
                    preparedFrame.VideoTexture,
                    preparedFrame.SubtitleTexture,
                    preparedFrame.HasSubtitleTexture,
                    1,
                    preparedFrame.SubtitleMode));
            sourceFbo = _compositeFbo;
        }

        EnsureWeaveTextureSize(width, height);
        BlitTextureToTextureFlipped(sourceFbo, _weaveFbo, width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        bool weavingSucceeded = false;
        SRWeaverClient? srWeaver = _srWeaver;
        if (srWeaver != null && srWeaver.IsInitialized)
        {
            try
            {
                _srWeaver!.Weave(_weaveTexture);
                weavingSucceeded = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[VideoPlayerControl] Leia weaving failed: " + ex.Message);
            }
        }

        if (!weavingSucceeded)
        {
            DrawTextureToFramebuffer(
                fb,
                width,
                height,
                new DrawFrameOptions(
                    1,
                    preparedFrame.VideoTexture,
                    preparedFrame.SubtitleTexture,
                    preparedFrame.HasSubtitleTexture,
                    1,
                    preparedFrame.SubtitleMode));
        }
    }

    private void BlitTextureToTextureFlipped(uint sourceFbo, uint targetFbo, int width, int height)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sourceFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
        _gl.BlitFramebuffer(0, 0, width, height, 0, height, width, 0, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u);
    }

    private unsafe void DrawTextureToFramebuffer(int fb, int width, int height, DrawFrameOptions options)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, options.VideoTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, options.HasSubtitleTexture == 1 ? options.SubtitleTexture : 0u);

        SetUniform(_uHasSubtitleTextureLocation, options.HasSubtitleTexture);
        SetUniform(_uFlipYLocation, options.FlipY);
        SetUniform(_uSbs3DEnabledLocation, options.Sbs3DEnabled);
        SetUniform(_uSubtitleModeLocation, options.SubtitleMode);

        _gl.DrawElements(PrimitiveType.Triangles, 6u, DrawElementsType.UnsignedShort, null);
        _gl.BindVertexArray(0u);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, 0u);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0u);
    }

    private void CacheShaderUniformLocations()
    {
        if (_gl == null)
        {
            return;
        }

        _uVideoTextureLocation = _gl.GetUniformLocation(_program, "uVideoTexture");
        _uSubtitleTextureLocation = _gl.GetUniformLocation(_program, "uSubtitleTexture");
        _uHasSubtitleTextureLocation = _gl.GetUniformLocation(_program, "uHasSubtitleTexture");
        _uFlipYLocation = _gl.GetUniformLocation(_program, "uFlipY");
        _uSbs3DEnabledLocation = _gl.GetUniformLocation(_program, "uSbs3DEnabled");
        _uSubtitleModeLocation = _gl.GetUniformLocation(_program, "uSubtitleMode");

        _gl.UseProgram(_program);
        SetUniform(_uVideoTextureLocation, 0);
        SetUniform(_uSubtitleTextureLocation, 1);
        _gl.UseProgram(0);
    }

    private void SetUniform(int location, int value)
    {
        if (location != -1)
        {
            _gl!.Uniform1(location, value);
        }
    }

    private uint CreateShaderProgram(string vertexSource, string fragmentSource)
    {
        if (_gl == null)
        {
            throw new InvalidOperationException("GL not initialized");
        }

        uint vertexShader = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(vertexShader, vertexSource);
        _gl.CompileShader(vertexShader);
        CheckShaderCompileErrors(vertexShader, "VERTEX");

        uint fragmentShader = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(fragmentShader, fragmentSource);
        _gl.CompileShader(fragmentShader);
        CheckShaderCompileErrors(fragmentShader, "FRAGMENT");

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vertexShader);
        _gl.AttachShader(program, fragmentShader);
        _gl.LinkProgram(program);
        CheckProgramLinkErrors(program);
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
        return program;
    }

    private void CheckShaderCompileErrors(uint shader, string type)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            Trace.WriteLine("[VideoPlayerControl] " + type + " shader compile error: " + _gl.GetShaderInfoLog(shader));
        }
    }

    private void CheckProgramLinkErrors(uint program)
    {
        if (_gl == null)
        {
            return;
        }

        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            Trace.WriteLine("[VideoPlayerControl] Program link error: " + _gl.GetProgramInfoLog(program));
        }
    }

    private void DumpStereoSubtitleDebugFrames(int width, int height, int extendedHeight)
    {
        if (!_sbs3DEnabled || (!_mpvTextSubtitleTrackSelected && !_mpvImageSubtitleTrackSelected) || _subtitleBandDebugDumpDone)
        {
            return;
        }

        DumpSubtitleBandDebugTextures(width, height, extendedHeight);
        _subtitleBandDebugDumpDone = true;
    }

    private void DumpSubtitleBandDebugTextures(int width, int height, int extendedHeight)
    {
        if (_gl == null)
        {
            return;
        }

        try
        {
            string fileLabel = string.IsNullOrWhiteSpace(_currentFile) ? "unknown" : Path.GetFileNameWithoutExtension(_currentFile);
            string safeLabel = SanitizePathSegment(fileLabel);
            string dir = Path.Combine(AppContext.BaseDirectory, "subtitle-band-debug", safeLabel + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            DumpTextureToBmp(_videoTexture, width, extendedHeight, Path.Combine(dir, "01-video.bmp"));
            DumpTextureToBmp(_subtitleTexture, width, extendedHeight, Path.Combine(dir, "02-subtitle.bmp"));
            DumpTextureToBmp(_compositeTexture, width, height, Path.Combine(dir, "03-composite.bmp"));
            File.WriteAllText(
                Path.Combine(dir, "info.txt"),
                $"file={_currentFile ?? "<null>"}{Environment.NewLine}video={width}x{height} extended={width}x{extendedHeight} textTrack={_mpvTextSubtitleTrackSelected} imageTrack={_mpvImageSubtitleTrackSelected} margin={_subtitleBandBottomMargin} sbs={_sbs3DEnabled} layout={_detectedSbsLayout}{Environment.NewLine}");
            Log($"Subtitle band debug dump saved to {dir}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VideoPlayerControl] Failed to dump subtitle band debug textures: {ex}");
        }
    }

    public unsafe void DumpTextureToBmp(uint texture, int width, int height, string path)
    {
        int size = width * height * 4;
        byte[] pixels = new byte[size];

        fixed (byte* ptr = pixels)
        {
            _gl!.BindTexture(TextureTarget.Texture2D, texture);
            _gl.GetTexImage(TextureTarget.Texture2D, 0, GLPixelFormat.Rgba, PixelType.UnsignedByte, ptr);
        }

        WriteBmp(path, pixels, width, height);
    }

    private void WriteBmp(string path, byte[] pixels, int width, int height)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        int fileSize = 54 + pixels.Length;
        bw.Write((ushort)0x4D42);
        bw.Write(fileSize);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(pixels.Length);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                bw.Write(pixels[i + 2]);
                bw.Write(pixels[i + 1]);
                bw.Write(pixels[i + 0]);
                bw.Write(pixels[i + 3]);
            }
        }
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private bool ShouldLogFrequentRender()
    {
        return Program.LogEnabled && (_renderCallCount <= 8 || _renderCallCount % 120 == 0);
    }
}

