using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Build-99 movable details are welded into Deck (DE2) and Cab (DM3). Split only
    // their triangles in a private mesh; preserve the source asset, UVs and materials.
    // The owner caches the result per source mesh, never from a frame callback.
    internal sealed class NativeHeaterGeometry : IDisposable
    {
        public Mesh Body { get; private set; }
        public Mesh Detail { get; private set; }
        public Vector3 Pivot { get; private set; }

        public static NativeHeaterGeometry Create(Mesh source, bool dm3)
        {
            if (source == null || !source.isReadable)
                throw new InvalidOperationException("The stock heater mesh is unavailable or unreadable.");
            var min = dm3 ? new Vector3(-0.6680f, 2.2016f, -3.6073f)
                : new Vector3(1.1599f, -0.2948f, 2.0695f);
            var max = dm3 ? new Vector3(-0.6264f, 2.2436f, -3.5912f)
                : new Vector3(1.1719f, -0.2792f, 2.1038f);
            var pivot = dm3 ? new Vector3(-0.6472235f, 2.222754f, -3.591311f)
                : new Vector3(1.1658912f, -0.2910f, 2.0866545f);
            var region = new Bounds((min + max) * 0.5f, max - min);
            var vertices = source.vertices;
            var selected = new bool[vertices.Length];
            for (var i = 0; i < vertices.Length; i++) selected[i] = region.Contains(vertices[i]);
            var remaining = new List<int>[source.subMeshCount];
            var detail = new List<int>[source.subMeshCount];
            var map = new Dictionary<int, int>();
            var originalIndices = new List<int>();
            var removedTriangles = 0;
            for (var sub = 0; sub < source.subMeshCount; sub++)
            {
                remaining[sub] = new List<int>();
                detail[sub] = new List<int>();
                var indices = source.GetTriangles(sub);
                for (var t = 0; t < indices.Length; t += 3)
                {
                    var take = selected[indices[t]] && selected[indices[t + 1]] && selected[indices[t + 2]];
                    if (take) removedTriangles++;
                    for (var j = 0; j < 3; j++)
                    {
                        var index = indices[t + j];
                        if (!take) { remaining[sub].Add(index); continue; }
                        int compactIndex;
                        if (!map.TryGetValue(index, out compactIndex))
                        {
                            compactIndex = map.Count;
                            map.Add(index, compactIndex);
                            originalIndices.Add(index);
                        }
                        detail[sub].Add(compactIndex);
                    }
                }
            }
            // Refuse a changed game asset rather than remove unrelated cabin geometry.
            if (removedTriangles < 12 || removedTriangles > 160 || map.Count < 12 || map.Count > 240)
                throw new InvalidOperationException("Stock heater geometry no longer matches build 99.");

            var result = new NativeHeaterGeometry { Pivot = pivot };
            try
            {
                result.Body = UnityEngine.Object.Instantiate(source);
                result.Body.name = source.name + "_DVSurvival_WithoutHeaterDetail";
                result.Detail = new Mesh { name = "DVSurvival_StockHeaterDetail" };
                var smallVertices = new Vector3[map.Count];
                var normals = source.normals;
                var tangents = source.tangents;
                var uv = source.uv;
                var smallNormals = new Vector3[map.Count];
                var smallTangents = new Vector4[map.Count];
                var smallUv = new Vector2[map.Count];
                for (var i = 0; i < originalIndices.Count; i++)
                {
                    var index = originalIndices[i];
                    smallVertices[i] = vertices[index] - pivot;
                    if (normals.Length == vertices.Length) smallNormals[i] = normals[index];
                    if (tangents.Length == vertices.Length) smallTangents[i] = tangents[index];
                    if (uv.Length == vertices.Length) smallUv[i] = uv[index];
                }
                result.Detail.vertices = smallVertices;
                if (normals.Length == vertices.Length) result.Detail.normals = smallNormals;
                if (tangents.Length == vertices.Length) result.Detail.tangents = smallTangents;
                if (uv.Length == vertices.Length) result.Detail.uv = smallUv;
                result.Detail.subMeshCount = source.subMeshCount;
                for (var sub = 0; sub < source.subMeshCount; sub++)
                {
                    result.Body.SetTriangles(remaining[sub], sub, false);
                    result.Detail.SetTriangles(detail[sub], sub, false);
                }
                result.Detail.RecalculateBounds();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Body != null) UnityEngine.Object.Destroy(Body);
            if (Detail != null) UnityEngine.Object.Destroy(Detail);
            Body = Detail = null;
        }
    }
}
