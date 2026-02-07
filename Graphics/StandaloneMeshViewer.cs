using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NewAxis.Services;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace NewAxis.Graphics
{
    public static class StandaloneMeshViewer
    {
        private static IWindow? _window;
        private static GL? _gl;
        private static IInputContext? _input;

        private static uint _program;
        private static uint _vao;
        private static uint _vbo;
        private static uint _ebo;

        private static int _uModel;
        private static int _uView;
        private static int _uProjection;

        // --- State ---
        private static float _yaw;
        private static float _pitch;
        private static int _indexCount;

        // --- Default Cube Data ---
        private static float[] _vertices = {
            // Front face
            -0.5f, -0.5f,  0.5f,
             0.5f, -0.5f,  0.5f,
             0.5f,  0.5f,  0.5f,
            -0.5f,  0.5f,  0.5f,
            // Back face
            -0.5f, -0.5f, -0.5f,
            -0.5f,  0.5f, -0.5f,
             0.5f,  0.5f, -0.5f,
             0.5f, -0.5f, -0.5f,
        };

        private static ushort[] _indices = {
            // Front
            0, 1, 2, 2, 3, 0,
            // Right
            1, 7, 6, 6, 2, 1,
            // Back
            7, 4, 5, 5, 6, 7,
            // Left
            4, 0, 3, 3, 5, 4,
            // Top
            3, 2, 6, 6, 5, 3,
            // Bottom
            4, 7, 1, 1, 0, 4
        };

        public static void Run()
        {
            var options = WindowOptions.Default;
            options.Size = new Silk.NET.Maths.Vector2D<int>(1280, 720);
            options.Title = "NewAxis Standalone 3D Viewer (SBS)";
            options.VSync = true;
            options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3));

            _window = Window.Create(options);

            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Update += OnUpdate;
            _window.Closing += OnClose;

            _window.Run();
        }

        private static unsafe void OnLoad()
        {
            _input = _window!.CreateInput();
            foreach (var keyboard in _input.Keyboards)
            {
                keyboard.KeyDown += KeyDown;
            }

            _gl = GL.GetApi(_window);

            // Shaders
            string vsSource = ShaderSources.MeshVertex;
            string fsSource = ShaderSources.MeshFragment;

            _program = CreateShaderProgram(vsSource, fsSource);

            _uModel = _gl.GetUniformLocation(_program, "uModel");
            _uView = _gl.GetUniformLocation(_program, "uView");
            _uProjection = _gl.GetUniformLocation(_program, "uProjection");

            // Initial Upload
            UploadMesh(_vertices, _indices);

            _gl.Enable(EnableCap.DepthTest);
        }

        private static unsafe void UploadMesh(float[] vertices, ushort[] indices)
        {
            if (_gl == null) return;

            // Cleanup old buffers if they exist (to support reloading)
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
            if (_vbo != 0) _gl.DeleteBuffer(_vbo);
            if (_ebo != 0) _gl.DeleteBuffer(_ebo);

            _indexCount = indices.Length;

            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);

            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            fixed (float* buf = vertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), buf, BufferUsageARB.StaticDraw);
            }

            _ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

            fixed (ushort* buf = indices)
            {
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), buf, BufferUsageARB.StaticDraw);
            }

            // Attribs (Index 0 for aPos)
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);
            _gl.EnableVertexAttribArray(0);

            _gl.BindVertexArray(0);
        }

        private static void OnUpdate(double delta)
        {
            _yaw += (float)delta * 0.8f;
            _pitch += (float)delta * 0.4f;
        }

        private static unsafe void OnRender(double delta)
        {
            var gl = _gl!;
            var windowSize = _window!.Size;

            gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_vao == 0) return;

            gl.UseProgram(_program);
            gl.BindVertexArray(_vao);

            // --- Matrix Logic ---
            var model = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0) * Matrix4x4.CreateTranslation(0, 0, -3.0f);

            // SBS Setup
            float halfWidth = (float)windowSize.X / 2.0f;
            float height = (float)windowSize.Y;

            float aspect = (float)windowSize.X / (float)windowSize.Y;

            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4.0f, aspect, 0.1f, 100.0f);

            // Upload Projection
            gl.UniformMatrix4(_uProjection, 1, false, (float*)&projection);
            // Upload Model
            gl.UniformMatrix4(_uModel, 1, false, (float*)&model);

            // --- LEFT EYE ---
            gl.Viewport(0, 0, (uint)halfWidth, (uint)height);

            // View Matrix Left (-0.03 offset = Camera Right)
            // standard stereo:
            // Interocular distance ~6.5cm. 
            // Left Eye is at -IPD/2.
            var viewLeft = Matrix4x4.CreateTranslation(0.03f, 0, 0);
            gl.UniformMatrix4(_uView, 1, false, (float*)&viewLeft);

            gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedShort, null);

            // --- RIGHT EYE ---
            gl.Viewport((int)halfWidth, 0, (uint)halfWidth, (uint)height);

            // View Matrix Right (+IPD/2)
            var viewRight = Matrix4x4.CreateTranslation(-0.03f, 0, 0);
            gl.UniformMatrix4(_uView, 1, false, (float*)&viewRight);

            gl.DrawElements(PrimitiveType.Triangles, (uint)_indexCount, DrawElementsType.UnsignedShort, null);

            gl.BindVertexArray(0);
        }

        private static void OnClose()
        {
            _gl?.DeleteBuffer(_vbo);
            _gl?.DeleteBuffer(_ebo);
            _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteProgram(_program);
        }

        private static void KeyDown(IKeyboard arg1, Key arg2, int arg3)
        {
            if (arg2 == Key.Escape)
            {
                _window?.Close();
            }

            if (arg2 == Key.F11)
            {
                if (_window!.WindowState == WindowState.Fullscreen)
                    _window.WindowState = WindowState.Normal;
                else
                    _window.WindowState = WindowState.Fullscreen;
            }

            if (arg2 == Key.O && arg1.IsKeyPressed(Key.ControlLeft))
            {
                LoadObjFile();
            }
        }

        private static void LoadObjFile()
        {
            // Scan current directory for first .obj
            var file = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.obj").FirstOrDefault();
            if (file != null)
            {
                try
                {
                    Console.WriteLine($"Loading {file}...");
                    var mesh = SimpleObjLoader.Load(file);
                    if (mesh.Vertices != null)
                    {
                        UploadMesh(mesh.Vertices, mesh.Indices);
                        Console.WriteLine($"Loaded {mesh.Vertices.Length / 3} vertices.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Err loading obj: {ex}");
                }
            }
            else
            {
                Console.WriteLine("No .obj file found in current directory.");
            }
        }

        private static uint CreateShaderProgram(string vsSource, string fsSource)
        {
            var gl = _gl!;
            uint vs = gl.CreateShader(ShaderType.VertexShader);
            gl.ShaderSource(vs, vsSource);
            gl.CompileShader(vs);
            CheckShader(vs);

            uint fs = gl.CreateShader(ShaderType.FragmentShader);
            gl.ShaderSource(fs, fsSource);
            gl.CompileShader(fs);
            CheckShader(fs);

            uint prog = gl.CreateProgram();
            gl.AttachShader(prog, vs);
            gl.AttachShader(prog, fs);
            gl.LinkProgram(prog);
            CheckProgram(prog);

            gl.DeleteShader(vs);
            gl.DeleteShader(fs);

            return prog;
        }

        private static void CheckShader(uint shader)
        {
            var gl = _gl!;
            string infoLog = gl.GetShaderInfoLog(shader);
            if (!string.IsNullOrWhiteSpace(infoLog))
            {
                Console.WriteLine($"Shader Error: {infoLog}");
            }
        }

        private static void CheckProgram(uint prog)
        {
            var gl = _gl!;
            string infoLog = gl.GetProgramInfoLog(prog);
            if (!string.IsNullOrWhiteSpace(infoLog))
            {
                Console.WriteLine($"Program Error: {infoLog}");
            }
        }
    }
}
