using System;
using System.Linq;

namespace NewAxis.Services
{
    public static class GpuUtils
    {


        public static bool IsAmdGpu()
        {
            try
            {
                var system32 = Environment.SystemDirectory;

                // key AMD driver DLLs
                var amdDlls = new[] { "atiadlxx.dll", "atiadlxy.dll", "amdvlk64.dll" };
                bool hasAmd = amdDlls.Any(dll => System.IO.File.Exists(System.IO.Path.Combine(system32, dll)));

                // key NVIDIA driver DLLs
                var nvidiaDlls = new[] { "nvapi64.dll", "nvapi.dll" };
                bool hasNvidia = nvidiaDlls.Any(dll => System.IO.File.Exists(System.IO.Path.Combine(system32, dll)));

                Console.WriteLine($"[GpuUtils] DLL Detection: HasAMD={hasAmd}, HasNVIDIA={hasNvidia}");

                if (hasAmd && hasNvidia)
                {
                    return false;
                }

                return hasAmd;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuUtils] GPU Detection by DLL failed: {ex.Message}");
                return false;
            }
        }
    }
}
