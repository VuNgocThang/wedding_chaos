using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using WeddingChaos.Core;
using Random = UnityEngine.Random;

namespace WeddingChaos.View
{
    /// <summary>Marker on a lounge sofa.</summary>
    public class BenchSlot : MonoBehaviour { public int Index; public Transform Seat; }

    /// <summary>
    /// Builds the wedding hall for a level, handles drag/tap input and plays every move as animation:
    /// hop out of the crowd, walk the free lane, step through the entrance, follow a nav-grid path around
    /// sofas and tables to the chair, sit, react. Rules live in <see cref="SeatingSim"/>.
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [Tooltip("0-based index of Resources/Levels/level_XX.txt")]
        public int levelIndex;
        public int levelCount = 50;

        const float CamPitch = 42f;
        const float CellSize = 1.15f, CrowdTopZ = -0.5f, BenchZ = 1.6f, StageZ = 10.6f, TableX = 1.9f;
        const float GuestRadius = 0.25f;

        SeatingSim sim;
        Transform world;
        Camera cam;
        Hud hud;
        NavGrid nav;
        readonly Dictionary<int, GuestView> views = new Dictionary<int, GuestView>();
        readonly Dictionary<char, TableView> tables = new Dictionary<char, TableView>();
        readonly Dictionary<char, Vector3> tablePos = new Dictionary<char, Vector3>();
        readonly Dictionary<char, float> lockedUntil = new Dictionary<char, float>();
        readonly HashSet<char> busyTables = new HashSet<char>();
        readonly List<BenchSlot> benchSlots = new List<BenchSlot>();
        readonly Dictionary<string, int> boosterCount = new Dictionary<string, int>();
        int running, coins = 1250;
        bool ended, egoUsed, bouncerMode;
        GuestView bride, groom;
        readonly List<GuestView> relatedLit = new List<GuestView>();
        readonly HashSet<int> freeIds = new HashSet<int>();
        GuestView infoTarget;
        // blockers
        readonly List<(Transform root, Transform anchor)> giftViews = new List<(Transform, Transform)>();
        readonly List<Transform> ribbonViews = new List<Transform>();
        readonly Dictionary<int, Transform> reserveTags = new Dictionary<int, Transform>();
        readonly List<(GuestView a, GuestView b, Transform bar)> handLinks = new List<(GuestView, GuestView, Transform)>();
        readonly Dictionary<int, int> pairChoice = new Dictionary<int, int>();
        GuestView selPartner; Vector3 partnerHome;

        struct Src { public bool Valid, Bench; public int R, C, I; }
        Src sel;
        GuestView selView;
        Vector3 selHome;
        bool pressing, dragging;
        Vector2 pressPos;
        Transform dragMarker;
        Vector3 camBase;
        float camShakeUntil, camShakeAmp;

        static readonly string[] GirlNames = { "Lily", "Mia", "Ava", "Zoe", "Ella", "Nina", "Ruby", "Luna", "Chloe", "Emma", "Ivy", "Rosa", "Lan", "Hoa", "Mai", "Vy", "Thảo", "Linh", "Anna", "Sara", "Kim", "Jade", "Hana", "Yến" };
        static readonly string[] BoyNames = { "Jack", "Leo", "Max", "Sam", "Ben", "Tom", "Noah", "Finn", "Owen", "Theo", "Kai", "Hugo", "Nam", "Huy", "Minh", "Tùng", "Đức", "Long", "Alex", "Ryan", "Dan", "Phong", "Khoa", "Bảo" };
        static readonly string[] GrandmaNames = { "Bà Rose", "Bà Daisy" };
        static readonly string[] UncleNames = { "Chú Bob", "Chú Frank" };
        static readonly string[] AuntieNames = { "Cô Gina", "Cô Pam", "Cô Sue", "Cô Liz" };
        static readonly string[] KidNames = { "Bé Bin", "Bé Na", "Bé Tí", "Bé Su", "Bé Bo", "Bé Mít" };

        private void Awake()
        {
            Setup();
        }

        void Setup()
        {
            Application.targetFrameRate = 60;
        }

        void Start()
        {
            SetupCameraAndLight();
            hud = Hud.Create(cam);
            hud.OnRestart = () => LoadLevel(levelIndex);
            hud.OnNext = () => LoadLevel((levelIndex + 1) % levelCount);
            hud.OnPrev = () => LoadLevel((levelIndex + levelCount - 1) % levelCount);
            hud.LevelCount = levelCount;
            hud.OnGoTo = n => LoadLevel(Mathf.Clamp(n - 1, 0, levelCount - 1));
            hud.OnBooster = UseBooster;
            GuestView.EmoteSink = hud.Emote;
            hud.SetCoins(coins);
            Sfx.Ensure();
            Perf.Ensure();
            if (Debug.isDebugBuild)
            {
                // dev builds: "-wcLevel 50" opens a level directly (used for performance runs)
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-wcLevel" && int.TryParse(args[i + 1], out var lv)) levelIndex = Mathf.Clamp(lv - 1, 0, levelCount - 1);
            }
            LoadLevel(levelIndex);
        }

        // ---------- setup ----------

        void SetupCameraAndLight()
        {
            cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Palette.Bg;
            cam.transform.rotation = Quaternion.Euler(CamPitch, 0, 0);
            Light sun = null;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Directional) sun = l;
            if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(55, -30, 0);
            sun.intensity = 0.95f;
            sun.color = Palette.Hex("#FFF3E8");
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.55f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Palette.Hex("#C7B3BC");
            RenderSettings.ambientEquatorColor = Palette.Hex("#B9A0A2");
            RenderSettings.ambientGroundColor = Palette.Hex("#9C8584");
        }

        void FrameCamera()
        {
            float minZ = CrowdTopZ - (sim.Rows - 1) * CellSize - 0.8f, maxZ = StageZ + 1.6f;
            float width = Mathf.Max(sim.Cols * CellSize, 2 * TableX + 3.8f) + 0.4f;
            float depth = (maxZ - minZ) * Mathf.Sin(CamPitch * Mathf.Deg2Rad) + 1.6f;
            float aspect = Mathf.Max(0.3f, cam.aspect);
            float topShare = 0.2f, bottomShare = 0.16f;            // HUD bands (top bar + drama card, boosters)
            float size = Mathf.Max(depth / 2f / (1 - topShare - bottomShare), width / (2f * aspect));
            cam.orthographicSize = size;
            // shift the view so the playfield centre sits between the HUD bands
            float shift = (topShare - bottomShare) * size;
            var target = new Vector3(0, 0, (minZ + maxZ) / 2f);
            cam.transform.position = target - cam.transform.forward * 40f + cam.transform.up * shift;
            camBase = cam.transform.position;
            cam.nearClipPlane = 1f; cam.farClipPlane = 100f;
        }

        void LoadLevel(int index)
        {
            StopAllCoroutines();
            levelIndex = index;
            if (world != null) { world.gameObject.SetActive(false); Destroy(world.gameObject); Resources.UnloadUnusedAssets(); }
            views.Clear(); tables.Clear(); tablePos.Clear(); lockedUntil.Clear(); busyTables.Clear(); benchSlots.Clear(); relatedLit.Clear();
            infoTarget = null; freeIds.Clear();
            giftViews.Clear(); ribbonViews.Clear(); reserveTags.Clear(); vipClock.Clear(); handLinks.Clear(); pairChoice.Clear(); selPartner = null;
            running = 0; ended = false; egoUsed = false; bouncerMode = false; sel = default; selView = null; pressing = dragging = false;
            boosterCount["bouncer"] = 1; boosterCount["sofa"] = 1; boosterCount["champagne"] = 1;
            hud.HideEnd(); hud.ClearBadges(); hud.ClearEmotes(); hud.ShowInfo(null); camShakeUntil = 0; hud.SetChips(new List<(Vector3, int)>());

            var asset = Resources.Load<TextAsset>($"Levels/level_{index + 1:00}");
            sim = SeatingSim.FromLevel(LevelDef.Parse(asset.text));
            world = new GameObject("World").transform;
            Perf.IgnoreFor(2f);

            BuildHall();
            BatchHall();
            BuildCrowd();
            BuildBench();
            BuildTables();
            BuildNav();
            FrameCamera();
            RefreshCrowd();
            hud.SetLevel(index + 1, sim.LevelName, sim.Tier);
            hud.SetDrama(sim.Drama, true);
            GuestView.HallDrama = sim.Drama;
            GuestView.StagePoint = new Vector3(0, 1.2f, StageZ + 0.5f);
            RefreshBoosters();
            string tip = BlockerTip(index + 1);
            Sfx.Play("level_start");
            Sfx.SetDrama(sim.Drama);
            Sfx.SetMusicLevel(0.32f);
            StartIntro(index, tip);
        }

        // ---------- onboarding ----------
        // Level 1 walks the player through one move with an arrow (tap a guest -> tap a lit seat), every level
        // that unlocks a rule opens with a short card saying what it costs, and the first table / first drama rise
        // get one line of explanation. The goal (seat everybody, keep Drama under 100%) is stated on every level.

        int tutStep;                 // level 1: 0 off, 1 tap a guest, 2 tap a seat, 3 done-ish, 4 waiting for first table
        int introShownFor = -1;
        bool explainedDrama, explainedTable;
        Transform tutBestSeat;

        static (string title, string body)? RuleIntro(int level)
        {
            const string G = "<color=#2F9E6E>", B = "<color=#D63A3A>", E = "</color>";
            switch (level)
            {
                case 1: return ("Cách chơi", "• Chạm <b>một khách có vòng vàng</b> (khách đi được ngay).\n• Chạm <b>một ghế đang sáng</b> ở bàn cùng màu để mời khách ngồi.\n• Mời <b>hết khách</b> vào bàn là <b>thắng</b>.\n• Xếp sai người cạnh nhau làm <b>Drama</b> tăng. Drama <b>100%</b> là <b>thua</b>!");
                case 2: return ("Mới: Cặp đôi", $"Cặp đôi muốn ngồi <b>sát cạnh nhau</b>: {G}−10%{E} Drama.\nBị tách ra (khác bàn, hoặc không ngồi sát): {B}+15%{E}.\n\nSố trên ghế cho biết trước Drama sẽ đổi bao nhiêu. Ghế có <b>★</b> là lựa chọn tốt nhất.");
                case 4: return ("Mới: Bà nội VIP", $"Bà nội phải ngồi <b>ghế ngôi sao ★</b> của bàn.\nNgồi ghế khác: {B}+20%{E}.");
                case 5: return ("Mới: Ông chú say", $"Ông chú ngồi <b>sát bà nội</b> là bị mắng: {B}+20%{E}.\nCho hai người ngồi xa nhau hoặc khác bàn.");
                case 7: return ("Mới: Người yêu cũ", $"Ngồi <b>sát nhau</b>: ném bánh {B}+30%{E}.\nNgồi <b>đối diện</b>: liếc nhau {B}+10%{E}.\nKhác bàn: không sao.");
                case 8: return ("Mới: Hội bà tám", $"Hai bà tám ngồi <b>sát nhau</b> mải buôn chuyện: {G}−15%{E}.");
                case 11: return ("Mới: Khách bí ẩn", "Khách trùm áo choàng <b>?</b> chỉ lộ màu thiệp khi có lối đi ra.\nMở đường cho họ để biết nên xếp vào đâu.");
                case 12: return ("Mới: Bạn thân", $"Bạn thân ngồi <b>cùng bàn</b>: {G}−8%{E}.");
                case 14: return ("Mới: Kình địch", $"Kình địch ngồi <b>cùng bàn</b> (dù không sát): {B}+20%{E}.\nĐể một người sang bàn sau.");
                case 15: return ("Nhiều quan hệ", "Một khách có thể dính nhiều quan hệ cùng lúc.\n<b>Chạm vào khách</b> để xem thẻ mong muốn: ô có <b>dấu tích</b> là đã ổn, ô có <b>dấu chéo</b> là đã làm Drama tăng.");
                case 21: return ("Mới: Em bé", $"Em bé phải ngồi <b>sát bố/mẹ</b>: {G}−5%{E}.\nNgồi xa bố mẹ: khóc ré {B}+15%{E}.");
                case 24: return ("Mới: Thầm thương", $"Ngồi <b>sát</b> người mình thầm thương: {G}−12%{E}.");
                default: return null;
            }
        }

        void StartIntro(int index, string blockerTip)
        {
            tutStep = 0; tutBestSeat = null; hud.Coach(null);
            bool fresh = introShownFor != index;          // a restart of the same level skips the cards
            introShownFor = index;
            string goal = $"Mời hết {sim.Total} khách vào bàn · giữ Drama dưới 100%";
            var intro = fresh ? RuleIntro(index + 1) : null;
            if (intro == null && fresh && blockerTip != null) intro = ("Mới!", blockerTip + "\n\n" + goal + ".");
            if (intro != null)
            {
                hud.GoalCard(intro.Value.title, intro.Value.body, index == 0 ? "Chơi thôi!" : "Đã hiểu", () =>
                {
                    if (index == 0) BeginLevelOneTutorial();
                    else hud.Toast(goal, 4f);
                });
                return;
            }
            string tierLine = sim.Tier == "super" ? "SIÊU KHÓ! " : sim.Tier == "hard" ? "KHÓ! " : "";
            hud.Toast(tierLine + goal, 4f);
        }

        void BeginLevelOneTutorial()
        {
            tutStep = 1;
            GuestView first = null;
            foreach (var id in freeIds) if (views.TryGetValue(id, out var v) && v) { first = v; break; }
            hud.Coach("Chạm vào một khách có <b>vòng vàng</b> dưới chân.", first ? first.EmoteAnchor : null);
        }

        /// <summary>Called after a guest is picked up; points at the best lit seat while the tutorial runs.</summary>
        void TutorialOnSelect()
        {
            if (tutStep == 1 || tutStep == 3)
            {
                tutStep = 2;
                hud.Coach("Ghế <b>đang sáng</b> là chỗ khách được ngồi. Số trên ghế = Drama sẽ đổi (OK = không đổi, <b>★</b> = tốt nhất). Chạm vào ghế!", tutBestSeat);
            }
            else if (levelIndex == 1 && !hud.GoalOpen && tutBestSeat && !explainedChips)
            {
                explainedChips = true;
                hud.Coach("<color=#2F9E6E>Xanh</color> = Drama giảm, <color=#D63A3A>đỏ</color> = Drama tăng. Chưa chắc thì chọn ghế có <b>★</b>.", tutBestSeat);
            }
        }
        bool explainedChips;

        void TutorialOnSeat()
        {
            if (tutStep == 2)
            {
                tutStep = 4;
                hud.Coach("Chuẩn rồi! Khách <b>không có vòng</b> đang bị chặn — mời người đứng trước đi trước. Mời hết khách là thắng!");
                StartCoroutine(Later(5f, () => { if (tutStep == 4 && hud.CoachActive) hud.Coach(null); }));
            }
            else if (explainedChips && hud.CoachActive) hud.Coach(null);
        }

        void TutorialOnTableDone()
        {
            if (explainedTable) return;
            explainedTable = true;
            hud.Coach("Bàn đủ 4 người sẽ được <b>dọn đi</b>, bàn mới cùng màu thay vào. Ai đã ngồi thì không đổi chỗ được nữa — nhìn số trên ghế trước khi chọn!");
            StartCoroutine(Later(5.5f, () => hud.Coach(null)));
        }

        void TutorialOnDramaUp(int delta)
        {
            if (explainedDrama || delta <= 0) return;
            explainedDrama = true;
            hud.Coach($"Drama vừa tăng {delta}%! Đầy 100% là đám cưới đổ vỡ. <b>Chạm vào khách</b> để xem họ muốn ngồi cạnh ai.");
            StartCoroutine(Later(5.5f, () => hud.Coach(null)));
        }

        /// <summary>
        /// The hall decor never moves: merge it into static batches so hundreds of small props cost a
        /// handful of draw calls. Runs right after BuildHall, when the world holds nothing but decor.
        /// </summary>
        void BatchHall()
        {
            var hall = new GameObject("Hall").transform;
            hall.SetParent(world, false);
            for (int i = world.childCount - 1; i >= 0; i--)
            {
                var c = world.GetChild(i);
                if (c != hall) c.SetParent(hall, true);
            }
            StaticBatchingUtility.Combine(hall.gameObject);
        }

        Vector3 CellPos(int r, int c) => new Vector3((c - (sim.Cols - 1) / 2f) * CellSize, 0, CrowdTopZ - r * CellSize);
        Vector3 EntrancePos(int c) => new Vector3(CellPos(0, c).x, 0, CrowdTopZ + 1.15f);

        /// <summary>Lounge sofas flank the red carpet so the aisle stays open.</summary>
        Vector3 BenchPos(int i)
        {
            int n = sim.Bench.Length, left = (n + 1) / 2;
            float x = i < left ? -0.95f - (left - 1 - i) * 1.05f : 0.95f + (i - left) * 1.05f;
            return new Vector3(x, 0, BenchZ);
        }

        readonly List<(Vector3 pos, float radius)> decorObstacles = new List<(Vector3, float)>();

        void BuildHall()
        {
            decorObstacles.Clear();
            Palette.Prim(PrimitiveType.Cube, world, new Vector3(0, -0.05f, 3), new Vector3(30, 0.1f, 34), Palette.Floor, name: "Floor");
            // soft parquet-like tiles around the tables
            for (int x = -4; x <= 4; x++)
                for (int z = 2; z <= 11; z++)
                    if ((x + z) % 2 == 0) Palette.Prim(PrimitiveType.Cube, world, new Vector3(x, 0.001f, z), new Vector3(1, 0.002f, 1), Palette.Hex("#E6D8CE"), name: "Tile");
            float aisleLen = StageZ - CrowdTopZ;
            Palette.Prim(PrimitiveType.Cube, world, new Vector3(0, 0.006f, (CrowdTopZ + StageZ) / 2f + 0.4f), new Vector3(1.0f, 0.01f, aisleLen), Palette.Carpet, name: "Aisle");
            foreach (var x in new[] { -0.52f, 0.52f })
                Palette.Prim(PrimitiveType.Cube, world, new Vector3(x, 0.008f, (CrowdTopZ + StageZ) / 2f + 0.4f), new Vector3(0.06f, 0.012f, aisleLen), Palette.Gold, name: "AisleTrim");
            // rose petals scattered on the aisle
            var rng = new System.Random(levelIndex * 31 + 7);
            for (int i = 0; i < 46; i++)
            {
                float px = (float)(rng.NextDouble() - 0.5) * 0.9f, pz = CrowdTopZ + 0.9f + (float)rng.NextDouble() * (aisleLen - 1.2f);
                var petal = Palette.Prim(PrimitiveType.Sphere, world, new Vector3(px, 0.014f, pz), new Vector3(0.11f, 0.01f, 0.07f), i % 3 == 0 ? Color.white : Palette.Flower, name: "Petal");
                petal.transform.rotation = Quaternion.Euler(0, (float)rng.NextDouble() * 180, 0);
            }
            float crowdW = sim.Cols * CellSize + 0.3f, crowdD = sim.Rows * CellSize + 0.3f;
            Palette.Prim(PrimitiveType.Cube, world, new Vector3(0, 0.004f, CrowdTopZ - (sim.Rows - 1) * CellSize / 2f), new Vector3(crowdW, 0.01f, crowdD), Palette.FloorDark, name: "CrowdFloor");
            // entrance: gold posts with a blush rope and balloon bunches
            Palette.Prim(PrimitiveType.Cube, world, new Vector3(0, 0.012f, CrowdTopZ + 0.62f), new Vector3(crowdW, 0.02f, 0.1f), Palette.Gold, name: "EntranceLine");
            foreach (var x in new[] { -crowdW / 2, crowdW / 2 })
            {
                Palette.Prim(PrimitiveType.Cylinder, world, new Vector3(x, 0.35f, CrowdTopZ + 0.62f), new Vector3(0.12f, 0.35f, 0.12f), Palette.Gold, name: "Post");
                Palette.Prim(PrimitiveType.Sphere, world, new Vector3(x, 0.75f, CrowdTopZ + 0.62f), Vector3.one * 0.18f, Palette.Gold, name: "PostTop");
                Balloons(new Vector3(x + Mathf.Sign(x) * 0.45f, 0, CrowdTopZ + 0.7f));
            }

            // flower columns along the hall sides
            foreach (var z in new[] { 2.9f, 6.9f, 10.0f })
                foreach (var x in new[] { -3.75f, 3.75f })
                    FlowerColumn(new Vector3(x, 0, z));

            // stage: platform, backdrop with drapes and a gold heart, flower arch, couple, string lights
            var stage = Palette.Pivot(world, "Stage", new Vector3(0, 0, StageZ));
            Palette.Prim(PrimitiveType.Cube, stage, new Vector3(0, 0.15f, 0.5f), new Vector3(5.6f, 0.3f, 1.3f), Palette.Cloth);
            Palette.Prim(PrimitiveType.Cube, stage, new Vector3(0, 0.08f, -0.18f), new Vector3(5.6f, 0.16f, 0.1f), Palette.Blush);
            Palette.Prim(PrimitiveType.Cube, stage, new Vector3(0, 1.6f, 1.25f), new Vector3(6.2f, 3.2f, 0.12f), Palette.Hex("#F6E7E0"), name: "Backdrop");
            for (int i = 0; i < 14; i++)
            {
                float x = -2.95f + i * 0.45f;
                if (Mathf.Abs(x) < 1.1f) continue;   // the arch frames the middle
                Palette.Prim(PrimitiveType.Capsule, stage, new Vector3(x, 1.55f, 1.15f), new Vector3(0.3f, 1.5f, 0.12f), i % 2 == 0 ? Color.white : Palette.Blush, name: "Drape");
            }
            var heart = Palette.Pivot(stage, "Heart", new Vector3(0, 2.95f, 1.1f));
            Palette.Prim(PrimitiveType.Sphere, heart, new Vector3(-0.17f, 0.08f, 0), Vector3.one * 0.38f, Palette.Gold);
            Palette.Prim(PrimitiveType.Sphere, heart, new Vector3(0.17f, 0.08f, 0), Vector3.one * 0.38f, Palette.Gold);
            Palette.Prim(PrimitiveType.Cube, heart, new Vector3(0, -0.12f, 0), new Vector3(0.38f, 0.38f, 0.3f), Palette.Gold).transform.localRotation = Quaternion.Euler(0, 0, 45);
            for (int i = 0; i <= 14; i++)
            {
                float a = Mathf.PI * i / 14f;
                var c = i % 3 == 0 ? Palette.Flower : i % 3 == 1 ? Color.white : Palette.Leaf;
                Palette.Prim(PrimitiveType.Sphere, stage, new Vector3(Mathf.Cos(a) * 1.8f, 0.3f + Mathf.Sin(a) * 2.0f, 0.8f), Vector3.one * 0.46f, c);
            }
            // a three-tier cake beside the couple
            var cake = Palette.Pivot(stage, "Cake", new Vector3(2.3f, 0.3f, 0.55f));
            Palette.Prim(PrimitiveType.Cylinder, cake, new Vector3(0, 0.12f, 0), new Vector3(0.6f, 0.12f, 0.6f), Palette.Cloth);
            Palette.Prim(PrimitiveType.Cylinder, cake, new Vector3(0, 0.34f, 0), new Vector3(0.44f, 0.1f, 0.44f), Palette.Blush);
            Palette.Prim(PrimitiveType.Cylinder, cake, new Vector3(0, 0.52f, 0), new Vector3(0.3f, 0.08f, 0.3f), Palette.Cloth);
            Palette.Prim(PrimitiveType.Sphere, cake, new Vector3(0, 0.66f, 0), Vector3.one * 0.12f, Palette.Flower);
            bride = GuestView.CreateCustom("Bride", stage, Archetype.Bride, Color.white, Palette.Skins[0], Palette.Hairs[1]);
            bride.transform.localPosition = new Vector3(-0.42f, 0.3f, 0.45f);
            bride.transform.rotation = Quaternion.LookRotation(Vector3.back);
            groom = GuestView.CreateCustom("Groom", stage, Archetype.Groom, Palette.Hex("#3E3456"), Palette.Skins[1], Palette.Hairs[2]);
            groom.transform.localPosition = new Vector3(0.42f, 0.3f, 0.45f);
            groom.transform.rotation = Quaternion.LookRotation(Vector3.back);
            bride.Partner = groom; groom.Partner = bride;
            // warm string lights over the stage and down both sides of the hall
            for (int i = 0; i < 17; i++)
                Palette.Prim(PrimitiveType.Sphere, stage, new Vector3(-4.8f + i * 0.6f, 3.1f - Mathf.Sin(i / 16f * Mathf.PI) * 0.45f, 1.3f), Vector3.one * 0.15f, Palette.Bulb, name: "Bulb");
            foreach (var x in new[] { -4.35f, 4.35f })
                for (int i = 0; i < 16; i++)
                {
                    float z = StageZ - 0.2f - i * 0.75f, sag = Mathf.Abs(Mathf.Sin(i * Mathf.PI / 4f)) * 0.35f;
                    Palette.Prim(PrimitiveType.Sphere, world, new Vector3(x, 2.4f - sag, z), Vector3.one * 0.13f, Palette.Bulb, name: "Bulb");
                }

            dragMarker = MeshFactory.Make(MeshFactory.Torus(0.4f, 0.05f), world, Vector3.zero, Vector3.one, Palette.Accent, "DragMarker").transform;
            dragMarker.gameObject.SetActive(false);
        }

        void FlowerColumn(Vector3 pos)
        {
            var col = Palette.Pivot(world, "FlowerColumn", pos);
            MeshFactory.Make(MeshFactory.Frustum(0.16f, 0.22f, 0.9f), col, Vector3.zero, Vector3.one, Palette.Cloth, "Column");
            Palette.Prim(PrimitiveType.Cylinder, col, new Vector3(0, 0.92f, 0), new Vector3(0.36f, 0.03f, 0.36f), Palette.Gold);
            Palette.Prim(PrimitiveType.Sphere, col, new Vector3(0, 1.15f, 0), Vector3.one * 0.46f, Palette.Flower);
            Palette.Prim(PrimitiveType.Sphere, col, new Vector3(0.15f, 1.22f, 0.12f), Vector3.one * 0.24f, Color.white);
            Palette.Prim(PrimitiveType.Sphere, col, new Vector3(-0.16f, 1.1f, 0.1f), Vector3.one * 0.22f, Palette.Blush);
            Palette.Prim(PrimitiveType.Sphere, col, new Vector3(0.05f, 0.98f, -0.18f), Vector3.one * 0.2f, Palette.Leaf);
            decorObstacles.Add((pos, 0.3f));
        }

        void Balloons(Vector3 pos)
        {
            var b = Palette.Pivot(world, "Balloons", pos);
            Palette.Prim(PrimitiveType.Cylinder, b, new Vector3(0, 0.05f, 0), new Vector3(0.14f, 0.05f, 0.14f), Palette.Gold);
            var cols = new[] { Palette.Flower, Color.white, Palette.Gold, Palette.Blush };
            var offs = new[] { new Vector3(-0.14f, 1.5f, 0), new Vector3(0.16f, 1.62f, 0.05f), new Vector3(0, 1.85f, -0.04f), new Vector3(0.02f, 1.35f, 0.14f) };
            for (int i = 0; i < offs.Length; i++)
            {
                Palette.Prim(PrimitiveType.Cylinder, b, new Vector3(offs[i].x * 0.5f, offs[i].y * 0.5f, offs[i].z * 0.5f), new Vector3(0.012f, offs[i].y * 0.5f, 0.012f), Palette.Hex("#EDE5DC"), name: "String");
                Palette.Prim(PrimitiveType.Sphere, b, offs[i], new Vector3(0.3f, 0.36f, 0.3f), cols[i], name: "Balloon");
            }
            decorObstacles.Add((pos, 0.25f));
        }

        void BuildCrowd()
        {
            int variant = 0, girls = 0, boys = 0, grandmas = 0, uncles = 0, aunties = 0, kids = 0;
            var girl = AssignGenders();
            for (int r = 0; r < sim.Rows; r++)
                for (int c = 0; c < sim.Cols; c++)
                {
                    if (sim.Pillar[r, c])
                    {
                        var pot = Palette.Pivot(world, "Plant", CellPos(r, c));
                        MeshFactory.Make(MeshFactory.Frustum(0.28f, 0.2f, 0.45f), pot, Vector3.zero, Vector3.one, Palette.Chair, "Pot");
                        Palette.Prim(PrimitiveType.Sphere, pot, new Vector3(0, 0.78f, 0), new Vector3(0.78f, 0.72f, 0.78f), Palette.Leaf);
                        Palette.Prim(PrimitiveType.Sphere, pot, new Vector3(0.14f, 1.0f, 0.18f), Vector3.one * 0.22f, Palette.Flower);
                        Palette.Prim(PrimitiveType.Sphere, pot, new Vector3(-0.16f, 0.9f, 0.16f), Vector3.one * 0.18f, Color.white);
                        continue;
                    }
                    var g = sim.Grid[r, c];
                    if (g == null) continue;
                    Archetype look; string name;
                    switch (g.Type)
                    {
                        case GuestType.Grandma: look = Archetype.Grandma; name = GrandmaNames[grandmas++ % GrandmaNames.Length]; break;
                        case GuestType.Uncle: look = Archetype.Uncle; name = UncleNames[uncles++ % UncleNames.Length]; break;
                        case GuestType.Gossip: look = Archetype.Auntie; name = AuntieNames[aunties++ % AuntieNames.Length]; break;
                        case GuestType.Kid: look = Archetype.Kid; name = KidNames[kids++ % KidNames.Length]; break;
                        default:
                            look = girl.TryGetValue(g.Id, out bool isGirl) ? (isGirl ? Archetype.Girl : Archetype.Boy) : (variant % 2 == 0 ? Archetype.Girl : Archetype.Boy);
                            name = look == Archetype.Girl ? GirlNames[girls++ % GirlNames.Length] : BoyNames[boys++ % BoyNames.Length];
                            break;
                    }
                    var v = GuestView.Create(g, world, variant++, look, name);
                    v.transform.position = CellPos(r, c);
                    v.transform.rotation = Quaternion.LookRotation(Vector3.back);
                    v.transform.localScale = Vector3.one * 0.92f;
                    views[g.Id] = v;
                    var icons = Badges(g);
                    if (icons.Count > 0) hud.AddNamePlate(v.BadgeAnchor, name, icons.ConvertAll(b => b.icon), Palette.Invite(g.Color), c % 2 == 1 ? 30 : 0);
                }
            AssignPartners();
            BuildBlockers();
        }

        /// <summary>Romantic links (couple, ex, crush) pair a girl with a boy; grandmas/aunties are women, uncles men.</summary>
        Dictionary<int, bool> AssignGenders()
        {
            var girl = new Dictionary<int, bool>();
            var all = new List<Guest>(sim.Guests.Values);
            all.Sort((x, y) => x.Id.CompareTo(y.Id));
            foreach (var g in all)
            {
                if (g.Type == GuestType.Grandma || g.Type == GuestType.Gossip) girl[g.Id] = true;
                else if (g.Type == GuestType.Uncle) girl[g.Id] = false;
            }
            bool Romantic(LinkKind k) => k == LinkKind.Couple || k == LinkKind.Ex || k == LinkKind.CrushOn || k == LinkKind.CrushedBy;
            int alt = 0;
            foreach (var root in all)
            {
                if (girl.ContainsKey(root.Id) && !root.Links.Exists(l => Romantic(l.Kind))) continue;
                if (!girl.ContainsKey(root.Id)) girl[root.Id] = alt++ % 2 == 0;
                var q = new Queue<Guest>(); q.Enqueue(root);
                while (q.Count > 0)
                {
                    var x = q.Dequeue();
                    foreach (var l in x.Links)
                    {
                        if (!Romantic(l.Kind) || girl.ContainsKey(l.Other)) continue;
                        var o = sim.Guests[l.Other];
                        if (o.Type == GuestType.Kid) continue;
                        girl[l.Other] = !girl[x.Id];
                        q.Enqueue(o);
                    }
                }
            }
            return girl;
        }

        /// <summary>Story counterparts drive ambient looks: lovers gaze, exes glare, kids look for mum, grandma watches the uncle.</summary>
        void AssignPartners()
        {
            int Rank(LinkKind k) => k == LinkKind.Hands ? -1 : k == LinkKind.Couple ? 0 : k == LinkKind.ChildOf ? 1 : k == LinkKind.ParentOf ? 2 : k == LinkKind.CrushOn ? 3 : k == LinkKind.Ex ? 4 : k == LinkKind.Rival ? 5 : 6;
            foreach (var a in views.Values)
            {
                Link best = null;
                foreach (var l in a.Guest.Links) if (views.ContainsKey(l.Other) && (best == null || Rank(l.Kind) < Rank(best.Kind))) best = l;
                if (best != null) { a.Partner = views[best.Other]; a.PartnerKind = best.Kind; continue; }
                foreach (var b in views.Values)
                {
                    if (a == b) continue;
                    var x = a.Guest; var y = b.Guest;
                    if ((x.Type == GuestType.Grandma && y.Type == GuestType.Uncle) || (x.Type == GuestType.Uncle && y.Type == GuestType.Grandma) || (x.Type == GuestType.Gossip && y.Type == GuestType.Gossip))
                    { a.Partner = b; break; }
                }
            }
        }

        static string LinkIcon(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Couple: return "heart";
                case LinkKind.Ex: return "brokenheart";
                case LinkKind.Rival: return "rival";
                case LinkKind.Bff: return "bff";
                case LinkKind.ParentOf: case LinkKind.ChildOf: return "baby";
                case LinkKind.Hands: return "hands";
                default: return "crush";
            }
        }

        static string TraitIcon(GuestType t)
        {
            switch (t)
            {
                case GuestType.Grandma: return "crown";
                case GuestType.Uncle: return "glass";
                case GuestType.Gossip: return "bubble";
                default: return null;
            }
        }

        /// <summary>Badges over a guest: trait first, then one per relationship with its pair letter (max 3).</summary>
        static List<(string icon, string letter)> Badges(Guest g)
        {
            var list = new List<(string, string)>();
            var t = TraitIcon(g.Type);
            if (t != null) list.Add((t, null));
            if (g.Reserved >= 0) list.Add(("reserve", null));
            foreach (var l in g.Links) if (list.Count < 2) list.Add((LinkIcon(l.Kind), l.Tag.ToString()));
            return list;
        }

        static string BadgeIcon(Guest g) => TraitIcon(g.Type) ?? (g.Links.Count > 0 ? LinkIcon(g.Links[0].Kind) : null);

        // ---------- blockers ----------

        static string BlockerTip(int level)
        {
            switch (level)
            {
                case 17: return "Hộp quà: cho khách CÙNG MÀU đứng cạnh rời hàng để mở quà (−5% Drama)!";
                case 22: return "Ghế có thẻ tên là của khách VIP: người khác ngồi vào thì VIP giận +15%. VIP chờ quá số lượt trên bảng tên cũng giận!";
                case 25: return "Hai khách nắm tay: đi cùng nhau và cần 2 ghế liền nhau.";
                case 31: return "Dây ruy băng: chỉ đứt khi một bàn cùng màu được xếp xong.";
                default: return null;
            }
        }

        void BuildBlockers()
        {
            for (int i = 0; i < sim.Gifts.Count; i++)
            {
                var gf = sim.Gifts[i];
                var col = Palette.Invite(gf.Color);
                var root = Palette.Pivot(world, "Gift" + i, CellPos(gf.Row, gf.Col));
                var box = Palette.Pivot(root, "Box", Vector3.zero);
                Palette.Prim(PrimitiveType.Cube, box, new Vector3(0, 0.3f, 0), new Vector3(0.7f, 0.6f, 0.7f), col);
                Palette.Prim(PrimitiveType.Cube, box, new Vector3(0, 0.3f, 0), new Vector3(0.14f, 0.62f, 0.72f), Palette.Cloth);
                Palette.Prim(PrimitiveType.Cube, box, new Vector3(0, 0.3f, 0), new Vector3(0.72f, 0.62f, 0.14f), Palette.Cloth);
                var lid = Palette.Pivot(box, "Lid", new Vector3(0, 0.64f, 0));
                Palette.Prim(PrimitiveType.Cube, lid, Vector3.zero, new Vector3(0.8f, 0.12f, 0.8f), Color.Lerp(col, Color.white, 0.3f));
                Palette.Prim(PrimitiveType.Sphere, lid, new Vector3(-0.12f, 0.12f, 0), new Vector3(0.22f, 0.15f, 0.12f), Palette.Gold);
                Palette.Prim(PrimitiveType.Sphere, lid, new Vector3(0.12f, 0.12f, 0), new Vector3(0.22f, 0.15f, 0.12f), Palette.Gold);
                Palette.Prim(PrimitiveType.Sphere, lid, new Vector3(0, 0.1f, 0), Vector3.one * 0.1f, Palette.Gold);
                var anchor = Palette.Pivot(root, "Anchor", new Vector3(0, 1.05f, 0));
                hud.AddNamePlate(anchor, gf.Count.ToString(), new List<string> { "gift" }, col);
                giftViews.Add((root, anchor));
            }
            for (int i = 0; i < sim.Ribbons.Count; i++)
            {
                var rb = sim.Ribbons[i];
                var col = Palette.Invite(rb.Color);
                Vector3 a = CellPos(rb.R1, rb.C1), b = CellPos(rb.R2, rb.C2);
                var root = Palette.Pivot(world, "Ribbon" + i, (a + b) / 2f);
                if (rb.R1 == rb.R2) root.rotation = Quaternion.Euler(0, 90, 0);   // cells side by side: the ribbon runs front to back
                foreach (var x in new[] { -0.52f, 0.52f })
                {
                    Palette.Prim(PrimitiveType.Cylinder, root, new Vector3(x, 0.32f, 0), new Vector3(0.07f, 0.32f, 0.07f), Palette.Gold);
                    Palette.Prim(PrimitiveType.Sphere, root, new Vector3(x, 0.68f, 0), Vector3.one * 0.11f, Palette.Gold);
                }
                var band = Palette.Pivot(root, "Band", Vector3.zero);
                Palette.Prim(PrimitiveType.Cube, band, new Vector3(0, 0.55f, 0), new Vector3(1.04f, 0.1f, 0.04f), col);
                Palette.Prim(PrimitiveType.Cube, band, new Vector3(0, 0.38f, 0), new Vector3(1.04f, 0.06f, 0.04f), col);
                Palette.Prim(PrimitiveType.Sphere, band, new Vector3(-0.09f, 0.57f, 0.02f), new Vector3(0.18f, 0.13f, 0.06f), col);
                Palette.Prim(PrimitiveType.Sphere, band, new Vector3(0.09f, 0.57f, 0.02f), new Vector3(0.18f, 0.13f, 0.06f), col);
                ribbonViews.Add(root);
            }
            foreach (var v in views.Values)
            {
                var mate = sim.HandsPartner(v.Guest);
                if (mate == null || mate.Id < v.Guest.Id || !views.TryGetValue(mate.Id, out var w)) continue;
                var bar = Palette.Prim(PrimitiveType.Capsule, world, Vector3.zero, Vector3.one, Palette.Sash, name: "HandLink").transform;
                handLinks.Add((v, w, bar));
            }
            UpdateHandLinks();
        }

        /// <summary>Two hand-holders stay visibly joined by their arms while they wait.</summary>
        void UpdateHandLinks()
        {
            for (int i = handLinks.Count - 1; i >= 0; i--)
            {
                var (a, b, bar) = handLinks[i];
                if (!bar) { handLinks.RemoveAt(i); continue; }
                if (!a || !b) { Destroy(bar.gameObject); handLinks.RemoveAt(i); continue; }
                Vector3 pa = a.transform.position + Vector3.up * 0.42f, pb = b.transform.position + Vector3.up * 0.42f;
                Vector3 d = pb - pa;
                bar.gameObject.SetActive(!a.IsHidden && !b.IsHidden);
                bar.position = (pa + pb) / 2f;
                bar.rotation = Quaternion.FromToRotation(Vector3.up, d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.right);
                bar.localScale = new Vector3(0.09f, Mathf.Max(0.05f, d.magnitude / 2f - 0.25f), 0.09f);
            }
        }

        void RemoveHandLink(GuestView v)
        {
            for (int i = handLinks.Count - 1; i >= 0; i--)
                if (handLinks[i].a == v || handLinks[i].b == v) { if (handLinks[i].bar) Destroy(handLinks[i].bar.gameObject); handLinks.RemoveAt(i); }
        }

        /// <summary>Name card on a reserved chair + a name tag above it, for every reserved guest of this colour still waiting.</summary>
        void DecorateReservations(TableView tv)
        {
            foreach (var g in sim.AllUnseated())
            {
                if (g.Reserved < 0 || g.Color != tv.Color || !views.TryGetValue(g.Id, out var v)) continue;
                var seat = tv.Anchors[g.Reserved];
                var card = Palette.Pivot(seat, "PlaceCard", new Vector3(0, 0.44f, 0.04f));
                var c1 = Palette.Prim(PrimitiveType.Cube, card, new Vector3(0, 0.06f, 0.03f), new Vector3(0.3f, 0.15f, 0.02f), Palette.Cloth);
                c1.transform.localRotation = Quaternion.Euler(-25, 0, 0);
                var c2 = Palette.Prim(PrimitiveType.Cube, card, new Vector3(0, 0.08f, 0.031f), new Vector3(0.22f, 0.03f, 0.022f), Palette.Invite(g.Color));
                c2.transform.localRotation = Quaternion.Euler(-25, 0, 0);
                var tag = Palette.Pivot(seat, "ReserveTag", new Vector3(0, 1.15f, 0));
                hud.AddNamePlate(tag, v.DisplayName, new List<string> { "reserve" }, Palette.Invite(g.Color));
                if (reserveTags.TryGetValue(g.Id, out var old) && old) hud.SetPlateMode(old, 0);
                reserveTags[g.Id] = tag;
            }
        }

        /// <summary>Someone other than the VIP took a name-card chair: the card goes, the VIP will sit anywhere now.</summary>
        void ClearReserveTagAt(TableView tv, int seat, Guest sitter)
        {
            if (!tv) return;
            int vipId = -1;
            foreach (var kv in reserveTags) if (kv.Key != sitter.Id && kv.Value && kv.Value.parent == tv.Anchors[seat]) { vipId = kv.Key; break; }
            if (vipId < 0) return;
            var tag = reserveTags[vipId];
            hud.SetPlateMode(tag, 0);
            var card = tag.parent.Find("PlaceCard");
            if (card) Destroy(card.gameObject);
            reserveTags.Remove(vipId);
        }

        readonly Dictionary<int, int> vipClock = new Dictionary<int, int>();

        /// <summary>VIP name plates (and their chair tags) count down the turns left before the VIP gets angry.</summary>
        void RefreshVipClocks()
        {
            foreach (var g in sim.AllUnseated())
            {
                if (g.Patience < 0 || !views.TryGetValue(g.Id, out var v) || !v) continue;
                int left = sim.PatienceLeft(g);
                if (vipClock.TryGetValue(g.Id, out var was) && was == left) continue;
                vipClock[g.Id] = left;
                string text = left < 0 ? v.DisplayName + " · giận!" : $"{v.DisplayName} · {left} lượt";
                hud.SetPlateText(v.BadgeAnchor, text);
                if (reserveTags.TryGetValue(g.Id, out var tag) && tag) hud.SetPlateText(tag, text);
                if (left >= 0 && left <= 2) { v.Emote("exclaim", 1.2f); if (left > 0) v.Play(GuestView.Act.CheckWatch, 1.2f); }
            }
        }

        /// <summary>Plays what the last move did to blockers: gift counters, gifts opening, ribbons being cut.</summary>
        void HandleBlockerEvents(bool pushDrama)
        {
            foreach (var e in sim.TakeEvents())
            {
                switch (e.Kind)
                {
                    case "gift":
                        if (e.Index < giftViews.Count)
                        {
                            hud.SetPlateText(giftViews[e.Index].anchor, e.CountAfter.ToString());
                            StartCoroutine(Bounce(giftViews[e.Index].root));
                            Sfx.Play("gift_tick", 1f, 1f + Mathf.Max(0, 3 - e.CountAfter) * 0.12f, 0f);
                        }
                        break;
                    case "gift_open":
                        if (e.Index < giftViews.Count)
                        {
                            var gv = giftViews[e.Index];
                            hud.SetPlateMode(gv.anchor, 0);
                            StartCoroutine(OpenGift(gv.root));
                            Sfx.Play("gift_open");
                            StartCoroutine(Later(0.45f, () => Sfx.Play("coin")));
                            hud.Popup(gv.root.position + Vector3.up * 1.2f, "Quà mừng cưới! −5%", true, 0.15f);
                            coins += 10; hud.SetCoins(coins);
                            if (bride) bride.Play(GuestView.Act.Cheer, 1f, null, "sparkle");
                        }
                        if (pushDrama) PushDrama();
                        break;
                    case "vip_angry":
                        if (views.TryGetValue(e.Index, out var vipView) && vipView)
                        {
                            vipView.Play(GuestView.Act.Furious, 2.2f, null, "anger");
                            vipView.CurrentMood = GuestView.Mood.Furious;
                            vipView.Shake();
                            hud.Popup(vipView.transform.position + Vector3.up * 1.6f, "VIP chờ lâu, nổi giận! +15%", false);
                            Sfx.Play("drama_up");
                        }
                        if (pushDrama) PushDrama();
                        break;
                    case "ribbon_cut":
                        if (e.Index < ribbonViews.Count) StartCoroutine(Later(1.6f, () => { if (ribbonViews[e.Index]) { StartCoroutine(CutRibbon(ribbonViews[e.Index])); Sfx.Play("ribbon_cut"); hud.Toast("Dây ruy băng đã được cắt, lối mở!"); } }));
                        break;
                }
            }
        }

        IEnumerator Bounce(Transform t)
        {
            for (float k = 0; k < 0.25f; k += Time.deltaTime)
            {
                if (!t) yield break;
                float s = Mathf.Sin(k / 0.25f * Mathf.PI) * 0.15f;
                t.localScale = new Vector3(1 + s, 1 - s, 1 + s);
                yield return null;
            }
            if (t) t.localScale = Vector3.one;
        }

        IEnumerator OpenGift(Transform root)
        {
            var lid = root.Find("Box/Lid");
            var bits = new List<(Transform t, Vector3 v)>();
            var cols = new[] { Palette.Gold, Palette.Flower, Color.white, Palette.Leaf, Palette.Blush };
            for (int i = 0; i < 14; i++)
            {
                var b = Palette.Prim(PrimitiveType.Cube, root, new Vector3(0, 0.7f, 0), Vector3.one * 0.09f, cols[i % cols.Length], name: "Confetti").transform;
                float a = i / 14f * Mathf.PI * 2;
                bits.Add((b, new Vector3(Mathf.Cos(a) * 1.6f, 3.2f + (i % 3) * 0.6f, Mathf.Sin(a) * 1.6f)));
            }
            for (float k = 0; k < 0.9f; k += Time.deltaTime)
            {
                if (!root) yield break;
                if (lid) { lid.localPosition += Vector3.up * Time.deltaTime * 3f; lid.localRotation *= Quaternion.Euler(0, 0, 400 * Time.deltaTime); }
                var box = root.Find("Box");
                if (box) box.localScale = Vector3.one * Mathf.Max(0, 1 - k / 0.6f);
                for (int i = 0; i < bits.Count; i++)
                {
                    var (t, v) = bits[i];
                    v += Vector3.down * 9f * Time.deltaTime;
                    bits[i] = (t, v);
                    t.localPosition += v * Time.deltaTime;
                    t.localRotation *= Quaternion.Euler(300 * Time.deltaTime, 200 * Time.deltaTime, 0);
                }
                yield return null;
            }
            if (root) Destroy(root.gameObject);
        }

        IEnumerator CutRibbon(Transform root)
        {
            var band = root.Find("Band");
            for (float k = 0; k < 0.45f; k += Time.deltaTime)
            {
                if (!root) yield break;
                if (band) { band.localScale = new Vector3(Mathf.Max(0.01f, 1 - k / 0.45f), 1, 1); band.localPosition = Vector3.down * k * 0.4f; }
                yield return null;
            }
            if (root) Destroy(root.gameObject);
        }

        void BuildBench()
        {
            foreach (var s in benchSlots) if (s) Destroy(s.gameObject);
            benchSlots.Clear();
            for (int i = 0; i < sim.Bench.Length; i++)
            {
                var sofa = Palette.Pivot(world, "Sofa" + i, BenchPos(i));
                Palette.Prim(PrimitiveType.Cube, sofa, new Vector3(0, 0.2f, 0.02f), new Vector3(0.95f, 0.36f, 0.72f), Palette.Hex("#EBC1B3"));
                Palette.Prim(PrimitiveType.Cube, sofa, new Vector3(0, 0.55f, 0.32f), new Vector3(0.95f, 0.62f, 0.16f), Palette.Hex("#E2A894"));
                foreach (var x in new[] { -0.46f, 0.46f })
                    Palette.Prim(PrimitiveType.Cube, sofa, new Vector3(x, 0.38f, 0.04f), new Vector3(0.12f, 0.36f, 0.7f), Palette.Hex("#D99683"));
                Palette.Prim(PrimitiveType.Cube, sofa, new Vector3(0, 0.39f, -0.02f), new Vector3(0.8f, 0.06f, 0.6f), Palette.Hex("#F6D6CB"));
                var col = sofa.gameObject.AddComponent<BoxCollider>();
                col.center = new Vector3(0, 0.45f, 0); col.size = new Vector3(1.05f, 1f, 0.95f);
                var slot = sofa.gameObject.AddComponent<BenchSlot>();
                slot.Index = i;
                slot.Seat = Palette.Pivot(sofa, "Seat", new Vector3(0, 0.08f, -0.02f));
                slot.Seat.rotation = Quaternion.LookRotation(Vector3.back);
                benchSlots.Add(slot);
            }
        }

        void BuildTables()
        {
            var positions = TableLayout(sim.Colors.Count);
            for (int i = 0; i < sim.Colors.Count; i++)
            {
                char col = sim.Colors[i];
                tablePos[col] = positions[i];
                tables[col] = TableView.Create(col, positions[i], world);
                DecorateReservations(tables[col]);
            }
        }

        static Vector3[] TableLayout(int n)
        {
            switch (n)
            {
                case 1: return new[] { new Vector3(-TableX, 0, 5.8f) };
                case 2: return new[] { new Vector3(-TableX, 0, 5.8f), new Vector3(TableX, 0, 5.8f) };
                case 3: return new[] { new Vector3(-TableX, 0, 8.0f), new Vector3(TableX, 0, 8.0f), new Vector3(-TableX, 0, 4.4f) };
                default: return new[] { new Vector3(-TableX, 0, 8.0f), new Vector3(TableX, 0, 8.0f), new Vector3(-TableX, 0, 4.4f), new Vector3(TableX, 0, 4.4f) };
            }
        }

        void BuildNav()
        {
            float minZ = CrowdTopZ - sim.Rows * CellSize - 1.5f;
            nav = new NavGrid(-5.5f, 5.5f, minZ, StageZ + 2f, 0.1f);
            // the waiting crowd is never walked through (guests leave it only along their own free lane)
            float crowdHalfW = sim.Cols * CellSize / 2f + 0.1f, crowdHalfD = sim.Rows * CellSize / 2f + 0.1f;
            nav.BlockRect(new Vector3(0, 0, CrowdTopZ - (sim.Rows - 1) * CellSize / 2f), new Vector2(crowdHalfW, crowdHalfD), GuestRadius);
            for (int i = 0; i < sim.Bench.Length; i++) nav.BlockRect(BenchPos(i), new Vector2(0.5f, 0.38f), GuestRadius);
            foreach (var kv in tablePos)
            {
                nav.BlockCircle(kv.Value, 0.82f + GuestRadius);
                for (int k = 0; k < Table.SeatCount; k++)
                {
                    var t = tables[kv.Key].Anchors[k];
                    nav.BlockRotatedRect(t.position, t.rotation, new Vector2(0.27f, 0.27f), GuestRadius);
                }
            }
            nav.BlockRect(new Vector3(0, 0, StageZ + 0.5f), new Vector2(2.85f, 0.7f), GuestRadius);
            foreach (var (p, r) in decorObstacles) nav.BlockCircle(p, r + GuestRadius);
        }

        void RefreshCrowd()
        {
            freeIds.Clear();
            for (int r = 0; r < sim.Rows; r++)
                for (int c = 0; c < sim.Cols; c++)
                {
                    var g = sim.Grid[r, c];
                    if (g == null || !views.TryGetValue(g.Id, out var v)) continue;
                    if (v.IsHidden && !g.Hidden) StartCoroutine(v.Reveal());
                    bool free = sim.HasPath(r, c);
                    v.SetFree(free);
                    if (free) freeIds.Add(g.Id);
                }
            foreach (var g in sim.Bench) if (g != null) freeIds.Add(g.Id);
            hud.SetSeated(sim.Seated, sim.Total);
            RefreshPlates();
        }

        /// <summary>Names only where the player can act: guests who can move now (crowd with a free lane, lounge),
        /// plus the selected guest and their relations. Blocked guests keep a small icon dot; seated guests none.</summary>
        void RefreshPlates()
        {
            RefreshVipClocks();
            foreach (var v in views.Values)
            {
                if (!v) continue;
                int mode;
                if (infoTarget && (v == infoTarget || relatedLit.Contains(v))) mode = 2;
                else if (!v.Waiting) mode = 0;
                else mode = freeIds.Contains(v.Guest.Id) ? 2 : 1;
                hud.SetPlateMode(v.BadgeAnchor, mode);
            }
        }

        void RefreshBoosters()
        {
            bool crowdLeft = false;
            for (int r = 0; r < sim.Rows && !crowdLeft; r++) for (int c = 0; c < sim.Cols; c++) if (sim.Grid[r, c] != null) { crowdLeft = true; break; }
            hud.SetBooster("bouncer", boosterCount["bouncer"], crowdLeft && sim.FreeBenchSlot() >= 0 && !ended);
            hud.SetBooster("sofa", boosterCount["sofa"], sim.Bench.Length < 6 && !ended);
            hud.SetBooster("champagne", boosterCount["champagne"], sim.Drama > 0 && !ended);
            hud.SetBoosterHighlight("bouncer", bouncerMode);
        }

        // ---------- input ----------

        Vector2Int lastScreen;

        void Update()
        {
            if (sim == null) return;
            // Flex to the screen: reframe when the window/device size or aspect changes.
            var scr = new Vector2Int(Screen.width, Screen.height);
            if (cam && scr != lastScreen && scr.x > 0 && scr.y > 0)
            {
                lastScreen = scr;
                cam.ResetAspect();
                FrameCamera();
            }
            UpdateHandLinks();
            if (cam)
            {
                if (Time.time < camShakeUntil)
                {
                    float k = (camShakeUntil - Time.time);
                    cam.transform.position = camBase + (cam.transform.right * (Mathf.PerlinNoise(Time.time * 30, 0) - 0.5f) + cam.transform.up * (Mathf.PerlinNoise(0, Time.time * 30) - 0.5f)) * camShakeAmp * Mathf.Clamp01(k * 3);
                }
                else if (camShakeUntil > 0) { cam.transform.position = camBase; camShakeUntil = 0; }
            }
            foreach (var kv in tables) if (kv.Value) kv.Value.SetLocked(TableLocked(kv.Key));
            if (ended) return;
            if (PointerDown(out var p) && !OverUI()) { pressing = true; dragging = false; pressPos = p; OnPress(p); }
            if (pressing)
            {
                var cur = PointerPosition();
                if (!dragging && sel.Valid && (cur - pressPos).magnitude > 14) dragging = true;
                if (dragging) OnDrag(cur);
                if (PointerUp()) { pressing = false; OnRelease(cur); }
            }
        }

        static bool PointerDown(out Vector2 pos)
        {
#if ENABLE_INPUT_SYSTEM
            var ptr = UnityEngine.InputSystem.Pointer.current;
            pos = ptr != null ? ptr.position.ReadValue() : Vector2.zero;
            return ptr != null && ptr.press.wasPressedThisFrame;
#else
            pos = Input.mousePosition;
            return Input.GetMouseButtonDown(0);
#endif
        }

        static bool PointerUp()
        {
#if ENABLE_INPUT_SYSTEM
            var ptr = UnityEngine.InputSystem.Pointer.current;
            return ptr == null || ptr.press.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(0);
#endif
        }

        static Vector2 PointerPosition()
        {
#if ENABLE_INPUT_SYSTEM
            var ptr = UnityEngine.InputSystem.Pointer.current;
            return ptr != null ? ptr.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }

        static bool OverUI() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        T HitAt<T>(Vector2 screen) where T : Component
        {
            var hits = Physics.RaycastAll(cam.ScreenPointToRay(screen), 200f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits) { var t = h.collider.GetComponentInParent<T>(); if (t) return t; }
            return null;
        }

        void OnPress(Vector2 p)
        {
            if (bouncerMode)
            {
                var target = HitAt<GuestView>(p);
                if (target && target.Guest != null && FindSource(target.Guest, out var s) && !s.Bench) { UseBouncerOn(s, target); return; }
                bouncerMode = false; RefreshBoosters(); hud.Toast("Đã hủy Bảo vệ.");
                return;
            }
            if (sel.Valid)
            {
                var seat = HitAt<SeatTarget>(p);
                if (seat && TrySeat(seat)) return;
                var sofa = HitAt<BenchSlot>(p);
                if (sofa && !sel.Bench && TryBench()) return;
            }
            var gv = HitAt<GuestView>(p);
            if (gv && gv.Guest != null)
            {
                if (FindSource(gv.Guest, out var src))
                {
                    if (sel.Valid && selView == gv) return;
                    Select(src, gv);
                }
                else { Deselect(); ShowInfoFor(gv); }    // seated guest: just show who they are
                return;
            }
            Deselect();
            hud.ShowInfo(null);
        }

        void OnDrag(Vector2 cur)
        {
            var ray = cam.ScreenPointToRay(cur);
            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float d))
            {
                dragMarker.gameObject.SetActive(true);
                dragMarker.position = ray.GetPoint(d) + Vector3.up * 0.03f;
            }
            if (selView) selView.transform.position = Vector3.Lerp(selView.transform.position, selHome + Vector3.up * 0.55f, Time.deltaTime * 12);
            if (selPartner) selPartner.transform.position = Vector3.Lerp(selPartner.transform.position, partnerHome + Vector3.up * 0.55f, Time.deltaTime * 12);
        }

        void OnRelease(Vector2 cur)
        {
            dragMarker.gameObject.SetActive(false);
            if (!dragging) return;
            dragging = false;
            if (!sel.Valid) return;
            var seat = HitAt<SeatTarget>(cur);
            if (seat && TrySeat(seat)) return;
            var sofa = HitAt<BenchSlot>(cur);
            if (sofa && !sel.Bench) TryBench();
        }

        bool FindSource(Guest g, out Src src)
        {
            src = default;
            for (int r = 0; r < sim.Rows; r++)
                for (int c = 0; c < sim.Cols; c++)
                    if (sim.Grid[r, c] == g) { src = new Src { Valid = true, R = r, C = c }; return true; }
            for (int i = 0; i < sim.Bench.Length; i++)
                if (sim.Bench[i] == g) { src = new Src { Valid = true, Bench = true, I = i }; return true; }
            return false;
        }

        bool TableLocked(char color) => busyTables.Contains(color) || (lockedUntil.TryGetValue(color, out var t) && Time.time < t);

        void Select(Src src, GuestView gv)
        {
            Deselect();
            ShowInfoFor(gv);
            if (gv.IsHidden) { Sfx.Play("deny"); gv.Shake(); gv.Emote("question", 1.2f); hud.Toast("Khách bí ẩn! Mở lối để biết họ là ai."); return; }
            var mate = sim.HandsPartner(gv.Guest);
            bool canLeave = src.Bench || (mate != null ? sim.PairCanLeave(src.R, src.C) : sim.HasPath(src.R, src.C));
            if (!canLeave)
            {
                Sfx.Play("deny");
                gv.Shake();
                gv.Play(GuestView.Act.Shrug, 1f, null, gv.WaitTime > 15 ? "anger" : "sweat");
                hud.Toast("Bị chặn đường! Đưa những khách phía trước vào chỗ trước.");
                return;
            }
            sel = src; selView = gv; selHome = gv.transform.position;
            Sfx.Play("select"); Sfx.Play("lift");
            gv.SetPose(GuestView.Pose.Lift);
            gv.transform.position = selHome + Vector3.up * 0.55f;
            if (mate != null && views.TryGetValue(mate.Id, out var pv) && pv)
            {
                selPartner = pv; partnerHome = pv.transform.position;
                pv.SetPose(GuestView.Pose.Lift);
                pv.transform.position = partnerHome + Vector3.up * 0.55f;
            }

            var g = gv.Guest;
            var chips = new List<(Vector3, int)>();
            pairChoice.Clear();
            if (tables.TryGetValue(g.Color, out var tv) && tv && !TableLocked(g.Color))
            {
                if (mate != null)
                {
                    // a pair: for each seat this guest could take, the partner's best neighbouring seat
                    var best = new Dictionary<int, int>();
                    foreach (var (k, j) in sim.PairSeats(g))
                    {
                        var probe = sim.Clone(); int before = probe.Drama;
                        probe.SeatPairFromCrowd(src.R, src.C, k, j);
                        int d = probe.Drama - before;
                        if (!best.ContainsKey(k) || d < best[k]) { best[k] = d; pairChoice[k] = j; }
                    }
                    foreach (var kv in best)
                    {
                        tv.SetHighlight(kv.Key, true, kv.Value);
                        chips.Add((tv.Anchors[kv.Key].position + Vector3.up * 1.0f, kv.Value));
                    }
                    if (best.Count == 0) hud.Toast("Cần 2 ghế liền nhau còn trống cho cặp nắm tay.");
                }
                else
                    foreach (var k in sim.SeatsFor(g))
                    {
                        int delta = 0;
                        foreach (var e in sim.PreviewEffects(g, k)) delta += e.Delta;
                        tv.SetHighlight(k, true, delta);
                        chips.Add((tv.Anchors[k].position + Vector3.up * 1.0f, delta));
                    }
            }
            else hud.Toast($"Bàn {Palette.InviteName(g.Color)} đang dọn, chờ chút hoặc cho khách ra Sảnh chờ.");
            hud.SetChips(chips);
            tutBestSeat = null;
            if (chips.Count > 0 && tv)
            {
                int bi = 0; for (int i = 1; i < chips.Count; i++) if (chips[i].Item2 < chips[bi].Item2) bi = i;
                float bestD = float.MaxValue;
                foreach (var a in tv.Anchors) { float d = (a.position + Vector3.up - chips[bi].Item1).sqrMagnitude; if (d < bestD) { bestD = d; tutBestSeat = a; } }
            }
            TutorialOnSelect();
        }

        void Deselect()
        {
            if (selView && sel.Valid)
            {
                selView.transform.position = selHome;
                selView.SetPose(sel.Bench ? GuestView.Pose.Sit : GuestView.Pose.Idle);
            }
            if (selPartner) { selPartner.transform.position = partnerHome; selPartner.SetPose(GuestView.Pose.Idle); }
            selPartner = null;
            foreach (var t in tables.Values) if (t) t.ClearHighlights();
            foreach (var v in relatedLit) if (v) v.SetRelated(false);
            relatedLit.Clear();
            hud.SetChips(new List<(Vector3, int)>());
            hud.FocusBadges(null);
            infoTarget = null;
            sel = default; selView = null;
            RefreshPlates();
        }

        // ---------- guest info ----------

        void ShowInfoFor(GuestView gv)
        {
            foreach (var v in relatedLit) if (v) v.SetRelated(false);
            relatedLit.Clear();
            var g = gv.Guest;
            if (gv.IsHidden)
            {
                hud.ShowInfo(new Hud.InfoData { Name = "???", Role = "Bí ẩn", Invite = Palette.Hidden,
                    Wishes = new List<Hud.Wish> { new Hud.Wish { Text = "Mở lối để biết là ai" } } });
                return;
            }
            foreach (var v in views.Values)
                if (v && v != gv && v.Guest != null && IsRelated(g, v.Guest)) { v.SetRelated(true); relatedLit.Add(v); }

            var wishes = new List<Hud.Wish>();
            string role;
            switch (g.Type)
            {
                case GuestType.Grandma:
                    role = "Bà nội";
                    wishes.Add(new Hud.Wish { Text = "Ngồi ghế ngôi sao", Delta = SeatingSim.GrandmaNotStar, State = StarState(g) });
                    wishes.Add(new Hud.Wish { Text = "Không sát ông chú", Delta = SeatingSim.GrandmaScold, State = NeighbourState(g, GuestType.Uncle, true) });
                    break;
                case GuestType.Uncle:
                    role = "Ông chú say";
                    wishes.Add(new Hud.Wish { Text = "Không sát bà nội", Delta = SeatingSim.GrandmaScold, State = NeighbourState(g, GuestType.Grandma, true) });
                    break;
                case GuestType.Gossip:
                    role = "Bà tám";
                    wishes.Add(new Hud.Wish { Text = "Ngồi sát bà tám khác", Delta = SeatingSim.GossipBonus, State = NeighbourState(g, GuestType.Gossip, false) });
                    break;
                case GuestType.Kid: role = "Em bé"; break;
                default: role = g.Links.Count == 0 ? "Khách mời" : LinkRole(g.Links[0].Kind); break;
            }
            if (g.Reserved >= 0)
            {
                bool seatedNow = sim.TryGetPlacement(g.Id, out _, out int mySeat, out _);
                wishes.Add(new Hud.Wish { Text = "Ghế có tên tôi là của tôi", Delta = SeatingSim.VipSeatTaken, State = seatedNow ? (mySeat == g.Reserved ? 1 : -1) : 0 });
                if (g.Patience >= 0)
                {
                    int left = sim.PatienceLeft(g);
                    wishes.Add(new Hud.Wish { Text = seatedNow ? "Được vào chỗ kịp lúc" : left < 0 ? "Chờ quá lâu rồi!" : $"Chờ tối đa {left} lượt nữa", Delta = SeatingSim.VipImpatient, State = sim.IsAngryVip(g) ? -1 : seatedNow ? 1 : 0 });
                }
                if (g.Links.Count == 0 && g.Type == GuestType.Normal) role = "Khách VIP";
            }
            foreach (var l in g.Links)
            {
                string who = views.TryGetValue(l.Other, out var ov) && ov ? ov.DisplayName : "?";
                wishes.Add(new Hud.Wish { Text = WishText(l.Kind, who, ov), Delta = WishDelta(l.Kind), State = LinkState(g, l) });
            }
            if (wishes.Count == 0) wishes.Add(new Hud.Wish { Text = "Ngồi đâu cũng được" });
            hud.ShowInfo(new Hud.InfoData { Name = gv.DisplayName, Role = role, Invite = Palette.Invite(g.Color), Wishes = wishes });
            var focus = new List<Transform> { gv.BadgeAnchor };
            foreach (var v in relatedLit) focus.Add(v.BadgeAnchor);
            hud.FocusBadges(focus);
            infoTarget = gv;
            RefreshPlates();
        }

        static string WishText(LinkKind k, string who, GuestView other)
        {
            bool mum = other && other.Look != Archetype.Boy && other.Look != Archetype.Uncle;
            switch (k)
            {
                case LinkKind.Couple: return "Ngồi sát " + who;
                case LinkKind.Ex: return "Tránh xa " + who;
                case LinkKind.Rival: return "Khác bàn với " + who;
                case LinkKind.Bff: return "Chung bàn với " + who;
                case LinkKind.ParentOf: return "Bé " + who.Replace("Bé ", "") + " ngồi sát";
                case LinkKind.ChildOf: return "Ngồi sát " + (mum ? "mẹ " : "bố ") + who;
                case LinkKind.CrushOn: return "Mong ngồi cạnh " + who;
                case LinkKind.Hands: return "Nắm tay " + who + ", ngồi liền";
                default: return who + " thầm thương";
            }
        }

        /// <summary>The one number that matters for the wish: the reward for the nice ones, the penalty for the rest.</summary>
        static int WishDelta(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Couple: return SeatingSim.CoupleBonus;
                case LinkKind.Ex: return SeatingSim.ExFight;
                case LinkKind.Rival: return SeatingSim.RivalClash;
                case LinkKind.Bff: return SeatingSim.BffBonus;
                case LinkKind.ParentOf: case LinkKind.ChildOf: return SeatingSim.KidCries;
                case LinkKind.Hands: return 0;
                default: return SeatingSim.CrushBonus;
            }
        }

        /// <summary>1 = the wish came true, -1 = it was broken, 0 = not decided yet (or a missed bonus).</summary>
        int LinkState(Guest g, Link l)
        {
            if (!sim.TryGetPlacement(g.Id, out char c1, out int s1, out int g1) || !sim.TryGetPlacement(l.Other, out char c2, out int s2, out int g2)) return 0;
            bool same = c1 == c2 && g1 == g2, adj = same && Table.Adjacent(s1, s2), opp = same && Table.Opposite(s1) == s2;
            switch (l.Kind)
            {
                case LinkKind.Couple: case LinkKind.ParentOf: case LinkKind.ChildOf: case LinkKind.Hands: return adj ? 1 : -1;
                case LinkKind.Ex: return adj || opp ? -1 : 1;
                case LinkKind.Rival: return same ? -1 : 1;
                case LinkKind.Bff: return same ? 1 : 0;
                default: return adj ? 1 : 0;
            }
        }

        int StarState(Guest g) => sim.TryGetPlacement(g.Id, out _, out int seat, out _) ? (seat == Table.StarSeat ? 1 : -1) : 0;

        /// <summary>Trait wishes about a neighbour type at the guest's current table (avoid = it must NOT be there).</summary>
        int NeighbourState(Guest g, GuestType other, bool avoid)
        {
            if (!sim.TryGetPlacement(g.Id, out char col, out int seat, out int gen)) return 0;
            if (!sim.Tables.TryGetValue(col, out var t) || t.Generation != gen) return avoid ? 1 : 0;
            var l = t.Seats[Table.Left(seat)]; var r = t.Seats[Table.Right(seat)];
            bool near = (l != null && l.Type == other) || (r != null && r.Type == other);
            return avoid ? (near ? -1 : 1) : (near ? 1 : 0);
        }

        static string LinkRole(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Couple: return "Cặp đôi";
                case LinkKind.Ex: return "Người yêu cũ";
                case LinkKind.Rival: return "Kình địch";
                case LinkKind.Bff: return "Bạn thân";
                case LinkKind.ParentOf: return "Phụ huynh";
                case LinkKind.ChildOf: return "Em bé";
                case LinkKind.CrushOn: return "Thầm thương";
                case LinkKind.Hands: return "Nắm tay";
                default: return "Được thầm thương";
            }
        }

        static Color LinkColor(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Couple: return Palette.Hex("#EF4F78");
                case LinkKind.Ex: return Palette.Hex("#6E5664");
                case LinkKind.Rival: return Palette.Hex("#E07B24");
                case LinkKind.Bff: return Palette.Hex("#25A5A0");
                case LinkKind.ParentOf: case LinkKind.ChildOf: return Palette.Hex("#5B9BE0");
                default: return Palette.Hex("#E2557A");
            }
        }

        static string LinkRule(LinkKind k, string who)
        {
            switch (k)
            {
                case LinkKind.Couple: return $"Người yêu <b>{who}</b>: ngồi sát <b>−10%</b>, bị tách <b>+15%</b>";
                case LinkKind.Ex: return $"Người yêu cũ <b>{who}</b>: ngồi sát <b>+30%</b> (ném bánh), đối diện <b>+10%</b>";
                case LinkKind.Rival: return $"Kình địch <b>{who}</b>: chung bàn là cãi nhau <b>+20%</b>";
                case LinkKind.Bff: return $"Bạn thân <b>{who}</b>: chung bàn <b>−8%</b>";
                case LinkKind.ParentOf: return $"Con <b>{who}</b>: ngồi sát <b>−5%</b>, xa con là bé khóc <b>+15%</b>";
                case LinkKind.ChildOf: return $"Bố/mẹ <b>{who}</b>: ngồi sát <b>−5%</b>, xa ra là khóc <b>+15%</b>";
                case LinkKind.CrushOn: return $"Thầm thương <b>{who}</b>: được ngồi sát <b>−12%</b>";
                default: return $"<b>{who}</b> thầm thương khách này: ngồi sát <b>−12%</b>";
            }
        }

        // ---------- moves ----------

        bool TrySeat(SeatTarget target)
        {
            var g = selView.Guest;
            if (target.Table == null || target.Table.Color != g.Color || TableLocked(g.Color)) return false;
            int k = target.Seat;
            if (selPartner) return TrySeatPair(k);
            bool ok = sel.Bench ? sim.CanSeatFromBench(sel.I, k) : sim.CanSeatFromCrowd(sel.R, sel.C, k);
            if (!ok) return false;

            var src = sel; var gv = selView;
            List<Cell> path = src.Bench ? null : sim.FindPath(src.R, src.C);
            Vector3 start = src.Bench ? benchSlots[src.I].transform.position : selHome;
            ClearSelectionVisuals();

            var res = src.Bench ? sim.SeatFromBench(src.I, k) : sim.SeatFromCrowd(src.R, src.C, k);
            var tv = tables[g.Color];
            ClearReserveTagAt(tv, k, g);
            tv.Seated[k] = gv;
            if (res.TableCompleted) busyTables.Add(g.Color);
            if (!src.Bench) CrowdWatch(gv);
            RefreshCrowd(); RefreshBoosters();
            HandleBlockerEvents(false);
            StartCoroutine(SeatRoutine(gv, start, path, src.Bench, tv, k, res));
            TutorialOnSeat();
            return true;
        }

        /// <summary>A hand-holding pair: the one with a free lane leads, the partner follows through its cell; they sit side by side.</summary>
        bool TrySeatPair(int k)
        {
            if (sel.Bench || !pairChoice.TryGetValue(k, out int j) || !sim.CanSeatPair(sel.R, sel.C, k, j)) return false;
            var src = sel; var gv = selView; var pv = selPartner;
            var g = gv.Guest; var mate = pv.Guest;
            sim.Locate(mate, out int pr, out int pc);
            bool meLead = sim.HasPath(src.R, src.C);
            var lead = meLead ? sim.FindPath(src.R, src.C) : sim.FindPath(pr, pc);
            var follow = new List<Cell> { meLead ? new Cell(pr, pc) : new Cell(src.R, src.C) };
            follow.AddRange(lead);
            Vector3 myStart = selHome, mateStart = partnerHome;
            ClearSelectionVisuals();
            pv.transform.position = mateStart; gv.transform.position = myStart;
            var (r1, r2) = sim.SeatPairFromCrowd(src.R, src.C, k, j);
            var tv = tables[g.Color];
            tv.Seated[k] = gv; tv.Seated[j] = pv;
            ClearReserveTagAt(tv, k, g); ClearReserveTagAt(tv, j, mate);
            if (r1.TableCompleted || r2.TableCompleted) busyTables.Add(g.Color);
            RemoveHandLink(gv);
            CrowdWatch(gv);
            RefreshCrowd(); RefreshBoosters();
            HandleBlockerEvents(false);
            var myRoute = meLead ? lead : follow; var mateRoute = meLead ? follow : lead;
            StartCoroutine(SeatRoutine(meLead ? gv : pv, meLead ? myStart : mateStart, meLead ? myRoute : mateRoute, false, tv, meLead ? k : j, meLead ? r1 : r2));
            StartCoroutine(Delayed(0.35f, SeatRoutine(meLead ? pv : gv, meLead ? mateStart : myStart, meLead ? mateRoute : myRoute, false, tv, meLead ? j : k, meLead ? r2 : r1)));
            return true;
        }

        IEnumerator Delayed(float seconds, IEnumerator routine)
        {
            running++;
            yield return new WaitForSeconds(seconds);
            running--;
            yield return routine;
        }

        bool TryBench()
        {
            var src = sel; var gv = selView;
            if (selPartner) { hud.Toast("Hai người nắm tay không chịu tách ra ngồi sofa!"); gv.Emote("hands", 1.2f); return false; }
            if (!sim.CanBench(src.R, src.C)) { Sfx.Play("deny"); hud.Toast("Sảnh chờ đã đầy."); return false; }
            var path = sim.FindPath(src.R, src.C);
            ClearSelectionVisuals();
            int slot = sim.MoveToBench(src.R, src.C);
            CrowdWatch(gv);
            RefreshCrowd(); RefreshBoosters();
            HandleBlockerEvents(true);
            StartCoroutine(BenchRoutine(gv, path, benchSlots[slot]));
            return true;
        }

        void ClearSelectionVisuals()
        {
            foreach (var t in tables.Values) if (t) t.ClearHighlights();
            foreach (var v in relatedLit) if (v) v.SetRelated(false);
            relatedLit.Clear();
            hud.SetChips(new List<(Vector3, int)>());
            hud.ShowInfo(null);
            hud.FocusBadges(null);
            infoTarget = null;
            sel = default; selView = null; selPartner = null;
            RefreshPlates();
        }

        /// <summary>Crowd lane cells after the start, then the entrance point in front of the rope.</summary>
        List<Vector3> CrowdExit(List<Cell> path)
        {
            var pts = new List<Vector3>();
            if (path == null) return pts;
            for (int i = 1; i < path.Count; i++) pts.Add(CellPos(path[i].Row, path[i].Col));
            pts.Add(EntrancePos(path[path.Count - 1].Col));
            return pts;
        }

        IEnumerator BenchRoutine(GuestView gv, List<Cell> path, BenchSlot slot)
        {
            running++;
            gv.SetFree(false);
            gv.Blocked = false;
            gv.transform.position = new Vector3(gv.transform.position.x, 0, gv.transform.position.z);
            gv.SetPose(GuestView.Pose.Idle);
            yield return gv.Hop();
            var pts = CrowdExit(path);
            Vector3 from = pts.Count > 0 ? pts[pts.Count - 1] : gv.transform.position;
            Vector3 front = slot.transform.position + Vector3.back * 0.8f;
            pts.AddRange(nav.FindPath(from, front));
            yield return gv.WalkTo(pts);
            yield return gv.SitAt(slot.Seat);
            Sfx.Play("bench", 0.8f);
            running--;
            CheckEnd();
        }

        IEnumerator SeatRoutine(GuestView gv, Vector3 start, List<Cell> path, bool fromBench, TableView tv, int seat, SeatResult res)
        {
            running++;
            gv.SetFree(false);
            gv.Waiting = false; gv.Blocked = false; gv.CurrentMood = GuestView.Mood.Neutral;
            gv.transform.position = new Vector3(start.x, 0, start.z);
            gv.SetPose(GuestView.Pose.Idle);
            yield return gv.Hop();

            var pts = fromBench ? new List<Vector3> { start + Vector3.back * 0.8f } : CrowdExit(path);
            Vector3 from = pts[pts.Count - 1];
            Vector3 approach = tv.ApproachPoint(seat);
            pts.AddRange(nav.FindPath(from, approach));
            yield return gv.WalkTo(pts);
            yield return gv.SitAt(tv.Anchors[seat]);
            Sfx.Play("seat", 1f, 1f + seat * 0.05f);
            gv.transform.SetParent(tv.Anchors[seat], true);
            if (reserveTags.TryGetValue(gv.Guest.Id, out var tag) && tag)
            {
                hud.SetPlateMode(tag, 0);
                var card = tag.parent.Find("PlaceCard");
                if (card) Destroy(card.gameObject);
                reserveTags.Remove(gv.Guest.Id);
            }
            RefreshPlates();

            PushDrama();
            float delay = 0; int net = 0;
            foreach (var e in res.Effects)
            {
                if (e.Key == EffectKeys.PerfectTable) continue;
                hud.Popup(tv.transform.position + Vector3.up * 1.4f, EffectText(e), e.Delta < 0, delay);
                delay += 0.5f; net += e.Delta;
                if (e.LocksTable) lockedUntil[tv.Color] = Time.time + 2.5f;
            }
            ReactHall(net, tv.transform.position + Vector3.up);
            if (net == 0 && res.Effects.Count == 0) hud.Popup(tv.transform.position + Vector3.up * 1.4f, "Chuẩn chỗ!", true);
            TutorialOnDramaUp(net);
            yield return Perform(res, gv, tv, seat);

            if (res.TableCompleted)
            {
                yield return new WaitForSeconds(0.3f);
                if (res.PerfectTable)
                {
                    hud.Popup(tv.transform.position + Vector3.up * 1.4f, "Bàn hoàn hảo −5%", true);
                    foreach (var m in tv.Seated) if (m) m.Emote("sparkle", 1.4f);
                    ReactHall(-5, tv.transform.position + Vector3.up);
                }
                coins += res.PerfectTable ? 15 : 5; hud.SetCoins(coins);
                Sfx.Play("table_done"); Sfx.Play("cheer", res.PerfectTable ? 1f : 0.7f);
                StartCoroutine(Later(0.5f, () => Sfx.Play("coin")));
                TutorialOnTableDone();
                yield return tv.Celebrate();
                PushDrama();
                if (res.NewTableSpawned) { tables[tv.Color] = TableView.Create(tv.Color, tablePos[tv.Color], world, popIn: true); DecorateReservations(tables[tv.Color]); }
                else tables.Remove(tv.Color);
                busyTables.Remove(tv.Color);
            }
            running--;
            RefreshBoosters();
            CheckEnd();
        }

        // ---------- acting ----------

        void PushDrama()
        {
            Sfx.SetDrama(sim.Drama);
            hud.SetDrama(sim.Drama);
            GuestView.HallDrama = sim.Drama;
        }

        void ShakeCam(float amp, float seconds)
        {
            camShakeAmp = Mathf.Max(amp, Time.time < camShakeUntil ? camShakeAmp : 0);
            camShakeUntil = Mathf.Max(camShakeUntil, Time.time + seconds);
        }

        static IEnumerator Later(float seconds, System.Action a)
        {
            yield return new WaitForSeconds(seconds);
            a();
        }

        /// <summary>Guests next to the one leaving the crowd turn their heads to watch them go.</summary>
        void CrowdWatch(GuestView leaver)
        {
            foreach (var v in views.Values)
            {
                if (!v || v == leaver || !v.Waiting || v.IsHidden) continue;
                if ((v.transform.position - leaver.transform.position).sqrMagnitude > 2.6f * 2.6f || Random.value > 0.75f) continue;
                v.LookAt(leaver.transform, 1.8f);
            }
        }

        /// <summary>The whole hall reacts to a drama change: gasps and sweat on a rise, claps and hearts on a drop.</summary>
        void ReactHall(int delta, Vector3 where)
        {
            if (delta == 0) return;
            bool big = Mathf.Abs(delta) >= 20;
            if (delta >= 20) { Sfx.Play("drama_big"); Sfx.Duck(0.35f, 1.2f); }
            else if (delta > 0) Sfx.Play("drama_up");
            else { Sfx.Play("drama_down"); if (delta <= -10) Sfx.Play("cheer", 0.45f); }
            int emotes = 0;
            foreach (var v in GuestView.All.ToArray())
            {
                if (!v || v.IsHidden || v.Guest == null) continue;
                var g = v;
                if (g.Waiting)
                {
                    float chance = delta > 0 ? (big ? 0.65f : 0.35f) : 0.4f;
                    if (Random.value > chance) continue;
                    string emote = null;
                    if (emotes < (big ? 4 : 2) && Random.value < 0.35f) { emotes++; emote = delta > 0 ? (Random.value < 0.5f ? "exclaim" : "sweat") : (Random.value < 0.5f ? "heart" : "note"); }
                    StartCoroutine(Later(Random.Range(0.05f, 0.55f), () =>
                    {
                        if (!g) return;
                        g.LookAt(where, 1.8f);
                        g.Play(delta > 0 ? GuestView.Act.Gasp : GuestView.Act.Clap, delta > 0 ? 0.9f : 1.2f);
                        if (emote != null) g.Emote(emote, 1.3f);
                    }));
                }
                else if (big && g.CurrentAct == GuestView.Act.None && (g.transform.position - where).sqrMagnitude > 2.5f * 2.5f)
                    g.LookAt(where, 1.6f);   // the other tables turn to look
            }
            if (delta > 0)
            {
                if (bride) bride.Play(big ? GuestView.Act.Facepalm : GuestView.Act.Gasp, big ? 1.6f : 0.9f, null, "sweat");
                if (groom) StartCoroutine(Later(0.25f, () => { if (groom) groom.Play(GuestView.Act.Shrug, 1.1f, null, big ? "dots" : null); }));
                if (big) ShakeCam(0.16f, 0.3f);
            }
            else
            {
                if (bride) bride.Play(GuestView.Act.Cheer, 1.2f, null, "heart");
                if (groom) StartCoroutine(Later(0.2f, () => { if (groom) groom.Play(GuestView.Act.Clap, 1.2f); }));
            }
        }

        GuestView SeatedNeighbour(TableView tv, int seat, System.Func<Guest, bool> pred)
        {
            foreach (int k in new[] { Table.Left(seat), Table.Right(seat) })
            {
                var o = tv.Seated[k];
                if (o && o != tv.Seated[seat] && o.Guest != null && pred(o.Guest)) return o;
            }
            return null;
        }

        void Mates(TableView tv, GuestView a, GuestView b, GuestView.Act act, string emote = null, float delay = 0.3f)
        {
            foreach (var m in tv.Seated)
            {
                if (!m || m == a || m == b) continue;
                var mm = m;
                StartCoroutine(Later(delay + Random.Range(0f, 0.3f), () =>
                {
                    if (!mm) return;
                    mm.Play(act, -1, a ? a.EmoteAnchor : null);
                    if (emote != null && Random.value < 0.6f) mm.Emote(emote, 1.3f);
                }));
            }
        }

        GuestView Other(Effect e) => e.OtherId != 0 && views.TryGetValue(e.OtherId, out var v) && v ? v : null;

        /// <summary>The little scene each drama effect plays at the table after the guest sits down.</summary>
        IEnumerator Perform(SeatResult res, GuestView gv, TableView tv, int seat)
        {
            var g = gv.Guest;
            bool any = false;
            foreach (var e in res.Effects)
            {
                if (e.Key == EffectKeys.PerfectTable) continue;
                any = true;
                var o = Other(e);
                Transform oLook = o ? o.EmoteAnchor : null;
                switch (e.Key)
                {
                    case EffectKeys.CoupleTogether:
                        Sfx.Play("love");
                        gv.Play(GuestView.Act.Love, 2f, oLook, "heart");
                        if (o) { o.Play(GuestView.Act.Love, 2f, gv.EmoteAnchor); StartCoroutine(Later(0.35f, () => { if (o) o.Emote("heart", 1.6f); })); o.CurrentMood = GuestView.Mood.Love; }
                        gv.CurrentMood = GuestView.Mood.Love;
                        Mates(tv, gv, o, GuestView.Act.Clap, "heart");
                        yield return new WaitForSeconds(2f);
                        break;
                    case EffectKeys.CoupleSplit:
                        gv.Play(GuestView.Act.Sad, 2.2f, oLook, "brokenheart");
                        gv.CurrentMood = GuestView.Mood.Sad;
                        if (o) StartCoroutine(Later(0.4f, () => { if (o) { o.Play(GuestView.Act.Sad, 2f, gv.EmoteAnchor, "brokenheart"); o.CurrentMood = GuestView.Mood.Sad; } }));
                        yield return new WaitForSeconds(2.2f);
                        break;
                    case EffectKeys.ExFight:
                        if (o) o.Play(GuestView.Act.Glare, 0.7f, gv.EmoteAnchor, "anger");
                        gv.Play(GuestView.Act.Throw, 0.9f, oLook, "anger");
                        yield return new WaitForSeconds(0.45f);
                        Sfx.Play("lift", 0.8f, 0.8f);
                        if (o) yield return ThrowCake(gv.transform.position + Vector3.up * 1.0f, o);
                        Sfx.Play("splat");
                        StartCoroutine(Later(0.25f, () => Sfx.Play("fight")));
                        ShakeCam(0.35f, 0.45f);
                        if (o) { o.Splat(3.5f); o.Play(GuestView.Act.Furious, 1.8f, gv.EmoteAnchor, "anger"); o.CurrentMood = GuestView.Mood.Furious; }
                        gv.Play(GuestView.Act.Furious, 1.6f, oLook);
                        gv.CurrentMood = GuestView.Mood.Furious;
                        Mates(tv, gv, o, GuestView.Act.Gasp, "exclaim", 0.05f);
                        yield return new WaitForSeconds(1.8f);
                        break;
                    case EffectKeys.ExGlare:
                        gv.Play(GuestView.Act.Glare, 2f, oLook, "anger");
                        if (o) { StartCoroutine(Later(0.3f, () => { if (o) o.Play(GuestView.Act.Glare, 1.8f, gv.EmoteAnchor, "anger"); })); o.CurrentMood = GuestView.Mood.Grumpy; }
                        gv.CurrentMood = GuestView.Mood.Grumpy;
                        Mates(tv, gv, o, GuestView.Act.Shrug, "sweat", 0.6f);
                        yield return new WaitForSeconds(2f);
                        break;
                    case EffectKeys.RivalClash:
                        // both stand up and argue across the table
                        gv.Play(GuestView.Act.Angry, 2f, oLook, "rival");
                        if (o) { StartCoroutine(Later(0.25f, () => { if (o) o.Play(GuestView.Act.Furious, 1.8f, gv.EmoteAnchor, "anger"); })); o.CurrentMood = GuestView.Mood.Furious; }
                        gv.CurrentMood = GuestView.Mood.Grumpy;
                        ShakeCam(0.14f, 0.25f);
                        Mates(tv, gv, o, GuestView.Act.Cower, "sweat", 0.4f);
                        yield return new WaitForSeconds(2f);
                        break;
                    case EffectKeys.BffTogether:
                        gv.Play(GuestView.Act.Cheer, 1.2f, oLook, "bff");
                        if (o) { o.Play(GuestView.Act.Cheer, 1.2f, gv.EmoteAnchor); o.CurrentMood = GuestView.Mood.Happy; }
                        gv.CurrentMood = GuestView.Mood.Happy;
                        yield return new WaitForSeconds(1.2f);
                        gv.Play(GuestView.Act.Chat, 1.2f, oLook); if (o) o.Play(GuestView.Act.Chat, 1.2f, gv.EmoteAnchor, "note");
                        yield return new WaitForSeconds(1f);
                        break;
                    case EffectKeys.FamilyTogether:
                        {
                            var kid = g.Type == GuestType.Kid ? gv : o; var parent = kid == gv ? o : gv;
                            if (kid) { kid.Play(GuestView.Act.Cheer, 1.4f, parent ? parent.EmoteAnchor : null, "heart"); kid.CurrentMood = GuestView.Mood.Happy; }
                            if (parent) { parent.Play(GuestView.Act.Love, 1.6f, kid ? kid.EmoteAnchor : null); parent.CurrentMood = GuestView.Mood.Happy; }
                            Mates(tv, gv, o, GuestView.Act.Clap);
                            yield return new WaitForSeconds(1.6f);
                        }
                        break;
                    case EffectKeys.KidCries:
                        {
                            var kid = g.Type == GuestType.Kid ? gv : o; var parent = kid == gv ? o : gv;
                            if (kid) { kid.Play(GuestView.Act.Sad, 2.4f, parent ? parent.EmoteAnchor : null, "sweat"); kid.CurrentMood = GuestView.Mood.Sad; }
                            if (parent) StartCoroutine(Later(0.4f, () => { if (parent) { parent.Play(GuestView.Act.Gasp, 1f, kid ? kid.EmoteAnchor : null, "exclaim"); parent.CurrentMood = GuestView.Mood.Nervous; } }));
                            var t2 = kid ? FindTableOf(kid) : null;
                            if (t2) Mates(t2, kid, parent, GuestView.Act.Cower, "sweat", 0.5f);
                            yield return new WaitForSeconds(2.2f);
                        }
                        break;
                    case EffectKeys.CrushHappy:
                        {
                            bool iLove = g.LinkTo(e.OtherId) != null && g.LinkTo(e.OtherId).Kind == LinkKind.CrushOn;
                            var lover = iLove ? gv : o; var loved = iLove ? o : gv;
                            if (lover) { lover.Play(GuestView.Act.Love, 2f, loved ? loved.EmoteAnchor : null, "crush"); lover.CurrentMood = GuestView.Mood.Love; }
                            if (loved) StartCoroutine(Later(0.6f, () => { if (loved) loved.Play(GuestView.Act.Shrug, 1f, lover ? lover.EmoteAnchor : null, "question"); }));
                            yield return new WaitForSeconds(2f);
                        }
                        break;
                    case EffectKeys.VipSeatTaken:
                        gv.Play(GuestView.Act.Shrug, 1.4f, oLook, "sweat");
                        if (o) { o.Play(GuestView.Act.Furious, 2f, gv.EmoteAnchor, "anger"); o.CurrentMood = GuestView.Mood.Furious; }
                        yield return new WaitForSeconds(1.4f);
                        break;
                    case EffectKeys.GrandmaNotStar:
                        gv.Play(GuestView.Act.Furious, 2f, tv.Anchors[Table.StarSeat], "anger");
                        gv.CurrentMood = GuestView.Mood.Grumpy;
                        Mates(tv, gv, null, GuestView.Act.Cower, "sweat", 0.4f);
                        yield return new WaitForSeconds(2f);
                        break;
                    case EffectKeys.GrandmaScoldsUncle:
                        {
                            var grandma = g.Type == GuestType.Grandma ? gv : o;
                            var uncle = g.Type == GuestType.Grandma ? o : gv;
                            if (grandma) { grandma.Play(GuestView.Act.Scold, 2.2f, uncle ? uncle.EmoteAnchor : null, "anger"); grandma.CurrentMood = GuestView.Mood.Grumpy; }
                            if (uncle) { StartCoroutine(Later(0.3f, () => { if (uncle) uncle.Play(GuestView.Act.Cower, 2f, grandma ? grandma.EmoteAnchor : null, "sweat"); })); uncle.CurrentMood = GuestView.Mood.Nervous; }
                            yield return new WaitForSeconds(2.3f);
                        }
                        break;
                    case EffectKeys.GossipPair:
                        gv.Play(GuestView.Act.Whisper, 1.6f, oLook, "dots");
                        if (o) o.Play(GuestView.Act.Whisper, 1.6f, gv.EmoteAnchor);
                        yield return new WaitForSeconds(1.6f);
                        gv.Play(GuestView.Act.Gasp, 0.7f, null, "exclaim");
                        if (o) o.Play(GuestView.Act.Gasp, 0.7f);
                        yield return new WaitForSeconds(0.7f);
                        gv.Play(GuestView.Act.Cheer, 1f, null, "sparkle");
                        if (o) { o.Play(GuestView.Act.Cheer, 1f); o.CurrentMood = GuestView.Mood.Happy; }
                        gv.CurrentMood = GuestView.Mood.Happy;
                        Mates(tv, gv, o, GuestView.Act.Clap);
                        yield return new WaitForSeconds(1f);
                        break;
                }
            }
            if (!any)
            {
                // a quiet arrival: wave hello, the table turns to greet
                gv.Play(GuestView.Act.Wave, 1.1f);
                foreach (var m in tv.Seated) if (m && m != gv) m.LookAt(gv.EmoteAnchor, 1.4f);
                GuestView first = null;
                foreach (var m in tv.Seated) if (m && m != gv) { first = m; break; }
                if (first) StartCoroutine(Later(0.35f, () => { if (first) first.Play(GuestView.Act.Wave, 1f, gv.EmoteAnchor); }));
                yield return new WaitForSeconds(0.8f);
            }
        }

        TableView FindTableOf(GuestView v)
        {
            foreach (var t in tables.Values) if (t) foreach (var m in t.Seated) if (m == v) return t;
            return null;
        }

        /// <summary>A slice of wedding cake arcs from one ex to the other.</summary>
        IEnumerator ThrowCake(Vector3 from, GuestView target)
        {
            var cake = Palette.Pivot(world, "FlyingCake", from);
            Palette.Prim(PrimitiveType.Cylinder, cake, Vector3.zero, new Vector3(0.26f, 0.08f, 0.26f), Palette.Hex("#F4D6C4"));
            Palette.Prim(PrimitiveType.Sphere, cake, new Vector3(0, 0.09f, 0), new Vector3(0.26f, 0.12f, 0.26f), Palette.Hex("#FFF8F0"));
            Palette.Prim(PrimitiveType.Sphere, cake, new Vector3(0, 0.16f, 0), Vector3.one * 0.08f, Palette.Accent);
            const float T = 0.45f;
            for (float t = 0; t < T; t += Time.deltaTime)
            {
                if (!target) break;
                float k = t / T;
                Vector3 to = target.EmoteAnchor.position - Vector3.up * 0.55f;
                cake.position = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 1.2f;
                cake.rotation = Quaternion.Euler(k * 540, k * 200, 0);
                yield return null;
            }
            Destroy(cake.gameObject);
        }

        static bool IsRelated(Guest a, Guest b) =>
            a != null && b != null && (a.LinkTo(b.Id) != null ||
                (a.Type == GuestType.Grandma && b.Type == GuestType.Uncle) || (a.Type == GuestType.Uncle && b.Type == GuestType.Grandma) ||
                (a.Type == GuestType.Gossip && b.Type == GuestType.Gossip));

        static string EffectText(Effect e)
        {
            switch (e.Key)
            {
                case EffectKeys.GrandmaNotStar: return "Bà nội đòi ghế ngôi sao! +20%";
                case EffectKeys.GrandmaScoldsUncle: return "Bà nội mắng ông chú! +20%";
                case EffectKeys.ExFight: return "Người yêu cũ ném bánh! +30%";
                case EffectKeys.ExGlare: return "Người yêu cũ liếc nhau +10%";
                case EffectKeys.CoupleTogether: return "Cặp đôi ngồi cạnh nhau −10%";
                case EffectKeys.CoupleSplit: return "Cặp đôi bị tách ra +15%";
                case EffectKeys.GossipPair: return "Bà tám mở bí mật −15%";
                case EffectKeys.RivalClash: return "Kình địch chung bàn! +20%";
                case EffectKeys.BffTogether: return "Bạn thân ngồi chung −8%";
                case EffectKeys.FamilyTogether: return "Bé ngồi cạnh bố mẹ −5%";
                case EffectKeys.KidCries: return "Bé xa bố mẹ, khóc ré! +15%";
                case EffectKeys.CrushHappy: return "Được ngồi cạnh crush −12%";
                case EffectKeys.VipSeatTaken: return "Ngồi nhầm ghế VIP! +15%";
                case EffectKeys.VipImpatient: return "VIP chờ lâu, nổi giận! +15%";
                default: return e.ToString();
            }
        }

        // ---------- boosters ----------

        void UseBooster(string id)
        {
            if (ended || boosterCount[id] <= 0) return;
            switch (id)
            {
                case "bouncer":
                    Deselect(); hud.ShowInfo(null);
                    bouncerMode = !bouncerMode;
                    if (bouncerMode) Sfx.Play("bouncer");
                    hud.Toast(bouncerMode ? "Bảo vệ: chạm vào BẤT KỲ khách nào trong đám đông, kể cả khách bị kẹt." : "Đã hủy Bảo vệ.", 3.5f);
                    break;
                case "sofa":
                    boosterCount[id]--;
                    sim.AddBenchSlot();
                    var keep = new List<GuestView>();
                    for (int i = 0; i < sim.Bench.Length; i++) keep.Add(sim.Bench[i] != null && views.TryGetValue(sim.Bench[i].Id, out var bv) ? bv : null);
                    BuildBench();
                    for (int i = 0; i < keep.Count; i++) if (keep[i]) keep[i].transform.SetPositionAndRotation(benchSlots[i].Seat.position, benchSlots[i].Seat.rotation);
                    BuildNav();
                    Sfx.Play("bench");
                    hud.Toast("Thêm 1 ghế sofa ở Sảnh chờ.");
                    break;
                case "champagne":
                    boosterCount[id]--;
                    sim.ChangeDrama(-30);
                    Sfx.Play("champagne"); StartCoroutine(Later(0.4f, () => Sfx.Play("cheer")));
                    PushDrama();
                    hud.Popup(new Vector3(0, 1.5f, StageZ - 1), "Champagne cho cả hội trường! −30%", true);
                    foreach (var g in GuestView.All.ToArray())
                    {
                        if (!g || g.IsHidden) continue;
                        if (g.Guest != null && g.CurrentMood != GuestView.Mood.Love) g.CurrentMood = GuestView.Mood.Happy;
                        var gg = g;
                        StartCoroutine(Later(Random.Range(0f, 0.5f), () => { if (gg) gg.Play(Random.value < 0.5f ? GuestView.Act.Cheer : GuestView.Act.Sip, 1.3f); }));
                        if (Random.value < 0.18f) StartCoroutine(Later(Random.Range(0.1f, 0.8f), () => { if (gg) gg.Emote(Random.value < 0.5f ? "sparkle" : "note", 1.3f); }));
                    }
                    break;
            }
            RefreshBoosters();
        }

        void UseBouncerOn(Src src, GuestView gv)
        {
            if (sim.HandsPartner(gv.Guest) != null) { hud.Toast("Bảo vệ không tách được hai người đang nắm tay."); return; }
            if (sim.FreeBenchSlot() < 0) { hud.Toast("Sảnh chờ đã đầy."); return; }
            bouncerMode = false;
            boosterCount["bouncer"]--;
            int slot = sim.BouncerToBench(src.R, src.C);
            RefreshCrowd(); RefreshBoosters();
            HandleBlockerEvents(true);
            StartCoroutine(BouncerRoutine(gv, benchSlots[slot]));
        }

        IEnumerator BouncerRoutine(GuestView gv, BenchSlot slot)
        {
            running++;
            if (gv.IsHidden) yield return gv.Reveal();
            gv.SetFree(false);
            gv.Blocked = false;
            gv.SetPose(GuestView.Pose.Lift);
            Vector3 a = gv.transform.position, b = slot.Seat.position;
            for (float t = 0; t < 0.8f; t += Time.deltaTime)
            {
                float k = t / 0.8f;
                gv.transform.position = Vector3.Lerp(a, b, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 2.2f;
                yield return null;
            }
            yield return gv.SitAt(slot.Seat);
            Sfx.Play("bench", 0.8f);
            hud.Toast("Bảo vệ đã đưa " + gv.DisplayName + " ra Sảnh chờ.");
            running--;
            CheckEnd();
        }

        // ---------- end ----------

        /// <summary>Time from the start of win.ogg to its big final chord (see Tools/AudioGen WIN_HIT).</summary>
        const float WinHit = 1.63f;

        /// <summary>
        /// The wedding celebration plays first (fanfare, church bells, confetti, the hall cheering);
        /// the result popup only appears once the fanfare lands on its final chord.
        /// </summary>
        IEnumerator WinSequence(int stars)
        {
            Sfx.Duck(0.05f, 4.5f);
            Sfx.Play("win");
            hud.ShowInfo(null); hud.SetChips(new List<(Vector3, int)>());
            var stage = new Vector3(0, 1.3f, StageZ);
            StartCoroutine(ConfettiBurst(stage, 16, 2.4f));
            foreach (var g in GuestView.All.ToArray())
            {
                if (!g || g.IsHidden || g.Guest == null) continue;
                var gg = g;
                StartCoroutine(Later(Random.Range(0.05f, 0.6f), () => { if (gg) { gg.CurrentMood = GuestView.Mood.Happy; gg.Play(Random.value < 0.6f ? GuestView.Act.Clap : GuestView.Act.Cheer, 2.4f, bride ? bride.EmoteAnchor : null); } }));
            }
            if (bride) bride.LookAt(groom ? groom.EmoteAnchor : null, 3f);
            if (groom) groom.LookAt(bride ? bride.EmoteAnchor : null, 3f);

            yield return new WaitForSeconds(WinHit);

            // the big chord: newlyweds cheer, hearts, confetti rain over the whole hall
            ShakeCam(0.12f, 0.25f);
            if (bride) { bride.SetPose(GuestView.Pose.Cheer); bride.Emote("heart", 2.5f); }
            if (groom) { groom.SetPose(GuestView.Pose.Cheer); groom.Emote("sparkle", 2.5f); }
            StartCoroutine(ConfettiBurst(stage, 30, 3.6f));
            foreach (var t in tables.Values) if (t) StartCoroutine(ConfettiBurst(t.transform.position + Vector3.up * 1.2f, 10, 2.6f));
            int emotes = 0;
            foreach (var g in GuestView.All.ToArray())
                if (g && g.Guest != null && !g.IsHidden && emotes < 8 && Random.value < 0.35f) { emotes++; g.Emote(Random.value < 0.5f ? "heart" : "sparkle", 1.6f); }

            yield return new WaitForSeconds(0.45f);
            hud.ShowEnd("Đám cưới thành công!", $"Drama cuối level: {sim.Drama}%\n+{30 * stars} xu", stars,
                "Level tiếp theo", () => LoadLevel((levelIndex + 1) % levelCount), () => LoadLevel(levelIndex));
        }

        IEnumerator ConfettiBurst(Vector3 at, int count, float power)
        {
            var root = Palette.Pivot(world, "Confetti", at);
            var cols = new[] { Palette.Gold, Palette.Flower, Color.white, Palette.Leaf, Palette.Blush, Palette.Hex("#86AEDD") };
            var bits = new List<(Transform t, Vector3 v, Vector3 spin)>(count);
            for (int i = 0; i < count; i++)
            {
                var b = Palette.Prim(PrimitiveType.Cube, root, Vector3.zero, new Vector3(0.1f, 0.02f, 0.07f), cols[i % cols.Length], name: "Bit").transform;
                float a = Random.value * Mathf.PI * 2, r = Random.Range(0.4f, 1f) * power * 0.55f;
                bits.Add((b, new Vector3(Mathf.Cos(a) * r, power * Random.Range(0.9f, 1.4f), Mathf.Sin(a) * r * 0.6f),
                    new Vector3(Random.Range(200, 600), Random.Range(100, 400), 0)));
            }
            for (float k = 0; k < 2.2f; k += Time.deltaTime)
            {
                if (!root) yield break;
                for (int i = 0; i < bits.Count; i++)
                {
                    var (t, v, spin) = bits[i];
                    v += Vector3.down * 7f * Time.deltaTime;
                    v *= 1f - 1.6f * Time.deltaTime;                 // air drag: confetti flutters down
                    bits[i] = (t, v, spin);
                    t.localPosition += v * Time.deltaTime;
                    t.localRotation *= Quaternion.Euler(spin * Time.deltaTime);
                }
                yield return null;
            }
            if (root) Destroy(root.gameObject);
        }

        void CheckEnd()
        {
            if (ended || running > 0) return;
            PushDrama();
            switch (sim.Status)
            {
                case GameStatus.Won:
                    ended = true;
                    int stars = sim.Drama <= 15 ? 3 : sim.Drama <= 45 ? 2 : 1;
                    if (bride) { bride.SetPose(GuestView.Pose.Cheer); bride.Emote("heart", 2.5f); }
                    if (groom) { groom.SetPose(GuestView.Pose.Cheer); groom.Emote("sparkle", 2.5f); }
                    coins += 30 * stars; hud.SetCoins(coins);
                    StartCoroutine(WinSequence(stars));
                    break;
                case GameStatus.LostDrama:
                    ended = true;
                    if (bride) bride.Play(GuestView.Act.Sad, 4f, null, "brokenheart");
                    if (groom) groom.Play(GuestView.Act.Facepalm, 4f, null, "sweat");
                    foreach (var g in GuestView.All) if (g && g.Guest != null && !g.IsHidden && Random.value < 0.6f) g.Play(Random.value < 0.5f ? GuestView.Act.Gasp : GuestView.Act.Angry, 1.5f);
                    camShakeAmp = 0.35f; camShakeUntil = Time.time + 0.5f;
                    Sfx.Duck(0.1f, 3.5f); Sfx.Play("lose"); Sfx.Play("gasp");
                    hud.ShowEnd("Đám cưới đổ vỡ!", $"Drama đã đầy. Chỉ còn {sim.Total - sim.Seated} khách nữa thôi!", -1,
                        egoUsed ? null : "Champagne −40% · chơi tiếp",
                        egoUsed ? (System.Action)null : () => { egoUsed = true; sim.ChangeDrama(-40); ended = false; hud.HideEnd(); PushDrama(); RefreshBoosters(); },
                        () => LoadLevel(levelIndex));
                    break;
                case GameStatus.LostStuck:
                    ended = true;
                    Sfx.Duck(0.1f, 3.5f); Sfx.Play("lose");
                    hud.ShowEnd("Kẹt cứng lối vào!", "Không khách nào còn đường vào chỗ ngồi.", -1,
                        egoUsed ? null : "+1 ghế sofa · chơi tiếp",
                        egoUsed ? (System.Action)null : () => { egoUsed = true; boosterCount["sofa"]++; ended = false; hud.HideEnd(); UseBooster("sofa"); },
                        () => LoadLevel(levelIndex));
                    break;
            }
        }
    }
}
