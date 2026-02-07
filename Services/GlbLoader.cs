using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;
using Image = SixLabors.ImageSharp.Image;

namespace NewAxis.Services
{
    public class GlbLoader
    {
        public struct MaterialInfo
        {
            public Vector4 BaseColorFactor;  // RGBA multiplier
            public string AlphaMode;         // "OPAQUE", "BLEND", "MASK"
            public float AlphaCutoff;        // For MASK mode
        }

        public struct GlbMeshData
        {
            public SimpleObjLoader.MeshData MeshData;
            public Dictionary<string, byte[]> EmbeddedTextures;
            public Dictionary<string, MaterialInfo> MaterialInfos;
        }

        public static GlbMeshData Load(string path)
        {
            var model = ModelRoot.Load(path);
            var result = new GlbMeshData
            {
                MeshData = new SimpleObjLoader.MeshData
                {
                    Parts = new List<SimpleObjLoader.MeshPart>()
                },
                EmbeddedTextures = new Dictionary<string, byte[]>(),
                MaterialInfos = new Dictionary<string, MaterialInfo>()
            };

            var finalVertices = new List<float>();
            var finalIndices = new List<ushort>();
            var vertexCache = new Dictionary<(Vector3, Vector2), ushort>();

            // For simplicity, we'll merge all meshes/primitives into one MeshData
            // but keep track of material transitions.

            foreach (var mesh in model.LogicalMeshes)
            {
                foreach (var primitive in mesh.Primitives)
                {
                    var material = primitive.Material;
                    string matName = material?.Name ?? $"mat_{result.MeshData.Parts.Count}";

                    // Extract material properties
                    if (material != null && !result.MaterialInfos.ContainsKey(matName))
                    {
                        var matInfo = new MaterialInfo
                        {
                            BaseColorFactor = new Vector4(1, 1, 1, 1), // Default: white, opaque
                            AlphaMode = "OPAQUE",
                            AlphaCutoff = 0.5f
                        };

                        // Extract base color factor from PBR Metallic Roughness
                        try
                        {
                            var baseColorChannel = material.FindChannel("BaseColor");
                            if (baseColorChannel.HasValue)
                            {
                                // Access the color parameter from the channel
                                var color = baseColorChannel.Value.Color;
                                matInfo.BaseColorFactor = color;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[GLB] Could not extract color factor for {matName}: {ex.Message}");
                        }

                        // Extract alpha mode
                        matInfo.AlphaMode = material.Alpha.ToString().ToUpperInvariant();
                        matInfo.AlphaCutoff = material.AlphaCutoff;

                        result.MaterialInfos[matName] = matInfo;

                        Console.WriteLine($"[GLB] Material {matName}: Color={matInfo.BaseColorFactor}, Alpha={matInfo.AlphaMode}, Cutoff={matInfo.AlphaCutoff}");
                    }

                    // Extract textures
                    if (material != null)
                    {
                        if (!result.EmbeddedTextures.ContainsKey(matName))
                        {
                            byte[]? baseColorBytes = null;

                            // In glTF 2.0, alpha is ALWAYS in the baseColorTexture's A channel
                            // There is NO separate opacity texture in the spec
                            foreach (var channel in material.Channels)
                            {
                                if (channel.Texture == null) continue;
                                var primaryImage = channel.Texture.PrimaryImage;
                                if (primaryImage == null) continue;

                                string key = channel.Key.ToLowerInvariant();
                                Console.WriteLine($"[GLB] Material {matName} has channel: {key}");

                                if (key == "basecolor" || key == "diffuse")
                                {
                                    baseColorBytes = primaryImage.Content.Content.ToArray();
                                    Console.WriteLine($"[GLB] Found baseColor texture for material {matName}");
                                    break; // Found the color texture, stop looking
                                }
                            }

                            if (baseColorBytes != null)
                            {
                                // Use the baseColor texture directly (it already contains alpha in the A channel)
                                result.EmbeddedTextures[matName] = baseColorBytes;
                            }
                            else if (result.MaterialInfos.TryGetValue(matName, out var matInfo))
                            {
                                // No texture found, create a 1x1 solid color texture from BaseColorFactor
                                Console.WriteLine($"[GLB] No texture for {matName}, creating solid color texture");
                                var solidColor = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(1, 1);
                                solidColor[0, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                                    (byte)(matInfo.BaseColorFactor.X * 255),
                                    (byte)(matInfo.BaseColorFactor.Y * 255),
                                    (byte)(matInfo.BaseColorFactor.Z * 255),
                                    (byte)(matInfo.BaseColorFactor.W * 255)
                                );
                                using var ms = new MemoryStream();
                                solidColor.SaveAsPng(ms);
                                result.EmbeddedTextures[matName] = ms.ToArray();
                                solidColor.Dispose();
                            }
                        }
                    }

                    int partStartIndex = finalIndices.Count;

                    // Get Accessors
                    var positions = primitive.GetVertices("POSITION").AsVector3Array();
                    var texCoords = primitive.GetVertices("TEXCOORD_0").AsVector2Array();
                    var indices = primitive.GetIndices();

                    foreach (var idx in indices)
                    {
                        var pos = positions[(int)idx];
                        var uv = texCoords.Count > (int)idx ? texCoords[(int)idx] : Vector2.Zero;

                        var key = (pos, uv);
                        if (!vertexCache.TryGetValue(key, out ushort newIdx))
                        {
                            newIdx = (ushort)(finalVertices.Count / 5);
                            finalVertices.Add(pos.X);
                            finalVertices.Add(pos.Y);
                            finalVertices.Add(pos.Z);
                            finalVertices.Add(uv.X);
                            finalVertices.Add(uv.Y); // glTF UVs are already flipped correctly for many loaders, but we'll see
                            vertexCache[key] = newIdx;
                        }
                        finalIndices.Add(newIdx);
                    }

                    result.MeshData.Parts.Add(new SimpleObjLoader.MeshPart
                    {
                        MaterialName = matName,
                        StartIndex = partStartIndex,
                        IndexCount = finalIndices.Count - partStartIndex
                    });
                }
            }

            result.MeshData.Vertices = finalVertices.ToArray();
            result.MeshData.Indices = finalIndices.ToArray();

            return result;
        }
    }
}
