using TaleWorlds.Library;

namespace BandItPlus.UI
{
    // Wave 4.24.0 (2026-06-12): view-model for the custom PARLEY panel —
    // BP's own three-option dialog (tribute / hunt / walk away) replacing the
    // vanilla MultiSelectionInquiry. Options carry enabled flags + hint lines;
    // Execute guards re-check the flags so a dead IsEnabled binding can never
    // bypass the rules.
    //
    // 2026-07-02 (v6/v7 polish): CINEMATIC ENTRANCE (strips slide in from the left
    // one after another), per-choice SOUNDS, breathing lamplight/gem pulses — and
    // (v7) the CHIEF ON THE BOARD: his live portrait + clan banner in the header
    // (the proven BanditKingBriefVM reflection recipes) and a per-CULTURE
    // "Law of the Wilds" lore variant chosen from his culture StringId.
    public class BanditParleyVM : ViewModel
    {
        private const float FADE_IN = 0.30f;
        private float _t;

        public string TitleText { get; }
        public string BodyText { get; }
        public string PayLabel { get; }
        public string PayHint { get; }
        public bool CanPay { get; }
        public string HuntLabel { get; }
        public string HuntHint { get; }
        public bool CanHunt { get; }
        public string WalkLabel => BandItPlus.Localization.Get("bp_parleyvm_001", "Walk away");
        public string WalkHint { get; }

        // v7: chief on the board (null chief -> both hidden; prefab gates on Has*)
        public object Portrait { get; }
        public bool HasPortrait { get; }
        public string BannerCode { get; }
        public bool HasBanner { get; }
        public string LoreText { get; }

        public BanditParleyVM(string title, string body,
            string payLabel, string payHint, bool canPay,
            string huntLabel, string huntHint, bool canHunt,
            string walkHint, TaleWorlds.CampaignSystem.Hero chief = null)
        {
            TitleText = title ?? "";
            BodyText = body ?? "";
            PayLabel = payLabel ?? "";
            PayHint = payHint ?? "";
            CanPay = canPay;
            HuntLabel = huntLabel ?? "";
            HuntHint = huntHint ?? "";
            CanHunt = canHunt;
            WalkHint = walkHint ?? "";

            try { Portrait = BuildPortrait(chief); } catch { Portrait = null; }
            HasPortrait = Portrait != null;
            try { BannerCode = chief != null && chief.Clan != null && chief.Clan.Banner != null ? chief.Clan.Banner.Serialize() : null; } catch { BannerCode = null; }
            HasBanner = !string.IsNullOrEmpty(BannerCode);
            LoreText = PickLore(chief);
        }

        public string CurtainHex
        {
            get
            {
                float a = _t >= FADE_IN ? 0f : 1f - (_t / FADE_IN);
                int b = (int)(a * 255f);
                return "#000000" + b.ToString("X2");
            }
        }

        // Wave 4.28.x: continuous clock for the living firelight pulses (never stops).
        private float _pulseT;

        // ---- v6 entrance cascade (floats — Gauntlet numeric props must be float) ----
        private float _headerAlpha = 1f, _strip1Alpha = 1f, _strip2Alpha = 1f, _strip3Alpha = 1f, _loreAlpha = 1f, _stakesAlpha = 1f;
        private float _strip1DriftX, _strip2DriftX, _strip3DriftX;
        public float HeaderAlpha { get { return _headerAlpha; } set { if (value != _headerAlpha) { _headerAlpha = value; OnPropertyChangedWithValue(value, "HeaderAlpha"); } } }
        public float Strip1Alpha { get { return _strip1Alpha; } set { if (value != _strip1Alpha) { _strip1Alpha = value; OnPropertyChangedWithValue(value, "Strip1Alpha"); } } }
        public float Strip2Alpha { get { return _strip2Alpha; } set { if (value != _strip2Alpha) { _strip2Alpha = value; OnPropertyChangedWithValue(value, "Strip2Alpha"); } } }
        public float Strip3Alpha { get { return _strip3Alpha; } set { if (value != _strip3Alpha) { _strip3Alpha = value; OnPropertyChangedWithValue(value, "Strip3Alpha"); } } }
        public float LoreAlpha { get { return _loreAlpha; } set { if (value != _loreAlpha) { _loreAlpha = value; OnPropertyChangedWithValue(value, "LoreAlpha"); } } }
        public float StakesAlpha { get { return _stakesAlpha; } set { if (value != _stakesAlpha) { _stakesAlpha = value; OnPropertyChangedWithValue(value, "StakesAlpha"); } } }
        public float Strip1DriftX { get { return _strip1DriftX; } set { if (value != _strip1DriftX) { _strip1DriftX = value; OnPropertyChangedWithValue(value, "Strip1DriftX"); } } }
        public float Strip2DriftX { get { return _strip2DriftX; } set { if (value != _strip2DriftX) { _strip2DriftX = value; OnPropertyChangedWithValue(value, "Strip2DriftX"); } } }
        public float Strip3DriftX { get { return _strip3DriftX; } set { if (value != _strip3DriftX) { _strip3DriftX = value; OnPropertyChangedWithValue(value, "Strip3DriftX"); } } }

        public void Tick(float dt)
        {
            _pulseT += dt;
            _t += dt;
            if (_t < FADE_IN + 0.05f)
                OnPropertyChanged(nameof(CurtainHex));

            // entrance: header -> strip 1 -> strip 2 -> strip 3 -> lore -> stakes
            HeaderAlpha = Ease(_t, 0.10f, 0.30f);
            float e1 = Ease(_t, 0.26f, 0.30f); Strip1Alpha = e1; Strip1DriftX = -46f * (1f - e1);
            float e2 = Ease(_t, 0.40f, 0.30f); Strip2Alpha = e2; Strip2DriftX = -46f * (1f - e2);
            float e3 = Ease(_t, 0.54f, 0.30f); Strip3Alpha = e3; Strip3DriftX = -46f * (1f - e3);
            LoreAlpha = Ease(_t, 0.70f, 0.38f);
            StakesAlpha = Ease(_t, 0.88f, 0.34f);

            OnPropertyChanged(nameof(CrestHaloHex));
            OnPropertyChanged(nameof(JewelHex));
        }

        // ease-out quad: fast in, gentle settle (KingBrief's curve)
        private static float Ease(float t, float start, float dur)
        {
            float p = (t - start) / dur;
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;
            return 1f - (1f - p) * (1f - p);
        }

        // Breathing firelight (v6: the board's lamplight pool).
        public string CrestHaloHex
        {
            get
            {
                double s = System.Math.Sin(_pulseT * 1.8) * 0.5 + 0.5;
                int a = 0x28 + (int)(0x2C * s);
                return "#C8881E" + a.ToString("X2");
            }
        }

        // Twinkling jewel glow (v6: the header gem).
        public string JewelHex
        {
            get
            {
                double s = System.Math.Sin(_pulseT * 2.6 + 1.0) * 0.5 + 0.5;
                int a = 0x50 + (int)(0x70 * s);
                return "#FFD27A" + a.ToString("X2");
            }
        }

        // v6 sounds: fire-and-forget 2D stings per choice.
        private static void PlaySound(string id)
        {
            try { TaleWorlds.Engine.SoundEvent.PlaySound2D(id); } catch { }
        }

        public void ExecutePay() { if (CanPay) { PlaySound("event:/ui/notification/coins_negative"); BanditParleyScreen.Choose("pay"); } }
        public void ExecuteHunt() { if (CanHunt) { PlaySound("event:/ui/mission/horns/attack"); BanditParleyScreen.Choose("hunt"); } }
        public void ExecuteWalk() { PlaySound("event:/ui/mission/horns/retreat"); BanditParleyScreen.Choose("leave"); }

        // ---- v7: portrait (namespace-resilient reflection; the BanditKingBriefVM recipe) ----
        private static object BuildPortrait(TaleWorlds.CampaignSystem.Hero hero)
        {
            if (hero == null || hero.CharacterObject == null) return null;
            System.Type charCodeType = null, charImgIdVMType = null, charImgIdType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                charCodeType    = charCodeType    ?? asm.GetType("TaleWorlds.Core.CharacterCode");
                charImgIdVMType = charImgIdVMType ?? asm.GetType("TaleWorlds.Core.ViewModelCollection.ImageIdentifiers.CharacterImageIdentifierVM");
                charImgIdType   = charImgIdType   ?? asm.GetType("TaleWorlds.Core.ImageIdentifiers.CharacterImageIdentifier");
            }
            if (charCodeType == null || charImgIdVMType == null) return null;

            var heroCharType = hero.CharacterObject.GetType();
            System.Reflection.MethodInfo createFrom = null;
            foreach (var m in charCodeType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                if (m.Name != "CreateFrom") continue;
                var p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(heroCharType)) { createFrom = m; break; }
            }
            if (createFrom == null) return null;
            var charCode = createFrom.Invoke(null, new object[] { hero.CharacterObject });
            if (charCode == null) return null;

            foreach (var c in charImgIdVMType.GetConstructors())
            {
                var cp = c.GetParameters();
                if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                    return c.Invoke(new object[] { charCode });
            }
            if (charImgIdType != null)
            {
                object charImgId = null;
                foreach (var c in charImgIdType.GetConstructors())
                {
                    var cp = c.GetParameters();
                    if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charCode.GetType()))
                    { charImgId = c.Invoke(new object[] { charCode }); break; }
                }
                if (charImgId != null)
                    foreach (var c in charImgIdVMType.GetConstructors())
                    {
                        var cp = c.GetParameters();
                        if (cp.Length == 1 && cp[0].ParameterType.IsAssignableFrom(charImgIdType))
                            return c.Invoke(new object[] { charImgId });
                    }
            }
            return null;
        }

        // ---- v7: the Law of the Wilds, sworn in the chief's own tongue ----
        private static string PickLore(TaleWorlds.CampaignSystem.Hero chief)
        {
            string culture = null;
            try { culture = chief != null && chief.Culture != null ? chief.Culture.StringId : null; } catch { }
            switch (culture)
            {
                case "forest_bandits":
                    return BandItPlus.Localization.Get("bp_parleyvm_002", "Under the green roof a peace is sworn on the oldest oak, and every bow in the canopy is witness. The forest keeps no parchment; it keeps the shape of your word in the way the branches let you pass.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_003", "Honour it, and the woods open before you — no arrow notched, no snare set. Break it, and the trees themselves seem to remember which way you ran.")
                    ;
                case "mountain_bandits":
                    return BandItPlus.Localization.Get("bp_parleyvm_004", "In the high passes an oath is spoken once and given to the echo. Stone does not bargain twice. The chief's word travels ridge to ridge faster than any rider, and so does yours.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_005", "Keep the peace and the passes stay open in any weather. Break it, and every goat-track grows a watcher, every scree-slope an ambush.");
                case "steppe_bandits":
                    return BandItPlus.Localization.Get("bp_parleyvm_006", "On the open steppe a peace is sworn to the sky, with a horse's breath for a seal. Nothing hides on the grass sea — not a rider, not a lie.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_007", "Hold to your word and the horizon stays empty where you ride. Break it, and the wind will carry your name to every saddle from here to the red dawn.");
                case "desert_bandits":
                    return BandItPlus.Localization.Get("bp_parleyvm_008", "In the dunes a peace is a water-debt: sworn over the last cup, shared in the shade of one cloth. No oath binds tighter than thirst.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_009", "Honour it, and wells you never dug will be left unpoisoned for you. Break it, and the desert's patience becomes the chief's — endless, and always waiting.");
                case "sea_raiders":
                    return BandItPlus.Localization.Get("bp_parleyvm_010", "A raider's peace is spat over the gunwale and sworn on the tide — salt takes the words down and does not give them back. The crews reckon a man by his wake, not his flag.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_011", "Keep faith and no longship shadows your sail. Break it, and every grey horizon hides a hull with your name carved in the oar-bench.");
                case "looters":
                    return BandItPlus.Localization.Get("bp_parleyvm_012", "Even broken men keep one law: a peace bought is a night's sleep earned. They have nothing left but hunger and memory, and memory is the sharper of the two.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_013", "Feed the bargain and they scatter from your road like crows from a cart. Cheat it, and desperation makes them braver than any lord's guard.");
                default:
                    return BandItPlus.Localization.Get("bp_parleyvm_014", "There is no ink in a bandit's peace. It is sworn over a fire, witnessed by armed men, and remembered longer than any lord's treaty. Coin buys silence; blood buys respect. Either way the road behind you grows quieter, and the watchers in the rocks lower their bows.")
                        + "\n\n" + BandItPlus.Localization.Get("bp_parleyvm_015", "But the wilds keep their ledgers too. A peace honoured carries your name to every campfire in the pass. A peace broken follows you further than any bounty.");
            }
        }
    }
}
