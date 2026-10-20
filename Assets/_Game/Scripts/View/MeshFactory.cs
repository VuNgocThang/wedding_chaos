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

        /// <summary>
        /// Low-poly stand-in for Unity's sphere (768 tris). Same size (diameter 1) and smooth normals,
        /// so it is a drop-in swap. lon x lat segments; 14x9 is ~230 tris and still reads round at our zoom.
        /// </summary>
        public static Mesh Sphere(int lon = 14, int lat = 9)
        {
            string key = $"sph{lon}x{lat}";
            if (cache.TryGetValue(key, out var m) && m) return m;
            m = Capsuleish(lon, lat, 0f, key);
            cache[key] = m;
            return m;
        }

        /// <summary>Low-poly stand-in for Unity's capsule (height 2, radius 0.5, along Y).</summary>
        public static Mesh Capsule(int lon = 14, int capRings = 5)
        {
            string key = $"cap{lon}x{capRings}";
            if (cache.TryGetValue(key, out var m) && m) return m;
            m = Capsuleish(lon, capRings * 2, 0.5f, key);
            cache[key] = m;
            return m;
        }

        /// <summary>Sphere of radius 0.5 whose two halves are pushed apart by +-halfGap along Y (0 = sphere).</summary>
        static Mesh Capsuleish(int lon, int lat, float halfGap, string name)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var tri = new List<int>();
            const float r = 0.5f;
            // rings from top pole to bottom pole; the equator ring is duplicated for a capsule's straight part
            var rings = new List<(float y, float rad, Vector3 nrm)>();
            for (int i = 0; i <= lat; i++)
            {
                float th = Mathf.PI * i / lat;                 // 0 at top
                float y = Mathf.Cos(th) * r, rad = Mathf.Sin(th) * r;
                var nrm = new Vector3(0, Mathf.Cos(th), 0);
                if (halfGap > 0 && i * 2 == lat)
                {
                    rings.Add((y + halfGap, rad, new Vector3(0, 0, 0)));
                    rings.Add((y - halfGap, rad, new Vector3(0, 0, 0)));
                    continue;
                }
                rings.Add((y + (i * 2 < lat ? halfGap : -halfGap), rad, nrm));
            }
            for (int i = 0; i < rings.Count; i++)
            {
                var (y, rad, nr) = rings[i];
                for (int j = 0; j <= lon; j++)
                {
                    float ph = 2 * Mathf.PI * j / lon;
                    var dir = new Vector3(Mathf.Cos(ph), 0, Mathf.Sin(ph));
                    v.Add(new Vector3(dir.x * rad, y, dir.z * rad));
                    float horiz = rad / r;
                    n.Add((dir * horiz + new Vector3(0, nr.y, 0)).normalized);
                }
            }
            int w = lon + 1;
            for (int i = 0; i < rings.Count - 1; i++)
                for (int j = 0; j < lon; j++)
                {
                    int a = i * w + j, b = a + w;
                    if (i > 0) { tri.Add(a); tri.Add(a + 1); tri.Add(b); }
                    if (i < rings.Count - 2) { tri.Add(a + 1); tri.Add(b + 1); tri.Add(b); }
                }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);   // keep readable: static batching and colliders need it
            return mesh;
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
