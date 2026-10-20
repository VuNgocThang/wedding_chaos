using System.Collections.Generic;
using UnityEngine;

namespace WeddingChaos.View
{
    /// <summary>Pastel Clay palette + tiny helpers for building placeholder geometry.</summary>
    public static class Palette
    {
        public static readonly Color Bg = Hex("#F1E7E0");
        public static readonly Color Floor = Hex("#ECE1D8");
        public static readonly Color FloorDark = Hex("#E0CDC2");
        public static readonly Color Carpet = Hex("#DDA0AC");
        public static readonly Color Cloth = Hex("#FAF3EA");
        public static readonly Color Chair = Hex("#F4EDE4");
        public static readonly Color ChairDark = Hex("#D9C6B5");
        public static readonly Color Leg = Hex("#5A4870");
        public static readonly Color Shoe = Hex("#3A2B4A");
        public static readonly Color Ink = Hex("#3A2731");
        public static readonly Color Gold = Hex("#E2B45E");
        public static readonly Color Accent = Hex("#E2557A");
        public static readonly Color Leaf = Hex("#7DB892");
        public static readonly Color Flower = Hex("#EE9DB0");
        public static readonly Color Hidden = Hex("#C9B8C0");
        public static readonly Color Good = Hex("#2F9E6E");
        public static readonly Color Bad = Hex("#D63A3A");
        public static readonly Color Blush = Hex("#F2C6CF");
        public static readonly Color Sash = Hex("#E79AAE");
        public static readonly Color Bulb = Hex("#FFE2A6");
        public static readonly Color[] Skins = { Hex("#F6D1B5"), Hex("#E8B48F"), Hex("#C98E66"), Hex("#8D5A3C") };
        public static readonly Color[] Hairs = { Hex("#4A2F3A"), Hex("#7A4A2A"), Hex("#2B2B2B"), Hex("#C9853F"), Hex("#9A6B4F") };

        public static Color Invite(char c)
        {
            switch (c)
            {
                case 'R': return Hex("#E58A96");
                case 'B': return Hex("#86AEDD");
                case 'G': return Hex("#80C29E");
                case 'Y': return Hex("#E8BE6C");
                default: return Color.gray;
            }
        }

        public static string InviteName(char c)
        {
            switch (c) { case 'R': return "đỏ"; case 'B': return "xanh dương"; case 'G': return "xanh lá"; case 'Y': return "vàng"; default: return "?"; }
        }

        public static Color Hex(string h) { ColorUtility.TryParseHtmlString(h, out var c); return c; }

        static readonly Dictionary<Color, Material> cache = new Dictionary<Color, Material>();
        static Material template;

        /// <summary>
        /// Base material loaded from Resources so the URP Lit shader is shipped in player builds.
        /// Shader.Find alone works in the Editor but returns null in a build, because a shader only
        /// referenced from code is stripped.
        /// </summary>
        static Material Template()
        {
            if (template != null) return template;
            template = Resources.Load<Material>("Materials/ClayLit");
            if (template == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (sh == null) sh = Shader.Find("Standard");
                if (sh == null) Debug.LogError("[WeddingChaos] No lit shader found; add Resources/Materials/ClayLit.mat");
                template = new Material(sh);
            }
            return template;
        }

        public static Material Mat(Color c)
        {
            if (cache.TryGetValue(c, out var m) && m != null) return m;
            m = new Material(Template()) { color = c };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.12f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.12f);
            cache[c] = m;
            return m;
        }

        static readonly Dictionary<Color, Material> cacheDouble = new Dictionary<Color, Material>();
        /// <summary>Same flat colour, both faces rendered (for thin procedural meshes).</summary>
        public static Material MatDouble(Color c)
        {
            if (cacheDouble.TryGetValue(c, out var m) && m != null) return m;
            m = new Material(Mat(c));
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            cacheDouble[c] = m;
            return m;
        }

        /// <summary>Creates a primitive child with a flat colour; colliders removed unless asked for.</summary>
        public static GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color, bool keepCollider = false, string name = null)
        {
            var go = GameObject.CreatePrimitive(type);
            if (name != null) go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = Mat(color);
            // Unity's sphere/capsule are 768/832 tris; dozens of them per guest add up fast on low-end GPUs.
            float size = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            if (type == PrimitiveType.Sphere) go.GetComponent<MeshFilter>().sharedMesh = size < 0.16f ? MeshFactory.Sphere(8, 6) : MeshFactory.Sphere();
            else if (type == PrimitiveType.Capsule) go.GetComponent<MeshFilter>().sharedMesh = size < 0.16f ? MeshFactory.Capsule(8, 3) : MeshFactory.Capsule();
            // Tiny or paper-flat parts (eyes, petals, tiles, trims) cast no visible shadow: skip them in the shadow pass.
            float mx = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)), mn = Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
            if (mx < 0.16f || mn < 0.03f) rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            rend.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            if (!keepCollider) Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        public static Transform Pivot(Transform parent, string name, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }
    }
}
