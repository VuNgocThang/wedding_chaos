using System.Collections.Generic;
using UnityEngine;

namespace WeddingChaos.View
{
    /// <summary>
    /// Walkable floor grid for the hall (XZ plane). Obstacles (crowd area, sofas, tables, chairs,
    /// stage) are stamped in, inflated by the guest radius. A* (8-way) + line-of-sight smoothing
    /// gives paths that never cut through objects or through the waiting crowd.
    /// </summary>
    public class NavGrid
    {
        readonly float cell, minX, minZ;
        readonly int w, h;
        readonly bool[] blocked;

        public NavGrid(float minX, float maxX, float minZ, float maxZ, float cell)
        {
            this.cell = cell; this.minX = minX; this.minZ = minZ;
            w = Mathf.CeilToInt((maxX - minX) / cell); h = Mathf.CeilToInt((maxZ - minZ) / cell);
            blocked = new bool[w * h];
        }

        int Idx(int x, int z) => z * w + x;
        bool In(int x, int z) => x >= 0 && z >= 0 && x < w && z < h;
        Vector3 Center(int x, int z) => new Vector3(minX + (x + 0.5f) * cell, 0, minZ + (z + 0.5f) * cell);
        void ToCell(Vector3 p, out int x, out int z) { x = Mathf.FloorToInt((p.x - minX) / cell); z = Mathf.FloorToInt((p.z - minZ) / cell); }

        public bool IsBlocked(Vector3 p) { ToCell(p, out int x, out int z); return !In(x, z) || blocked[Idx(x, z)]; }

        public void BlockRect(Vector3 center, Vector2 halfSize, float inflate)
        {
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var c = Center(x, z);
                    if (Mathf.Abs(c.x - center.x) <= halfSize.x + inflate && Mathf.Abs(c.z - center.z) <= halfSize.y + inflate) blocked[Idx(x, z)] = true;
                }
        }

        public void BlockRotatedRect(Vector3 center, Quaternion rot, Vector2 halfSize, float inflate)
        {
            var inv = Quaternion.Inverse(rot);
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var local = inv * (Center(x, z) - center);
                    if (Mathf.Abs(local.x) <= halfSize.x + inflate && Mathf.Abs(local.z) <= halfSize.y + inflate) blocked[Idx(x, z)] = true;
                }
        }

        public void BlockCircle(Vector3 center, float radius)
        {
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var c = Center(x, z);
                    if ((c.x - center.x) * (c.x - center.x) + (c.z - center.z) * (c.z - center.z) <= radius * radius) blocked[Idx(x, z)] = true;
                }
        }

        /// <summary>Nearest walkable cell centre to p (spiral search).</summary>
        public Vector3 NearestFree(Vector3 p)
        {
            ToCell(p, out int cx, out int cz);
            for (int r = 0; r < 30; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                        int x = cx + dx, z = cz + dz;
                        if (In(x, z) && !blocked[Idx(x, z)]) return Center(x, z);
                    }
            return p;
        }

        /// <summary>Smoothed path from a to b (both snapped to free cells). Endpoints included as given.</summary>
        public List<Vector3> FindPath(Vector3 a, Vector3 b)
        {
            var start = NearestFree(a); var goal = NearestFree(b);
            ToCell(start, out int sx, out int sz); ToCell(goal, out int gx, out int gz);
            var open = new SortedSet<(float f, int i)>(Comparer<(float f, int i)>.Create((p, q) => p.f != q.f ? p.f.CompareTo(q.f) : p.i.CompareTo(q.i)));
            var g = new Dictionary<int, float>(); var came = new Dictionary<int, int>();
            int s = Idx(sx, sz), goalI = Idx(gx, gz);
            g[s] = 0; open.Add((H(sx, sz, gx, gz), s));
            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 }, dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
            bool found = false;
            while (open.Count > 0)
            {
                var cur = open.Min; open.Remove(cur);
                if (cur.i == goalI) { found = true; break; }
                int x = cur.i % w, z = cur.i / w;
                for (int d = 0; d < 8; d++)
                {
                    int nx = x + dx[d], nz = z + dz[d];
                    if (!In(nx, nz) || blocked[Idx(nx, nz)]) continue;
                    if (d >= 4 && (blocked[Idx(x + dx[d], z)] || blocked[Idx(x, z + dz[d])])) continue;   // no corner cutting
                    int ni = Idx(nx, nz);
                    float ng = g[cur.i] + (d >= 4 ? 1.4142f : 1f);
                    if (g.TryGetValue(ni, out float old) && old <= ng) continue;
                    if (g.ContainsKey(ni)) open.Remove((old + H(nx, nz, gx, gz), ni));
                    g[ni] = ng; came[ni] = cur.i;
                    open.Add((ng + H(nx, nz, gx, gz), ni));
                }
            }
            var raw = new List<Vector3>();
            if (found)
            {
                for (int i = goalI; ; i = came[i]) { raw.Add(Center(i % w, i / w)); if (i == s) break; }
                raw.Reverse();
            }
            else raw.Add(start);
            // string-pull
            var pts = new List<Vector3> { a };
            var anchor = start;
            for (int i = 1; i < raw.Count; i++)
                if (!LineFree(anchor, raw[i])) { anchor = raw[i - 1]; pts.Add(anchor); }
            pts.Add(goal);
            if ((b - goal).sqrMagnitude > 0.0001f) pts.Add(b);
            return pts;
        }

        static float H(int x, int z, int gx, int gz)
        {
            int ax = Mathf.Abs(x - gx), az = Mathf.Abs(z - gz);
            return Mathf.Max(ax, az) + 0.4142f * Mathf.Min(ax, az);
        }

        bool LineFree(Vector3 a, Vector3 b)
        {
            float len = Vector3.Distance(a, b);
            if (len < 0.0001f) return !IsBlocked(a);
            var side = Vector3.Cross(Vector3.up, (b - a) / len) * (cell * 0.6f);   // keep a small clearance on both sides
            int steps = Mathf.CeilToInt(len / (cell * 0.2f));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)Mathf.Max(1, steps));
                if (IsBlocked(p) || IsBlocked(p + side) || IsBlocked(p - side)) return false;
            }
            return true;
        }
    }
}
