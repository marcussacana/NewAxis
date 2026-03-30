using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using NewAxis.Graphics;
using NewAxis.Services;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;
using Path = System.IO.Path;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace NewAxis.Controls
{
    public class MeshViewerControl : OpenGlControlBase
    {
        private float _yaw;
        private float _pitch;

        // Stereo Parameters
        private float _stereoSeparation = 0.18f;
        private float _stereoConvergence = 0.03f;

        // Interaction Parameters
        public bool AutoRotate { get; set; } = true;
        private float _moveX = 0.0f;
        private float _moveY = 0.0f;
        private float _moveZ = -2.0f;
        private float _scale = 1.0f;

        public float Scale
        {
            get => _scale;
            set { _scale = value; RequestNextFrameRendering(); }
        }

        public float MoveX
        {
            get => _moveX;
            set { _moveX = value; RequestNextFrameRendering(); }
        }

        public float MoveY
        {
            get => _moveY;
            set { _moveY = value; RequestNextFrameRendering(); }
        }

        public float MoveZ
        {
            get => _moveZ;
            set { _moveZ = value; RequestNextFrameRendering(); }
        }

        public float Yaw
        {
            get => _yaw;
            set { _yaw = value; RequestNextFrameRendering(); }
        }

        public float Pitch
        {
            get => _pitch;
            set { _pitch = value; RequestNextFrameRendering(); }
        }

        public float StereoSeparation
        {
            get => _stereoSeparation;
            set { _stereoSeparation = value; RequestNextFrameRendering(); }
        }

        public float StereoConvergence
        {
            get => _stereoConvergence;
            set { _stereoConvergence = value; RequestNextFrameRendering(); }
        }

        public bool SwapEyes { get; set; } = false;

        private float _parallaxIntensity = 0.48f;
        public float ParallaxIntensity
        {
            get => _parallaxIntensity;
            set { _parallaxIntensity = Math.Clamp(value, 0.0f, 2.0f); RequestNextFrameRendering(); }
        }

        private bool _useDitheredBlend;
        public bool UseDitheredBlend
        {
            get => _useDitheredBlend;
            set
            {
                if (_useDitheredBlend == value) return;
                _useDitheredBlend = value;
                RequestNextFrameRendering();
            }
        }

        private bool _showStage = true;
        public bool ShowStage
        {
            get => _showStage;
            set
            {
                if (_showStage == value) return;
                _showStage = value;
                RequestNextFrameRendering();
            }
        }

        private Avalonia.Media.Color _backgroundColor = Avalonia.Media.Color.FromArgb(255, 0, 0, 0); // Default opaque black
        public Avalonia.Media.Color BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                if (_backgroundColor == value) return;
                _backgroundColor = value;
                RequestNextFrameRendering();
            }
        }

        // --- GL API ---
        private GL? _gl;

        // --- BUFFERS ---
        private uint _program;
        private uint _vao;
        private uint _vbo;
        private uint _ebo;

        // --- UNIFORMS ---
        private int _uModel;
        private int _uView;
        private int _uProjection;
        private int _uTexture;
        private int _uHasTexture;
        private int _uBaseColorFactor;
        private int _uAlphaMode;
        private int _uAlphaCutoff;
        private int _uUseDitheredBlend;

        // --- DATA ---
        private SimpleObjLoader.MeshData? _pendingMeshData;
        private string? _pendingBaseDir;
        private GlbLoader.GlbMeshData? _pendingGlbData;
        private SimpleObjLoader.MeshData? _currentMeshData;
        private Dictionary<string, uint> _textures = new Dictionary<string, uint>();
        private Dictionary<string, GlbLoader.MaterialInfo> _materialInfos = new Dictionary<string, GlbLoader.MaterialInfo>();
        private uint _defaultTexture;
        private uint _stageTexture;
        private uint _stageVao;
        private uint _stageVbo;
        private uint _stageEbo;
        private int _stageIndexCount;

        // --- GAME BRIDGE & FBO ---
        private SRWeaverClient? _srWeaver;
        private DateTime _lastTrackingCheck = DateTime.MinValue;
        private DateTime _lastContextRecoveryAttemptUtc = DateTime.MinValue;
        private uint _fbo;
        private uint _textureColorBuffer;
        private uint _rboDepth;
        private int _lastWidth;
        private int _lastHeight;
        private Vector3 _currentEyeL = new Vector3(-30.0f, 0, 0);
        private Vector3 _currentEyeR = new Vector3(30.0f, 0, 0);
        private bool _isTrackingActive;
        private bool _windowEligibleFor3D;
        private bool _lensHintRequested;
        private Window? _hostWindow;
        private Vector3 _meshCenterLocal = Vector3.Zero;
        private Vector3 _meshBoundsMinLocal = new Vector3(-0.5f, -0.5f, -0.5f);
        private Vector3 _meshBoundsMaxLocal = new Vector3(0.5f, 0.5f, 0.5f);

        protected override unsafe void OnOpenGlInit(GlInterface gli)
        {
            _gl = GL.GetApi(gli.GetProcAddress);
            while (_gl.GetError() != GLEnum.NoError) ;
            CheckError("Init: Start");

            if (_gl == null) return;

            Console.WriteLine($"[GL] Version: {_gl.GetStringS(StringName.Version)}");
            Console.WriteLine($"[GL] Renderer: {_gl.GetStringS(StringName.Renderer)}");

            // Load shader sources from compile-time constants for AOT safety
            string vsSource = ShaderSources.MeshVertex;
            string fsSource = ShaderSources.MeshFragment;

            if (string.IsNullOrEmpty(vsSource) || string.IsNullOrEmpty(fsSource))
                throw new Exception("Failed to load shaders from resources.");

            _program = CreateShaderProgram(vsSource, fsSource);
            _gl.UseProgram(_program);

            _uModel = _gl.GetUniformLocation(_program, "uModel");
            _uView = _gl.GetUniformLocation(_program, "uView");
            _uProjection = _gl.GetUniformLocation(_program, "uProjection");
            _uTexture = _gl.GetUniformLocation(_program, "uTexture");
            _uHasTexture = _gl.GetUniformLocation(_program, "uHasTexture");
            _uBaseColorFactor = _gl.GetUniformLocation(_program, "uBaseColorFactor");
            _uAlphaMode = _gl.GetUniformLocation(_program, "uAlphaMode");
            _uAlphaCutoff = _gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uUseDitheredBlend = _gl.GetUniformLocation(_program, "uUseDitheredBlend");

            _defaultTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _defaultTexture);
            byte[] white = { 255, 255, 255, 255 };
            fixed (byte* p = white)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            _gl.Enable(EnableCap.DepthTest);
            CreateStageResources();
            LoadDefaultCube();

            _srWeaver = new SRWeaverClient();
            var tl = TopLevel.GetTopLevel(this);
            AttachWindowHooks(tl as Window);
            var windowHandle = tl?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _srWeaver.Initialize(3840, 2160, windowHandle);

            tl!.KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.S)
                {
                    SwapEyes = !SwapEyes;
                    _srWeaver.SetSwap(SwapEyes);
                    Console.WriteLine($"[SRViewer] SwapEyes: {SwapEyes}");
                }
            };

            DispatcherTimer.Run(() =>
            {
                bool needsRender = false;
                if (AutoRotate)
                {
                    _yaw += 0.02f;
                    needsRender = true;
                }

                // If tracking is active, we MUST render every frame to update the perspective
                if (_isTrackingActive)
                {
                    needsRender = true;
                }

                if (needsRender)
                {
                    RequestNextFrameRendering();
                }
                return true;
            }, TimeSpan.FromMilliseconds(16));
        }

        private void AttachWindowHooks(Window? window)
        {
            if (_hostWindow == window) return;
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
            if (_hostWindow == null) return;
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
            bool eligible = _hostWindow != null &&
                            _hostWindow.IsActive &&
                            _hostWindow.WindowState == WindowState.FullScreen;
            if (_windowEligibleFor3D == eligible) return;

            _windowEligibleFor3D = eligible;
            if (!eligible)
            {
                ApplyLensHint(false);
            }
            RequestNextFrameRendering();
        }

        private void ApplyLensHint(bool enabled)
        {
            if (_srWeaver?.IsInitialized != true)
            {
                _lensHintRequested = false;
                return;
            }

            if (_lensHintRequested == enabled) return;
            _srWeaver.SetLensHintEnabled(enabled);
            _lensHintRequested = enabled;
        }

        public void ResetCamera()
        {
            _yaw = 0; _pitch = 0; _moveX = 0; _moveY = 0; _moveZ = -2.0f; _scale = 1.0f;
            RequestNextFrameRendering();
        }

        public void LoadMesh(SimpleObjLoader.MeshData mesh, string baseDir)
        {
            lock (this)
            {
                _pendingMeshData = mesh;
                _pendingBaseDir = baseDir;
                _pendingGlbData = null;
            }
            RequestNextFrameRendering();
        }

        public void LoadGlb(GlbLoader.GlbMeshData glbData)
        {
            lock (this)
            {
                _pendingGlbData = glbData;
                _pendingMeshData = null;
            }
            RequestNextFrameRendering();
        }

        private unsafe void UploadMesh(SimpleObjLoader.MeshData mesh)
        {
            if (_gl == null) return;
            if (_vao == 0) _vao = _gl.GenVertexArray();
            if (_vbo == 0) _vbo = _gl.GenBuffer();
            if (_ebo == 0) _ebo = _gl.GenBuffer();

            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* pData = mesh.Vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Vertices.Length * sizeof(float)), pData, BufferUsageARB.StaticDraw);

            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            fixed (ushort* pInd = mesh.Indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(ushort)), pInd, BufferUsageARB.StaticDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 20, (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 20, (void*)12);
            _gl.EnableVertexAttribArray(1);

            _gl.BindVertexArray(0);
            _currentMeshData = mesh;
            var bounds = ComputeMeshBounds(mesh.Vertices);
            _meshCenterLocal = bounds.Center;
            _meshBoundsMinLocal = bounds.Min;
            _meshBoundsMaxLocal = bounds.Max;
            CheckError("UploadMesh: Done");
        }

        private readonly struct MeshBounds
        {
            public MeshBounds(Vector3 min, Vector3 max)
            {
                Min = min;
                Max = max;
                Center = (min + max) * 0.5f;
            }

            public Vector3 Min { get; }
            public Vector3 Max { get; }
            public Vector3 Center { get; }
        }

        private static MeshBounds ComputeMeshBounds(float[] vertices)
        {
            if (vertices == null || vertices.Length < 3)
            {
                return new MeshBounds(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f));
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i + 2 < vertices.Length; i += 5)
            {
                float x = vertices[i];
                float y = vertices[i + 1];
                float z = vertices[i + 2];

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }

            return new MeshBounds(
                new Vector3(minX, minY, minZ),
                new Vector3(maxX, maxY, maxZ));
        }

        private unsafe void CreateStageResources()
        {
            if (_gl == null) return;

            float[] vertices =
            {
                // Back face
                -0.5f, -0.5f, -0.5f, 0f, 0f,  0.5f, -0.5f, -0.5f, 4f, 0f,  0.5f, 0.5f, -0.5f, 4f, 4f,  -0.5f, 0.5f, -0.5f, 0f, 4f,
                // Left face
                -0.5f, -0.5f, 0.5f, 0f, 0f,  -0.5f, -0.5f, -0.5f, 4f, 0f,  -0.5f, 0.5f, -0.5f, 4f, 4f,  -0.5f, 0.5f, 0.5f, 0f, 4f,
                // Right face
                 0.5f, -0.5f, -0.5f, 0f, 0f,   0.5f, -0.5f, 0.5f, 4f, 0f,   0.5f, 0.5f, 0.5f, 4f, 4f,   0.5f, 0.5f, -0.5f, 0f, 4f,
                // Top face
                -0.5f, 0.5f, -0.5f, 0f, 0f,   0.5f, 0.5f, -0.5f, 4f, 0f,   0.5f, 0.5f, 0.5f, 4f, 4f,  -0.5f, 0.5f, 0.5f, 0f, 4f,
                // Bottom face
                -0.5f, -0.5f, 0.5f, 0f, 0f,   0.5f, -0.5f, 0.5f, 4f, 0f,   0.5f, -0.5f, -0.5f, 4f, 4f,  -0.5f, -0.5f, -0.5f, 0f, 4f
            };

            ushort[] indices =
            {
                0, 1, 2, 2, 3, 0,
                4, 5, 6, 6, 7, 4,
                8, 9, 10, 10, 11, 8,
                12, 13, 14, 14, 15, 12,
                16, 17, 18, 18, 19, 16
            };

            _stageIndexCount = indices.Length;
            _stageVao = _gl.GenVertexArray();
            _stageVbo = _gl.GenBuffer();
            _stageEbo = _gl.GenBuffer();

            _gl.BindVertexArray(_stageVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _stageVbo);
            fixed (float* pData = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), pData, BufferUsageARB.StaticDraw);

            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _stageEbo);
            fixed (ushort* pInd = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), pInd, BufferUsageARB.StaticDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 20, (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 20, (void*)12);
            _gl.EnableVertexAttribArray(1);

            _gl.BindVertexArray(0);

            _stageTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _stageTexture);

            const int textureSize = 128;
            const int checkerSize = 16;
            byte[] pixels = new byte[textureSize * textureSize * 4];
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    int tile = ((x / checkerSize) + (y / checkerSize)) & 1;
                    byte shade = tile == 0 ? (byte)62 : (byte)118;
                    int idx = (y * textureSize + x) * 4;
                    pixels[idx + 0] = shade;
                    pixels[idx + 1] = shade;
                    pixels[idx + 2] = shade;
                    pixels[idx + 3] = 255;
                }
            }

            fixed (byte* p = pixels)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, textureSize, textureSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        private void LoadDefaultCube()
        {
            float[] vertices = {
                -0.5f,-0.5f, 0.5f, 0,0,  0.5f,-0.5f, 0.5f, 1,0,  0.5f, 0.5f, 0.5f, 1,1, -0.5f, 0.5f, 0.5f, 0,1,
                -0.5f,-0.5f,-0.5f, 1,0, -0.5f, 0.5f,-0.5f, 1,1,  0.5f, 0.5f,-0.5f, 0,1,  0.5f,-0.5f,-0.5f, 0,0,
                -0.5f, 0.5f, 0.5f, 0,0,  0.5f, 0.5f, 0.5f, 1,0,  0.5f, 0.5f,-0.5f, 1,1, -0.5f, 0.5f,-0.5f, 0,1,
                -0.5f,-0.5f,-0.5f, 0,0,  0.5f,-0.5f,-0.5f, 1,0,  0.5f,-0.5f, 0.5f, 1,1, -0.5f,-0.5f, 0.5f, 0,1,
                 0.5f,-0.5f, 0.5f, 0,0,  0.5f,-0.5f,-0.5f, 1,0,  0.5f, 0.5f,-0.5f, 1,1,  0.5f, 0.5f, 0.5f, 0,1,
                -0.5f,-0.5f,-0.5f, 0,0, -0.5f,-0.5f, 0.5f, 1,0, -0.5f, 0.5f, 0.5f, 1,1, -0.5f, 0.5f,-0.5f, 0,1
            };
            ushort[] indices = {
                0,1,2, 2,3,0, 4,5,6, 6,7,4, 8,9,10, 10,11,8, 12,13,14, 14,15,12, 16,17,18, 18,19,16, 20,21,22, 22,23,20
            };
            var mesh = new SimpleObjLoader.MeshData
            {
                Vertices = vertices,
                Indices = indices,
                Parts = new List<SimpleObjLoader.MeshPart> { new SimpleObjLoader.MeshPart { StartIndex = 0, IndexCount = indices.Length, MaterialName = "default" } }
            };
            UploadMesh(mesh);
        }

        private void LoadTextures(SimpleObjLoader.MeshData mesh, string? baseDir, Dictionary<string, byte[]>? embedded = null, Dictionary<string, GlbLoader.MaterialInfo>? materialInfos = null)
        {
            if (_gl == null) return;
            foreach (var tex in _textures.Values) _gl.DeleteTexture(tex);
            _textures.Clear();
            _materialInfos.Clear();

            // Copy material infos if provided
            if (materialInfos != null)
            {
                foreach (var kvp in materialInfos)
                {
                    _materialInfos[kvp.Key] = kvp.Value;
                }
            }

            var mtlMapping = new Dictionary<string, string>();
            if (baseDir != null && !string.IsNullOrEmpty(mesh.MtlLib))
            {
                string mtlPath = Path.Combine(baseDir, mesh.MtlLib);
                Console.WriteLine($"[GL] Loading MTL: {mtlPath}");
                mtlMapping = MtlLoader.Load(mtlPath);
            }

            var meshMaterials = mesh.Parts.Select(p => p.MaterialName).Distinct().ToList();
            var loadedFiles = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            foreach (var matName in meshMaterials)
            {
                // Priority 1: Embedded
                if (embedded != null && embedded.TryGetValue(matName, out var bytes))
                {
                    uint texId = LoadTextureBytes(bytes);
                    if (texId != 0)
                    {
                        _textures[matName] = texId;
                        Console.WriteLine($"[GL] Loaded embedded texture for material {matName}");
                        continue;
                    }
                }

                // Priority 2: MTL / File
                if (baseDir != null && mtlMapping.TryGetValue(matName, out var texFile) && !string.IsNullOrEmpty(texFile))
                {
                    string fullPath = Path.Combine(baseDir, texFile);
                    if (File.Exists(fullPath))
                    {
                        if (!loadedFiles.TryGetValue(fullPath, out uint texId))
                        {
                            Console.WriteLine($"[GL] Loading Texture: {fullPath} for material {matName}");
                            texId = LoadTextureFile(fullPath);
                            if (texId != 0) loadedFiles[fullPath] = texId;
                        }

                        if (texId != 0) _textures[matName] = texId;
                    }
                    else
                    {
                        Console.WriteLine($"[GL] Texture file not found: {fullPath} for material {matName}");
                    }
                }
            }

            Console.WriteLine($"[GL] Texture mapping complete. Loaded {_textures.Count} textures for {meshMaterials.Count} materials.");
        }

        private void MapTexture(string file, params string[] materials)
        {
            Console.WriteLine($"[GL] Mapping texture '{file}' to materials: {string.Join(", ", materials)}");
            uint tex = LoadTextureFile(file);
            if (tex != 0) foreach (var mat in materials) _textures[mat] = tex;
            else Console.WriteLine($"[GL] Failed to load texture file for mapping: {file}");
        }

        private unsafe uint LoadTextureBytes(byte[] data)
        {
            if (_gl == null) return 0;
            try
            {
                using var image = Image.Load<Rgba32>(data);
                uint tex = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, tex);
                byte[] pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);
                fixed (byte* p = pixels)
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.GenerateMipmap(TextureTarget.Texture2D);
                return tex;
            }
            catch { return 0; }
        }

        private unsafe uint LoadTextureFile(string path)
        {
            if (_gl == null) return 0;
            try
            {
                using var image = Image.Load<Rgba32>(path);
                uint tex = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, tex);
                byte[] pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);
                fixed (byte* p = pixels)
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.GenerateMipmap(TextureTarget.Texture2D);
                return tex;
            }
            catch { return 0; }
        }

        private int _lastPWidth;
        private int _lastPHeight;

        protected override unsafe void OnOpenGlRender(GlInterface gli, int fb)
        {
            if (_gl == null) return;
            while (_gl.GetError() != GLEnum.NoError) ;

            if (_hostWindow == null)
            {
                AttachWindowHooks(TopLevel.GetTopLevel(this) as Window);
            }
            UpdateWindowEligibility();

            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            int pW = (int)Math.Max(1, Bounds.Width * scaling);
            int pH = (int)Math.Max(1, Bounds.Height * scaling);

            if (pW != _lastPWidth || pH != _lastPHeight)
            {
                _lastPWidth = pW; _lastPHeight = pH;
                Console.WriteLine($"[GL] Viewport: {pW}x{pH} (Logical: {Bounds.Width}x{Bounds.Height}, Scale: {scaling})");
            }

            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.StencilTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            if (_pendingMeshData != null || _pendingGlbData != null)
            {
                lock (this)
                {
                    if (_pendingMeshData != null)
                    {
                        LoadTextures(_pendingMeshData.Value, _pendingBaseDir!);
                        UploadMesh(_pendingMeshData.Value);
                        _pendingMeshData = null;
                        _pendingBaseDir = null;
                    }
                    else if (_pendingGlbData != null)
                    {
                        LoadTextures(_pendingGlbData.Value.MeshData, null, _pendingGlbData.Value.EmbeddedTextures, _pendingGlbData.Value.MaterialInfos);
                        UploadMesh(_pendingGlbData.Value.MeshData);
                        _pendingGlbData = null;
                    }
                }
            }

            int fboW = pW;
            int fboH = pH;
            EnsureFbo(fboW, fboH);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            // Poll tracking - ALWAYS poll as fast as possible for max responsiveness
            bool runtimeReadyForStereo = false;
            if (_srWeaver != null)
            {
                _srWeaver.PollInternalLogs();
                var runtimeState = _srWeaver.GetRuntimeState();
                if (!runtimeState.ContextValid &&
                    DateTime.UtcNow - _lastContextRecoveryAttemptUtc > TimeSpan.FromSeconds(2))
                {
                    _lastContextRecoveryAttemptUtc = DateTime.UtcNow;
                    if (_srWeaver.TryRecoverContext())
                    {
                        runtimeState = _srWeaver.GetRuntimeState();
                    }
                }

                runtimeReadyForStereo = runtimeState.SrAvailable && runtimeState.ContextValid;
                Vector3 eyeL = Vector3.Zero;
                Vector3 eyeR = Vector3.Zero;
                bool canUseTracking = runtimeReadyForStereo && _srWeaver.GetEyePositions(out eyeL, out eyeR);
                if (canUseTracking)
                {
                    // SDK uses +X Right, +Y Up, +Z Away (millimeters)
                    _currentEyeL = eyeL;
                    _currentEyeR = eyeR;
                    _isTrackingActive = true;
                }
                else
                {
                    _isTrackingActive = false;
                }
                _lastTrackingCheck = DateTime.Now;
            }

            bool shouldDisplayStereo = runtimeReadyForStereo && _windowEligibleFor3D;
            ApplyLensHint(shouldDisplayStereo);

            _gl.ClearColor(_backgroundColor.R / 255.0f, _backgroundColor.G / 255.0f, _backgroundColor.B / 255.0f, _backgroundColor.A / 255.0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_vao != 0 && _program != 0 && _currentMeshData != null)
            {
                _gl.UseProgram(_program);

                float meshScale = MathF.Max(0.01f, MathF.Abs(_scale));
                float meshCenterX = (_meshBoundsMinLocal.X + _meshBoundsMaxLocal.X) * 0.5f;
                float meshCenterY = (_meshBoundsMinLocal.Y + _meshBoundsMaxLocal.Y) * 0.5f;
                float meshCenterZ = (_meshBoundsMinLocal.Z + _meshBoundsMaxLocal.Z) * 0.5f;
                var userTranslation = Matrix4x4.CreateTranslation(_moveX, _moveY, _moveZ);

                // Keep object floating at the exact center of the stage.
                var model =
                    Matrix4x4.CreateTranslation(-meshCenterX, -meshCenterY, -meshCenterZ) *
                    Matrix4x4.CreateScale(meshScale) *
                    Matrix4x4.CreateRotationY(_yaw) *
                    Matrix4x4.CreateRotationX(_pitch) *
                    userTranslation;

                Vector3 meshSize = (_meshBoundsMaxLocal - _meshBoundsMinLocal) * meshScale;
                // Use bounding-sphere diameter with extra margin so any rotation stays inside.
                float stageSide = MathF.Max(1.2f, meshSize.Length() * 1.30f);
                var stageModel =
                    Matrix4x4.CreateScale(stageSide, stageSide, stageSide) *
                    userTranslation;

                // --- SBS Logic ---
                // For Anamorphic SBS, we use the FULL aspect ratio (e.g. 16:9) 
                // but render into a half-width viewport (8:9). 
                // This "squeezes" the image horizontally so it looks correct 
                // when the 3D display expands it back.
                // --- OFF-AXIS PROJECTION PARALLAX SYSTEM ---
                // This implements the "Window into 3D World" effect (Fish Tank VR).
                // 1. Camera moves to head position (Translation ONLY, no rotation)
                // 2. Projection frustum is sheared so the screen plane remains fixed

                // Base parameters
                float dist = 3.0f; // Distance from camera to object/screen center
                float near = 0.1f;
                float far = 1000.0f;
                float fovY = MathF.PI / 4.0f; // 45 degrees
                float aspect = (float)fboW / fboH;
                float tanHalfY = MathF.Tan(fovY / 2.0f);
                float tanHalfX = tanHalfY * aspect;

                // Base camera position from head tracking
                // Note: We use a larger scale here as requested by user to make effect visible
                float headX = 0, headY = 0;

                // If SwapEyes is true, we put the RIGHT eye view in the LEFT viewport
                Vector3 eyeL = SwapEyes ? _currentEyeR : _currentEyeL;
                Vector3 eyeR = SwapEyes ? _currentEyeL : _currentEyeR;

                if (_isTrackingActive && _parallaxIntensity > 0.001f)
                {
                    // Average eye position
                    float rawHeadX = (eyeL.X + eyeR.X) * 0.5f;
                    float rawHeadY = (eyeL.Y + eyeR.Y) * 0.5f;

                    // Scale for visual intensity
                    headX = rawHeadX * 0.01f * _parallaxIntensity;
                    headY = rawHeadY * 0.01f * _parallaxIntensity;

                    // Debug log
                    if (DateTime.Now.Millisecond % 500 < 16)
                    {
                        System.Diagnostics.Trace.WriteLine($"[Parallax] Head: ({headX:F2}, {headY:F2}), Dist: {dist}");
                    }
                }
                else
                {
                    if (DateTime.Now.Millisecond % 1000 < 16) // ~1 time per second
                    {
                        System.Diagnostics.Trace.WriteLine($"[Parallax] DISABLED - Tracking: {_isTrackingActive}, Intensity: {_parallaxIntensity:F2}");
                    }
                }

                _gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                Vector3 focusPoint = Vector3.Transform(_meshCenterLocal, model);
                // Keep manual mouse pan (MoveX/MoveY) independent from head-tracking frustum anchoring.
                // Otherwise panning gets interpreted as frustum shear and looks like distortion.
                float trackingAnchorX = focusPoint.X - _moveX;
                float trackingAnchorY = focusPoint.Y - _moveY;
                float trackingAnchorZ = focusPoint.Z;

                float halfW = fboW / 2.0f;
                float height = fboH;

                // DEPTH ATTENUATION FOR DISTANT OBJECTS
                // Object is at _moveZ (negative) relative to screen plane (0).
                // We want separation to decrease as object gets further away to prevent divergence.
                float distFromScreen = MathF.Max(0.0f, -trackingAnchorZ);
                float depthFactor = 1.0f;

                if (distFromScreen > 0)
                {
                    // Soft attenuation: 1 / (1 + 0.5 * dist)
                    // At 2m away, stereo effect is halved.
                    depthFactor = 1.0f / (1.0f + distFromScreen * 0.5f);
                }

                float effectiveSeparation = _stereoSeparation * depthFactor;
                float effectiveConvergence = _stereoConvergence * depthFactor;

                _gl.Viewport(0, 0, (uint)halfW, (uint)height);

                // --- LEFT EYE ---
                {
                    // 1. Calculate Eye Position in World Space
                    // Start with head position + stereo offset
                    // CORRECTION: Left Eye is usually at -X, Right Eye at +X
                    float eyePosX = headX - (effectiveSeparation / 2.0f);

                    // Only apply tracked IPD adjustment if parallax is active
                    if (_isTrackingActive && _parallaxIntensity > 0.0f)
                    {
                        eyePosX += (-eyeL.X * 0.001f);
                    }

                    float eyePosY = headY;
                    float eyePosZ = dist; // Camera is at +Z looking at -Z

                    // 2. View Matrix: Move world so eye is at origin (Inverse Translation)
                    var view = Matrix4x4.CreateTranslation(-eyePosX, -eyePosY, -eyePosZ);

                    // 3. Projection Matrix: Off-Axis Frustum
                    // Keep tracking anchored to the current object center instead of a fixed world origin.
                    float distToFocus = MathF.Max(near + 0.01f, eyePosZ - trackingAnchorZ);
                    float centerShiftX = (trackingAnchorX - eyePosX) * near / distToFocus;
                    float centerShiftY = (trackingAnchorY - eyePosY) * near / distToFocus;

                    float left = -near * tanHalfX + centerShiftX;
                    float right = near * tanHalfX + centerShiftX;
                    float bottom = -near * tanHalfY + centerShiftY;
                    float top = near * tanHalfY + centerShiftY;

                    var proj = CreatePerspectiveOffCenter(left, right, bottom, top, near, far);

                    // 4. Apply Stereo Convergence (Popout)
                    // Positive convergence brings objects closer (Cross-eye)
                    proj.M31 += effectiveConvergence;

                    _gl.UniformMatrix4(_uProjection, 1, false, (float*)&proj);
                    _gl.UniformMatrix4(_uView, 1, false, (float*)&view);
                    if (_showStage) DrawStage(stageModel);
                    _gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                    DrawMeshParts();
                }

                // --- RIGHT EYE ---
                _gl.Viewport((int)halfW, 0, (uint)halfW, (uint)height);
                {
                    // 1. Calculate Eye Position
                    // CORRECTION: Right Eye is at +X
                    float eyePosX = headX + (effectiveSeparation / 2.0f);

                    // Only apply tracked IPD adjustment if parallax is active
                    if (_isTrackingActive && _parallaxIntensity > 0.0f)
                    {
                        eyePosX += (-eyeR.X * 0.001f);
                    }

                    float eyePosY = headY;
                    float eyePosZ = dist;

                    // 2. View Matrix
                    var view = Matrix4x4.CreateTranslation(-eyePosX, -eyePosY, -eyePosZ);

                    // 3. Projection Matrix
                    float distToFocus = MathF.Max(near + 0.01f, eyePosZ - trackingAnchorZ);
                    float centerShiftX = (trackingAnchorX - eyePosX) * near / distToFocus;
                    float centerShiftY = (trackingAnchorY - eyePosY) * near / distToFocus;

                    float left = -near * tanHalfX + centerShiftX;
                    float right = near * tanHalfX + centerShiftX;
                    float bottom = -near * tanHalfY + centerShiftY;
                    float top = near * tanHalfY + centerShiftY;

                    var proj = CreatePerspectiveOffCenter(left, right, bottom, top, near, far);

                    // 4. Apply Stereo Convergence (Popout)
                    proj.M31 -= effectiveConvergence;

                    _gl.UniformMatrix4(_uProjection, 1, false, (float*)&proj);
                    _gl.UniformMatrix4(_uView, 1, false, (float*)&view);
                    if (_showStage) DrawStage(stageModel);
                    _gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                    DrawMeshParts();
                }

                _gl.BindVertexArray(0);

                // Blit to target Framebuffer
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
                _gl.Viewport(0, 0, (uint)pW, (uint)pH);
                _gl.Disable(EnableCap.ScissorTest);

                // Draw logic for blit...
                _gl.ClearColor(0, 0, 0, 1);
                _gl.Clear(ClearBufferMask.ColorBufferBit);

                bool weavingSucceeded = false;
                if (shouldDisplayStereo && _srWeaver != null && _srWeaver.IsInitialized)
                {
                    try { _srWeaver.Weave(_textureColorBuffer); weavingSucceeded = true; } catch { }
                }

                if (!weavingSucceeded)
                {
                    BlitMonoFromLeftEye(fb, fboW, fboH, pW, pH);
                }
                CheckError("Render: End");
            }
        }

        private void BlitMonoFromLeftEye(int outputFramebuffer, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
        {
            if (_gl == null) return;

            int halfWidth = Math.Max(1, sourceWidth / 2);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)outputFramebuffer);
            _gl.BlitFramebuffer(0, 0, halfWidth, sourceHeight, 0, 0, outputWidth, outputHeight, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        }

        private unsafe void DrawStage(Matrix4x4 stageModel)
        {
            if (_gl == null || _stageVao == 0 || _stageTexture == 0 || _stageIndexCount <= 0) return;

            _gl.UniformMatrix4(_uModel, 1, false, (float*)&stageModel);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _stageTexture);
            _gl.Uniform1(_uTexture, 0);
            _gl.Uniform1(_uHasTexture, 1);
            _gl.Uniform4(_uBaseColorFactor, 1.0f, 1.0f, 1.0f, 1.0f);
            _gl.Uniform1(_uAlphaMode, 0);
            _gl.Uniform1(_uAlphaCutoff, 0.5f);
            _gl.Uniform1(_uUseDitheredBlend, 0);
            _gl.DepthMask(true);

            _gl.BindVertexArray(_stageVao);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_stageIndexCount, DrawElementsType.UnsignedShort, (void*)0);
            _gl.BindVertexArray(0);
        }

        private unsafe void DrawMeshParts()
        {
            if (_gl == null || _currentMeshData == null) return;
            _gl.BindVertexArray(_vao);
            bool ditheredBlend = _useDitheredBlend;

            // Two-pass rendering for correct transparency:
            // Pass 1: Render OPAQUE and MASK materials with depth write ON
            // Pass 2: Render BLEND materials with depth write OFF

            // Pass 1: Opaque objects
            _gl.DepthMask(true);
            foreach (var part in _currentMeshData.Value.Parts)
            {
                // Get material info to check alpha mode
                int alphaMode = 0; // Default OPAQUE
                if (part.MaterialName != null && _materialInfos.TryGetValue(part.MaterialName, out var matInfo))
                {
                    alphaMode = matInfo.AlphaMode switch
                    {
                        "BLEND" => 1,
                        "MASK" => 2,
                        _ => 0
                    };
                }

                // Skip BLEND materials in this pass unless dithered mode is active
                if (alphaMode == 1 && !ditheredBlend) continue;

                DrawMeshPart(part);
            }

            if (!ditheredBlend)
            {
                // Pass 2: Transparent objects (BLEND mode)
                // Disable depth write so transparent pixels don't block objects behind them
                _gl.DepthMask(false);
                foreach (var part in _currentMeshData.Value.Parts)
                {
                    // Get material info to check alpha mode
                    int alphaMode = 0;
                    if (part.MaterialName != null && _materialInfos.TryGetValue(part.MaterialName, out var matInfo))
                    {
                        alphaMode = matInfo.AlphaMode switch
                        {
                            "BLEND" => 1,
                            "MASK" => 2,
                            _ => 0
                        };
                    }

                    // Only render BLEND materials in this pass
                    if (alphaMode != 1) continue;

                    DrawMeshPart(part);
                }
            }

            // Restore depth write for next frame
            _gl.DepthMask(true);
            _gl.BindVertexArray(0);
        }

        private unsafe void DrawMeshPart(SimpleObjLoader.MeshPart part)
        {
            if (_gl == null) return;

            uint tex = _defaultTexture; bool hasT = false;
            if (part.MaterialName != null && _textures.TryGetValue(part.MaterialName, out uint mT)) { tex = mT; hasT = true; }
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.Uniform1(_uTexture, 0);
            _gl.Uniform1(_uHasTexture, hasT ? 1 : 0);

            // Set material properties
            Vector4 baseColor = new Vector4(1, 1, 1, 1);
            int alphaMode = 0; // 0=OPAQUE, 1=BLEND, 2=MASK
            float alphaCutoff = 0.5f;

            if (part.MaterialName != null && _materialInfos.TryGetValue(part.MaterialName, out var matInfo))
            {
                baseColor = matInfo.BaseColorFactor;
                alphaMode = matInfo.AlphaMode switch
                {
                    "BLEND" => 1,
                    "MASK" => 2,
                    _ => 0 // OPAQUE
                };
                alphaCutoff = matInfo.AlphaCutoff;
            }

            _gl.Uniform4(_uBaseColorFactor, baseColor.X, baseColor.Y, baseColor.Z, baseColor.W);
            _gl.Uniform1(_uAlphaMode, alphaMode);
            _gl.Uniform1(_uAlphaCutoff, alphaCutoff);
            _gl.Uniform1(_uUseDitheredBlend, _useDitheredBlend ? 1 : 0);

            _gl.DrawElements(PrimitiveType.Triangles, (uint)part.IndexCount, DrawElementsType.UnsignedShort, (void*)(part.StartIndex * 2));
        }

        // Helper for Off-Axis Projection
        private static Matrix4x4 CreatePerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
        {
            float x = (2.0f * near) / (right - left);
            float y = (2.0f * near) / (top - bottom);
            float a = (right + left) / (right - left);
            float b = (top + bottom) / (top - bottom);
            float c = -(far + near) / (far - near);
            float d = -(2.0f * far * near) / (far - near);

            return new Matrix4x4(
                x, 0, 0, 0,
                0, y, 0, 0,
                a, b, c, -1,
                0, 0, d, 0
            );
        }

        private unsafe void EnsureFbo(int w, int h)
        {
            if (_gl == null || (_fbo != 0 && w == _lastWidth && h == _lastHeight)) return;
            _lastWidth = w; _lastHeight = h;
            if (_fbo == 0) _fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            if (_textureColorBuffer == 0) _textureColorBuffer = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _textureColorBuffer);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _textureColorBuffer, 0);
            if (_rboDepth == 0) _rboDepth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _rboDepth);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _rboDepth);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void CheckError(string ctx = "")
        {
            if (_gl == null) return;
            GLEnum err;
            while ((err = _gl.GetError()) != GLEnum.NoError) Console.WriteLine($"[GL Error] {err} | Context: {ctx}");
        }

        private unsafe uint CreateShaderProgram(string vs, string fs)
        {
            if (_gl == null) return 0;
            uint Compile(ShaderType t, string s)
            {
                uint sh = _gl.CreateShader(t); _gl.ShaderSource(sh, s); _gl.CompileShader(sh);
                string log = _gl.GetShaderInfoLog(sh); if (!string.IsNullOrEmpty(log)) Console.WriteLine($"[GL] Shader {t} Log: {log}");
                return sh;
            }
            uint v = Compile(ShaderType.VertexShader, vs); uint f = Compile(ShaderType.FragmentShader, fs);
            uint p = _gl.CreateProgram(); _gl.AttachShader(p, v); _gl.AttachShader(p, f); _gl.LinkProgram(p);
            string pLog = _gl.GetProgramInfoLog(p); if (!string.IsNullOrEmpty(pLog)) Console.WriteLine($"[GL] Program Log: {pLog}");
            _gl.DeleteShader(v); _gl.DeleteShader(f); return p;
        }

        protected override void OnOpenGlDeinit(GlInterface gli)
        {
            ApplyLensHint(false);
            _srWeaver?.Dispose();
            _srWeaver = null;
            DetachWindowHooks();
            if (_gl != null)
            {
                foreach (var tex in _textures.Values) _gl.DeleteTexture(tex);
                _gl.DeleteTexture(_defaultTexture); _gl.DeleteProgram(_program); _gl.DeleteVertexArray(_vao);
                _gl.DeleteBuffer(_vbo); _gl.DeleteBuffer(_ebo); _gl.DeleteFramebuffer(_fbo);
                _gl.DeleteTexture(_textureColorBuffer); _gl.DeleteRenderbuffer(_rboDepth);
                _gl.DeleteTexture(_stageTexture);
                _gl.DeleteVertexArray(_stageVao);
                _gl.DeleteBuffer(_stageVbo);
                _gl.DeleteBuffer(_stageEbo);
            }
            base.OnOpenGlDeinit(gli);
        }
    }
}
