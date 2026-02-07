using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NewAxis.Services
{
    /// <summary>
    /// Client wrapper for Leia3DBridge.dll
    /// Provides weaving/interlacing functionality for Leia 3D displays
    /// </summary>
    public class SRWeaverClient : IDisposable
    {
        private const string DllName = "Leia3DBridge.dll";

        private bool _initialized;
        public bool IsInitialized => _initialized;
        private IntPtr _windowHandle;
        private int _width;
        private int _height;
        private bool _extendedApiAvailable = true;

        public readonly struct RuntimeState
        {
            public RuntimeState(bool srAvailable, bool contextValid, bool lensEnabled, bool extendedApiAvailable)
            {
                SrAvailable = srAvailable;
                ContextValid = contextValid;
                LensEnabled = lensEnabled;
                ExtendedApiAvailable = extendedApiAvailable;
            }

            public bool SrAvailable { get; }
            public bool ContextValid { get; }
            public bool LensEnabled { get; }
            public bool ExtendedApiAvailable { get; }
        }

        #region P/Invoke Declarations

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool InitializeLeia(int width, int height, IntPtr windowHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Weave(uint textureId, int width, int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DeinitializeLeia();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void GetRecommendedResolution(out int width, out int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetTrackingEnabled(bool enable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetLatency(int frames);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetSwapEyes(bool swap);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetLensHint(bool enable);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsSRAvailable();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsContextValid();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "IsLensEnabledState")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool IsLensEnabledState();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool RecoverContext();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool GetPredictedEyePositions(float[] left, float[] right);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetBridgeVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool GetLastLogMessage(StringBuilder outBuffer, int maxSize);

        #endregion

        /// <summary>
        /// Initialize the SR Weaver with the specified dimensions and window handle
        /// </summary>
        public bool Initialize(uint width, uint height, IntPtr windowHandle)
        {
            try
            {
                _windowHandle = windowHandle;
                _width = (int)width;
                _height = (int)height;

                // Call the bridge initialization
                bool result = InitializeLeia(_width, _height, _windowHandle);

                // Check version
                try
                {
                    IntPtr verPtr = GetBridgeVersion();
                    string version = Marshal.PtrToStringAnsi(verPtr) ?? "Unknown";
                    Console.WriteLine($"[SRWeaver] Bridge Version: {version}");
                }
                catch
                {
                    Console.WriteLine("[SRWeaver] Failed to get bridge version (might be old DLL)");
                }
                PollInternalLogs();

                if (result)
                {
                    _initialized = true;

                    // Enable head tracking and set latency
                    SetTrackingEnabled(true);
                    SetLatency(1); // 1 frame latency for optimal response

                    Console.WriteLine("[SRWeaver] Initialization complete (Tracking Enabled)");
                    PollInternalLogs();
                    return true;
                }
                else
                {
                    Console.WriteLine("[SRWeaver] Failed to initialize Leia SDK");
                }
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"[SRWeaver] {DllName} or dependencies not found: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[SRWeaver] Inner: {ex.InnerException.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SRWeaver] Failed to initialize: {ex.GetType().Name} - {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[SRWeaver] Inner: {ex.InnerException.Message}");
            }
            return false;
        }

        public void PollInternalLogs()
        {
            try
            {
                var sb = new StringBuilder(512);
                while (GetLastLogMessage(sb, sb.Capacity))
                {
                    Console.WriteLine($"[LeiaBridge] {sb.ToString()}");
                }
            }
            catch
            {
                // Ignore if method not found
            }
        }

        /// <summary>
        /// Update the eye swapping state
        /// </summary>
        public void SetSwap(bool swap)
        {
            if (!_initialized) return;
            SetSwapEyes(swap);
        }

        public void SetLensHintEnabled(bool enable)
        {
            if (!_initialized || !_extendedApiAvailable) return;
            try
            {
                SetLensHint(enable);
            }
            catch (EntryPointNotFoundException)
            {
                _extendedApiAvailable = false;
            }
            catch
            {
            }
        }

        public RuntimeState GetRuntimeState()
        {
            if (!_initialized)
            {
                return new RuntimeState(srAvailable: false, contextValid: false, lensEnabled: false, extendedApiAvailable: _extendedApiAvailable);
            }

            if (!_extendedApiAvailable)
            {
                return new RuntimeState(srAvailable: true, contextValid: true, lensEnabled: false, extendedApiAvailable: false);
            }

            try
            {
                return new RuntimeState(
                    srAvailable: IsSRAvailable(),
                    contextValid: IsContextValid(),
                    lensEnabled: IsLensEnabledState(),
                    extendedApiAvailable: true);
            }
            catch (EntryPointNotFoundException)
            {
                _extendedApiAvailable = false;
                return new RuntimeState(srAvailable: true, contextValid: true, lensEnabled: false, extendedApiAvailable: false);
            }
            catch
            {
                return new RuntimeState(srAvailable: true, contextValid: false, lensEnabled: false, extendedApiAvailable: _extendedApiAvailable);
            }
        }

        public bool TryRecoverContext()
        {
            if (!_initialized || !_extendedApiAvailable) return false;
            try
            {
                return RecoverContext();
            }
            catch (EntryPointNotFoundException)
            {
                _extendedApiAvailable = false;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Enable or disable head tracking
        /// </summary>
        public void SetTrackingEnabledStatus(bool enable)
        {
            if (!_initialized) return;
            SetTrackingEnabled(enable);
        }

        /// <summary>
        /// Get the current predicted eye positions for debugging
        /// </summary>
        public bool GetEyePositions(out System.Numerics.Vector3 left, out System.Numerics.Vector3 right)
        {
            left = System.Numerics.Vector3.Zero;
            right = System.Numerics.Vector3.Zero;

            if (!_initialized) return false;

            float[] lArr = new float[3];
            float[] rArr = new float[3];

            if (GetPredictedEyePositions(lArr, rArr))
            {
                left = new System.Numerics.Vector3(lArr[0], lArr[1], lArr[2]);
                right = new System.Numerics.Vector3(rArr[0], rArr[1], rArr[2]);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Weave the source texture to the screen
        /// </summary>
        /// <param name="sourceTextureId">OpenGL texture ID containing SBS stereo content</param>
        public void Weave(uint sourceTextureId)
        {
            if (!_initialized)
            {
                Console.WriteLine("[SRWeaver] Warning: Weave called before initialization");
                return;
            }

            try
            {
                // Call the bridge weave function directly
                Weave(sourceTextureId, _width, _height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SRWeaver] Weave failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the recommended resolution for the connected 3D display
        /// </summary>
        public (int width, int height) GetRecommendedResolution()
        {
            try
            {
                int w, h;
                GetRecommendedResolution(out w, out h);
                return (w, h);
            }
            catch
            {
                return (0, 0);
            }
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try
                {
                    if (_extendedApiAvailable)
                    {
                        try { SetLensHint(false); } catch { }
                    }
                    DeinitializeLeia();
                    Console.WriteLine("[SRWeaver] Weaver destroyed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SRWeaver] Error during disposal: {ex.Message}");
                }
                _initialized = false;
            }
        }
    }
}

