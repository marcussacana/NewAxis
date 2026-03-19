using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace NewAxis.Services
{
    public static class LibMpv
    {
        private const string DllName = "libmpv-2.dll";
        private static readonly string DllPath = Path.Combine(AppContext.BaseDirectory, DllName);
        private static bool _resolverInstalled;

        static LibMpv()
        {
            TryInstallResolver();
        }

        private static void TryInstallResolver()
        {
            if (_resolverInstalled)
            {
                return;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, ResolveLibMpv);
                _resolverInstalled = true;
            }
            catch (InvalidOperationException)
            {
                _resolverInstalled = true;
            }
        }

        private static IntPtr ResolveLibMpv(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }

            if (File.Exists(DllPath) && NativeLibrary.TryLoad(DllPath, out IntPtr handle))
            {
                return handle;
            }

            return IntPtr.Zero;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr mpvHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr mpvHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_set_option_string(IntPtr mpvHandle, string name, string data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_command(IntPtr mpvHandle, IntPtr args);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_set_property_string(IntPtr mpvHandle, string name, string data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr mpv_get_property_string(IntPtr mpvHandle, string name);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_wait_event(IntPtr mpvHandle, double timeout);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_request_log_messages(IntPtr mpvHandle, string min_level);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(IntPtr mpvHandle, string name, mpv_format format, out double data);

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_event
        {
            public mpv_event_id event_id;
            public int error;
            public ulong reply_userdata;
            public IntPtr data;
        }

        public enum mpv_event_id
        {
            NONE = 0,
            LOG_MESSAGE = 2,
            FILE_LOADED = 8,
            VIDEO_RECONFIG = 21
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_event_log_message
        {
            public string prefix;
            public string level;
            public string text;
            public uint log_level;
        }

        public enum mpv_format
        {
            DOUBLE = 5
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, IntPtr params_ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_render_context_set_update_callback(IntPtr ctx, mpv_render_update_fn callback, IntPtr callback_ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_render(IntPtr ctx, IntPtr params_ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_render_video_only(IntPtr ctx, IntPtr params_ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_render_subtitles(IntPtr ctx, IntPtr params_ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_render_context_free(IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void mpv_render_update_fn(IntPtr cb_ctx);

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_render_param
        {
            public mpv_render_param_type type;
            public IntPtr data;
        }

        public enum mpv_render_param_type
        {
            INVALID = 0,
            API_TYPE = 1,
            OPENGL_INIT_PARAMS = 2,
            FORWARD_TARGET = 3,
            FLIP_Y = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_opengl_init_params
        {
            public get_proc_address_fn get_proc_address;
            public IntPtr get_proc_address_ctx;
            public IntPtr extra_exts;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr get_proc_address_fn(IntPtr ctx, string name);

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_opengl_fbo
        {
            public int fbo;
            public int w;
            public int h;
            public int internal_format;
        }

        public static int ExecuteCommand(IntPtr mpvHandle, params string[] args)
        {
            var pointers = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    pointers[i] = StringToUtf8HGlobal(args[i]);
                }

                pointers[args.Length] = IntPtr.Zero;

                var argPtr = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
                try
                {
                    Marshal.Copy(pointers, 0, argPtr, pointers.Length);
                    int rc = mpv_command(mpvHandle, argPtr);
                    Trace.Write("LibMpv", $"mpv_command({string.Join(" ", args)}) => {rc}");
                    return rc;
                }
                finally
                {
                    Marshal.FreeHGlobal(argPtr);
                }
            }
            finally
            {
                foreach (var ptr in pointers)
                {
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                }
            }
        }

        private static IntPtr StringToUtf8HGlobal(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + '\0');
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }
    }

    public class MpvContext : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public IntPtr Handle => _handle;

        public void Initialize(params string[] options)
        {
            if (_handle != IntPtr.Zero)
                throw new InvalidOperationException("MPV already initialized");

            _handle = LibMpv.mpv_create();
            if (_handle == IntPtr.Zero)
                throw new Exception("Failed to create MPV instance");
            Trace.Write("MpvContext", $"mpv_create ok handle=0x{_handle.ToInt64():X}");

            foreach (var opt in options)
            {
                if (!opt.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = opt.Substring(2).Split('=', 2);
                var name = parts[0];
                var value = parts.Length > 1 ? parts[1] : "yes";
                int rc = LibMpv.mpv_set_option_string(_handle, name, value);
                Trace.Write("MpvContext", $"set_option {name}={value} => {rc}");
            }

            int error = LibMpv.mpv_initialize(_handle);
            if (error < 0)
            {
                LibMpv.mpv_terminate_destroy(_handle);
                _handle = IntPtr.Zero;
                throw new Exception($"Failed to initialize MPV: {error}");
            }
            Trace.Write("MpvContext", "mpv_initialize ok.");

            LibMpv.mpv_request_log_messages(_handle, Program.LogEnabled ? "warn" : "no");
        }

        public event Action? FileLoaded;

        public void PollEvents()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            while (true)
            {
                IntPtr eventPtr = LibMpv.mpv_wait_event(_handle, 0);
                var mpvEvent = Marshal.PtrToStructure<LibMpv.mpv_event>(eventPtr);
                if (mpvEvent.event_id == LibMpv.mpv_event_id.NONE)
                {
                    break;
                }
                if (Program.LogEnabled)
                {
                    Trace.Write("MpvContext", $"event id={mpvEvent.event_id} error={mpvEvent.error}");
                }

                if (mpvEvent.event_id == LibMpv.mpv_event_id.FILE_LOADED)
                {
                    FileLoaded?.Invoke();
                }
            }
        }

        public void LoadFile(string path)
        {
            if (_handle == IntPtr.Zero)
            {
                Trace.Write("MpvContext", $"LoadFile skipped (MPV not initialized). path={path}");
                return;
            }
            Trace.Write("MpvContext", $"LoadFile path={path}");
            LibMpv.ExecuteCommand(_handle, "loadfile", path);
        }

        public void Command(params string[] args)
        {
            if (_handle == IntPtr.Zero)
            {
                Trace.Write("MpvContext", $"Command skipped (MPV not initialized). args={string.Join(" ", args)}");
                return;
            }
            LibMpv.ExecuteCommand(_handle, args);
        }

        public void SetProperty(string name, string value)
        {
            if (_handle == IntPtr.Zero)
            {
                Trace.Write("MpvContext", $"SetProperty skipped (MPV not initialized). {name}={value}");
                return;
            }
            int rc = LibMpv.mpv_set_property_string(_handle, name, value);
            if (rc < 0)
            {
                Trace.Write("MpvContext", $"SetProperty {name}={value} => {rc}");
            }
        }

        public double GetPropertyDouble(string name)
        {
            if (_handle == IntPtr.Zero) return 0;
            int rc = LibMpv.mpv_get_property(_handle, name, LibMpv.mpv_format.DOUBLE, out double value);
            if (rc < 0)
            {
                return 0;
            }
            return value;
        }

        public string? GetPropertyString(string name)
        {
            if (_handle == IntPtr.Zero) return null;

            IntPtr ptr = LibMpv.mpv_get_property_string(_handle, name);
            if (ptr == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringAnsi(ptr);
            }
            finally
            {
                LibMpv.mpv_free(ptr);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            if (_handle != IntPtr.Zero)
            {
                LibMpv.mpv_terminate_destroy(_handle);
                _handle = IntPtr.Zero;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~MpvContext()
        {
            Dispose();
        }
    }
}

