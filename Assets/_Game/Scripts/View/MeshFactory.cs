using System.Collections.Generic;
using UnityEngine;

namespace WeddingChaos.View
{
    /// <summary>Procedural meshes the built-in primitives lack: frustum (skirts, hats, pots) and torus (glasses, rims, necklaces).</summary>
    public static class MeshFactory
    {
        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        /// <summary>Closed frustum from y=0 (radius rBottom) to y=height (radius rTop).</summary>
        public static Mesh Frustum(float rTop, float rBottom, float height, int seg = 24)
        {
            string key = $"f{rTop:F3}_{rBottom:F3}_{height:F3}_{seg}";
            if (cache.TryGetValue(key, out var m) && m) return m;
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2 / seg; var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v.Add(d * rBottom); v.Add(d * rTop + Vector3.up * height);
            }
            for (int i = 0; i < seg; i++)
            {
                int b = i * 2;
                t.AddRange(new[] { b, b + 1, b + 2, b + 2, b + 1, b + 3 });
            }
            // caps
            int cb = v.Count; v.Add(Vector3.zero); int ct = v.Count; v.Add(Vector3.up * height);
            int ringB = v.Count;
            for (int i = 0; i <= seg; i++) { float a = i * Mathf.PI * 2 / seg; var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)); v.Add(d * rBottom); v.Add(d * rTop + Vector3.up * height); }
            for (int i = 0; i < seg; i++)
            {
                int r = ringB + i * 2;
                t.AddRange(new[] { cb, r + 2, r });
                t.AddRange(new[] { ct, r + 1, r + 3 });
            }
            m = new Mesh { name = key };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            cache[key] = m;
            return m;
        }

        /// <summary>Torus in the XZ plane. arc &lt; 1 makes a partial ring (e.g. a cane hook).</summary>
        public static Mesh Torus(float radius, float tube, float arc = 1f, int seg = 28, int sides = 10)
        {
            string key = $"t{radius:F3}_{tube:F3}_{arc:F2}";
            if (cache.TryGetValue(key, out var m) && m) return m;
            var v = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i <= seg; i++)
            {
                float u = i * Mathf.PI * 2 * arc / seg;
                var c = new Vector3(Mathf.Cos(u), 0, Mathf.Sin(u)) * radius;
                for (int j = 0; j <= sides; j++)
                {
                    float w = j * Mathf.PI * 2 / sides;
                    var n = new Vector3(Mathf.Cos(u) * Mathf.Cos(w), Mathf.Sin(w), Mathf.Sin(u) * Mathf.Cos(w));
                    v.Add(c + n * tube);
                }
            }
            for (int i = 0; i < seg; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = i * (sides + 1) + j, b = a + sides + 1;
                    t.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
                }
            m = new Mesh { name = key };
            m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds();
            cache[key] = m;
            return m;
        }

        public static GameObject Make(Mesh mesh, Transform parent, Vector3 localPos, Vector3 scale, Color c, string name = "Mesh")
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mesh.name.StartsWith("t") ? Palette.MatDouble(c) : Palette.Mat(c);
            return go;
        }
    }
}
