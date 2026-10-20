using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WeddingChaos.View
{
    /// <summary>
    /// Screen-space HUD in the casual-puzzle style (Royal Match / Seat Away family): level pill + coins,
    /// a drama meter card with the bride's mood, round boosters with count badges, a guest info card,
    /// floating badges over special guests, seat chips, popups and a star end panel.
    /// </summary>
    public class Hud : MonoBehaviour
    {
        public static Font Display, Body, BodyHeavy;

        RectTransform safe, badgeLayer, chipLayer, popupLayer, emoteLayer, dramaCard;
        float shownDrama, targetDrama, dramaPunch, dramaPunchSign;
        Camera cam;
        Text levelText, levelName, coinText, dramaPct, guestsText, toastText;
        Image dramaFill, moodFace, levelPill;
        RectTransform infoCard, toast, menu;
        Image infoBg;
        Text infoName, infoRole;
        const int MaxWishes = 4;
        const float WishTop = 92, WishStep = 62;
        readonly List<(RectTransform row, Image box, Text text, Image chip, Text chipText, Image divider)> wishRows = new List<(RectTransform, Image, Text, Image, Text, Image)>();
        GameObject endPanel;
        Text endTitle, endBody; Image[] endStars = new Image[3];
        Button endPrimary; Text endPrimaryLabel; Action endPrimaryAction, endSecondaryAction;
        readonly Dictionary<string, (Button btn, Text count)> boosters = new Dictionary<string, (Button, Text)>();
        float toastUntil;
        Rect lastSafe;

        public Action OnRestart, OnNext, OnPrev;
        /// <summary>Settings → "Đi tới level": the 1-based level number the player typed.</summary>
        public Action<int> OnGoTo;
        public int LevelCount = 50;
        InputField goInput;
        int currentLevel = 1;
        public Action<string> OnBooster;

        class Badge { public Transform anchor; public RectTransform rect; public float dx, dy; public CanvasGroup group; public GameObject full, dot; public int mode = 2; }
        readonly List<Badge> badges = new List<Badge>();
        readonly List<(RectTransform rect, Vector3 world)> chips = new List<(RectTransform, Vector3)>();
        class EmoteUi { public Transform anchor; public RectTransform rect; public Image bg, icon; public float t, dur; }
        readonly List<EmoteUi> emotes = new List<EmoteUi>();

        // ---------- creation ----------

        public static Hud Create(Camera cam)
        {
            LoadFonts();
            var go = new GameObject("HUD");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920); scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            var hud = go.AddComponent<Hud>();
            hud.cam = cam;
            hud.Build();
            EnsureEventSystem();
            return hud;
        }

        static void LoadFonts()
        {
            if (Display) return;
            var fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Display = Resources.Load<Font>("Fonts/Baloo2-ExtraBold") ?? fallback;
            Body = Resources.Load<Font>("Fonts/Nunito-Bold") ?? fallback;
            BodyHeavy = Resources.Load<Font>("Fonts/Nunito-Black") ?? fallback;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        // ---------- ui helpers ----------

        static RectTransform R(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false);
            r.anchorMin = aMin; r.anchorMax = aMax; r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
            return r;
        }

        static Image Img(RectTransform r, string sprite, Color c, bool raycast = false)
        {
            var img = r.gameObject.AddComponent<Image>();
            img.sprite = IconFactory.Get(sprite);
            img.type = sprite.StartsWith("ui_") ? Image.Type.Sliced : Image.Type.Simple;
            img.preserveAspect = !sprite.StartsWith("ui_");
            img.color = c; img.raycastTarget = raycast;
            return img;
        }

        static Text Txt(RectTransform r, string s, Font f, int size, Color c, TextAnchor a)
        {
            var t = r.gameObject.AddComponent<Text>();
            t.font = f; t.fontSize = size; t.color = c; t.alignment = a; t.text = s; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.lineSpacing = 0.95f;
            return t;
        }

        static RectTransform Fill(Transform parent, string name, float l = 0, float b = 0, float rgt = 0, float top = 0)
        {
            var r = R(name, parent, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rgt, -top);
            return r;
        }

        static void Shadow(Graphic g, float y = -6, float a = 0.25f)
        {
            var s = g.gameObject.AddComponent<Shadow>();
            s.effectColor = new Color(0.23f, 0.15f, 0.19f, a); s.effectDistance = new Vector2(0, y);
        }

        static RectTransform Card(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size, Color c)
        {
            var shadow = R(name + "Shadow", parent, aMin, aMax, pivot, pos + new Vector2(0, -10), size + new Vector2(36, 36));
            Img(shadow, "ui_shadow", Color.white);
            var r = R(name, parent, aMin, aMax, pivot, pos, size);
            Img(r, "ui_round", c, true);
            shadow.SetSiblingIndex(r.GetSiblingIndex() - 1 < 0 ? 0 : r.GetSiblingIndex() - 1);
            return r;
        }

        Button PillButton(Transform parent, string label, Color bg, Color fg, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size, int fontSize, Action onClick)
        {
            var r = R("Btn " + label, parent, aMin, aMax, pivot, pos, size);
            var img = Img(r, "ui_pill", bg, true);
            Shadow(img, -8, 0.35f);
            var b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(() => { Sfx.Play("ui_tap"); onClick(); });
            var t = Txt(Fill(r, "Label", 10, 4, 10, 0), label, Display, fontSize, fg, TextAnchor.MiddleCenter);
            return b;
        }

        // ---------- build ----------

        void Build()
        {
            safe = Fill(transform, "Safe");
            ApplySafeArea();

            badgeLayer = Fill(safe, "Badges");
            chipLayer = Fill(safe, "Chips");
            emoteLayer = Fill(safe, "Emotes");

            // --- top bar ---
            var gear = R("Settings", safe, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0.5f, 0.5f), new Vector2(100, -90), new Vector2(110, 110));
            var gImg = Img(gear, "circle", Color.white, true); Shadow(gImg);
            Img(Fill(gear, "Icon", 26, 26, 26, 26), "gear", Color.white);
            var gb = gear.gameObject.AddComponent<Button>(); gb.targetGraphic = gImg; gb.onClick.AddListener(() =>
            {
                Sfx.Play("ui_tap");
                bool open = !menu.gameObject.activeSelf;
                menu.gameObject.SetActive(open);
                if (open) menu.SetAsLastSibling();          // above the guest info card and toasts
                if (open && goInput) goInput.text = currentLevel.ToString();
            });

            var pill = R("LevelPill", safe, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, -80), new Vector2(380, 104));
            var pImg = Img(pill, "ui_pill", Palette.Accent); Shadow(pImg, -8, 0.35f);
            levelPill = pImg;
            levelText = Txt(Fill(pill, "Text", 0, 6, 0, 0), "LEVEL 1", Display, 54, Color.white, TextAnchor.MiddleCenter);
            levelName = Txt(R("LevelName", safe, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, -158), new Vector2(700, 50)), "", BodyHeavy, 32, Palette.Hex("#8C6474"), TextAnchor.MiddleCenter);

            var coins = R("Coins", safe, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0.5f, 0.5f), new Vector2(-170, -90), new Vector2(240, 84));
            var cImg = Img(coins, "ui_pill", Color.white); Shadow(cImg);
            Img(R("Coin", coins, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(40, 0), new Vector2(72, 72)), "coin", Color.white);
            coinText = Txt(Fill(coins, "Text", 80, 4, 20, 0), "1,250", Display, 44, Palette.Ink, TextAnchor.MiddleCenter);

            // --- drama card ---
            var drama = Card("Drama", safe, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -205), new Vector2(-80, 150), Color.white);
            dramaCard = drama;
            moodFace = Img(R("Mood", drama, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(85, 0), new Vector2(112, 112)), "face_happy", Color.white);
            Txt(R("Label", drama, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(165, -14), new Vector2(300, 50)), "DRAMA", Display, 38, Palette.Ink, TextAnchor.MiddleLeft);
            guestsText = Txt(R("Guests", drama, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-190, -14), new Vector2(360, 50)), "Khách 0/0", BodyHeavy, 30, Palette.Hex("#8C6474"), TextAnchor.MiddleRight);
            var track = R("Track", drama, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 0), new Vector2(165, 28), new Vector2(-370, 46));
            Img(track, "ui_pill", Palette.Hex("#F6DCD3"));
            var fillHost = Fill(track, "FillHost", 5, 5, 5, 5);
            var fillR = R("Fill", fillHost, Vector2.zero, new Vector2(0, 1), new Vector2(0, 0.5f), Vector2.zero, Vector2.zero);
            fillR.offsetMin = Vector2.zero; fillR.offsetMax = Vector2.zero;
            dramaFill = Img(fillR, "ui_pill", Palette.Accent);
            var tick = R("Danger", track, new Vector2(0.7f, 0), new Vector2(0.7f, 1), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6, 10));
            Img(tick, "ui_pill", new Color(1, 1, 1, 0.9f));
            dramaPct = Txt(R("Pct", drama, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-26, -14), new Vector2(170, 90)), "0%", Display, 60, Palette.Accent, TextAnchor.MiddleRight);

            // --- settings menu ---
            menu = Card("Menu", safe, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(50, -150), new Vector2(420, 624), Color.white);
            PillButton(menu, "Chơi lại", Palette.Accent, Color.white, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -30), new Vector2(360, 82), 40, () => { menu.gameObject.SetActive(false); OnRestart?.Invoke(); });
            PillButton(menu, "‹ Level trước", Palette.Hex("#F3E5DF"), Palette.Ink, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -128), new Vector2(360, 82), 38, () => { menu.gameObject.SetActive(false); OnPrev?.Invoke(); });
            PillButton(menu, "Level sau ›", Palette.Hex("#F3E5DF"), Palette.Ink, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -226), new Vector2(360, 82), 38, () => { menu.gameObject.SetActive(false); OnNext?.Invoke(); });
            // go to any level: a number box + GO
            var goBox = R("GoTo", menu, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(-90, -324), new Vector2(180, 82));
            var goBg = Img(goBox, "ui_pill", Palette.Hex("#F3E5DF"), true);
            var goText = Txt(Fill(goBox, "Text", 20, 4, 20, 0), "", Display, 40, Palette.Ink, TextAnchor.MiddleCenter);
            goText.supportRichText = false;
            var goHint = Txt(Fill(goBox, "Hint", 20, 4, 20, 0), "Level…", BodyHeavy, 32, new Color(0.23f, 0.15f, 0.19f, 0.45f), TextAnchor.MiddleCenter);
            goInput = goBox.gameObject.AddComponent<InputField>();
            goInput.targetGraphic = goBg; goInput.textComponent = goText; goInput.placeholder = goHint;
            goInput.contentType = InputField.ContentType.IntegerNumber;
            goInput.characterLimit = 3;
            void Go()
            {
                if (!int.TryParse(goInput.text, out int lv)) { Toast($"Nhập số level từ 1 đến {LevelCount}."); return; }
                lv = Mathf.Clamp(lv, 1, LevelCount);
                menu.gameObject.SetActive(false);
                OnGoTo?.Invoke(lv);
            }
            goInput.onEndEdit.AddListener(_ =>
            {
#if ENABLE_INPUT_SYSTEM
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) Go();
#endif
            });
            PillButton(menu, "GO", Palette.Accent, Color.white, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(100, -324), new Vector2(160, 82), 40, Go);

            Button soundBtn = null, musicBtn = null;
            void LabelToggles()
            {
                soundBtn.GetComponentInChildren<Text>().text = Sfx.SoundOn ? "Âm thanh: Bật" : "Âm thanh: Tắt";
                musicBtn.GetComponentInChildren<Text>().text = Sfx.MusicOn ? "Nhạc: Bật" : "Nhạc: Tắt";
            }
            soundBtn = PillButton(menu, "Âm thanh", Palette.Hex("#F3E5DF"), Palette.Ink, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -422), new Vector2(360, 82), 36, () => { Sfx.SoundOn = !Sfx.SoundOn; LabelToggles(); });
            musicBtn = PillButton(menu, "Nhạc", Palette.Hex("#F3E5DF"), Palette.Ink, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -520), new Vector2(360, 82), 36, () => { Sfx.MusicOn = !Sfx.MusicOn; LabelToggles(); });
            LabelToggles();
            menu.parent.Find("MenuShadow").gameObject.SetActive(false);
            menu.gameObject.SetActive(false);

            // --- boosters ---
            Booster("bouncer", "sunglasses", "Bảo vệ", -290);
            Booster("sofa", "sofa", "Ghế phụ", 0);
            Booster("champagne", "flute", "Champagne", 290);

            // --- info card: a sticky note in the guest's invitation colour, one short wish per line ---
            infoCard = Card("Info", safe, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -195), new Vector2(-80, 220), Color.white);
            infoBg = infoCard.GetComponent<Image>();
            infoName = Txt(R("Name", infoCard, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1), new Vector2(34, -12), new Vector2(-300, 76)), "", Display, 56, Palette.Ink, TextAnchor.MiddleLeft);
            infoRole = Txt(R("Role", infoCard, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-32, -18), new Vector2(300, 50)), "", BodyHeavy, 30, new Color(0.23f, 0.15f, 0.19f, 0.7f), TextAnchor.MiddleRight);
            for (int i = 0; i < MaxWishes; i++)
            {
                var row = R("Wish" + i, infoCard, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, -WishTop - i * WishStep), new Vector2(-60, WishStep));
                var div = Img(R("Divider", row, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0, 3)), "ui_pill", new Color(0.23f, 0.15f, 0.19f, 0.14f));
                var box = Img(R("Box", row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0), new Vector2(40, 40)), "box", Palette.Ink);
                var text = Txt(R("Text", row, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 0.5f), new Vector2(56, 0), new Vector2(-190, 0)), "", BodyHeavy, 36, Palette.Ink, TextAnchor.MiddleLeft);
                text.resizeTextForBestFit = true; text.resizeTextMinSize = 24; text.resizeTextMaxSize = 36;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                var chipR = R("Chip", row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f), Vector2.zero, new Vector2(112, 48));
                var chip = Img(chipR, "ui_pill", Palette.Good);
                var chipText = Txt(Fill(chipR, "Text", 0, 3, 0, 0), "", Display, 34, Color.white, TextAnchor.MiddleCenter);
                wishRows.Add((row, box, text, chip, chipText, div));
            }
            infoCard.parent.Find("InfoShadow").SetSiblingIndex(infoCard.GetSiblingIndex() - 1);
            ShowInfo(null);

            // --- toast ---
            toast = R("Toast", safe, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 318), new Vector2(940, 80));
            var tImg = Img(toast, "ui_pill", new Color(0.23f, 0.15f, 0.19f, 0.9f));
            toastText = Txt(Fill(toast, "Text", 30, 4, 30, 0), "", BodyHeavy, 30, Color.white, TextAnchor.MiddleCenter);
            toast.gameObject.SetActive(false);

            popupLayer = Fill(safe, "Popups");

            // --- end panel ---
            endPanel = Fill(transform, "End").gameObject;
            var dim = endPanel.AddComponent<Image>(); dim.color = new Color(0.23f, 0.15f, 0.19f, 0.6f);
            var end = Card("EndCard", endPanel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(900, 820), Color.white);
            var ribbon = R("Ribbon", end, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(760, 130));
            var rImg = Img(ribbon, "ui_pill", Palette.Accent); Shadow(rImg, -8, 0.35f);
            endTitle = Txt(Fill(ribbon, "Text", 20, 8, 20, 0), "", Display, 60, Color.white, TextAnchor.MiddleCenter);
            for (int i = 0; i < 3; i++)
                endStars[i] = Img(R("Star" + i, end, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2((i - 1) * 190, i == 1 ? -200 : -230), i == 1 ? new Vector2(190, 190) : new Vector2(150, 150)), "star", Color.white);
            endBody = Txt(R("Body", end, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(-100, 170)), "", Body, 38, Palette.Hex("#5E4652"), TextAnchor.MiddleCenter);
            endPrimary = PillButton(end, "", Palette.Accent, Color.white, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 175), new Vector2(620, 110), 46, () => endPrimaryAction?.Invoke());
            endPrimaryLabel = endPrimary.GetComponentInChildren<Text>();
            PillButton(end, "Chơi lại", Palette.Hex("#F3E5DF"), Palette.Ink, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 50), new Vector2(620, 100), 42, () => endSecondaryAction?.Invoke());
            endPanel.SetActive(false);
        }

        void Booster(string id, string icon, string label, float x)
        {
            var r = R("Booster " + id, safe, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(x, 100), new Vector2(180, 180));
            var img = Img(r, "circle", Color.white, true); Shadow(img, -10, 0.3f);
            Img(Fill(r, "Icon", 36, 46, 36, 30), icon, Color.white);
            Txt(R("Label", r, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, 2), new Vector2(260, 50)), label, BodyHeavy, 30, Palette.Ink, TextAnchor.UpperCenter);
            var badge = R("Count", r, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0.5f, 0.5f), new Vector2(-24, -24), new Vector2(66, 66));
            Img(badge, "circle", Palette.Accent);
            var count = Txt(Fill(badge, "Text", 0, 3, 0, 0), "1", Display, 38, Color.white, TextAnchor.MiddleCenter);
            var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = img;
            b.onClick.AddListener(() => { Sfx.Play("ui_tap"); OnBooster?.Invoke(id); });
            boosters[id] = (b, count);
        }

        void ApplySafeArea()
        {
            var sa = Screen.safeArea; lastSafe = sa;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            safe.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            safe.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
        }

        // ---------- public API ----------

        public void SetLevel(int number, string name, string tier = "")
        {
            levelText.text = "LEVEL " + number;
            currentLevel = number;
            string tag = tier == "super" ? "SIÊU KHÓ" : tier == "hard" ? "KHÓ" : tier == "medium" ? "KHÓ VỪA" : null;
            levelName.text = tag == null ? name : $"<color=#{ColorUtility.ToHtmlStringRGB(TierColor(tier))}>{tag}</color> · {name}";
            levelPill.color = TierColor(tier);
        }

        public static Color TierColor(string tier) =>
            tier == "super" ? Palette.Hex("#8E5BD6") : tier == "hard" ? Palette.Hex("#D63A3A") : tier == "medium" ? Palette.Hex("#F08A4B") : Palette.Accent;
        public void SetSeated(int seated, int total) => guestsText.text = $"Khách {seated}/{total}";
        public void SetCoins(int coins) => coinText.text = coins.ToString("N0");

        /// <summary>Sets the drama target. The bar counts towards it, and the card punches red on a rise or green on a drop.</summary>
        public void SetDrama(int drama, bool instant = false)
        {
            if (instant) { shownDrama = targetDrama = drama; dramaPunch = 0; RenderDrama(); return; }
            if (drama != (int)targetDrama) { dramaPunch = 1; dramaPunchSign = drama > targetDrama ? 1 : -1; }
            targetDrama = drama;
        }

        void RenderDrama()
        {
            int d = Mathf.RoundToInt(shownDrama);
            dramaPct.text = d + "%";
            dramaFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(shownDrama / 100f), 1);
            dramaFill.enabled = shownDrama > 0.5f;
            Color c = d >= 70 ? Palette.Bad : d >= 40 ? Palette.Hex("#F08A4B") : Palette.Accent;
            if (dramaPunch > 0) c = Color.Lerp(c, dramaPunchSign > 0 ? Palette.Bad : Palette.Good, dramaPunch);
            dramaFill.color = c; dramaPct.color = c;
            moodFace.sprite = IconFactory.Get(d >= 70 ? "face_panic" : d >= 40 ? "face_worried" : "face_happy");
            float wob = dramaPunch * Mathf.Sin(Time.time * 40);
            dramaCard.localScale = Vector3.one * (1 + dramaPunch * 0.05f);
            dramaCard.localRotation = Quaternion.Euler(0, 0, dramaPunchSign > 0 ? wob * 1.5f : 0);
            moodFace.rectTransform.localRotation = Quaternion.Euler(0, 0, wob * 12);
            moodFace.rectTransform.localScale = Vector3.one * (1 + dramaPunch * 0.25f);
            dramaPct.rectTransform.localScale = Vector3.one * (1 + dramaPunch * 0.3f);
        }

        /// <summary>Speech-bubble emote (heart, anger, sweat, !, ?, zzz...) popping above a guest's head.</summary>
        public void Emote(Transform anchor, string icon, float seconds)
        {
            if (!anchor) return;
            foreach (var e in emotes) if (e.anchor == anchor && e.rect) { e.icon.sprite = IconFactory.Get(icon); e.t = 0; e.dur = seconds; return; }
            Sfx.Play("emote", 1f, 1f, 0.12f);
            var r = R("Emote", emoteLayer, Vector2.zero, Vector2.zero, new Vector2(0.2f, 0.1f), Vector2.zero, new Vector2(110, 110));
            var bg = Img(r, "emote_bg", Color.white); Shadow(bg, -4, 0.28f);
            var ic = Img(Fill(r, "Icon", 18, 16, 10, 18), icon, Color.white);
            emotes.Add(new EmoteUi { anchor = anchor, rect = r, bg = bg, icon = ic, t = 0, dur = seconds });
        }

        void TickEmotes()
        {
            for (int i = emotes.Count - 1; i >= 0; i--)
            {
                var e = emotes[i];
                e.t += Time.deltaTime;
                if (!e.rect || !e.anchor || e.t >= e.dur) { if (e.rect) Destroy(e.rect.gameObject); emotes.RemoveAt(i); continue; }
                bool vis = e.anchor.gameObject.activeInHierarchy;
                e.rect.gameObject.SetActive(vis);
                if (!vis) continue;
                float s = e.t < 0.14f ? Mathf.Lerp(0.2f, 1.18f, e.t / 0.14f) : e.t < 0.24f ? Mathf.Lerp(1.18f, 1f, (e.t - 0.14f) / 0.1f) : 1 + Mathf.Sin(e.t * 6) * 0.03f;
                float a = Mathf.Clamp01((e.dur - e.t) / 0.25f);
                e.rect.localScale = Vector3.one * s * (0.6f + 0.4f * a);
                e.rect.anchoredPosition = WorldToSafe(e.anchor.position) + new Vector2(34, 26 + e.t * 10);
                e.bg.color = new Color(1, 1, 1, a); e.icon.color = new Color(1, 1, 1, a);
            }
        }

        public void ClearEmotes() { foreach (var e in emotes) if (e.rect) Destroy(e.rect.gameObject); emotes.Clear(); }

        public void SetBooster(string id, int count, bool interactable)
        {
            if (!boosters.TryGetValue(id, out var b)) return;
            b.count.text = count.ToString();
            b.btn.interactable = interactable && count > 0;
            b.btn.targetGraphic.color = b.btn.interactable ? Color.white : new Color(1, 1, 1, 0.55f);
        }

        public void SetBoosterHighlight(string id, bool on)
        {
            if (boosters.TryGetValue(id, out var b)) b.btn.targetGraphic.color = on ? Palette.Hex("#FFE07A") : Color.white;
        }

        public void Toast(string text, float seconds = 2.6f)
        {
            toastText.text = text;
            toast.gameObject.SetActive(!string.IsNullOrEmpty(text));
            toastUntil = Time.time + seconds;
        }

        /// <summary>One line of the info note. Delta = the drama it is about (negative = reward, positive = penalty, 0 = none).
        /// State: 0 not decided yet, 1 fulfilled (ticked), -1 broken (crossed).</summary>
        public struct Wish { public string Text; public int Delta; public int State; }

        public struct InfoData
        {
            public string Name, Role;
            public Color Invite;
            public List<Wish> Wishes;
        }

        public void ShowInfo(InfoData? data)
        {
            bool on = data.HasValue;
            infoCard.gameObject.SetActive(on);
            infoCard.parent.Find("InfoShadow").gameObject.SetActive(on);
            if (!on) return;
            var d = data.Value;
            infoBg.color = Color.Lerp(d.Invite, Color.white, 0.38f);
            infoName.text = d.Name;
            infoRole.text = d.Role;
            int n = d.Wishes == null ? 0 : Mathf.Min(MaxWishes, d.Wishes.Count);
            for (int i = 0; i < wishRows.Count; i++)
            {
                var w = wishRows[i];
                w.row.gameObject.SetActive(i < n);
                if (i >= n) continue;
                var wish = d.Wishes[i];
                w.divider.gameObject.SetActive(i > 0);
                w.text.text = wish.Text;
                w.box.sprite = IconFactory.Get(wish.State > 0 ? "box_check" : wish.State < 0 ? "box_cross" : "box");
                w.box.color = Color.white;
                w.chip.gameObject.SetActive(wish.Delta != 0);
                w.chip.color = wish.Delta < 0 ? Palette.Good : Palette.Bad;
                w.chipText.text = (wish.Delta > 0 ? "+" : "−") + Mathf.Abs(wish.Delta);
            }
            float total = WishTop + n * WishStep + 18;
            infoCard.sizeDelta = new Vector2(infoCard.sizeDelta.x, total);
            var sh = infoCard.parent.Find("InfoShadow") as RectTransform;
            if (sh) sh.sizeDelta = new Vector2(sh.sizeDelta.x, total + 16);
        }

        // ---------- world-anchored UI ----------

        /// <summary>Round badge over a guest. slot/count spread several badges side by side (one per relationship).</summary>
        public void AddBadge(Transform anchor, string icon, string letter, int slot = 0, int count = 1)
        {
            float size = count > 2 ? 50 : 58;
            var r = R("Badge", badgeLayer, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            var bg = Img(r, "circle", Color.white); Shadow(bg, -4, 0.3f);
            Img(Fill(r, "Icon", 8, 8, 8, 8), icon, Color.white);
            if (!string.IsNullOrEmpty(letter))
            {
                var tag = R("Letter", r, new Vector2(1, 0), new Vector2(1, 0), new Vector2(0.5f, 0.5f), new Vector2(-2, 4), new Vector2(32, 32));
                Img(tag, "circle", Palette.Ink);
                Txt(Fill(tag, "Text", 0, 2, 0, 0), letter, Display, 24, Color.white, TextAnchor.MiddleCenter);
            }
            var grp = r.gameObject.AddComponent<CanvasGroup>(); grp.blocksRaycasts = false;
            badges.Add(new Badge { anchor = anchor, rect = r, dx = (slot - (count - 1) / 2f) * (size - 4), group = grp });
        }

        /// <summary>Name plate over a special guest: a full plate (icon + name, tinted with the invitation colour)
        /// and a compact dot (icon only). <see cref="SetPlateMode"/> picks which one shows.</summary>
        public void AddNamePlate(Transform anchor, string name, IList<string> icons, Color tint, float lift = 0)
        {
            var r = R("Plate", badgeLayer, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0f), Vector2.zero, Vector2.zero);
            var full = R("Full", r, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(100, 38));
            var bg = Img(full, "ui_pill", Color.Lerp(tint, Color.white, 0.6f)); Shadow(bg, -3, 0.22f);
            float x = 8;
            string icon = icons.Count > 0 ? icons[0] : null;
            if (icon != null)
            {
                Img(R("Icon", full, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x, 0), new Vector2(28, 28)), icon, Color.white);
                x += 27;
            }
            var t = Txt(R("Name", full, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x + 3, 1), new Vector2(220, 36)), name, BodyHeavy, 24, Palette.Ink, TextAnchor.MiddleLeft);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            full.sizeDelta = new Vector2(x + 3 + t.preferredWidth + 12, 38);
            var dot = R("Dot", r, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0f), new Vector2(0, -6), new Vector2(34, 34));
            Img(dot, "circle", Color.Lerp(tint, Color.white, 0.6f));
            if (icon != null) Img(Fill(dot, "Icon", 6, 6, 6, 6), icon, Color.white);
            var grp = r.gameObject.AddComponent<CanvasGroup>(); grp.blocksRaycasts = false;
            badges.Add(new Badge { anchor = anchor, rect = r, dx = 0, dy = lift, group = grp, full = full.gameObject, dot = dot.gameObject });
            SetPlateMode(anchor, 2);
        }

        /// <summary>Changes the words on a plate (e.g. a gift counter) and refits it.</summary>
        public void SetPlateText(Transform anchor, string text)
        {
            foreach (var b in badges)
            {
                if (b.anchor != anchor || b.full == null) continue;
                var t = b.full.transform.Find("Name")?.GetComponent<Text>();
                if (t == null) continue;
                t.text = text;
                var full = (RectTransform)b.full.transform;
                full.sizeDelta = new Vector2(t.rectTransform.anchoredPosition.x + t.preferredWidth + 12, full.sizeDelta.y);
                full.localScale = Vector3.one * 1.25f;
                StartCoroutine(Settle(full));
            }
        }

        IEnumerator Settle(RectTransform r)
        {
            for (float k = 0; k < 0.2f; k += Time.deltaTime) { if (!r) yield break; r.localScale = Vector3.one * Mathf.Lerp(1.25f, 1f, k / 0.2f); yield return null; }
            if (r) r.localScale = Vector3.one;
        }

        /// <summary>0 = hidden, 1 = icon dot only, 2 = full plate with the name.</summary>
        public void SetPlateMode(Transform anchor, int mode)
        {
            foreach (var b in badges)
            {
                if (b.anchor != anchor || b.full == null) continue;
                b.mode = mode;
                b.full.SetActive(mode == 2);
                b.dot.SetActive(mode == 1);
            }
        }

        /// <summary>While a guest is selected only their badges and their relations' stay bright; null restores all.</summary>
        public void FocusBadges(ICollection<Transform> anchors)
        {
            foreach (var b in badges)
                if (b.group) b.group.alpha = anchors == null || anchors.Contains(b.anchor) ? 1f : 0.22f;
        }

        public void ClearBadges() { foreach (var b in badges) if (b.rect) { b.rect.gameObject.SetActive(false); Destroy(b.rect.gameObject); } badges.Clear(); }

        public void SetChips(List<(Vector3 world, int delta)> list)
        {
            foreach (var c in chips) if (c.rect) Destroy(c.rect.gameObject);
            chips.Clear();
            foreach (var (world, delta) in list)
            {
                if (delta == 0) continue;
                var r = R("Chip", chipLayer, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(118, 58));
                var img = Img(r, "ui_pill", delta > 0 ? Palette.Bad : Palette.Good); Shadow(img, -4, 0.3f);
                Txt(Fill(r, "Text", 0, 3, 0, 0), (delta > 0 ? "+" : "−") + Mathf.Abs(delta), Display, 40, Color.white, TextAnchor.MiddleCenter);
                chips.Add((r, world));
            }
        }

        public void Popup(Vector3 world, string text, bool good, float delay = 0) => StartCoroutine(PopupRoutine(world, text, good, delay));

        IEnumerator PopupRoutine(Vector3 world, string text, bool good, float delay)
        {
            if (delay > 0) yield return new WaitForSeconds(delay);
            var r = R("Popup", popupLayer, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560, 84));
            var img = Img(r, "ui_pill", good ? Palette.Good : Palette.Bad); Shadow(img, -6, 0.3f);
            var t = Txt(Fill(r, "Text", 20, 4, 20, 0), text, Display, 38, Color.white, TextAnchor.MiddleCenter);
            for (float k = 0; k < 1.9f; k += Time.deltaTime)
            {
                r.anchoredPosition = WorldToSafe(world) + new Vector2(0, 110 + k * 55);
                float s = k < 0.15f ? Mathf.Lerp(0.6f, 1.08f, k / 0.15f) : k < 0.25f ? Mathf.Lerp(1.08f, 1f, (k - 0.15f) / 0.1f) : 1f;
                r.localScale = Vector3.one * s;
                float a = k < 1.45f ? 1 : 1 - (k - 1.45f) / 0.45f;
                img.color = new Color(img.color.r, img.color.g, img.color.b, a); t.color = new Color(1, 1, 1, a);
                yield return null;
            }
            Destroy(r.gameObject);
        }

        /// <summary>World point → position inside the safe-area rect (bottom-left origin), via viewport space so it holds
        /// for any screen, render texture or canvas mode.</summary>
        Vector2 WorldToSafe(Vector3 world)
        {
            if (cam == null) cam = Camera.main;
            Vector3 vp = cam.WorldToViewportPoint(world);
            var size = ((RectTransform)transform).rect.size;
            return new Vector2(vp.x * size.x - safe.anchorMin.x * size.x, vp.y * size.y - safe.anchorMin.y * size.y);
        }

        CanvasScaler scaler;

        /// <summary>Tall phones scale UI by width, wide screens (tablets, foldables) by height, so nothing overflows.</summary>
        void FitScaler()
        {
            if (!scaler) scaler = GetComponent<CanvasScaler>();
            if (!scaler || Screen.height <= 0) return;
            float aspect = (float)Screen.width / Screen.height, refAspect = 1080f / 1920f;
            scaler.matchWidthOrHeight = aspect > refAspect ? 1f : 0f;
        }

        void LateUpdate()
        {
            if (Screen.safeArea != lastSafe) ApplySafeArea();
            FitScaler();
            for (int i = badges.Count - 1; i >= 0; i--)
            {
                var b = badges[i];
                if (!b.rect) { badges.RemoveAt(i); continue; }
                if (!b.anchor) { Destroy(b.rect.gameObject); badges.RemoveAt(i); continue; }
                bool vis = b.anchor.gameObject.activeInHierarchy;
                b.rect.gameObject.SetActive(vis);
                if (vis) b.rect.anchoredPosition = WorldToSafe(b.anchor.position) + new Vector2(b.dx, (b.mode == 2 ? b.dy : 0) + Mathf.Abs(b.dx) * -0.12f);
            }
            foreach (var (rect, world) in chips) if (rect) rect.anchoredPosition = WorldToSafe(world) + new Vector2(0, 30);
            if (toast.gameObject.activeSelf && Time.time > toastUntil) toast.gameObject.SetActive(false);
            TickEmotes();
            if (dramaCard != null)
            {
                shownDrama = Mathf.MoveTowards(shownDrama, targetDrama, Time.deltaTime * Mathf.Max(25f, Mathf.Abs(targetDrama - shownDrama) * 3f));
                dramaPunch = Mathf.MoveTowards(dramaPunch, 0, Time.deltaTime * 1.6f);
                RenderDrama();
            }
        }

        public void ShowEnd(string title, string body, int stars, string primaryLabel, Action primary, Action secondary)
        {
            endTitle.text = title; endBody.text = body;
            for (int i = 0; i < 3; i++) { endStars[i].gameObject.SetActive(stars >= 0); endStars[i].sprite = IconFactory.Get(i < stars ? "star" : "star_empty"); }
            endPrimaryLabel.text = primaryLabel ?? "";
            endPrimary.gameObject.SetActive(primary != null);
            endPrimaryAction = primary; endSecondaryAction = secondary;
            endPanel.SetActive(true);
            endPanel.transform.SetAsLastSibling();
        }

        public void HideEnd() => endPanel.SetActive(false);
    }
}
