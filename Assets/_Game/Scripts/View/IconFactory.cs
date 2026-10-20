using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeddingChaos.View
{
    /// <summary>
    /// Generates crisp UI sprites at runtime from signed distance functions:
    /// guest badges (heart, broken heart, crown, glass, speech bubble), HUD icons
    /// and 9-slice rounded panels. Placeholder until the art team delivers an atlas;
    /// keys stay the same so swapping to real sprites is a lookup change.
    /// </summary>
    public static class IconFactory
    {
        static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        public static Sprite Get(string key)
        {
            if (cache.TryGetValue(key, out var s) && s) return s;
            s = Build(key);
            cache[key] = s;
            return s;
        }

        // ---------- painter ----------
        sealed class Painter
        {
            readonly int n; readonly Color[] px;
            public Painter(int size) { n = size; px = new Color[n * n]; }

            public void Fill(Func<Vector2, float> sdf, Color c, float soft = 1.3f)
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        var p = new Vector2((x + 0.5f) / n * 2 - 1, (y + 0.5f) / n * 2 - 1);
                        float dpx = sdf(p) * n / 2f;
                        float a = Mathf.Clamp01(0.5f - dpx / soft) * c.a;
                        if (a <= 0) continue;
                        int i = y * n + x;
                        var d = px[i];
                        float outA = a + d.a * (1 - a);
                        if (outA <= 0) continue;
                        px[i] = new Color((c.r * a + d.r * d.a * (1 - a)) / outA, (c.g * a + d.g * d.a * (1 - a)) / outA, (c.b * a + d.b * d.a * (1 - a)) / outA, outA);
                    }
            }

            public void Stroke(Func<Vector2, float> sdf, float halfWidth, Color c) => Fill(p => Mathf.Abs(sdf(p)) - halfWidth, c);

            public Sprite ToSprite(string name, Vector4 border = default)
            {
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                tex.SetPixels(px);
                tex.Apply(false, true);
                return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, border);
            }
        }

        // ---------- sdf helpers ----------
        static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;
        static float Box(Vector2 p, Vector2 c, Vector2 h, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - h + new Vector2(r, r);
            return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - r;
        }
        static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            var pa = p - a; var ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }
        static float Poly(Vector2 p, Vector2[] v)
        {
            float d = Vector2.Dot(p - v[0], p - v[0]); float s = 1;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
            {
                var e = v[j] - v[i]; var w = p - v[i];
                var b = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Vector2.Dot(e, e));
                d = Mathf.Min(d, Vector2.Dot(b, b));
                bool c1 = p.y >= v[i].y, c2 = p.y < v[j].y, c3 = e.x * w.y > e.y * w.x;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
            }
            return s * Mathf.Sqrt(d);
        }
        static float HeartRaw(Vector2 p)
        {
            p.x = Mathf.Abs(p.x);
            if (p.y + p.x > 1) return (p - new Vector2(0.25f, 0.75f)).magnitude - Mathf.Sqrt(2) / 4;
            var a = p - new Vector2(0, 1); var b = p - 0.5f * Mathf.Max(p.x + p.y, 0) * Vector2.one;
            return Mathf.Sqrt(Mathf.Min(Vector2.Dot(a, a), Vector2.Dot(b, b))) * Mathf.Sign(p.x - p.y);
        }
        static float Heart(Vector2 p, float s, Vector2 c) => HeartRaw((p - c - new Vector2(0, -0.58f * s)) / s) * s;
        static Vector2[] Star(int points, float r1, float r2, Vector2 c, float rot = 90)
        {
            var v = new Vector2[points * 2];
            for (int i = 0; i < v.Length; i++)
            {
                float a = (rot + i * 180f / points) * Mathf.Deg2Rad; float r = i % 2 == 0 ? r1 : r2;
                v[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            return v;
        }
        static float Union(float a, float b) => Mathf.Min(a, b);
        static float Sub(float a, float b) => Mathf.Max(a, -b);

        static readonly Color Ink = new Color32(58, 39, 49, 255);
        static Color C(string h) => Palette.Hex(h);

        // ---------- icons ----------
        static Sprite Build(string key)
        {
            var P = new Painter(key.StartsWith("ui_") ? 64 : 128);
            switch (key)
            {
                case "ui_round":   // 9-slice rounded panel
                    P.Fill(p => Box(p, Vector2.zero, new Vector2(1, 1), 0.7f), Color.white);
                    return P.ToSprite(key, new Vector4(24, 24, 24, 24));
                case "ui_shadow":
                    P.Fill(p => Box(p, Vector2.zero, new Vector2(0.86f, 0.86f), 0.6f), new Color(0, 0, 0, 0.22f), 9f);
                    return P.ToSprite(key, new Vector4(28, 28, 28, 28));
                case "ui_pill":
                    P.Fill(p => Box(p, Vector2.zero, new Vector2(1, 1), 1f), Color.white);
                    return P.ToSprite(key, new Vector4(31, 31, 31, 31));
                case "circle":
                    P.Fill(p => Circle(p, Vector2.zero, 0.98f), Color.white);
                    break;
                case "ring":
                    P.Stroke(p => Circle(p, Vector2.zero, 0.86f), 0.1f, Color.white);
                    break;
                case "heart":
                    P.Fill(p => Heart(p, 0.85f, Vector2.zero), C("#EF4F78"));
                    P.Fill(p => Circle(p, new Vector2(-0.38f, 0.32f), 0.13f), new Color(1, 1, 1, 0.75f));
                    break;
                case "brokenheart":
                    P.Fill(p => Heart(p, 0.9f, Vector2.zero), C("#7E5F70"));
                    P.Stroke(p => Union(Union(Seg(p, new Vector2(0.02f, 0.5f), new Vector2(-0.14f, 0.15f)), Seg(p, new Vector2(-0.14f, 0.15f), new Vector2(0.12f, -0.12f))), Seg(p, new Vector2(0.12f, -0.12f), new Vector2(-0.02f, -0.55f))), 0.045f, Color.white);
                    break;
                case "crown":
                    P.Fill(p => Poly(p, new[] { new Vector2(-0.8f, -0.5f), new Vector2(0.8f, -0.5f), new Vector2(0.86f, 0.42f), new Vector2(0.42f, 0.02f), new Vector2(0, 0.62f), new Vector2(-0.42f, 0.02f), new Vector2(-0.86f, 0.42f) }), C("#F6B93B"));
                    P.Fill(p => Box(p, new Vector2(0, -0.58f), new Vector2(0.82f, 0.13f), 0.08f), C("#E09A1E"));
                    P.Fill(p => Circle(p, new Vector2(0, 0.66f), 0.13f), C("#EF4F78"));
                    P.Fill(p => Circle(p, new Vector2(-0.86f, 0.46f), 0.11f), C("#7FB0F0"));
                    P.Fill(p => Circle(p, new Vector2(0.86f, 0.46f), 0.11f), C("#7FB0F0"));
                    break;
                case "glass":
                    P.Fill(p => Sub(Circle(p, new Vector2(0, 0.25f), 0.55f), Box(p, new Vector2(0, 1.05f), new Vector2(1, 0.62f), 0)), C("#E8E2F4"));
                    P.Fill(p => Sub(Circle(p, new Vector2(0, 0.25f), 0.48f), Box(p, new Vector2(0, 0.95f), new Vector2(1, 0.75f), 0)), C("#B5325A"));
                    P.Fill(p => Box(p, new Vector2(0, -0.45f), new Vector2(0.07f, 0.35f), 0.04f), C("#E8E2F4"));
                    P.Fill(p => Box(p, new Vector2(0, -0.82f), new Vector2(0.4f, 0.07f), 0.07f), C("#E8E2F4"));
                    P.Stroke(p => Sub(Circle(p, new Vector2(0, 0.25f), 0.55f), Box(p, new Vector2(0, 1.05f), new Vector2(1, 0.62f), 0)), 0.03f, C("#8E5BD6"));
                    break;
                case "bubble":
                    P.Fill(p => Union(Box(p, new Vector2(0, 0.12f), new Vector2(0.85f, 0.6f), 0.35f), Poly(p, new[] { new Vector2(-0.5f, -0.3f), new Vector2(-0.1f, -0.3f), new Vector2(-0.6f, -0.85f) })), C("#25A5A0"));
                    for (int i = -1; i <= 1; i++) { float x = i * 0.36f; P.Fill(p => Circle(p, new Vector2(x, 0.12f), 0.12f), Color.white); }
                    break;
                case "star":
                    P.Fill(p => Poly(p, Star(5, 0.95f, 0.42f, new Vector2(0, -0.04f))), C("#F6B93B"));
                    P.Fill(p => Poly(p, Star(5, 0.62f, 0.27f, new Vector2(0, -0.04f))), C("#FFD36E"));
                    break;
                case "star_empty":
                    P.Fill(p => Poly(p, Star(5, 0.95f, 0.42f, new Vector2(0, -0.04f))), C("#E6D6CF"));
                    break;
                case "coin":
                    P.Fill(p => Circle(p, Vector2.zero, 0.9f), C("#F6B93B"));
                    P.Stroke(p => Circle(p, Vector2.zero, 0.6f), 0.08f, C("#E09A1E"));
                    P.Fill(p => Box(p, Vector2.zero, new Vector2(0.09f, 0.36f), 0.05f), C("#E09A1E"));
                    break;
                case "gear":
                    P.Fill(p => Sub(Poly(p, Star(8, 0.95f, 0.72f, Vector2.zero, 0)), Circle(p, Vector2.zero, 0.3f)), Ink);
                    break;
                case "sunglasses":
                    P.Fill(p => Union(Box(p, new Vector2(-0.45f, 0), new Vector2(0.38f, 0.28f), 0.2f), Box(p, new Vector2(0.45f, 0), new Vector2(0.38f, 0.28f), 0.2f)), Ink);
                    P.Stroke(p => Seg(p, new Vector2(-0.1f, 0.12f), new Vector2(0.1f, 0.12f)), 0.07f, Ink);
                    P.Fill(p => Circle(p, new Vector2(-0.58f, 0.1f), 0.09f), new Color(1, 1, 1, 0.6f));
                    break;
                case "sofa":
                    P.Fill(p => Box(p, new Vector2(0, 0.2f), new Vector2(0.62f, 0.38f), 0.18f), C("#E2B8A9"));
                    P.Fill(p => Box(p, new Vector2(0, -0.25f), new Vector2(0.86f, 0.22f), 0.14f), C("#D98D68"));
                    P.Fill(p => Union(Box(p, new Vector2(-0.8f, -0.1f), new Vector2(0.14f, 0.36f), 0.12f), Box(p, new Vector2(0.8f, -0.1f), new Vector2(0.14f, 0.36f), 0.12f)), C("#C87452"));
                    P.Fill(p => Union(Box(p, new Vector2(-0.6f, -0.6f), new Vector2(0.07f, 0.14f), 0.04f), Box(p, new Vector2(0.6f, -0.6f), new Vector2(0.07f, 0.14f), 0.04f)), Ink);
                    break;
                case "flute":
                    P.Fill(p => Poly(p, new[] { new Vector2(-0.28f, 0.85f), new Vector2(0.28f, 0.85f), new Vector2(0.16f, -0.1f), new Vector2(-0.16f, -0.1f) }), C("#F6C35B"));
                    P.Fill(p => Box(p, new Vector2(0, -0.45f), new Vector2(0.05f, 0.35f), 0.03f), C("#E8E2F4"));
                    P.Fill(p => Box(p, new Vector2(0, -0.82f), new Vector2(0.34f, 0.06f), 0.06f), C("#E8E2F4"));
                    for (int i = 0; i < 3; i++) { var c = new Vector2(i % 2 == 0 ? -0.06f : 0.07f, 0.15f + i * 0.22f); P.Fill(p => Circle(p, c, 0.05f), Color.white); }
                    break;
                case "person":
                    P.Fill(p => Circle(p, new Vector2(0, 0.38f), 0.36f), Ink);
                    P.Fill(p => Sub(Circle(p, new Vector2(0, -0.75f), 0.75f), Box(p, new Vector2(0, -1.4f), new Vector2(1, 0.5f), 0)), Ink);
                    break;
                case "face_happy": case "face_worried": case "face_panic":
                    P.Fill(p => Circle(p, Vector2.zero, 0.92f), C("#FFE3C8"));
                    P.Stroke(p => Circle(p, Vector2.zero, 0.92f), 0.05f, C("#E8B48F"));
                    P.Fill(p => Circle(p, new Vector2(-0.32f, 0.12f), 0.1f), Ink);
                    P.Fill(p => Circle(p, new Vector2(0.32f, 0.12f), 0.1f), Ink);
                    P.Fill(p => Circle(p, new Vector2(-0.55f, -0.18f), 0.14f), new Color(0.95f, 0.55f, 0.6f, 0.6f));
                    P.Fill(p => Circle(p, new Vector2(0.55f, -0.18f), 0.14f), new Color(0.95f, 0.55f, 0.6f, 0.6f));
                    if (key == "face_happy") P.Stroke(p => Sub(Circle(p, new Vector2(0, -0.15f), 0.32f), Box(p, new Vector2(0, 0.3f), new Vector2(1, 0.45f), 0)), 0.06f, Ink);
                    else if (key == "face_worried") P.Stroke(p => Sub(Circle(p, new Vector2(0, -0.62f), 0.32f), Box(p, new Vector2(0, -0.95f), new Vector2(1, 0.33f), 0)), 0.06f, Ink);
                    else { P.Fill(p => Box(p, new Vector2(0, -0.42f), new Vector2(0.22f, 0.16f), 0.15f), Ink); P.Fill(p => Box(p, new Vector2(0.62f, 0.55f), new Vector2(0.08f, 0.16f), 0.08f), C("#7FB0F0")); }
                    break;
                // ----- blockers -----
                case "gift":
                    P.Fill(p => Box(p, new Vector2(0, -0.25f), new Vector2(0.72f, 0.55f), 0.1f), C("#E58A96"));
                    P.Fill(p => Box(p, new Vector2(0, 0.38f), new Vector2(0.85f, 0.16f), 0.08f), C("#F2C6CF"));
                    P.Fill(p => Box(p, new Vector2(0, -0.1f), new Vector2(0.13f, 0.72f), 0.03f), C("#E2B45E"));
                    P.Fill(p => Union(Circle(p, new Vector2(-0.24f, 0.68f), 0.2f), Circle(p, new Vector2(0.24f, 0.68f), 0.2f)), C("#E2B45E"));
                    break;
                case "reserve":     // a folded place card
                    P.Fill(p => Poly(p, new[] { new Vector2(-0.85f, -0.55f), new Vector2(0.85f, -0.55f), new Vector2(0.6f, 0.45f), new Vector2(-0.6f, 0.45f) }), C("#FFFFFF"));
                    P.Stroke(p => Poly(p, new[] { new Vector2(-0.85f, -0.55f), new Vector2(0.85f, -0.55f), new Vector2(0.6f, 0.45f), new Vector2(-0.6f, 0.45f) }), 0.05f, Ink);
                    P.Fill(p => Seg(p, new Vector2(-0.4f, -0.08f), new Vector2(0.4f, -0.08f)) - 0.07f, C("#E79AAE"));
                    P.Fill(p => Circle(p, new Vector2(0, 0.65f), 0.16f), C("#E2B45E"));
                    break;
                case "hands":       // two linked hands as two circles with a bar
                    P.Fill(p => Seg(p, new Vector2(-0.6f, 0), new Vector2(0.6f, 0)) - 0.16f, C("#E79AAE"));
                    P.Fill(p => Circle(p, new Vector2(-0.55f, 0), 0.32f), C("#F6D1B5"));
                    P.Fill(p => Circle(p, new Vector2(0.55f, 0), 0.32f), C("#E8B48F"));
                    P.Fill(p => Heart(p, 0.32f, new Vector2(0, 0.55f)), C("#EF4F78"));
                    break;
                // ----- info-note checkboxes -----
                case "box": case "box_check": case "box_cross":
                    P.Fill(p => Box(p, Vector2.zero, new Vector2(0.78f, 0.78f), 0.22f), new Color(1, 1, 1, 0.65f));
                    P.Stroke(p => Box(p, Vector2.zero, new Vector2(0.72f, 0.72f), 0.2f), 0.08f, new Color(0.23f, 0.15f, 0.19f, 0.75f));
                    if (key == "box_check")
                        P.Fill(p => Union(Seg(p, new Vector2(-0.5f, 0.05f), new Vector2(-0.12f, -0.38f)), Seg(p, new Vector2(-0.12f, -0.38f), new Vector2(0.62f, 0.62f))) - 0.13f, C("#2F9E6E"));
                    if (key == "box_cross")
                        P.Fill(p => Union(Seg(p, new Vector2(-0.42f, -0.42f), new Vector2(0.42f, 0.42f)), Seg(p, new Vector2(-0.42f, 0.42f), new Vector2(0.42f, -0.42f))) - 0.12f, C("#D63A3A"));
                    break;
                // ----- relationship badges -----
                case "rival":      // lightning bolt
                    P.Fill(p => Poly(p, new[] { new Vector2(0.2f, 0.95f), new Vector2(-0.5f, -0.05f), new Vector2(-0.02f, -0.05f), new Vector2(-0.25f, -0.95f), new Vector2(0.55f, 0.2f), new Vector2(0.06f, 0.2f), new Vector2(0.34f, 0.95f) }), C("#F08A2B"));
                    P.Stroke(p => Poly(p, new[] { new Vector2(0.2f, 0.95f), new Vector2(-0.5f, -0.05f), new Vector2(-0.02f, -0.05f), new Vector2(-0.25f, -0.95f), new Vector2(0.55f, 0.2f), new Vector2(0.06f, 0.2f), new Vector2(0.34f, 0.95f) }), 0.035f, C("#B5502A"));
                    break;
                case "bff":        // two linked friendship rings
                    P.Stroke(p => Circle(p, new Vector2(-0.3f, 0), 0.44f), 0.13f, C("#25A5A0"));
                    P.Stroke(p => Circle(p, new Vector2(0.3f, 0), 0.44f), 0.13f, C("#F6B93B"));
                    P.Fill(p => Mathf.Max(Mathf.Abs(Circle(p, new Vector2(-0.3f, 0), 0.44f)) - 0.13f, -p.y), C("#25A5A0"));
                    break;
                case "baby":       // baby bottle
                    P.Fill(p => Box(p, new Vector2(0, -0.22f), new Vector2(0.36f, 0.58f), 0.22f), C("#BFE3FF"));
                    P.Fill(p => Box(p, new Vector2(0, -0.4f), new Vector2(0.36f, 0.4f), 0.22f), C("#FFFFFF"));
                    P.Fill(p => Box(p, new Vector2(0, 0.42f), new Vector2(0.4f, 0.1f), 0.06f), C("#F59BB4"));
                    P.Fill(p => Union(Circle(p, new Vector2(0, 0.72f), 0.17f), Box(p, new Vector2(0, 0.58f), new Vector2(0.1f, 0.1f), 0.03f)), C("#F6C35B"));
                    P.Stroke(p => Box(p, new Vector2(0, -0.22f), new Vector2(0.36f, 0.58f), 0.22f), 0.035f, C("#7FB0F0"));
                    break;
                case "crush":      // pink heart with an arrow through it
                    P.Fill(p => Seg(p, new Vector2(-0.85f, -0.6f), new Vector2(0.85f, 0.55f)) - 0.05f, C("#B5325A"));
                    P.Fill(p => Heart(p, 0.72f, new Vector2(0, -0.02f)), C("#FF8FB8"));
                    P.Fill(p => Seg(p, new Vector2(0.25f, 0.17f), new Vector2(0.85f, 0.55f)) - 0.05f, C("#B5325A"));
                    P.Fill(p => Poly(p, new[] { new Vector2(0.95f, 0.62f), new Vector2(0.62f, 0.55f), new Vector2(0.82f, 0.3f) }), C("#B5325A"));
                    P.Fill(p => Circle(p, new Vector2(-0.3f, 0.28f), 0.1f), new Color(1, 1, 1, 0.75f));
                    break;
                // ----- emote bubbles -----
                case "emote_bg":
                    P.Fill(p => Union(Circle(p, new Vector2(0.06f, 0.08f), 0.84f), Poly(p, new[] { new Vector2(-0.62f, -0.3f), new Vector2(-0.22f, -0.62f), new Vector2(-0.88f, -0.92f) })), Color.white);
                    break;
                case "anger":
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int sy = -1; sy <= 1; sy += 2)
                        {
                            var c = new Vector2(sx * 0.66f, sy * 0.66f); float fx = sx, fy = sy;
                            P.Fill(p => Mathf.Max(Mathf.Max(Mathf.Abs(Circle(p, c, 0.5f)) - 0.12f, -(fx * p.x - 0.07f)), -(fy * p.y - 0.07f)), C("#E8434F"));
                        }
                    break;
                case "sweat":
                    P.Fill(p => Union(Circle(p, new Vector2(0, -0.25f), 0.48f), Poly(p, new[] { new Vector2(0, 0.88f), new Vector2(-0.42f, -0.08f), new Vector2(0.42f, -0.08f) })), C("#6EC1F0"));
                    P.Fill(p => Circle(p, new Vector2(-0.17f, -0.25f), 0.12f), new Color(1, 1, 1, 0.85f));
                    break;
                case "exclaim":
                    P.Fill(p => Box(p, new Vector2(0, 0.22f), new Vector2(0.17f, 0.55f), 0.16f), C("#E8434F"));
                    P.Fill(p => Circle(p, new Vector2(0, -0.7f), 0.18f), C("#E8434F"));
                    break;
                case "question":
                    P.Fill(p => { float d = Mathf.Abs(Circle(p, new Vector2(0, 0.38f), 0.36f)) - 0.13f; if (p.x < 0 && p.y < 0.4f) d = Mathf.Max(d, 0.2f); return Union(d, Seg(p, new Vector2(0.02f, 0.03f), new Vector2(0, -0.25f)) - 0.13f); }, C("#8E5BD6"));
                    P.Fill(p => Circle(p, new Vector2(0, -0.66f), 0.16f), C("#8E5BD6"));
                    break;
                case "dots":
                    for (int i = -1; i <= 1; i++) { float x = i * 0.5f; P.Fill(p => Circle(p, new Vector2(x, 0), 0.19f), Ink); }
                    break;
                case "zzz":
                    {
                        float Z(Vector2 p, Vector2 c, float s) => Union(Union(Seg(p, c + new Vector2(-s, s), c + new Vector2(s, s)), Seg(p, c + new Vector2(s, s), c + new Vector2(-s, -s))), Seg(p, c + new Vector2(-s, -s), c + new Vector2(s, -s)));
                        P.Fill(p => Z(p, new Vector2(-0.22f, -0.22f), 0.42f) - 0.12f, C("#7FA0D8"));
                        P.Fill(p => Z(p, new Vector2(0.52f, 0.55f), 0.22f) - 0.08f, C("#7FA0D8"));
                    }
                    break;
                case "note":
                    P.Fill(p => (new Vector2((p.x + 0.28f) / 1.25f, p.y + 0.52f)).magnitude - 0.24f, C("#8E5BD6"));
                    P.Fill(p => Seg(p, new Vector2(0.0f, -0.5f), new Vector2(0.0f, 0.7f)) - 0.075f, C("#8E5BD6"));
                    P.Fill(p => Seg(p, new Vector2(0.0f, 0.7f), new Vector2(0.45f, 0.35f)) - 0.1f, C("#8E5BD6"));
                    break;
                case "sparkle":
                    P.Fill(p => Poly(p, Star(4, 0.92f, 0.26f, new Vector2(-0.12f, -0.08f))), C("#F6B93B"));
                    P.Fill(p => Poly(p, Star(4, 0.36f, 0.11f, new Vector2(0.6f, 0.58f))), C("#FFD36E"));
                    break;
                default:
                    P.Fill(p => Circle(p, Vector2.zero, 0.8f), Color.magenta);
                    break;
            }
            return P.ToSprite(key);
        }
    }
}
