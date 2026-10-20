using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WeddingChaos.Core;

namespace WeddingChaos.View
{
    public enum Archetype { Girl, Boy, Grandma, Uncle, Auntie, Bride, Groom, Kid }

    /// <summary>
    /// Placeholder 2.5D chibi built from primitives + procedural meshes, one silhouette per archetype
    /// (girl, boy, grandma, drunk uncle, gossip auntie, bride, groom), animated procedurally.
    /// Animation = base stance (stand / walk / sit / lifted) + a timed <see cref="Act"/> layered on top
    /// + a persistent <see cref="Mood"/> that sets the resting face. Guests pick ambient acts by
    /// themselves (waiting, bored, blocked, in love, glaring at the ex...) and the controller triggers
    /// story acts when drama changes. Final art swaps the visuals for rigged models behind the same API.
    /// </summary>
    public class GuestView : MonoBehaviour
    {
        public enum Pose { Idle, Lift, Walk, Sit, Cheer, Angry, Sad }

        public enum Act
        {
            None, LookAround, CheckWatch, Phone, TapFoot, Tiptoe, Wave, Stretch, Yawn, Sip, Hiccup, Fan,
            Whisper, Chat, Clap, Cheer, Love, Angry, Furious, Sad, Gasp, Glare, Scold, Cower, Throw, Shrug, Dance, Facepalm
        }

        public enum Mood { Neutral, Happy, Love, Grumpy, Furious, Nervous, Sad }

        enum Eyes { Normal, Happy, Wide, Squint, Closed }
        enum Mouth { Flat, Smile, Frown, Open, Talk, O }
        enum Brows { None, Angry, Worried }

        struct Rig
        {
            public float legLX, legRX, armLX, armLZ, armRX, armRZ, hipY, hipYaw, hipPitch, lean, headPitch, headYaw, headRoll, squashX, squashY, standUp;
        }

        struct Face { public Eyes eyes; public Mouth mouth; public Brows brows; public bool tear; public float blush; }

        // ---------- shared state ----------

        /// <summary>Every live guest (crowd, bench, tables, couple on stage).</summary>
        public static readonly List<GuestView> All = new List<GuestView>();
        /// <summary>Current drama 0..100; guests get nervous when it is high and relaxed when low.</summary>
        public static float HallDrama;
        /// <summary>Where the stage / couple is, for "peek at the stage" looks.</summary>
        public static Vector3 StagePoint = new Vector3(0, 1, 10);
        /// <summary>HUD hook that shows an emote bubble above an anchor: (anchor, icon, seconds).</summary>
        public static System.Action<Transform, string, float> EmoteSink;
        static float nextAmbientEmote;

        // ---------- per guest ----------

        public Guest Guest;
        public Archetype Look;
        public string DisplayName = "";
        public Pose CurrentPose { get; private set; } = Pose.Idle;
        public Act CurrentAct => act;
        public Transform BadgeAnchor { get; private set; }
        public Transform EmoteAnchor { get; private set; }
        public float WalkSpeed => Look == Archetype.Grandma ? 2.1f : Look == Archetype.Uncle ? 2.6f : Look == Archetype.Kid ? 3.5f : 3.2f;

        /// <summary>Still waiting for a table seat (in the crowd or on a lounge sofa).</summary>
        public bool Waiting;
        /// <summary>In the crowd with no free lane out.</summary>
        public bool Blocked;
        public float WaitTime;
        /// <summary>Story counterpart: couple partner, ex, the other gossip, grandma/uncle.</summary>
        public GuestView Partner;
        public LinkKind PartnerKind;
        public Mood CurrentMood = Mood.Neutral;
        public bool Ambient = true;

        Transform visual, hips, head, armL, armR, legL, legR, mouth, browL, browR, cloak;
        readonly Transform[] eyes = new Transform[2], blush = new Transform[2], happyEyes = new Transform[2];
        readonly GameObject[] shines = new GameObject[2];
        GameObject mouthSmile, mouthFrown, mouthOpen, tear, phone, fan, flute;
        readonly List<Renderer> outfitParts = new List<Renderer>();
        GameObject freeRing, relatedRing;
        float phase, poseTime, seed;
        Vector3 shake;
        bool hidden, seated;
        Color outfit, skin, hair;

        Act act; float actT, actDur;
        Transform lookTarget; Vector3? lookPoint; float lookUntil, lookYaw, bodyYaw;
        float nextAmbient, nextBlink, blinkUntil;

        static readonly Vector3 EyeNormal = new Vector3(0.075f, 0.09f, 0.05f);
        static readonly Vector3 BlushSize = new Vector3(0.09f, 0.05f, 0.04f);

        public static GuestView Create(Guest g, Transform parent, int variant, Archetype look, string displayName)
        {
            var go = new GameObject("Guest " + displayName + " " + g);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<GuestView>();
            v.Guest = g; v.Look = look; v.DisplayName = displayName; v.seed = variant * 1.37f;
            v.outfit = Palette.Invite(g.Color);
            v.skin = Palette.Skins[(variant * 7 + 1) % Palette.Skins.Length];
            v.hair = look == Archetype.Grandma ? Palette.Hex("#EDE8F0") : look == Archetype.Uncle ? Palette.Hex("#8C8C94") : Palette.Hairs[variant % Palette.Hairs.Length];
            v.Build();
            var col = go.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0, 0.65f, 0); col.height = 1.35f; col.radius = 0.42f;
            v.SetHidden(g.Hidden);
            v.Waiting = true;
            return v;
        }

        public static GuestView CreateCustom(string name, Transform parent, Archetype look, Color outfit, Color skin, Color hair)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<GuestView>();
            v.Look = look; v.DisplayName = name; v.outfit = outfit; v.skin = skin; v.hair = hair; v.seed = name.Length * 2.1f;
            v.Build();
            return v;
        }

        void OnEnable() { All.Add(this); nextAmbient = Time.time + Random.Range(0.6f, 4.5f); nextBlink = Time.time + Random.Range(1f, 4f); }
        void OnDisable() { All.Remove(this); }

        // ---------- building ----------

        GameObject P(PrimitiveType t, Transform parent, Vector3 pos, Vector3 scale, Color c, string name = null) => Palette.Prim(t, parent, pos, scale, c, name: name);
        GameObject M(Mesh m, Transform parent, Vector3 pos, Vector3 scale, Color c, string name = "Mesh") => MeshFactory.Make(m, parent, pos, scale, c, name);
        GameObject Outfit(GameObject go) { outfitParts.Add(go.GetComponent<Renderer>()); return go; }

        void Build()
        {
            visual = Palette.Pivot(transform, "Visual", Vector3.zero);
            hips = Palette.Pivot(visual, "Hips", new Vector3(0, 0.42f, 0));
            head = Palette.Pivot(hips, "Head", new Vector3(0, 0.66f, 0));
            BadgeAnchor = Palette.Pivot(visual, "BadgeAnchor", new Vector3(0, 1.72f, 0));
            EmoteAnchor = Palette.Pivot(transform, "EmoteAnchor", new Vector3(0, 1.62f, 0));

            bool girlish = Look == Archetype.Girl || Look == Archetype.Auntie || Look == Archetype.Bride || Look == Archetype.Grandma || Look == Archetype.Kid;
            Color legColor = girlish ? skin : Look == Archetype.Groom ? Palette.Hex("#2E2640") : Palette.Hex("#46506E");
            Color shoe = Look == Archetype.Bride ? Color.white : Palette.Shoe;
            float legLen = Look == Archetype.Grandma ? 0.26f : 0.3f;
            legL = Limb("LegL", new Vector3(0.1f, 0, 0), legLen, girlish ? 0.1f : 0.13f, legColor, shoe, false);
            legR = Limb("LegR", new Vector3(-0.1f, 0, 0), legLen, girlish ? 0.1f : 0.13f, legColor, shoe, false);
            float shoulderX = Look == Archetype.Uncle ? 0.33f : 0.25f;
            armL = Limb("ArmL", new Vector3(shoulderX, 0.33f, 0), 0.26f, 0.1f, outfit, skin, true);
            armR = Limb("ArmR", new Vector3(-shoulderX, 0.33f, 0), 0.26f, 0.1f, outfit, skin, true);

            BuildFace();
            switch (Look)
            {
                case Archetype.Girl: BuildGirl(); break;
                case Archetype.Boy: BuildBoy(); break;
                case Archetype.Grandma: BuildGrandma(); break;
                case Archetype.Uncle: BuildUncle(); break;
                case Archetype.Auntie: BuildAuntie(); break;
                case Archetype.Bride: BuildBride(); break;
                case Archetype.Groom: BuildGroom(); break;
                case Archetype.Kid: BuildKid(); break;
            }
            BuildExpressions();
            BuildProps();

            freeRing = MeshFactory.Make(MeshFactory.Torus(0.42f, 0.045f), transform, new Vector3(0, 0.03f, 0), Vector3.one, Palette.Gold, "FreeRing");
            freeRing.SetActive(false);
            relatedRing = MeshFactory.Make(MeshFactory.Torus(0.5f, 0.06f), transform, new Vector3(0, 0.04f, 0), Vector3.one, Palette.Accent, "RelatedRing");
            relatedRing.SetActive(false);

            // hooded cloak shown while the guest is still unknown ("?")
            cloak = Palette.Pivot(transform, "Cloak", Vector3.zero);
            M(MeshFactory.Frustum(0.22f, 0.4f, 0.95f), cloak, new Vector3(0, 0.02f, 0), Vector3.one, Palette.Hidden, "Robe");
            P(PrimitiveType.Sphere, cloak, new Vector3(0, 1.08f, -0.02f), new Vector3(0.66f, 0.66f, 0.64f), Palette.Hidden, "Hood");
            P(PrimitiveType.Sphere, cloak, new Vector3(0, 1.04f, 0.16f), new Vector3(0.42f, 0.4f, 0.4f), Palette.Hex("#6E6270"), "Shadow");
            cloak.gameObject.SetActive(false);
        }

        Transform Limb(string name, Vector3 pivotPos, float length, float thick, Color c, Color end, bool arm)
        {
            var p = Palette.Pivot(hips, name, pivotPos);
            var sleeve = P(PrimitiveType.Capsule, p, new Vector3(0, -length / 2, 0), new Vector3(thick, length / 2, thick), c);
            if (arm) outfitParts.Add(sleeve.GetComponent<Renderer>());
            if (arm) P(PrimitiveType.Sphere, p, new Vector3(0, -length, 0.01f), Vector3.one * (thick * 1.25f), end, "Hand");
            else P(PrimitiveType.Sphere, p, new Vector3(0, -length, 0.04f), new Vector3(thick * 1.3f, thick * 0.9f, thick * 1.7f), end, "Shoe");
            return p;
        }

        void BuildFace()
        {
            P(PrimitiveType.Sphere, head, Vector3.zero, Vector3.one * 0.58f, skin, "Skull");
            for (int i = 0; i < 2; i++)
            {
                float x = i == 0 ? 0.105f : -0.105f;
                eyes[i] = P(PrimitiveType.Sphere, head, new Vector3(x, 0.01f, 0.27f), EyeNormal, Palette.Ink, "Eye").transform;
                shines[i] = P(PrimitiveType.Sphere, head, new Vector3(x + 0.015f, 0.035f, 0.295f), Vector3.one * 0.025f, Color.white, "Shine");
                blush[i] = P(PrimitiveType.Sphere, head, new Vector3(x * 1.75f, -0.08f, 0.235f), BlushSize, Look == Archetype.Uncle ? Palette.Hex("#F07070") : Palette.Flower, "Blush").transform;
            }
            mouth = P(PrimitiveType.Sphere, head, new Vector3(0, -0.11f, 0.265f), new Vector3(0.12f, 0.045f, 0.04f), Look == Archetype.Auntie ? Palette.Hex("#D63A5A") : Palette.Hex("#9A3F55"), "Mouth").transform;
            browL = P(PrimitiveType.Cube, head, new Vector3(0.1f, 0.11f, 0.27f), new Vector3(0.1f, 0.022f, 0.02f), Palette.Ink, "Brow").transform;
            browR = P(PrimitiveType.Cube, head, new Vector3(-0.1f, 0.11f, 0.27f), new Vector3(0.1f, 0.022f, 0.02f), Palette.Ink, "Brow").transform;
            bool frown = Guest != null && (Guest.Has(LinkKind.Ex) || Guest.Has(LinkKind.Rival));
            browL.gameObject.SetActive(frown); browR.gameObject.SetActive(frown);
            if (frown) { browL.localRotation = Quaternion.Euler(0, 0, 18); browR.localRotation = Quaternion.Euler(0, 0, -18); }
        }

        void BuildGirl()
        {
            Outfit(P(PrimitiveType.Sphere, hips, new Vector3(0, 0.3f, 0), new Vector3(0.4f, 0.36f, 0.34f), outfit, "Torso"));
            Outfit(M(MeshFactory.Frustum(0.17f, 0.36f, 0.42f), hips, new Vector3(0, -0.15f, 0), Vector3.one, outfit, "Skirt"));
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.1f, -0.03f), new Vector3(0.63f, 0.47f, 0.62f), hair, "HairCap");
            P(PrimitiveType.Sphere, head, new Vector3(0, -0.12f, -0.17f), new Vector3(0.56f, 0.64f, 0.34f), hair, "HairBack");
            P(PrimitiveType.Sphere, head, new Vector3(0.05f, 0.2f, 0.17f), new Vector3(0.48f, 0.16f, 0.28f), hair, "Bangs");
            Color bow = outfit == Palette.Invite('R') ? Color.white : Palette.Hex("#EF4F78");
            P(PrimitiveType.Sphere, head, new Vector3(-0.2f, 0.27f, 0.02f), new Vector3(0.13f, 0.1f, 0.06f), bow, "BowL");
            P(PrimitiveType.Sphere, head, new Vector3(-0.08f, 0.31f, 0.02f), new Vector3(0.13f, 0.1f, 0.06f), bow, "BowR");
        }

        void BuildBoy()
        {
            Outfit(M(MeshFactory.Frustum(0.24f, 0.27f, 0.44f), hips, new Vector3(0, 0.0f, 0), Vector3.one, outfit, "Shirt"));
            P(PrimitiveType.Sphere, hips, new Vector3(0, 0.44f, 0), new Vector3(0.48f, 0.12f, 0.44f), outfit, "Shoulders");
            M(MeshFactory.Frustum(0.25f, 0.24f, 0.12f), hips, new Vector3(0, -0.06f, 0), Vector3.one, Palette.Hex("#46506E"), "Belt");
            P(PrimitiveType.Cube, hips, new Vector3(0.045f, 0.4f, 0.25f), new Vector3(0.07f, 0.06f, 0.03f), Palette.Ink, "BowTieL").transform.localRotation = Quaternion.Euler(0, 0, 30);
            P(PrimitiveType.Cube, hips, new Vector3(-0.045f, 0.4f, 0.25f), new Vector3(0.07f, 0.06f, 0.03f), Palette.Ink, "BowTieR").transform.localRotation = Quaternion.Euler(0, 0, -30);
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.12f, -0.02f), new Vector3(0.62f, 0.42f, 0.62f), hair, "HairCap");
            P(PrimitiveType.Sphere, head, new Vector3(0.1f, 0.22f, 0.16f), new Vector3(0.36f, 0.14f, 0.26f), hair, "Fringe").transform.localRotation = Quaternion.Euler(0, 0, -12);
        }

        void BuildGrandma()
        {
            visual.localScale = Vector3.one * 0.92f;
            Outfit(M(MeshFactory.Frustum(0.22f, 0.32f, 0.5f), hips, new Vector3(0, -0.04f, 0), Vector3.one, outfit, "Cardigan"));
            M(MeshFactory.Frustum(0.26f, 0.38f, 0.32f), hips, new Vector3(0, -0.22f, 0), Vector3.one, Palette.Hex("#7A6890"), "LongSkirt");
            var shawl = M(MeshFactory.Torus(0.21f, 0.075f), hips, new Vector3(0, 0.42f, 0), Vector3.one, Palette.Hex("#FFF1E0"), "Shawl");
            shawl.transform.localRotation = Quaternion.Euler(12, 0, 0);
            var pearls = M(MeshFactory.Torus(0.17f, 0.025f), hips, new Vector3(0, 0.46f, 0.04f), Vector3.one, Color.white, "Pearls");
            pearls.transform.localRotation = Quaternion.Euler(30, 0, 0);
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.1f, -0.04f), new Vector3(0.62f, 0.44f, 0.62f), hair, "HairCap");
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.34f, -0.12f), Vector3.one * 0.24f, hair, "Bun");
            foreach (var x in new[] { -0.11f, 0.11f })
            {
                var lens = M(MeshFactory.Torus(0.085f, 0.014f), head, new Vector3(x, 0.01f, 0.29f), Vector3.one, Palette.Hex("#B8862E"), "Glasses");
                lens.transform.localRotation = Quaternion.Euler(90, 0, 0);
            }
            P(PrimitiveType.Cube, head, new Vector3(0, 0.02f, 0.3f), new Vector3(0.05f, 0.015f, 0.015f), Palette.Hex("#B8862E"), "Bridge");
            // cane in the right hand
            var cane = P(PrimitiveType.Cylinder, armR, new Vector3(0, -0.5f, 0.05f), new Vector3(0.045f, 0.24f, 0.045f), Palette.Hex("#8A5A3A"), "Cane");
            var hook = M(MeshFactory.Torus(0.07f, 0.022f, 0.5f), armR, new Vector3(0.07f, -0.27f, 0.05f), Vector3.one, Palette.Hex("#8A5A3A"), "CaneHook");
            hook.transform.localRotation = Quaternion.Euler(90, 0, 0);
            hips.localRotation = Quaternion.Euler(8, 0, 0);
        }

        void BuildUncle()
        {
            Outfit(P(PrimitiveType.Sphere, hips, new Vector3(0, 0.24f, 0.02f), new Vector3(0.68f, 0.62f, 0.6f), outfit, "Belly"));
            P(PrimitiveType.Sphere, hips, new Vector3(0, 0.22f, 0.3f), Vector3.one * 0.05f, Palette.Hex("#F3D2B5"), "ShirtButton");
            var tie = P(PrimitiveType.Cube, hips, new Vector3(0.04f, 0.38f, 0.27f), new Vector3(0.08f, 0.24f, 0.03f), Palette.Hex("#B5325A"), "LooseTie");
            tie.transform.localRotation = Quaternion.Euler(-20, 0, 14);
            M(MeshFactory.Frustum(0.3f, 0.3f, 0.1f), hips, new Vector3(0, -0.08f, 0), Vector3.one, Palette.Hex("#46506E"), "Belt");
            foreach (var x in new[] { -0.26f, 0.26f })
                P(PrimitiveType.Sphere, head, new Vector3(x, 0.0f, -0.05f), new Vector3(0.14f, 0.22f, 0.24f), hair, "SideHair");
            P(PrimitiveType.Sphere, head, new Vector3(0, -0.02f, -0.24f), new Vector3(0.4f, 0.2f, 0.12f), hair, "BackHair");
            P(PrimitiveType.Capsule, head, new Vector3(0, -0.085f, 0.265f), new Vector3(0.07f, 0.12f, 0.06f), Palette.Hex("#5E4A44"), "Mustache").transform.localRotation = Quaternion.Euler(0, 0, 90);
            P(PrimitiveType.Sphere, head, new Vector3(0, -0.02f, 0.3f), Vector3.one * 0.1f, Palette.Hex("#E86A6A"), "RedNose");
            mouth.localPosition = new Vector3(0, -0.15f, 0.25f);
        }

        void BuildAuntie()
        {
            Outfit(P(PrimitiveType.Sphere, hips, new Vector3(0, 0.3f, 0), new Vector3(0.46f, 0.38f, 0.38f), outfit, "Torso"));
            Outfit(M(MeshFactory.Frustum(0.2f, 0.34f, 0.48f), hips, new Vector3(0, -0.2f, 0), Vector3.one, outfit, "Dress"));
            Color curl = Palette.Hex("#8A3B2B");
            var curls = new[] { new Vector3(0, 0.26f, -0.04f), new Vector3(0.2f, 0.2f, -0.06f), new Vector3(-0.2f, 0.2f, -0.06f), new Vector3(0.26f, 0.0f, -0.1f), new Vector3(-0.26f, 0.0f, -0.1f), new Vector3(0.12f, 0.1f, -0.22f), new Vector3(-0.12f, 0.1f, -0.22f), new Vector3(0, 0.05f, -0.26f) };
            foreach (var c in curls) P(PrimitiveType.Sphere, head, c, Vector3.one * 0.3f, curl, "Curl");
            foreach (var x in new[] { -0.29f, 0.29f })
            {
                var hoop = M(MeshFactory.Torus(0.06f, 0.014f), head, new Vector3(x, -0.12f, 0.02f), Vector3.one, Palette.Gold, "Earring");
                hoop.transform.localRotation = Quaternion.Euler(0, 0, 90);
            }
            P(PrimitiveType.Cube, armL, new Vector3(0.02f, -0.3f, 0.06f), new Vector3(0.18f, 0.15f, 0.07f), Palette.Hex("#25A5A0"), "Handbag");
        }

        void BuildBride()
        {
            Outfit(P(PrimitiveType.Sphere, hips, new Vector3(0, 0.3f, 0), new Vector3(0.4f, 0.36f, 0.34f), outfit, "Torso"));
            Outfit(M(MeshFactory.Frustum(0.18f, 0.5f, 0.55f), hips, new Vector3(0, -0.4f, 0), Vector3.one, outfit, "Gown"));
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.1f, -0.03f), new Vector3(0.63f, 0.47f, 0.62f), hair, "HairCap");
            P(PrimitiveType.Sphere, head, new Vector3(0, -0.08f, -0.2f), new Vector3(0.7f, 0.8f, 0.3f), Palette.Hex("#F7F3FA"), "Veil");
            P(PrimitiveType.Sphere, armL, new Vector3(-0.05f, -0.3f, 0.1f), Vector3.one * 0.16f, Palette.Flower, "Bouquet");
        }

        void BuildKid()
        {
            visual.localScale = Vector3.one * 0.7f;
            head.localScale = Vector3.one * 1.18f;
            head.localPosition = new Vector3(0, 0.6f, 0);
            Outfit(M(MeshFactory.Frustum(0.2f, 0.28f, 0.42f), hips, new Vector3(0, -0.06f, 0), Vector3.one, outfit, "Overall"));
            P(PrimitiveType.Sphere, hips, new Vector3(0, 0.36f, 0), new Vector3(0.42f, 0.14f, 0.38f), Color.white, "Collar");
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.12f, -0.03f), new Vector3(0.62f, 0.44f, 0.62f), hair, "HairCap");
            foreach (var x in new[] { -0.3f, 0.3f })
                P(PrimitiveType.Sphere, head, new Vector3(x, 0.08f, -0.06f), Vector3.one * 0.2f, hair, "Pigtail");
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.34f, 0.02f), new Vector3(0.1f, 0.14f, 0.1f), hair, "Cowlick");
            // a balloon on a string, the kid's tell at a glance
            P(PrimitiveType.Cylinder, hips, new Vector3(0.27f, 0.38f, 0.02f), new Vector3(0.012f, 0.3f, 0.012f), Palette.Hex("#EDE8F5"), "String");
            P(PrimitiveType.Sphere, hips, new Vector3(0.29f, 0.84f, 0.02f), new Vector3(0.28f, 0.32f, 0.28f), Palette.Hex("#FF8FA3"), "Balloon");
        }

        void BuildGroom()
        {
            Outfit(M(MeshFactory.Frustum(0.24f, 0.27f, 0.44f), hips, new Vector3(0, 0.0f, 0), Vector3.one, outfit, "Suit"));
            P(PrimitiveType.Cube, hips, new Vector3(0, 0.28f, 0.24f), new Vector3(0.12f, 0.26f, 0.03f), Color.white, "ShirtFront");
            P(PrimitiveType.Cube, hips, new Vector3(0, 0.4f, 0.26f), new Vector3(0.14f, 0.05f, 0.03f), Palette.Ink, "BowTie");
            P(PrimitiveType.Sphere, head, new Vector3(0, 0.12f, -0.02f), new Vector3(0.62f, 0.42f, 0.62f), hair, "HairCap");
        }

        // ---------- expressions & props ----------

        void BuildExpressions()
        {
            Vector3 mp = mouth.localPosition;
            Color lip = Look == Archetype.Auntie ? Palette.Hex("#D63A5A") : Palette.Hex("#9A3F55");
            mouthSmile = M(MeshFactory.Torus(0.055f, 0.017f, 0.5f), head, mp + new Vector3(0, 0.03f, 0.005f), Vector3.one, lip, "Smile");
            mouthSmile.transform.localRotation = Quaternion.Euler(90, 0, 0);
            mouthFrown = M(MeshFactory.Torus(0.05f, 0.017f, 0.5f), head, mp + new Vector3(0, -0.035f, 0.005f), Vector3.one, lip, "Frown");
            mouthFrown.transform.localRotation = Quaternion.Euler(-90, 0, 0);
            mouthOpen = P(PrimitiveType.Sphere, head, mp, new Vector3(0.11f, 0.1f, 0.05f), Palette.Hex("#7A2E40"), "MouthOpen");
            for (int i = 0; i < 2; i++)
            {
                var e = M(MeshFactory.Torus(0.05f, 0.015f, 0.5f), head, eyes[i].localPosition + new Vector3(0, -0.02f, 0.012f), Vector3.one, Palette.Ink, "HappyEye");
                e.transform.localRotation = Quaternion.Euler(-90, 0, 0);
                happyEyes[i] = e.transform;
            }
            tear = P(PrimitiveType.Sphere, head, new Vector3(0.12f, -0.03f, 0.27f), new Vector3(0.05f, 0.07f, 0.04f), Palette.Hex("#8FD3FF"), "Tear");
            mouthSmile.SetActive(false); mouthFrown.SetActive(false); mouthOpen.SetActive(false); tear.SetActive(false);
            foreach (var h in happyEyes) h.gameObject.SetActive(false);
        }

        void BuildProps()
        {
            // champagne flute (left hand). The drunk uncle never puts his down.
            var f = Palette.Pivot(armL, "Flute", new Vector3(0, -0.3f, 0.07f));
            P(PrimitiveType.Cylinder, f, new Vector3(0, 0.02f, 0), new Vector3(0.025f, 0.06f, 0.025f), Palette.Hex("#EDE8F5"), "Stem");
            M(MeshFactory.Frustum(0.07f, 0.03f, 0.16f), f, new Vector3(0, 0.08f, 0), Vector3.one, Palette.Gold, "Bowl");
            f.localRotation = Quaternion.Euler(180, 0, 0);
            flute = f.gameObject;
            flute.SetActive(Look == Archetype.Uncle);
            // phone (right hand)
            phone = P(PrimitiveType.Cube, armR, new Vector3(0, -0.31f, 0.06f), new Vector3(0.12f, 0.2f, 0.03f), Palette.Hex("#3B3550"), "Phone");
            P(PrimitiveType.Cube, phone.transform, new Vector3(0, 0, 0.6f), new Vector3(0.8f, 0.85f, 0.4f), Palette.Hex("#9FD8FF"), "Screen");
            phone.SetActive(false);
            // hand fan (grandma, left hand)
            fan = M(MeshFactory.Frustum(0.2f, 0.02f, 0.02f), armL, new Vector3(0, -0.32f, 0.08f), Vector3.one, Palette.Hex("#F59BB4"), "Fan");
            fan.transform.localRotation = Quaternion.Euler(90, 0, 0);
            fan.SetActive(false);
        }

        // ---------- state ----------

        public void SetHidden(bool h)
        {
            hidden = h;
            visual.gameObject.SetActive(!h);
            cloak.gameObject.SetActive(h);
        }

        public bool IsHidden => hidden;

        public IEnumerator Reveal()
        {
            if (!hidden) yield break;
            SetHidden(false);
            for (float t = 0; t < 0.3f; t += Time.deltaTime)
            {
                float k = t / 0.3f;
                visual.localScale = Vector3.one * BaseScale * (1 + Mathf.Sin(k * Mathf.PI) * 0.25f);
                yield return null;
            }
            visual.localScale = Vector3.one * BaseScale;
            Play(Act.Cheer, 0.9f);
            Emote("sparkle", 1.3f);
        }

        float BaseScale => Look == Archetype.Grandma ? 0.92f : Look == Archetype.Kid ? 0.7f : 1f;

        public void SetFree(bool free)
        {
            if (freeRing) freeRing.SetActive(free && !hidden);
            if (Waiting) Blocked = !free;
        }

        public void SetRelated(bool on) { if (relatedRing) relatedRing.SetActive(on); }

        public void SetPose(Pose p)
        {
            if (p == Pose.Walk || p == Pose.Lift || p == Pose.Idle) seated = false;
            if (p == Pose.Sit) seated = true;
            if (p == Pose.Walk || p == Pose.Lift) StopAct();
            if (p != CurrentPose) { CurrentPose = p; poseTime = 0; }
        }

        public void Shake() { StartCoroutine(ShakeRoutine()); }

        IEnumerator ShakeRoutine()
        {
            for (float t = 0; t < 0.35f; t += Time.deltaTime) { shake = new Vector3(Mathf.Sin(t * 60) * 0.06f, 0, 0); yield return null; }
            shake = Vector3.zero;
        }

        // ---------- acts, looks, emotes ----------

        public static float DefaultDuration(Act a)
        {
            switch (a)
            {
                case Act.LookAround: return 2.4f;
                case Act.CheckWatch: return 1.7f;
                case Act.Phone: return 2.6f;
                case Act.TapFoot: return 1.8f;
                case Act.Tiptoe: return 1.6f;
                case Act.Wave: return 1.3f;
                case Act.Stretch: return 1.6f;
                case Act.Yawn: return 1.5f;
                case Act.Sip: return 1.5f;
                case Act.Hiccup: return 0.6f;
                case Act.Fan: return 2.2f;
                case Act.Whisper: return 2.2f;
                case Act.Chat: return 2.4f;
                case Act.Clap: return 1.4f;
                case Act.Cheer: return 1.2f;
                case Act.Love: return 1.8f;
                case Act.Angry: return 1.3f;
                case Act.Furious: return 1.8f;
                case Act.Sad: return 2.0f;
                case Act.Gasp: return 0.9f;
                case Act.Glare: return 1.8f;
                case Act.Scold: return 2.0f;
                case Act.Cower: return 1.8f;
                case Act.Throw: return 0.9f;
                case Act.Shrug: return 1.0f;
                case Act.Dance: return 2.2f;
                case Act.Facepalm: return 1.5f;
                default: return 1f;
            }
        }

        /// <summary>Play a timed act on top of the current stance. Optional look target and emote bubble.</summary>
        public void Play(Act a, float duration = -1, Transform look = null, string emote = null)
        {
            if (CurrentPose == Pose.Walk || CurrentPose == Pose.Lift) return;
            act = a; actT = 0; actDur = duration > 0 ? duration : DefaultDuration(a);
            if (look) LookAt(look, actDur + 0.3f);
            if (emote != null) Emote(emote, Mathf.Max(1.2f, actDur));
            nextAmbient = Mathf.Max(nextAmbient, Time.time + actDur + Random.Range(1f, 2.5f));
        }

        public IEnumerator PlayAndWait(Act a, float duration = -1, Transform look = null, string emote = null)
        {
            Play(a, duration, look, emote);
            yield return new WaitForSeconds(duration > 0 ? duration : DefaultDuration(a));
        }

        public void StopAct() { act = Act.None; }

        public void LookAt(Transform target, float seconds) { lookTarget = target; lookPoint = null; lookUntil = Time.time + seconds; }
        public void LookAt(Vector3 point, float seconds) { lookTarget = null; lookPoint = point; lookUntil = Time.time + seconds; }

        /// <summary>Emote bubble above the head. Ambient emotes are rate-limited hall-wide so the screen never floods.</summary>
        public void Emote(string icon, float seconds = 1.5f, bool ambient = false)
        {
            if (ambient)
            {
                if (Time.time < nextAmbientEmote) return;
                nextAmbientEmote = Time.time + 0.75f;
            }
            EmoteSink?.Invoke(EmoteAnchor, icon, seconds);
        }

        /// <summary>Cream on the face after a flying cake.</summary>
        public void Splat(float seconds = 3.5f) { StartCoroutine(SplatRoutine(seconds)); }

        IEnumerator SplatRoutine(float seconds)
        {
            var root = Palette.Pivot(head, "Splat", Vector3.zero);
            var cream = Palette.Hex("#FFF8F0");
            P(PrimitiveType.Sphere, root, new Vector3(0.05f, 0.04f, 0.27f), new Vector3(0.3f, 0.24f, 0.12f), cream);
            P(PrimitiveType.Sphere, root, new Vector3(-0.12f, -0.06f, 0.25f), new Vector3(0.2f, 0.18f, 0.1f), cream);
            P(PrimitiveType.Sphere, root, new Vector3(0.1f, -0.12f, 0.24f), new Vector3(0.14f, 0.16f, 0.08f), cream);
            P(PrimitiveType.Sphere, root, new Vector3(0.0f, 0.2f, 0.22f), Vector3.one * 0.1f, Palette.Accent);
            for (float t = 0; t < 0.18f; t += Time.deltaTime) { root.localScale = Vector3.one * Mathf.Lerp(0.3f, 1.1f, t / 0.18f); yield return null; }
            root.localScale = Vector3.one;
            yield return new WaitForSeconds(seconds);
            for (float t = 0; t < 0.3f; t += Time.deltaTime) { root.localScale = Vector3.one * (1 - t / 0.3f); yield return null; }
            Destroy(root.gameObject);
        }

        // ---------- ambient life ----------

        struct Option { public Act act; public float w; public Transform look; public Vector3? point; public string emote; }

        void TickAmbient()
        {
            if (!Ambient || act != Act.None || Time.time < nextAmbient) return;
            if (CurrentPose != Pose.Idle && CurrentPose != Pose.Sit) return;
            nextAmbient = Time.time + (seated ? Random.Range(2.8f, 6.5f) : Random.Range(2.2f, 5.2f));
            if (hidden) { if (Random.value < 0.2f) Emote("question", 1.2f, true); return; }

            var opts = new List<Option>(12);
            mergeTarget = opts;
            void Add(Act a, float w, Transform look = null, Vector3? point = null, string emote = null) { if (w > 0) opts.Add(new Option { act = a, w = w, look = look, point = point, emote = emote }); }
            var partner = Partner && Partner.isActiveAndEnabled ? Partner : null;
            Transform pt = partner ? partner.head : null;
            bool young = Look == Archetype.Girl || Look == Archetype.Boy;
            bool kid = Look == Archetype.Kid;
            bool high = HallDrama >= 70, calm = HallDrama <= 20;

            if (Guest == null)
            {
                // bride & groom on stage
                Add(Act.LookAround, 2); Add(Act.Wave, 1.5f, null, null, "heart");
                Add(Act.Love, 1.5f, pt, null, "heart");
                if (high) { Add(Act.Facepalm, 2.5f, null, null, "sweat"); Add(Act.Gasp, 1.2f, null, null, "exclaim"); }
                if (calm) { Add(Act.Dance, 1.5f, null, null, "note"); Add(Act.Clap, 1); }
            }
            else if (Waiting && !seated)
            {
                // standing in the crowd
                Add(Act.LookAround, 2.5f);
                Add(Act.CheckWatch, 0.8f + (Blocked ? 1.5f : 0) + WaitTime / 25f, null, null, WaitTime > 20 ? "dots" : null);
                Add(Act.TapFoot, Blocked ? 1.8f + WaitTime / 30f : 0.3f, null, null, WaitTime > 25 ? "anger" : null);
                Add(Act.Tiptoe, Blocked ? 1.4f : 0.4f, null, StagePoint);
                Add(Act.Shrug, Blocked ? 0.9f : 0, null, null, "dots");
                Add(Act.Wave, Blocked ? 0 : 1.6f, null, null, "exclaim");
                Add(Act.Phone, young ? 1.4f : 0);
                Add(Act.Stretch, WaitTime > 18 ? 1f : 0.2f);
                Add(Act.Yawn, WaitTime > 28 ? 1.3f : 0, null, null, "zzz");
                AddArchetype(partner, pt, false);
                if (high) { Add(Act.Gasp, 1.4f, null, StagePoint, "sweat"); Add(Act.Cower, 0.4f); }
                if (calm) { Add(Act.Clap, 0.8f, null, StagePoint); Add(Act.Dance, 0.6f, null, null, "note"); }
            }
            else if (Waiting)
            {
                // sitting on a lounge sofa, waiting for a table
                Add(Act.LookAround, 2); Add(Act.CheckWatch, 1.8f, null, null, "dots");
                Add(Act.Phone, young ? 1.6f : 0); Add(Act.Yawn, 1f, null, null, "zzz");
                Add(Act.TapFoot, Look == Archetype.Grandma ? 1.2f : 0.6f);
                AddArchetype(partner, pt, true);
                if (high) Add(Act.Gasp, 1f, null, null, "sweat");
            }
            else
            {
                // seated at a table
                var mate = NearestSeatedMate();
                Add(Act.LookAround, 1.5f, null, StagePoint);
                Add(Act.Chat, mate ? 3f : 0, mate, null, "dots");
                Add(Act.Sip, 1.6f);
                Add(Act.Clap, 0.8f, null, StagePoint);
                Add(Act.Phone, young ? 0.6f : 0);
                AddArchetype(partner, pt, true);
                switch (CurrentMood)
                {
                    case Mood.Love: Add(Act.Love, 3f, pt, null, "heart"); break;
                    case Mood.Happy: Add(Act.Cheer, 0.6f, null, null, "sparkle"); Add(Act.Dance, 0.8f, null, null, "note"); break;
                    case Mood.Grumpy: Add(Act.Glare, 3f, pt, null, "anger"); break;
                    case Mood.Furious: Add(Act.Angry, 2f, pt, null, "anger"); Add(Act.Glare, 2f, pt, null, "anger"); break;
                    case Mood.Nervous: Add(Act.Cower, 1.5f, pt, null, "sweat"); break;
                    case Mood.Sad: Add(Act.Sad, 3f, pt, null, "brokenheart"); break;
                }
                if (high) Add(Act.Gasp, 0.8f, null, null, "sweat");
                if (calm) Add(Act.Dance, 0.8f, null, null, "note");
            }

            float sum = 0; foreach (var o in opts) sum += o.w;
            if (sum <= 0) return;
            float pick = Random.value * sum;
            foreach (var o in opts)
            {
                pick -= o.w;
                if (pick > 0) continue;
                Play(o.act, -1, o.look);
                if (o.point.HasValue) LookAt(o.point.Value, actDur);
                if (o.emote != null && Random.value < 0.5f) Emote(o.emote, 1.4f, true);
                return;
            }
        }

        void AddArchetypeOption(List<Option> opts, Act a, float w, Transform look = null, string emote = null) { if (w > 0) opts.Add(new Option { act = a, w = w, look = look, emote = emote }); }

        // archetype / relationship flavour shared by every waiting context
        void AddArchetype(GuestView partner, Transform pt, bool sitting)
        {
            pendingArchetype.Clear();
            switch (Look)
            {
                case Archetype.Uncle:
                    AddArchetypeOption(pendingArchetype, Act.Sip, 3f); AddArchetypeOption(pendingArchetype, Act.Hiccup, 2.2f, null, "dots");
                    AddArchetypeOption(pendingArchetype, Act.Dance, sitting ? 0.6f : 1.2f, null, "note");
                    if (partner && partner.Look == Archetype.Grandma) AddArchetypeOption(pendingArchetype, Act.Cower, 0.8f, pt, "sweat");
                    break;
                case Archetype.Grandma:
                    AddArchetypeOption(pendingArchetype, Act.Fan, 2.5f);
                    if (partner && partner.Look == Archetype.Uncle) AddArchetypeOption(pendingArchetype, Act.Glare, 1.4f, pt, "anger");
                    break;
                case Archetype.Kid:
                    AddArchetypeOption(pendingArchetype, Act.Dance, 2f, null, "note");
                    AddArchetypeOption(pendingArchetype, Act.Wave, 1.4f);
                    AddArchetypeOption(pendingArchetype, Act.Tiptoe, 1.2f);
                    if (Blocked && WaitTime > 15) AddArchetypeOption(pendingArchetype, Act.Sad, 2f, pt, "sweat");
                    break;
                case Archetype.Auntie:
                    var n = NearestNeighbour(1.9f);
                    AddArchetypeOption(pendingArchetype, Act.Whisper, n ? 3.5f : 0.5f, n ? n.head : null, "dots");
                    if (partner && partner != n) AddArchetypeOption(pendingArchetype, Act.Wave, 1f, pt, "bubble");
                    break;
            }
            if (Guest != null && partner)
            {
                switch (PartnerKind)
                {
                    case LinkKind.Couple: AddArchetypeOption(pendingArchetype, Act.Love, 2.6f, pt, "heart"); break;
                    case LinkKind.CrushOn: AddArchetypeOption(pendingArchetype, Act.Love, 2.2f, pt, "crush"); break;
                    case LinkKind.CrushedBy: AddArchetypeOption(pendingArchetype, Act.Shrug, 0.8f, pt, "question"); break;
                    case LinkKind.Ex: AddArchetypeOption(pendingArchetype, Act.Glare, 2.6f, pt, "anger"); break;
                    case LinkKind.Rival: AddArchetypeOption(pendingArchetype, Act.Glare, 2.2f, pt, "rival"); break;
                    case LinkKind.Bff: AddArchetypeOption(pendingArchetype, Act.Wave, 2.2f, pt, "bff"); break;
                    case LinkKind.ParentOf: AddArchetypeOption(pendingArchetype, Act.Wave, 1.8f, pt, "baby"); break;
                    case LinkKind.ChildOf: AddArchetypeOption(pendingArchetype, Act.Tiptoe, 2.2f, pt, "baby"); break;
                    case LinkKind.Hands: AddArchetypeOption(pendingArchetype, Act.Love, 1.6f, pt, "hands"); break;
                }
            }
            // merged into the caller's list through the shared buffer
            mergeTarget?.AddRange(pendingArchetype);
        }

        readonly List<Option> pendingArchetype = new List<Option>();
        List<Option> mergeTarget;

        GuestView NearestNeighbour(float maxDist)
        {
            GuestView best = null; float bd = maxDist * maxDist;
            foreach (var o in All)
            {
                if (o == this || o.hidden || o.Guest == null || o.Waiting != Waiting) continue;
                float d = (o.transform.position - transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        Transform NearestSeatedMate()
        {
            GuestView best = null; float bd = 2.6f * 2.6f;
            foreach (var o in All)
            {
                if (o == this || o.Waiting || !o.seated || o.Guest == null) continue;
                float d = (o.transform.position - transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = o; }
            }
            return best ? best.head : null;
        }

        // ---------- per-frame animation ----------

        void Update()
        {
            if (hips == null) return;
            float dt = Time.deltaTime;
            poseTime += dt;
            float t = Time.time + seed;
            if (Waiting) WaitTime += dt;
            TickAmbient();
            if (act != Act.None) { actT += dt; if (actT >= actDur) act = Act.None; }

            if (hidden)
            {
                cloak.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 1.8f) * 4);
                cloak.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(t * 0.9f)) * 0.02f, 0) + shake;
                return;
            }

            var r = new Rig();
            var f = BaseFace();
            float sw = 0, legSit = 0, hop = 0, lean = 0, breathe = 1;
            bool sitStance = seated && CurrentPose != Pose.Walk && CurrentPose != Pose.Lift;
            switch (CurrentPose)
            {
                case Pose.Lift:
                    phase += dt * 11; sw = Mathf.Sin(phase) * 25; r.armLZ = r.armRZ = 35 + Mathf.Sin(phase * 0.7f) * 15;
                    f.eyes = Eyes.Wide; f.mouth = Mouth.O; break;
                case Pose.Walk:
                    phase += dt * (Look == Archetype.Grandma ? 7 : 11);
                    sw = Mathf.Sin(phase) * (Look == Archetype.Grandma ? 22 : 40);
                    hop = Mathf.Abs(Mathf.Sin(phase)) * 0.05f;
                    if (Look == Archetype.Uncle) lean = Mathf.Sin(t * 5) * 10;
                    break;
                default:
                    if (sitStance) legSit = 1;
                    else breathe = 1 + Mathf.Sin(t * 2.4f) * 0.02f;
                    if (Look == Archetype.Uncle) { lean = Mathf.Sin(t * 1.6f) * 7; if (Mathf.Repeat(t, 3.2f) < 0.15f) hop = 0.05f; }
                    if (Look == Archetype.Grandma) r.armRX = -10;
                    if (Look == Archetype.Auntie && act == Act.None && Mathf.Repeat(t, 4f) < 1.6f) { r.armRX = -130; f.mouth = Mouth.Talk; }
                    if (Blocked && Waiting && WaitTime > 12 && act == Act.None) { r.armLZ += 28; r.armRZ += 28; r.armLX += 18; r.armRX += 18; }   // hands on hips
                    break;
            }
            // looping "poses" kept for the table celebration / ending
            if (CurrentPose == Pose.Cheer) EvalAct(Act.Cheer, poseTime, 999, t, ref r, ref f);
            else if (CurrentPose == Pose.Angry) EvalAct(Act.Angry, poseTime, 999, t, ref r, ref f);
            else if (CurrentPose == Pose.Sad) EvalAct(Act.Sad, poseTime, 999, t, ref r, ref f);
            if (act != Act.None && CurrentPose != Pose.Walk && CurrentPose != Pose.Lift) EvalAct(act, actT, actDur, t, ref r, ref f);

            // look-at: head first, then a little body twist
            float wantHead = 0, wantBody = 0;
            Vector3? lp = lookTarget ? lookTarget.position : lookPoint;
            if (lp.HasValue && Time.time < lookUntil && CurrentPose != Pose.Walk)
            {
                var d = Quaternion.Inverse(transform.rotation) * (lp.Value - transform.position);
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                wantHead = Mathf.Clamp(yaw, -70, 70);
                wantBody = Mathf.Clamp(yaw - wantHead, -35, 35) + Mathf.Clamp(yaw, -25, 25) * 0.5f;
            }
            lookYaw = Mathf.Lerp(lookYaw, wantHead, dt * 7);
            bodyYaw = Mathf.Lerp(bodyYaw, wantBody, dt * 5);

            legSit *= 1 - r.standUp;
            float baseY = Look == Archetype.Grandma ? 0.4f : 0.42f;
            float basePitch = Look == Archetype.Grandma ? 8 : 0;
            hips.localPosition = new Vector3(0, baseY - legSit * 0.04f + hop + r.hipY, 0) + shake;
            hips.localScale = new Vector3(1 + r.squashX, breathe * (1 + r.squashY), 1 + r.squashX);
            hips.localRotation = Quaternion.Euler(basePitch + r.hipPitch, r.hipYaw + bodyYaw, 0);
            visual.localRotation = Quaternion.Euler(0, 0, lean + r.lean);
            legL.localRotation = Quaternion.Euler(sw - legSit * 85 + r.legLX, 0, 0);
            legR.localRotation = Quaternion.Euler(-sw - legSit * 85 + r.legRX, 0, 0);
            armL.localRotation = Quaternion.Euler(-sw * 0.8f + r.armLX, 0, r.armLZ);
            armR.localRotation = Quaternion.Euler(sw * 0.8f + r.armRX, 0, -r.armRZ);
            float idleTilt = Mathf.Sin(t * 1.7f) * 3;
            head.localRotation = Quaternion.Euler(r.headPitch, lookYaw + r.headYaw, idleTilt + r.headRoll);

            // props
            if (phone) phone.SetActive(act == Act.Phone);
            if (fan) fan.SetActive(act == Act.Fan);
            if (flute) flute.SetActive(Look == Archetype.Uncle || act == Act.Sip);

            ApplyFace(f, t);
        }

        Face BaseFace()
        {
            var f = new Face { eyes = Eyes.Normal, mouth = Mouth.Flat, brows = Brows.None };
            if (Guest != null && (Guest.Has(LinkKind.Ex) || Guest.Has(LinkKind.Rival))) f.brows = Brows.Angry;
            switch (CurrentMood)
            {
                case Mood.Happy: f.mouth = Mouth.Smile; break;
                case Mood.Love: f.mouth = Mouth.Smile; f.blush = 0.6f + Mathf.Sin(Time.time * 4) * 0.4f; break;
                case Mood.Grumpy: f.mouth = Mouth.Frown; f.brows = Brows.Angry; break;
                case Mood.Furious: f.mouth = Mouth.Frown; f.brows = Brows.Angry; f.eyes = Eyes.Squint; break;
                case Mood.Nervous: f.brows = Brows.Worried; break;
                case Mood.Sad: f.mouth = Mouth.Frown; f.brows = Brows.Worried; break;
                default:
                    if (seated && !Waiting) f.mouth = Mouth.Smile;
                    if (Waiting && Blocked && WaitTime > 15) f.mouth = Mouth.Frown;
                    if (HallDrama >= 70 && f.brows == Brows.None) f.brows = Brows.Worried;
                    break;
            }
            return f;
        }

        void EvalAct(Act a, float at, float dur, float t, ref Rig r, ref Face f)
        {
            float env = Mathf.Clamp01(at / 0.15f) * Mathf.Clamp01((dur - at) / 0.2f);
            float k01 = Mathf.Clamp01(at / Mathf.Max(0.01f, dur));
            switch (a)
            {
                case Act.LookAround:
                    r.headYaw += Mathf.Sin(at * 2.6f) * 50 * env; r.hipYaw += Mathf.Sin(at * 2.6f) * 10 * env; break;
                case Act.CheckWatch:
                    r.armLX += -95 * env; r.armLZ += -38 * env; r.headPitch += 24 * env; r.headYaw += 14 * env;
                    if (k01 > 0.55f) { f.mouth = Mouth.Frown; f.brows = Brows.Worried; }
                    break;
                case Act.Phone:
                    r.armRX += -78 * env; r.armRZ += -28 * env; r.armLX += -55 * env; r.armLZ += -30 * env; r.headPitch += 26 * env;
                    if (Mathf.Repeat(at, 0.9f) < 0.12f) r.armLX += -10 * env;
                    f.mouth = k01 > 0.5f ? Mouth.Smile : Mouth.Flat; break;
                case Act.TapFoot:
                    if (Look == Archetype.Grandma) r.armRX += (-25 - Mathf.Abs(Mathf.Sin(at * 12)) * 18) * env;   // cane tapping
                    else
                    {
                        if (seated) r.legRX += Mathf.Abs(Mathf.Sin(at * 13)) * 20 * env;
                        else r.legRX += -Mathf.Abs(Mathf.Sin(at * 13)) * 24 * env;
                        r.armLZ += 28 * env; r.armRZ += 28 * env; r.armLX += 18 * env; r.armRX += 18 * env;
                    }
                    f.mouth = Mouth.Frown; f.brows = Brows.Angry; break;
                case Act.Tiptoe:
                    r.hipY += (0.08f + Mathf.Sin(at * 9) * 0.02f) * env; r.headPitch += -16 * env; r.legLX += 8 * env; r.legRX += 8 * env;
                    r.lean += Mathf.Sin(at * 3) * 8 * env; f.eyes = Eyes.Wide; break;
                case Act.Wave:
                    r.armRZ += (150 + Mathf.Sin(at * 16) * 22) * env; r.headRoll += 8 * env; f.mouth = Mouth.Smile; f.eyes = Eyes.Happy; break;
                case Act.Stretch:
                    r.armLZ += 165 * env; r.armRZ += 165 * env; r.squashY += 0.07f * env; r.squashX -= 0.03f * env; f.eyes = Eyes.Closed; f.mouth = Mouth.Open; break;
                case Act.Yawn:
                    r.armRX += -150 * env; r.armRZ += -15 * env; r.headPitch += -14 * env; r.hipY -= 0.02f * env; f.eyes = Eyes.Closed; f.mouth = Mouth.Open; break;
                case Act.Sip:
                    {
                        float lift = Mathf.Clamp01(Mathf.Sin(k01 * Mathf.PI) * 1.6f);
                        r.armLX += -140 * lift; r.armLZ += -22 * lift; r.headPitch += -12 * lift;
                        f.eyes = lift > 0.85f ? Eyes.Closed : Eyes.Normal; f.mouth = k01 > 0.7f ? Mouth.Smile : Mouth.Flat;
                        if (Look == Archetype.Uncle) f.blush = 0.8f;
                    }
                    break;
                case Act.Hiccup:
                    {
                        float k = at < 0.2f ? Mathf.Sin(at / 0.2f * Mathf.PI) : 0;
                        r.hipY += k * 0.09f; r.squashY += k * 0.08f; r.headPitch += -k * 14;
                        f.eyes = k > 0 ? Eyes.Wide : Eyes.Squint; f.mouth = Mouth.O; f.blush = 1;
                    }
                    break;
                case Act.Fan:
                    r.armLX += -110 * env; r.armLZ += (-15 + Mathf.Sin(at * 18) * 18) * env; r.headRoll += -6 * env; f.eyes = Eyes.Happy; f.mouth = Mouth.Smile; break;
                case Act.Whisper:
                    r.armRX += -125 * env; r.armRZ += -40 * env; r.hipPitch += 10 * env; r.headRoll += 12 * env;
                    f.mouth = Mouth.Talk; f.eyes = Eyes.Squint; break;
                case Act.Chat:
                    r.armRX += (-55 + Mathf.Sin(at * 7) * 25) * env; r.armRZ += (15 + Mathf.Sin(at * 5) * 15) * env; r.headRoll += Mathf.Sin(at * 4) * 6 * env;
                    f.mouth = Mouth.Talk; break;
                case Act.Clap:
                    {
                        float c = Mathf.Abs(Mathf.Sin(at * 15));
                        r.armLX += -75 * env; r.armRX += -75 * env; r.armLZ += (-32 + c * 26) * env; r.armRZ += (-32 + c * 26) * env;
                        f.mouth = Mouth.Smile; f.eyes = Eyes.Happy;
                    }
                    break;
                case Act.Cheer:
                    r.armLZ += (150 + Mathf.Sin(t * 14) * 15) * env; r.armRZ += (150 + Mathf.Sin(t * 14 + 1) * 15) * env;
                    r.hipY += Mathf.Abs(Mathf.Sin(at * 9)) * 0.13f * env; r.standUp = 0.5f * env;
                    f.eyes = Eyes.Happy; f.mouth = Mouth.Open; f.blush = 1; break;
                case Act.Love:
                    r.armLX += -70 * env; r.armRX += -70 * env; r.armLZ += -35 * env; r.armRZ += -35 * env; r.headRoll += 14 * env;
                    r.lean += Mathf.Sin(at * 4) * 6 * env; r.hipY += Mathf.Abs(Mathf.Sin(at * 5)) * 0.03f * env;
                    f.eyes = Eyes.Happy; f.mouth = Mouth.Smile; f.blush = 1.6f; break;
                case Act.Angry:
                    r.armLX += (-60 + Mathf.Sin(t * 20) * 25) * env; r.armRX += (-60 + Mathf.Sin(t * 20 + 2) * 25) * env; r.headRoll += Mathf.Sin(t * 22) * 10 * env;
                    f.brows = Brows.Angry; f.mouth = Mouth.Frown; break;
                case Act.Furious:
                    r.standUp = env; r.hipY += Mathf.Abs(Mathf.Sin(at * 11)) * 0.08f * env;
                    r.armLZ += (130 + Mathf.Sin(t * 24) * 25) * env; r.armRZ += (130 + Mathf.Sin(t * 24 + 1.5f) * 25) * env;
                    r.headRoll += Mathf.Sin(t * 26) * 9 * env; f.brows = Brows.Angry; f.mouth = Mouth.Open; break;
                case Act.Sad:
                    r.headPitch += 20 * env; r.armLZ += -12 * env; r.armRZ += -12 * env; r.armLX += -15 * env; r.armRX += -15 * env;
                    r.squashY -= 0.04f * env; r.hipY -= 0.02f * env; f.brows = Brows.Worried; f.mouth = Mouth.Frown; f.tear = true; break;
                case Act.Gasp:
                    r.hipY += at < 0.25f ? Mathf.Sin(at / 0.25f * Mathf.PI) * 0.08f : 0;
                    r.armLX += -150 * env; r.armRX += -150 * env; r.armLZ += -25 * env; r.armRZ += -25 * env; r.hipPitch += -8 * env;
                    f.eyes = Eyes.Wide; f.mouth = Mouth.O; f.brows = Brows.Worried; break;
                case Act.Glare:
                    r.headPitch += 6 * env; r.armLX += -60 * env; r.armRX += -60 * env; r.armLZ += -55 * env; r.armRZ += -55 * env;
                    f.brows = Brows.Angry; f.eyes = Eyes.Squint; f.mouth = Mouth.Frown; break;
                case Act.Scold:
                    r.standUp = env; r.armRX += (-130 + Mathf.Sin(at * 16) * 30) * env; r.armLZ += 30 * env; r.hipPitch += 6 * env;
                    f.brows = Brows.Angry; f.mouth = Mouth.Talk; break;
                case Act.Cower:
                    r.squashY -= 0.12f * env; r.squashX += 0.04f * env; r.armLX += -160 * env; r.armRX += -160 * env; r.armLZ += -10 * env; r.armRZ += -10 * env;
                    r.headPitch += 18 * env; r.lean += Mathf.Sin(t * 40) * 2.5f * env; f.eyes = Eyes.Closed; f.brows = Brows.Worried; f.mouth = Mouth.Frown; break;
                case Act.Throw:
                    {
                        float ang = k01 < 0.4f ? Mathf.Lerp(0, 70, k01 / 0.4f) : Mathf.Lerp(70, -170, Mathf.Clamp01((k01 - 0.4f) / 0.2f));
                        r.armRX += ang * env;
                        r.hipYaw += (k01 < 0.4f ? -20 * k01 / 0.4f : Mathf.Lerp(-20, 15, Mathf.Clamp01((k01 - 0.4f) / 0.2f))) * env;
                        r.standUp = env; f.brows = Brows.Angry; f.mouth = Mouth.Open;
                    }
                    break;
                case Act.Shrug:
                    {
                        float k = Mathf.Sin(k01 * Mathf.PI);
                        r.armLZ += 45 * k; r.armRZ += 45 * k; r.armLX += -45 * k; r.armRX += -45 * k; r.hipY += 0.03f * k; r.headRoll += 12 * k;
                        f.mouth = Mouth.Frown; f.brows = Brows.Worried;
                    }
                    break;
                case Act.Dance:
                    r.lean += Mathf.Sin(at * 7) * 12 * env; r.hipY += Mathf.Abs(Mathf.Sin(at * 7)) * 0.05f * env;
                    r.armLZ += (70 + Mathf.Sin(at * 7) * 40) * env; r.armRZ += (70 - Mathf.Sin(at * 7) * 40) * env; r.headRoll += Mathf.Sin(at * 7 + 1) * 10 * env;
                    f.eyes = Eyes.Happy; f.mouth = Mouth.Smile; break;
                case Act.Facepalm:
                    r.armRX += -155 * env; r.armRZ += -35 * env; r.headPitch += 22 * env; r.headRoll += -8 * env;
                    f.eyes = Eyes.Closed; f.mouth = Mouth.Frown; f.brows = Brows.Worried; break;
            }
        }

        void ApplyFace(Face f, float t)
        {
            // blink
            if (Time.time > nextBlink) { blinkUntil = Time.time + 0.11f; nextBlink = Time.time + Random.Range(2.2f, 5f); }
            var eyesMode = f.eyes;
            if (Time.time < blinkUntil && (eyesMode == Eyes.Normal || eyesMode == Eyes.Wide)) eyesMode = Eyes.Closed;

            bool happy = eyesMode == Eyes.Happy;
            Vector3 es = eyesMode == Eyes.Wide ? new Vector3(0.09f, 0.11f, 0.05f)
                       : eyesMode == Eyes.Squint ? new Vector3(0.085f, 0.035f, 0.05f)
                       : eyesMode == Eyes.Closed ? new Vector3(0.085f, 0.014f, 0.05f) : EyeNormal;
            for (int i = 0; i < 2; i++)
            {
                eyes[i].gameObject.SetActive(!happy);
                eyes[i].localScale = es;
                shines[i].SetActive(eyesMode == Eyes.Normal || eyesMode == Eyes.Wide);
                happyEyes[i].gameObject.SetActive(happy);
                blush[i].localScale = BlushSize * (1 + f.blush * 0.45f);
            }

            bool br = f.brows != Brows.None;
            browL.gameObject.SetActive(br); browR.gameObject.SetActive(br);
            float browRot = f.brows == Brows.Worried ? -15 : 18;
            browL.localRotation = Quaternion.Euler(0, 0, browRot); browR.localRotation = Quaternion.Euler(0, 0, -browRot);

            var m = f.mouth;
            bool talkOpen = m == Mouth.Talk && Mathf.Repeat(t * 9, 2) < 1;
            mouth.gameObject.SetActive(m == Mouth.Flat || (m == Mouth.Talk && !talkOpen));
            mouthSmile.SetActive(m == Mouth.Smile);
            mouthFrown.SetActive(m == Mouth.Frown);
            mouthOpen.SetActive(m == Mouth.Open || m == Mouth.O || talkOpen);
            mouthOpen.transform.localScale = m == Mouth.O ? new Vector3(0.07f, 0.085f, 0.05f) : talkOpen ? new Vector3(0.09f, 0.06f, 0.05f) : new Vector3(0.12f, 0.1f, 0.05f);

            tear.SetActive(f.tear);
            if (f.tear) tear.transform.localPosition = new Vector3(0.13f, -0.02f - Mathf.Repeat(t, 0.9f) / 0.9f * 0.16f, 0.265f);
        }

        // ---------- movement ----------

        public IEnumerator Hop(float height = 0.35f, float duration = 0.22f)
        {
            Vector3 p0 = transform.position;
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                transform.position = p0 + Vector3.up * (Mathf.Sin(t / duration * Mathf.PI) * height);
                yield return null;
            }
            transform.position = p0;
        }

        public IEnumerator WalkTo(List<Vector3> points)
        {
            SetPose(Pose.Walk);
            foreach (var target in points)
            {
                Vector3 to = new Vector3(target.x, 0, target.z);
                while (true)
                {
                    Vector3 pos = transform.position; pos.y = 0;
                    Vector3 d = to - pos;
                    if (d.magnitude < 0.02f) break;
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(d), Time.deltaTime * 14);
                    transform.position = Vector3.MoveTowards(pos, to, WalkSpeed * Time.deltaTime);
                    yield return null;
                }
            }
        }

        public IEnumerator SitAt(Transform seat)
        {
            Quaternion from = transform.rotation; Vector3 p0 = transform.position;
            for (float t = 0; t < 0.28f; t += Time.deltaTime)
            {
                float k = t / 0.28f;
                transform.rotation = Quaternion.Slerp(from, seat.rotation, k);
                transform.position = Vector3.Lerp(p0, seat.position, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 0.25f;
                yield return null;
            }
            transform.SetPositionAndRotation(seat.position, seat.rotation);
            SetPose(Pose.Sit);
            Vector3 baseScale = visual.localScale;
            for (float t = 0; t < 0.2f; t += Time.deltaTime)
            {
                float s = Mathf.Sin(t / 0.2f * Mathf.PI) * 0.18f;
                visual.localScale = Vector3.Scale(baseScale, new Vector3(1 + s, 1 - s, 1 + s));
                yield return null;
            }
            visual.localScale = baseScale;
        }

        /// <summary>Back-compat: play the act matching a reaction pose, then settle.</summary>
        public IEnumerator React(Pose pose, float duration = 1.2f)
        {
            var a = pose == Pose.Cheer ? Act.Cheer : pose == Pose.Angry ? Act.Angry : pose == Pose.Sad ? Act.Sad : Act.None;
            if (a != Act.None) Play(a, duration);
            yield return new WaitForSeconds(duration);
        }
    }
}
