using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NewAxis.Services
{
    public class SimpleObjLoader
    {
        public struct MeshPart
        {
            public string MaterialName;
            public int StartIndex;
            public int IndexCount;
        }

        public struct MeshData
        {
            public float[] Vertices; // x, y, z, u, v
            public ushort[] Indices;
            public List<MeshPart> Parts;
            public string? MtlLib;
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            public int PosIdx;
            public int UvIdx;

            public bool Equals(VertexKey other) => PosIdx == other.PosIdx && UvIdx == other.UvIdx;
            public override bool Equals(object? obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(PosIdx, UvIdx);
        }

        public static MeshData Load(string path)
        {
            var meshData = new MeshData();
            var posList = new List<float[]>();
            var uvList = new List<float[]>();

            var indices = new List<ushort>();
            var meshParts = new List<MeshPart>();

            var vertexCache = new Dictionary<VertexKey, ushort>();
            var finalVertices = new List<float>(); // Flattened Pos(3) + UV(2)

            string? currentMaterial = null;
            int partStartIndex = 0;

            void ClosePart()
            {
                if (indices.Count > partStartIndex)
                {
                    meshParts.Add(new MeshPart
                    {
                        MaterialName = currentMaterial ?? "default",
                        StartIndex = partStartIndex,
                        IndexCount = indices.Count - partStartIndex
                    });
                    partStartIndex = indices.Count;
                }
            }

            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                switch (parts[0])
                {
                    case "v":
                        posList.Add(new[] {
                            float.Parse(parts[1], CultureInfo.InvariantCulture),
                            float.Parse(parts[2], CultureInfo.InvariantCulture),
                            float.Parse(parts[3], CultureInfo.InvariantCulture)
                        });
                        break;
                    case "vt":
                        uvList.Add(new[] {
                            float.Parse(parts[1], CultureInfo.InvariantCulture),
                            float.Parse(parts[2], CultureInfo.InvariantCulture)
                        });
                        break;
                    case "mtllib":
                        meshData.MtlLib = parts[1];
                        Console.WriteLine($"[OBJ] mtllib found: {meshData.MtlLib}");
                        break;
                    case "usemtl":
                        ClosePart();
                        currentMaterial = parts[1];
                        break;
                    case "f":
                        var faceVKeys = new List<VertexKey>();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var subParts = parts[i].Split('/');
                            int pIdx = int.Parse(subParts[0]) - 1;
                            int tIdx = subParts.Length > 1 && !string.IsNullOrEmpty(subParts[1]) ? int.Parse(subParts[1]) - 1 : -1;

                            faceVKeys.Add(new VertexKey { PosIdx = pIdx, UvIdx = tIdx });
                        }

                        // Triangulate face (Fan)
                        for (int i = 1; i < faceVKeys.Count - 1; i++)
                        {
                            indices.Add(GetOrCreateVertex(faceVKeys[0]));
                            indices.Add(GetOrCreateVertex(faceVKeys[i]));
                            indices.Add(GetOrCreateVertex(faceVKeys[i + 1]));
                        }
                        break;
                }
            }

            ClosePart();

            ushort GetOrCreateVertex(VertexKey key)
            {
                if (vertexCache.TryGetValue(key, out ushort idx)) return idx;

                ushort newIdx = (ushort)(finalVertices.Count / 5);

                // Add Position
                var pos = posList[key.PosIdx];
                finalVertices.Add(pos[0]);
                finalVertices.Add(pos[1]);
                finalVertices.Add(pos[2]);

                // Add UV
                if (key.UvIdx >= 0 && key.UvIdx < uvList.Count)
                {
                    var uv = uvList[key.UvIdx];
                    finalVertices.Add(uv[0]);
                    finalVertices.Add(1.0f - uv[1]); // Invert V for OpenGL
                }
                else
                {
                    finalVertices.Add(0);
                    finalVertices.Add(0);
                }

                vertexCache[key] = newIdx;
                return newIdx;
            }

            meshData.Vertices = finalVertices.ToArray();
            meshData.Indices = indices.ToArray();
            meshData.Parts = meshParts;

            return meshData;
        }
    }
}
