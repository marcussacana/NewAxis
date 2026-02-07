using System;
using System.Collections.Generic;
using System.IO;

namespace NewAxis.Services
{
    public class MtlLoader
    {
        public static Dictionary<string, string> Load(string path)
        {
            var materials = new Dictionary<string, string>();
            if (!File.Exists(path)) return materials;

            string? currentMaterial = null;

            foreach (var line in File.ReadLines(path))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#"))
                    continue;

                var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var tag = parts[0].ToLowerInvariant();

                if (tag == "newmtl")
                {
                    currentMaterial = parts[1];
                    if (!materials.ContainsKey(currentMaterial))
                        materials[currentMaterial] = "";
                    Console.WriteLine($"[MTL] newmtl: {currentMaterial}");
                }
                else if ((tag == "map_kd" || tag == "map_ka") && currentMaterial != null)
                {
                    // Prefer existing mapping if any, but overwrite if map_kd (diffuse) is found
                    if (string.IsNullOrEmpty(materials[currentMaterial]) || tag == "map_kd")
                    {
                        materials[currentMaterial] = parts[parts.Length - 1];
                        Console.WriteLine($"[MTL] Material '{currentMaterial}' {tag}: {materials[currentMaterial]}");
                    }
                }
            }

            return materials;
        }
    }
}
