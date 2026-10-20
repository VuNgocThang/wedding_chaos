using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WeddingChaos.View
{
    /// <summary>
    /// Keeps the game at a steady 60 fps on mid/low-end phones.
    /// - Locks the frame rate to 60, no vsync throttling, screen never dims mid-level.
    /// - Works on a runtime copy of the URP asset (so the Editor never writes these tweaks to disk).
    /// - Picks a starting tier from the device's specs, then watches real frame times and steps the
    ///   quality down (render scale, then shadow quality, then shadows off) when frames run long.
    ///   It never steps back up, so the picture does not "breathe" while playing.
    /// </summary>
    public class Perf : MonoBehaviour
    {
        public static int Tier { get; private set; }

        // tier: render scale, shadow map size (0 = shadows off)
        static readonly (float scale, int shadow)[] Tiers =
        {
            (1.00f, 2048),
            (0.85f, 1024),
            (0.75f, 1024),
            (0.66f, 512),
            (0.58f, 0),
        };

        const float Budget = 1f / 55f;     // a frame longer than this counts as slow (60 fps target, small slack)
        const float Window = 2f;           // seconds per measurement window
        const int SlowWindowsToDrop = 2;   // consecutive slow windows before stepping down

        static Perf inst;
        UniversalRenderPipelineAsset urp;
        float windowTime, ignoreUntil;
        int frames, slowFrames, slowWindows;

        public static void Ensure()
        {
            if (inst) return;
            var go = new GameObject("Perf");
            DontDestroyOnLoad(go);
            inst = go.AddComponent<Perf>();
            inst.Init();
        }

        /// <summary>Call around known one-off spikes (level load) so they are not read as a slow device.</summary>
        public static void IgnoreFor(float seconds)
        {
            if (inst) inst.ignoreUntil = Mathf.Max(inst.ignoreUntil, Time.unscaledTime + seconds);
        }

        void Init()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            QualitySettings.shadowCascades = 1;

            var src = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (src != null)
            {
                urp = Instantiate(src);
                urp.name = src.name + " (runtime)";
                urp.supportsHDR = false;          // no post FX in this game: an LDR target saves bandwidth
                urp.msaaSampleCount = 1;
                urp.shadowCascadeCount = 1;
                urp.shadowDistance = 60f;         // ortho camera sits 40 units back; this just covers the hall
                GraphicsSettings.defaultRenderPipeline = urp;
                QualitySettings.renderPipeline = urp;
            }

            Tier = Application.isMobilePlatform ? StartTier() : 0;
            Apply();
            ignoreUntil = Time.unscaledTime + 3f;
        }

        /// <summary>Rough device class from specs: weaker phones start one or two steps lower.</summary>
        static int StartTier()
        {
            int ram = SystemInfo.systemMemorySize, cores = SystemInfo.processorCount;
            int vram = SystemInfo.graphicsMemorySize;
            if (ram < 3000 || cores <= 4 || vram < 1024) return 3;
            if (ram < 5000 || cores <= 6) return 2;
            return 1;
        }

        void Apply()
        {
            var t = Tiers[Mathf.Clamp(Tier, 0, Tiers.Length - 1)];
            if (urp != null)
            {
                urp.renderScale = t.scale;
                if (t.shadow > 0) urp.mainLightShadowmapResolution = t.shadow;
            }
            var sun = FindSun();
            if (sun) sun.shadows = t.shadow == 0 ? LightShadows.None : Tier <= 1 ? LightShadows.Soft : LightShadows.Hard;
        }

        static Light FindSun()
        {
            var s = RenderSettings.sun;
            if (s) return s;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Directional) return l;
            return null;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (Time.unscaledTime < ignoreUntil || dt > 0.25f) { ResetWindow(); return; }
            frames++;
            if (dt > Budget) slowFrames++;
            windowTime += dt;
            if (windowTime < Window) return;

            // slow window: more than a quarter of frames missed 60 fps
            bool slow = slowFrames > frames / 4;
            slowWindows = slow ? slowWindows + 1 : 0;
            ResetWindow();
            if (slowWindows >= SlowWindowsToDrop && Tier < Tiers.Length - 1)
            {
                Tier++;
                slowWindows = 0;
                Apply();
                ignoreUntil = Time.unscaledTime + 1f;
                Debug.Log($"[WeddingChaos] Perf tier -> {Tier} (scale {Tiers[Tier].scale}, shadows {Tiers[Tier].shadow})");
            }
        }

        void ResetWindow() { windowTime = 0; frames = 0; slowFrames = 0; }

        // development builds: log the real frame cost every few seconds so it can be read from the player log
        float statT; int statFrames; double statCpu, statMax;
        readonly FrameTiming[] timing = new FrameTiming[1];

        void LateUpdate()
        {
            if (!Debug.isDebugBuild) return;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
            {
                statCpu += timing[0].cpuMainThreadFrameTime;
                statMax = System.Math.Max(statMax, timing[0].cpuMainThreadFrameTime);
            }
            statFrames++; statT += Time.unscaledDeltaTime;
            if (statT < 5f) return;
            Debug.Log($"[WeddingChaos] fps {statFrames / statT:F1}  cpu main {statCpu / statFrames:F2} ms (max {statMax:F2})  tier {Tier}");
            statT = 0; statFrames = 0; statCpu = 0; statMax = 0;
        }

        /// <summary>Re-apply after a scene rebuild (a new sun light may have been created).</summary>
        public static void Reapply()
        {
            if (inst) inst.Apply();
        }
    }
}
