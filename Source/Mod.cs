#nullable disable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using VanillaPsycastsExpanded;

namespace PsycastSynergies
{
    public class PsycastSynergiesSettings : ModSettings
    {
        // Per hard level invested in an ability: bonus to its scaled stats.
        public float perLevelPct = 0.05f;
        // Per level invested in OTHER abilities of the same path (Diablo-2 synergy).
        public float synergyPct = 0.015f;
        // Hard cap on how many levels a single ability can hold (gated by psycaster level below).
        public int maxSkillLevel = 10;
        public bool skillFx = true;
        // Fog of war: hide the identity of every un-learned psycast on the tree behind a "?" cover
        // until it is unlocked. Off by default.
        public bool fogOfWar = false;
        // Fog of war sub-mode: a psycast whose prerequisites are already met stays visible, so the
        // choices actually in front of the player are readable and only what lies deeper is covered.
        public bool fogRevealNext = true;
        // Turn off the whole cross-ability synergy / empower system: skills scale from their OWN
        // invested levels only, and the tooltip/hover no longer show "receives from" / "empowers".
        // Off by default (synergies stay on).
        public bool disableSynergies = false;
        // Hide the player-facing reset buttons (for hardcore / no-take-backs modpacks). Off by default.
        public bool disableSkillReset = false;   // hides the psycast tab's "Reset skills" button
        public bool disableSpecReset = false;    // hides the constellation window's "Reset specializations" refund button
        // Temporarily boost the caster's Psychic Sensitivity during a cast so effects that derive
        // from it (very common in addons that hardcode radius/damage as X*sensitivity) also scale.
        public bool scaleViaSensitivity = true;

        public bool scalePower = true;
        public bool scaleRadius = true;
        public bool scaleDuration = true;

        // Tradeoff: each level also raises the cast's psyfocus cost + entropy (heat).
        // Scales with this skill's OWN level only (synergy bonuses stay free).
        public bool scaleCost = true;
        public float costPerLevelPct = 0.05f;

        // Boons/buffs: scale the magnitude (severity) of hediffs the cast applies, when the
        // cast specifies an explicit severity. (Duration of buffs always scales via scaleDuration.)
        public bool scaleBuffStrength = true;

        // Performance: memoize hot math (StatMultiplier, skill caps, charge maxima, tier reads)
        // within the current tick/frame. Values refresh instantly on any change; turning this off
        // recomputes everything from scratch on every call (diagnostic escape hatch).
        public bool perfCaching = true;

        // Flat psycaster level cap written to VPE's maxLevel. Enlightenment / Transcendence tiers do NOT affect it.
        public bool overrideVpeLevelCap = true;
        public int vpeLevelCap = 400;
        // When ISEKAI RPG Leveling is active, strip its StatParts from the psycaster stats so it can't inflate them.
        public bool suppressIsekaiPsycastStats = true;

        // How many psycaster levels grant one specialization point.
        public int specLevelsPerPoint = 4;
        public float specXpPerPoint = 26f;               // cast/kill spec-XP needed per specialization point
        // Psycaster levels required per allowed skill level (gates dumping levels early).
        public int psyLevelsPerSkillLevel = 3;

        // (B) Frozen synergy graph: abilityDefName -> its primary stat + fixed sources/edges.
        // Computed once (PsycastInfo.EnsureFrozen) and persisted so synergies never reshuffle.
        public Dictionary<string, FrozenSyn> frozenSyn = new Dictionary<string, FrozenSyn>();
        public int graphVersion = 0;   // PsycastInfo.GraphVersion stamp - mismatch triggers a one-time rebuild

        // In-game balance editor (PlayerTuning): the player's personal re-picks, layered on top of
        // ManualBalance.json and the frozen graph. Keys are defNames (prim/primStr/srcs) or
        // "src|tgt" pairs (edge/edgeStr); srcs values are semicolon-joined FULL source lists.
        public Dictionary<string, int> tunePrimStat = new Dictionary<string, int>();
        public Dictionary<string, float> tunePrimStr = new Dictionary<string, float>();
        public Dictionary<string, int> tuneEdgeStat = new Dictionary<string, int>();
        public Dictionary<string, float> tuneEdgeStr = new Dictionary<string, float>();
        public Dictionary<string, string> tuneSrcs = new Dictionary<string, string>();

        // VPE path-access tweaks (both default ON).
        public bool disableGeneRequirements = true;
        public bool enableLockedMechTrees = true;
        public bool lockPathsToEnlightenment = true;   // paths unlock only via the awakening cards (or dev mode)
        public bool hideUnlearnedPaths = true;         // VPE-native tab: list only unlocked paths (active while lockPaths is on; tab dev mode bypasses)
        public List<string> autoUnlockedPaths = new List<string>();
        public bool disableTreeAbilityUnlocks = false;
        public bool restrictUnlocksByPsyLevel = false;
        public bool allowPsytrainerBypassLevelRequirement = false;

        // XP system. Casting (tier-scaled) is the primary source; meditation is a reduced trickle;
        // meditating pawns can randomly break through ("Enlightenment") for a big burst.
        public float meditationXpMult = 0.35f;
        public float castXpPerTier = 20f;
        public bool noPsyfocusDecay = true;              // QoL: psyfocus never drains on its own (gain via meditation, spend via casting)
        public bool autoPsycasterStats = true;           // VPE heat/recovery/sensitivity auto per level; psyfocus cost unaffected; no manual stat spend
        public bool enlightenmentEnabled = true;
        public bool empirePsylinkIntegrate = true;       // external psylink (Empire/anima) triggers our Awakening instead
        public bool gateUntieredPsylinks = true;         // strip a generated psylink from any pawn without an Awakened+ tier
        public float awakenedSpawnChance = 0.05f;        // chance ANY eligible pawn spawns as a fresh Awakened+ psycaster
        public bool noAwakenedStartingPawns = true;      // starting colonists (new-game character creation) never roll the random Awakened+ spawn
        public bool cardRevealAll = false;               // awakening cards: the other cards can be turned by hand and any revealed card re-picked
        public int cardPickCount = 0;                    // cards dealt per tier-up pick: 0 = auto (3, or 5 at Tier II), 1-8 = fixed
        public bool cardRedeal = true;                   // first awakening: offer one Reroll once a card is face up
        public float enlightenmentChance = 0.02f;        // base hourly breakthrough chance while meditating, BEFORE Illuminated (0 = none)
        public float transcendChance = 0.02f;            // the same, for a psycaster PAST Illuminated (tier 4+); transcendBreakthroughCurve still stacks on top
        public float enlightenmentStreakBonus = 0.015f;  // +chance per consecutive hour meditated
        public float transcendBreakthroughCurve = 0.15f; // each Transcendent tier (>3) multiplies breakthrough chance by +this (still hard-capped at 0.6)
        public float enlightenmentFrac = 1.0f;           // psycaster XP burst = this × next-level XP (1.0 = a full level)
        public float enlightenmentSaturationFactor = 0.5f; // daily-meditation falloff: breakthrough chance × 1/(1+saturation×this)
        public float awakenGuaranteeHours = 36f;          // cumulative meditation hours that GUARANTEE a non-psycaster Awakens (~1 week dedicated)
        public float pilgrimGuaranteeHours = 60f;         // tier 1-2 meditation hours that GUARANTEE a pilgrimage offer (T3 climb x1.5; 0 = storyteller only)
        public bool medBars = true;                       // compact meditation progress bars in the psycast tab
        // Psycaster roster (main tab) view prefs. Not settings-tab rows - they are toggled in the window
        // itself and persisted here so the view survives a restart.
        public bool rosterIncludeCaptives = true;         // list prisoners and slaves (display only - they never accrue)
        public bool rosterHideIncapable = false;          // hide pawns who cannot awaken at all
        public float rosterX, rosterY, rosterW, rosterH;  // remembered window rect (rosterW 0 = never placed)
        public int balanceVersion = 0;                   // one-time stamp so changed balance defaults override a stale saved config
        public float comaSafeHours = 6f;                 // hours/day of meditation before coma risk starts (24 = never risky)
        public float comaRiskPerHour = 0.05f;            // psychic-coma chance per hour over the safe window
        public int tier2SpecPoints = 4;                  // bonus specialization points granted on reaching Tier II
        public int tier3SpecPoints = 6;                  // bonus specialization points granted on reaching Tier III
        public bool transcendEnabled = true;             // allow Illuminated pawns to keep climbing into open-ended Transcendent tiers
        public float transcendBaseHours = 48f;           // meditation hours for the FIRST Transcendent tier (Tier IV)
        public float transcendGrowth = 1.6f;             // geometric cost multiplier per Transcendent tier (diminishing returns)
        public int transcendForgoPoints = 4;             // spec points granted when forgoing a Transcendent path card

        // Enemy psycaster tiers (raiders, esp. Empire, can spawn Enlightened).
        public bool enemyTiersEnabled = true;
        public bool enemyTier1 = true;
        public bool enemyTier2 = true;
        public bool enemyTier3 = true;
        public float enemyTierFreq = 0.5f;               // chance a hostile psycaster receives any tier
        public bool enemyAscension = false;              // Tier III enemies can spawn with an unlocked Apotheosis path (OFF)

        // Tier II pilgrimage quest.
        public int pilgrimMeditationTicks = 50000;       // total actual meditation needed (~20h, default ~2.5 days at the daily cap)
        public int pilgrimDailyMaxTicks = 15000;         // per-day meditation cap (8h) so the pilgrimage spans real elapsed time
        public int pilgrimWaveIntervalTicks = 30000;     // 12h between Ancient Psycaster waves at the site
        public float pilgrimWavePointsScale = 1.0f;      // multiplier on wave threat points
        public string pilgrimFocusDef = "PS_PilgrimThrone";   // which focus building spawns at the altar-chain site (T2)

        // Anima pilgrimage chain (pacifist, multi-site).
        public int animaPilgrimTicksPerSite = 50000;     // ~20h of meditation per site
        public int animaPilgrimT2Sites = 3;              // number of anima sites at T2
        public int animaPilgrimT3Sites = 4;              // number of anima sites at T3 (last is the giant tree)

        // A full day of safe meditation means the window can never be exceeded, so the whole coma
        // system is off. Checked explicitly rather than left to the arithmetic, because the streak
        // term and the deferred-reroll term add risk independently of today's hours and would keep
        // trickling comas in at 24h.
        public bool ComaRiskOff => comaSafeHours >= 24f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref perLevelPct, "perLevelPct", 0.05f);
            Scribe_Values.Look(ref synergyPct, "synergyPct", 0.015f);
            Scribe_Values.Look(ref maxSkillLevel, "maxSkillLevel", 10);
            Scribe_Values.Look(ref skillFx, "skillFx", true);
            Scribe_Values.Look(ref fogOfWar, "fogOfWar", false);
            Scribe_Values.Look(ref fogRevealNext, "fogRevealNext", true);
            Scribe_Values.Look(ref disableSynergies, "disableSynergies", false);
            Scribe_Values.Look(ref disableSkillReset, "disableSkillReset", false);
            Scribe_Values.Look(ref disableSpecReset, "disableSpecReset", false);
            Scribe_Values.Look(ref scaleViaSensitivity, "scaleViaSensitivity", true);
            Scribe_Values.Look(ref scalePower, "scalePower", true);
            Scribe_Values.Look(ref scaleRadius, "scaleRadius", true);
            Scribe_Values.Look(ref scaleDuration, "scaleDuration", true);
            Scribe_Values.Look(ref scaleCost, "scaleCost", true);
            Scribe_Values.Look(ref costPerLevelPct, "costPerLevelPct", 0.05f);
            Scribe_Values.Look(ref scaleBuffStrength, "scaleBuffStrength", true);
            Scribe_Values.Look(ref perfCaching, "perfCaching", true);
            Scribe_Values.Look(ref overrideVpeLevelCap, "overrideVpeLevelCap", true);
            Scribe_Values.Look(ref vpeLevelCap, "vpeLevelCap", 400);
            Scribe_Values.Look(ref suppressIsekaiPsycastStats, "suppressIsekaiPsycastStats", true);
            Scribe_Values.Look(ref specLevelsPerPoint, "specLevelsPerPoint", 4);
            Scribe_Values.Look(ref specXpPerPoint, "specXpPerPoint", 26f);
            Scribe_Values.Look(ref psyLevelsPerSkillLevel, "psyLevelsPerSkillLevel", 3);
            Scribe_Collections.Look(ref frozenSyn, "frozenSyn", LookMode.Value, LookMode.Deep);
            if (frozenSyn == null) frozenSyn = new Dictionary<string, FrozenSyn>();
            Scribe_Values.Look(ref graphVersion, "graphVersion", 0);
            Scribe_Collections.Look(ref tunePrimStat, "tunePrimStat", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref tunePrimStr, "tunePrimStr", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref tuneEdgeStat, "tuneEdgeStat", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref tuneEdgeStr, "tuneEdgeStr", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref tuneSrcs, "tuneSrcs", LookMode.Value, LookMode.Value);
            if (tunePrimStat == null) tunePrimStat = new Dictionary<string, int>();
            if (tunePrimStr == null) tunePrimStr = new Dictionary<string, float>();
            if (tuneEdgeStat == null) tuneEdgeStat = new Dictionary<string, int>();
            if (tuneEdgeStr == null) tuneEdgeStr = new Dictionary<string, float>();
            if (tuneSrcs == null) tuneSrcs = new Dictionary<string, string>();
            Scribe_Values.Look(ref disableGeneRequirements, "disableGeneRequirements", true);
            Scribe_Values.Look(ref enableLockedMechTrees, "enableLockedMechTrees", true);
            Scribe_Values.Look(ref lockPathsToEnlightenment, "lockPathsToEnlightenment", true);
            Scribe_Values.Look(ref hideUnlearnedPaths, "hideUnlearnedPaths", true);
            Scribe_Collections.Look(ref autoUnlockedPaths, "autoUnlockedPaths", LookMode.Value);
            if (autoUnlockedPaths == null) autoUnlockedPaths = new List<string>();
            Scribe_Values.Look(ref disableTreeAbilityUnlocks, "disableTreeAbilityUnlocks", false);
            Scribe_Values.Look(ref restrictUnlocksByPsyLevel, "restrictUnlocksByPsyLevel", false);
            Scribe_Values.Look(ref allowPsytrainerBypassLevelRequirement, "allowPsytrainerBypassLevelRequirement", false);
            Scribe_Values.Look(ref meditationXpMult, "meditationXpMult", 0.35f);
            Scribe_Values.Look(ref castXpPerTier, "castXpPerTier", 20f);
            Scribe_Values.Look(ref noPsyfocusDecay, "noPsyfocusDecay", true);
            Scribe_Values.Look(ref autoPsycasterStats, "autoPsycasterStats", true);
            Scribe_Values.Look(ref enlightenmentEnabled, "enlightenmentEnabled", true);
            Scribe_Values.Look(ref empirePsylinkIntegrate, "empirePsylinkIntegrate", true);
            Scribe_Values.Look(ref gateUntieredPsylinks, "gateUntieredPsylinks", true);
            Scribe_Values.Look(ref awakenedSpawnChance, "awakenedSpawnChance", 0.05f);
            Scribe_Values.Look(ref noAwakenedStartingPawns, "noAwakenedStartingPawns", true);
            Scribe_Values.Look(ref cardRevealAll, "cardRevealAll", false);
            Scribe_Values.Look(ref cardPickCount, "cardPickCount", 0);
            Scribe_Values.Look(ref cardRedeal, "cardRedeal", true);
            Scribe_Values.Look(ref enlightenmentChance, "enlightenmentChance", 0.02f);
            Scribe_Values.Look(ref transcendChance, "transcendChance", 0.02f);
            Scribe_Values.Look(ref enlightenmentStreakBonus, "enlightenmentStreakBonus", 0.015f);
            Scribe_Values.Look(ref transcendBreakthroughCurve, "transcendBreakthroughCurve", 0.15f);
            Scribe_Values.Look(ref enlightenmentFrac, "enlightenmentFrac", 1.0f);
            Scribe_Values.Look(ref enlightenmentSaturationFactor, "enlightenmentSaturationFactor", 0.5f);
            Scribe_Values.Look(ref awakenGuaranteeHours, "awakenGuaranteeHours", 36f);
            Scribe_Values.Look(ref pilgrimGuaranteeHours, "pilgrimGuaranteeHours", 60f);
            Scribe_Values.Look(ref medBars, "medBars", true);
            Scribe_Values.Look(ref rosterIncludeCaptives, "rosterIncludeCaptives", true);
            Scribe_Values.Look(ref rosterHideIncapable, "rosterHideIncapable", false);
            Scribe_Values.Look(ref rosterX, "rosterX", 0f);
            Scribe_Values.Look(ref rosterY, "rosterY", 0f);
            Scribe_Values.Look(ref rosterW, "rosterW", 0f);
            Scribe_Values.Look(ref rosterH, "rosterH", 0f);
            Scribe_Values.Look(ref comaSafeHours, "comaSafeHours", 6f);
            Scribe_Values.Look(ref comaRiskPerHour, "comaRiskPerHour", 0.05f);
            Scribe_Values.Look(ref tier2SpecPoints, "tier2SpecPoints", 4);
            Scribe_Values.Look(ref tier3SpecPoints, "tier3SpecPoints", 6);
            Scribe_Values.Look(ref transcendEnabled, "transcendEnabled", true);
            Scribe_Values.Look(ref transcendBaseHours, "transcendBaseHours", 48f);
            Scribe_Values.Look(ref transcendGrowth, "transcendGrowth", 1.6f);
            Scribe_Values.Look(ref transcendForgoPoints, "transcendForgoPoints", 4);
            Scribe_Values.Look(ref enemyTiersEnabled, "enemyTiersEnabled", true);
            Scribe_Values.Look(ref enemyTier1, "enemyTier1", true);
            Scribe_Values.Look(ref enemyTier2, "enemyTier2", true);
            Scribe_Values.Look(ref enemyTier3, "enemyTier3", true);
            Scribe_Values.Look(ref enemyTierFreq, "enemyTierFreq", 0.5f);
            Scribe_Values.Look(ref enemyAscension, "enemyAscension", false);
            Scribe_Values.Look(ref pilgrimMeditationTicks, "pilgrimMeditationTicks", 50000);
            Scribe_Values.Look(ref pilgrimDailyMaxTicks, "pilgrimDailyMaxTicks", 15000);
            Scribe_Values.Look(ref pilgrimWaveIntervalTicks, "pilgrimWaveIntervalTicks", 30000);
            Scribe_Values.Look(ref pilgrimWavePointsScale, "pilgrimWavePointsScale", 1.0f);
            Scribe_Values.Look(ref pilgrimFocusDef, "pilgrimFocusDef", "PS_PilgrimThrone");
            Scribe_Values.Look(ref animaPilgrimTicksPerSite, "animaPilgrimTicksPerSite", 50000);
            Scribe_Values.Look(ref animaPilgrimT2Sites, "animaPilgrimT2Sites", 3);
            Scribe_Values.Look(ref animaPilgrimT3Sites, "animaPilgrimT3Sites", 4);
            Scribe_Values.Look(ref balanceVersion, "balanceVersion", 0);

            // One-time migration: when the saved config predates a balance-default change, push the new
            // values in (otherwise the old saved knobs would mask the new defaults forever).
            if (Scribe.mode == LoadSaveMode.LoadingVars && balanceVersion < 5)
            {
                if (balanceVersion < 2) { castXpPerTier = 20f; enlightenmentFrac = 1.0f; enlightenmentSaturationFactor = 0.5f; }
                if (balanceVersion < 3) pilgrimDailyMaxTicks = 15000;   // 6h/day, matching the safe meditation window
                if (balanceVersion < 5) { vpeLevelCap = 400; overrideVpeLevelCap = true; }   // flat 400 cap, tier-independent
                balanceVersion = 5;
            }
        }
    }

    public class PsycastSynergiesMod : Mod
    {
        public static PsycastSynergiesSettings Settings;
        public static PsycastSynergiesMod Instance;
        private Vector2 settingsScroll;          // settings panel scroll position
        private int settingsTab;                 // active settings tab

        public PsycastSynergiesMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<PsycastSynergiesSettings>();
        }

        public override string SettingsCategory() => "Psycasts²";

        // Push our configured cap into VPE's live settings. Called at startup and
        // whenever our settings change. No-op (and non-destructive) when the override
        // is off - we never lower VPE's own configured value.
        public static void ApplyVpeLevelCap()
        {
            var vpe = PsycastsMod.Settings;
            if (vpe == null || Settings == null || !Settings.overrideVpeLevelCap) return;
            vpe.maxLevel = Mathf.Max(1, Settings.vpeLevelCap);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            ApplyVpeLevelCap();
            // Sliders/toggles feed the multiplier math - drop the tick memo and any cached tooltip
            // model so the new values show immediately.
            PerfCache.Bump();
            PsycastUnlockRules.SyncAllAutoUnlockedPaths();
        }

        // Category list down the left of the settings window. Names are keys: "PS_SetTab_" + entry.
        private static readonly string[] SettingsTabs =
            { "Levels", "Paths", "Meditation", "Pilgrimage", "Enemies", "Display", "Advanced" };
        private float[] settingsTabH;            // per-tab remembered scroll height (avoids stale-height clipping)

        private const float RailWidth = 176f;
        private const float RailRowH = 32f;
        private const float SubIndent = 24f;
        private static readonly Color HintColor = new Color(0.72f, 0.74f, 0.76f);

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (settingsTabH == null || settingsTabH.Length != SettingsTabs.Length)
            {
                settingsTabH = new float[SettingsTabs.Length];
                // Generous first-frame guess: the real height is measured at the end of the first draw.
                // Guessing SHORT costs a frame of clipping; see the maxOneColumn note below for why it
                // used to cost the whole page.
                for (int k = 0; k < settingsTabH.Length; k++) settingsTabH[k] = 3000f;
            }
            // Category rail. We DETECT a click here but apply it only AFTER drawing the content, so the
            // active category is constant across this frame's IMGUI event passes - switching mid-frame swaps
            // the control set between passes and leaves the new page's sliders and checkboxes dead.
            int clicked = -1;
            Text.Anchor = TextAnchor.MiddleLeft;
            for (int i = 0; i < SettingsTabs.Length; i++)
            {
                var row = new Rect(inRect.x, inRect.y + i * RailRowH, RailWidth, RailRowH);
                if (settingsTab == i) Widgets.DrawHighlightSelected(row);
                else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.Label(new Rect(row.x + 8f, row.y, row.width - 14f, row.height),
                    ("PS_SetTab_" + SettingsTabs[i]).Translate());
                if (Widgets.ButtonInvisible(row)) clicked = i;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawLineVertical(inRect.x + RailWidth + 8f, inRect.y, inRect.height);

            int tab = settingsTab;
            float bodyX = inRect.x + RailWidth + 20f;
            var body = new Rect(bodyX, inRect.y, inRect.xMax - bodyX, inRect.height);
            var viewRect = new Rect(0f, 0f, body.width - 20f, Mathf.Max(body.height, settingsTabH[tab]));
            Widgets.BeginScrollView(body, ref settingsScroll, viewRect);
            var l = new Listing_Standard();
            // MUST be set. Listing_Standard breaks into a SECOND COLUMN as soon as content passes
            // listingRect.height, and NewColumn() resets curY to 0 - so a page taller than our guessed
            // viewRect drew its remainder off to the right (invisible), and then reported a tiny
            // CurHeight back into settingsTabH. Next frame the viewRect was tiny, so the break happened
            // one row in, and the page collapsed to nothing but its first section head. The pages only
            // grew past the old 1200px guess when every setting gained a visible explanation line.
            l.maxOneColumn = true;
            l.Begin(viewRect);
            switch (tab)
            {
                case 0: TabLevels(l); break;
                case 1: TabPaths(l); break;
                case 2: TabMeditation(l); break;
                case 3: TabPilgrimage(l); break;
                case 4: TabEnemies(l); break;
                case 5: TabDisplay(l); break;
                default: TabAdvanced(l); break;
            }
            settingsTabH[tab] = l.CurHeight + 24f;
            l.End();
            Widgets.EndScrollView();

            if (clicked >= 0 && clicked != settingsTab) { settingsTab = clicked; settingsScroll = Vector2.zero; }
        }

        // ---- setting widgets: a control, then a plain-English line under it ----
        static void Head(Listing_Standard l, string text)
        {
            l.Gap(10f);
            Text.Font = GameFont.Medium;
            Widgets.Label(l.GetRect(30f), text);
            Text.Font = GameFont.Small;
            l.GapLine(4f);
        }

        // The always-visible explanation under a setting. Small grey text, wrapped to the column.
        // Font follows Text.TinyFontSupported: Text.Font's setter silently substitutes Small when tiny
        // text is turned off, and measuring with one font while drawing in another mis-sizes the row.
        static void Hint(Listing_Standard l, string text, bool indent = true)
        {
            if (text.NullOrEmpty()) return;
            GameFont pf = Text.Font;
            Text.Font = Text.TinyFontSupported ? GameFont.Tiny : GameFont.Small;
            float pad = indent ? SubIndent : 0f;
            float w = l.ColumnWidth - pad;
            float h = Text.CalcHeight(text, w);
            Rect r = l.GetRect(h);
            r.xMin += pad;
            GUI.color = HintColor;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.Font = pf;
            l.Gap(4f);
        }

        // Listing.Indent only moves the cursor, it does NOT narrow the column, so a rect drawn after it
        // would overrun the scrollbar. Shrink ColumnWidth by the same amount and put it back.
        static void PushSub(Listing_Standard l) { l.Indent(SubIndent); l.ColumnWidth -= SubIndent; }
        static void PopSub(Listing_Standard l) { l.Outdent(SubIndent); l.ColumnWidth += SubIndent; }

        static void FS(Listing_Standard l, string label, ref float val, float min, float max, string hint, bool integer = false)
        {
            Rect r = l.GetRect(Text.LineHeight);
            Widgets.Label(r, label);
            float v = l.Slider(val, min, max);
            val = integer ? Mathf.Round(v) : v;
            Hint(l, hint, false);
        }
        static void IS(Listing_Standard l, string label, ref int val, int min, int max, string hint)
        {
            float f = val; FS(l, label, ref f, min, max, hint, true); val = Mathf.RoundToInt(f);
        }
        static void CB(Listing_Standard l, string label, ref bool val, string hint)
        {
            l.CheckboxLabeled(label, ref val);
            Hint(l, hint);
        }

        void TabLevels(Listing_Standard l)
        {
            var s = Settings;
            // Snapshot every toggle-gated row's visibility at the START of the draw. A RimWorld checkbox
            // flips its bool mid-frame (on MouseUp); if a conditional slider below it were gated on the
            // LIVE bool, the control set would differ between IMGUI passes, corrupting GUI state and
            // blanking a chunk of the page. Reading a frame-start snapshot keeps the control set stable.
            bool showLinkPct = !s.disableSynergies, showCostRow = s.scaleCost;

            Head(l, "PS_SetH_Spending".Translate());
            FS(l, "PS_SetPerLevel".Translate((s.perLevelPct * 100f).ToString("F0")), ref s.perLevelPct, 0f, 0.25f,
                "PS_SetPerLevelTip".Translate());
            IS(l, "PS_SetMaxSkillLevel".Translate(s.maxSkillLevel), ref s.maxSkillLevel, 1, 30,
                "PS_SetMaxSkillLevelTip".Translate());
            IS(l, "PS_SetPsyLevelsPerSkill".Translate(s.psyLevelsPerSkillLevel), ref s.psyLevelsPerSkillLevel, 1, 10,
                "PS_SetPsyLevelsPerSkillTip".Translate());
            CB(l, "PS_SetAutoStats".Translate(), ref s.autoPsycasterStats, "PS_SetAutoStatsTip".Translate());

            Head(l, "PS_SetH_Links".Translate());
            // Stored as "disableSynergies" but shown the way round a player reads it.
            bool linksOn = !s.disableSynergies;
            CB(l, "PS_SetLinks".Translate(), ref linksOn, "PS_SetLinksTip".Translate());
            s.disableSynergies = !linksOn;
            if (showLinkPct)
            {
                PushSub(l);
                FS(l, "PS_SetSynergyPct".Translate((s.synergyPct * 100f).ToString("F0")), ref s.synergyPct, 0f, 0.10f,
                    "PS_SetSynergyPctTip".Translate());
                PopSub(l);
            }

            Head(l, "PS_SetH_WhatChanges".Translate());
            CB(l, "PS_SetScalePower".Translate(), ref s.scalePower, "PS_SetScalePowerTip".Translate());
            CB(l, "PS_SetScaleRadius".Translate(), ref s.scaleRadius, "PS_SetScaleRadiusTip".Translate());
            CB(l, "PS_SetScaleDuration".Translate(), ref s.scaleDuration, "PS_SetScaleDurationTip".Translate());
            CB(l, "PS_SetScaleBuff".Translate(), ref s.scaleBuffStrength, "PS_SetScaleBuffTip".Translate());
            CB(l, "PS_SetScaleSens".Translate(), ref s.scaleViaSensitivity, "PS_SetScaleSensTip".Translate());

            Head(l, "PS_SetH_Cost".Translate());
            CB(l, "PS_SetScaleCost".Translate(), ref s.scaleCost, "PS_SetScaleCostTip".Translate());
            if (showCostRow)
            {
                PushSub(l);
                FS(l, "PS_SetCostPerLevel".Translate((s.costPerLevelPct * 100f).ToString("F0")), ref s.costPerLevelPct, 0f, 0.25f,
                    "PS_SetCostPerLevelTip".Translate());
                PopSub(l);
            }
        }

        void TabPaths(Listing_Standard l)
        {
            var s = Settings;
            bool showPsytrainerBypass = s.restrictUnlocksByPsyLevel;

            Head(l, "PS_SetH_GettingTrees".Translate());
            CB(l, "PS_SetLockPaths".Translate(), ref s.lockPathsToEnlightenment, "PS_SetLockPathsTip".Translate());
            CB(l, "PS_SetNoGeneReq".Translate(), ref s.disableGeneRequirements, "PS_SetNoGeneReqTip".Translate());
            CB(l, "PS_SetMechTrees".Translate(), ref s.enableLockedMechTrees, "PS_SetMechTreesTip".Translate());

            Head(l, "PS_SetH_AutoPaths".Translate());
            Hint(l, "PS_SetAutoPathsTip".Translate(), false);
            Hint(l, "PS_SetAutoPathsCount".Translate(PsycastUnlockRules.AutoUnlockedPathCount), false);
            foreach (var path in PsycastUnlockRules.SelectablePaths)
            {
                bool on = s.autoUnlockedPaths.Contains(path.defName);
                bool prev = on;
                CB(l, path.LabelCap, ref on, null);
                if (on == prev) continue;
                if (on) s.autoUnlockedPaths.Add(path.defName);
                else s.autoUnlockedPaths.Remove(path.defName);
            }

            Head(l, "PS_SetH_AbilityUnlocks".Translate());
            CB(l, "PS_SetDisableAbilityUnlocks".Translate(), ref s.disableTreeAbilityUnlocks, "PS_SetDisableAbilityUnlocksTip".Translate());
            CB(l, "PS_SetRestrictUnlockLevel".Translate(), ref s.restrictUnlocksByPsyLevel, "PS_SetRestrictUnlockLevelTip".Translate());
            if (showPsytrainerBypass)
            {
                PushSub(l);
                CB(l, "PS_SetAllowPsytrainerBypass".Translate(), ref s.allowPsytrainerBypassLevelRequirement, "PS_SetAllowPsytrainerBypassTip".Translate());
                PopSub(l);
            }

            Head(l, "PS_SetH_SpecPoints".Translate());
            IS(l, "PS_SetSpecLevels".Translate(s.specLevelsPerPoint), ref s.specLevelsPerPoint, 1, 20,
                "PS_SetSpecLevelsTip".Translate());
            FS(l, "PS_SetSpecXp".Translate(s.specXpPerPoint.ToString("F0")), ref s.specXpPerPoint, 10f, 80f,
                "PS_SetSpecXpTip".Translate(), true);
            IS(l, "PS_SetTier2Points".Translate(s.tier2SpecPoints), ref s.tier2SpecPoints, 0, 12,
                "PS_SetTier2PointsTip".Translate());
            IS(l, "PS_SetTier3Points".Translate(s.tier3SpecPoints), ref s.tier3SpecPoints, 0, 16,
                "PS_SetTier3PointsTip".Translate());
        }

        void TabMeditation(Listing_Standard l)
        {
            var s = Settings;
            // Frame-start snapshots (see TabLevels) so a checkbox flip can't reshape the control set mid-draw.
            bool showBreak = s.enlightenmentEnabled, showGate = s.gateUntieredPsylinks, showTrans = s.transcendEnabled;

            Head(l, "PS_SetH_EarningLevels".Translate());
            FS(l, "PS_SetCastXp".Translate(s.castXpPerTier.ToString("F0"), (s.castXpPerTier * 3f).ToString("F0")), ref s.castXpPerTier, 0f, 60f,
                "PS_SetCastXpTip".Translate(), true);
            FS(l, "PS_SetMedXp".Translate((s.meditationXpMult * 100f).ToString("F0")), ref s.meditationXpMult, 0f, 1f,
                "PS_SetMedXpTip".Translate());
            CB(l, "PS_SetNoDecay".Translate(), ref s.noPsyfocusDecay, "PS_SetNoDecayTip".Translate());

            Head(l, "PS_SetH_Breakthroughs".Translate());
            CB(l, "PS_SetBreakthroughs".Translate(), ref s.enlightenmentEnabled, "PS_SetBreakthroughsTip".Translate());
            if (TieringControl.MeditationAwakeningDisabled)
                ModOverrideNote(l, "PS_Ovr_MedAwaken".Translate());
            if (showBreak)
            {
                PushSub(l);
                FS(l, "PS_SetBreakChance".Translate((s.enlightenmentChance * 100f).ToString("F1")), ref s.enlightenmentChance, 0f, 0.15f,
                    "PS_SetBreakChanceTip".Translate());
                FS(l, "PS_SetBreakChanceTrans".Translate((s.transcendChance * 100f).ToString("F1")), ref s.transcendChance, 0f, 0.15f,
                    "PS_SetBreakChanceTransTip".Translate());
                FS(l, "PS_SetBreakSize".Translate((s.enlightenmentFrac * 100f).ToString("F0")), ref s.enlightenmentFrac, 0.2f, 1.5f,
                    "PS_SetBreakSizeTip".Translate());
                FS(l, "PS_SetFalloff".Translate(s.enlightenmentSaturationFactor.ToString("F2")), ref s.enlightenmentSaturationFactor, 0f, 1.5f,
                    "PS_SetFalloffTip".Translate());
                FS(l, "PS_SetGuarantee".Translate(s.awakenGuaranteeHours.ToString("F0")), ref s.awakenGuaranteeHours, 6f, 120f,
                    "PS_SetGuaranteeTip".Translate(), true);
                FS(l, "PS_SetPilgrimPity".Translate(s.pilgrimGuaranteeHours.ToString("F0")), ref s.pilgrimGuaranteeHours, 0f, 240f,
                    "PS_SetPilgrimPityTip".Translate(), true);
                FS(l, "PS_SetTransCurve".Translate((1f + s.transcendBreakthroughCurve).ToString("F2")), ref s.transcendBreakthroughCurve, 0f, 0.4f,
                    "PS_SetTransCurveTip".Translate());
                PopSub(l);
            }

            Head(l, "PS_SetH_TooLong".Translate());
            // Snapshot BEFORE the slider draws, so the row set below cannot change mid-frame.
            bool comaOff = s.ComaRiskOff;
            FS(l, comaOff ? "PS_SetSafeWindowOff".Translate().ToString()
                          : "PS_SetSafeWindow".Translate(s.comaSafeHours.ToString("F1")).ToString(),
                ref s.comaSafeHours, 0f, 24f, "PS_SetSafeWindowTip".Translate());
            if (!comaOff)
                FS(l, "PS_SetComaRisk".Translate((s.comaRiskPerHour * 100f).ToString("F0")), ref s.comaRiskPerHour, 0f, 0.25f,
                    "PS_SetComaRiskTip".Translate());

            Head(l, "PS_SetH_Cards".Translate());
            CB(l, "PS_SetRevealAll".Translate(), ref s.cardRevealAll, "PS_SetRevealAllTip".Translate());
            IS(l, "PS_SetCardCount".Translate(s.cardPickCount <= 0 ? "PS_SetCardCountAuto".Translate().ToString() : s.cardPickCount.ToString()), ref s.cardPickCount, 0, 8,
                "PS_SetCardCountTip".Translate());
            CB(l, "PS_SetCardRedeal".Translate(), ref s.cardRedeal, "PS_SetCardRedealTip".Translate());
            Hint(l, "PS_SetChooseLaterInfo".Translate(), false);

            Head(l, "PS_SetH_WhoBecomes".Translate());
            CB(l, "PS_SetEmpirePsylink".Translate(), ref s.empirePsylinkIntegrate, "PS_SetEmpirePsylinkTip".Translate());
            if (TieringControl.ExternalPsylinkAwakeningDisabled)
                ModOverrideNote(l, "PS_Ovr_ExtPsylink".Translate());
            CB(l, "PS_SetPsylinkGate".Translate(), ref s.gateUntieredPsylinks, "PS_SetPsylinkGateTip".Translate());
            if (TieringControl.PsylinkGateDisabled)
                ModOverrideNote(l, "PS_Ovr_PsylinkGate".Translate());
            else if (TieringControl.RandomAwakenedSpawnsDisabled)
                ModOverrideNote(l, "PS_Ovr_RandomSpawns".Translate());
            if (showGate)
            {
                PushSub(l);
                FS(l, "PS_SetSpawnChance".Translate((s.awakenedSpawnChance * 100f).ToString("F0")), ref s.awakenedSpawnChance, 0f, 1f,
                    "PS_SetSpawnChanceTip".Translate());
                CB(l, "PS_SetNoStartAwakened".Translate(), ref s.noAwakenedStartingPawns, "PS_SetNoStartAwakenedTip".Translate());
                PopSub(l);
            }

            Head(l, "PS_SetH_Transcendence".Translate());
            CB(l, "PS_SetTranscend".Translate(), ref s.transcendEnabled, "PS_SetTranscendTip".Translate());
            if (TieringControl.TranscendenceDisabled)
                ModOverrideNote(l, "PS_Ovr_Transcend".Translate());
            if (showTrans)
            {
                PushSub(l);
                FS(l, "PS_SetTransBase".Translate(s.transcendBaseHours.ToString("F0")), ref s.transcendBaseHours, 12f, 200f,
                    "PS_SetTransBaseTip".Translate(), true);
                FS(l, "PS_SetTransGrowth".Translate(s.transcendGrowth.ToString("F2")), ref s.transcendGrowth, 1.1f, 3f,
                    "PS_SetTransGrowthTip".Translate());
                IS(l, "PS_SetForgoPoints".Translate(s.transcendForgoPoints), ref s.transcendForgoPoints, 0, 12,
                    "PS_SetForgoPointsTip".Translate());
                PopSub(l);
            }
        }

        // Amber note under a setting row when a loaded mod's TieringOverrideDef has taken a path over.
        private static void ModOverrideNote(Listing_Standard l, string what)
        {
            GameFont pf = Text.Font;
            Text.Font = Text.TinyFontSupported ? GameFont.Tiny : GameFont.Small;
            string msg = "PS_SetOverrideNote".Translate(what, TieringControl.OwnerLabel);
            float w = l.ColumnWidth - SubIndent;
            Rect r = l.GetRect(Text.CalcHeight(msg, w));
            r.xMin += SubIndent;
            GUI.color = new Color(1f, 0.72f, 0.35f);
            Widgets.Label(r, msg);
            GUI.color = Color.white;
            Text.Font = pf;
            l.Gap(4f);
        }

        void TabPilgrimage(Listing_Standard l)
        {
            var s = Settings;
            if (TieringControl.PilgrimagesDisabled)
                ModOverrideNote(l, "PS_Ovr_Pilgrimages".Translate());

            Head(l, "PS_SetH_Altar".Translate());
            float days = s.pilgrimDailyMaxTicks > 0 ? (float)s.pilgrimMeditationTicks / s.pilgrimDailyMaxTicks : 0f;
            IS(l, "PS_SetAltarTotal".Translate((s.pilgrimMeditationTicks / 2500f).ToString("F1"), days.ToString("F1")), ref s.pilgrimMeditationTicks, 10000, 150000,
                "PS_SetAltarTotalTip".Translate());
            IS(l, "PS_SetAltarDaily".Translate((s.pilgrimDailyMaxTicks / 2500f).ToString("F1")), ref s.pilgrimDailyMaxTicks, 0, 60000,
                "PS_SetAltarDailyTip".Translate());
            IS(l, "PS_SetWaveInterval".Translate((s.pilgrimWaveIntervalTicks / 2500f).ToString("F1")), ref s.pilgrimWaveIntervalTicks, 5000, 120000,
                "PS_SetWaveIntervalTip".Translate());
            FS(l, "PS_SetWaveScale".Translate(s.pilgrimWavePointsScale.ToString("F2")), ref s.pilgrimWavePointsScale, 0.1f, 3f,
                "PS_SetWaveScaleTip".Translate());

            Head(l, "PS_SetH_AltarFocus".Translate());
            Hint(l, "PS_SetT3Throne".Translate(), false);
            string[][] focusOpts = {
                new[]{"PS_PilgrimThrone","PS_SetFocus_PilgrimThrone".Translate().ToString()},
                new[]{"PS_PilgrimAltar","PS_SetFocus_PilgrimAltar".Translate().ToString()},
                new[]{"MeditationSpot","PS_SetFocus_MeditationSpot".Translate().ToString()},
                new[]{"Throne","PS_SetFocus_Throne".Translate().ToString()},
                new[]{"GrandThrone","PS_SetFocus_GrandThrone".Translate().ToString()},
            };
            foreach (var opt in focusOpts)
                if (l.RadioButton(opt[1], s.pilgrimFocusDef == opt[0])) s.pilgrimFocusDef = opt[0];

            Head(l, "PS_SetH_Anima".Translate());
            IS(l, "PS_SetAnimaPerSite".Translate((s.animaPilgrimTicksPerSite / 2500f).ToString("F1")), ref s.animaPilgrimTicksPerSite, 10000, 150000,
                "PS_SetAnimaPerSiteTip".Translate());
            IS(l, "PS_SetAnimaT2".Translate(s.animaPilgrimT2Sites), ref s.animaPilgrimT2Sites, 1, 6,
                "PS_SetAnimaT2Tip".Translate());
            IS(l, "PS_SetAnimaT3".Translate(s.animaPilgrimT3Sites), ref s.animaPilgrimT3Sites, 1, 8,
                "PS_SetAnimaT3Tip".Translate());
        }

        void TabEnemies(Listing_Standard l)
        {
            var s = Settings;
            bool showEnemy = s.enemyTiersEnabled;   // frame-start snapshot (see TabLevels)
            Head(l, "PS_SetH_Enemies".Translate());
            CB(l, "PS_SetEnemyTiers".Translate(), ref s.enemyTiersEnabled, "PS_SetEnemyTiersTip".Translate());
            if (TieringControl.EnemyTiersDisabled)
                ModOverrideNote(l, "PS_Ovr_EnemyTiers".Translate());
            if (showEnemy)
            {
                PushSub(l);
                CB(l, "PS_SetEnemyT1".Translate(), ref s.enemyTier1, "PS_SetEnemyT1Tip".Translate());
                CB(l, "PS_SetEnemyT2".Translate(), ref s.enemyTier2, "PS_SetEnemyT2Tip".Translate());
                CB(l, "PS_SetEnemyT3".Translate(), ref s.enemyTier3, "PS_SetEnemyT3Tip".Translate());
                FS(l, "PS_SetEnemyFreq".Translate((s.enemyTierFreq * 100f).ToString("F0")), ref s.enemyTierFreq, 0f, 1f,
                    "PS_SetEnemyFreqTip".Translate());
                CB(l, "PS_SetEnemyAscension".Translate(), ref s.enemyAscension, "PS_SetEnemyAscensionTip".Translate());
                PopSub(l);
            }
        }

        void TabDisplay(Listing_Standard l)
        {
            var s = Settings;
            bool showFogMode = s.fogOfWar;   // frame-start snapshot (see TabLevels)

            Head(l, "PS_SetH_Tree".Translate());
            CB(l, "PS_SetFogOfWar".Translate(), ref s.fogOfWar, "PS_SetFogOfWarTip".Translate());
            if (showFogMode)
            {
                PushSub(l);
                CB(l, "PS_SetFogNext".Translate(), ref s.fogRevealNext, "PS_SetFogNextTip".Translate());
                PopSub(l);
            }
            CB(l, "PS_SetHidePaths".Translate(), ref s.hideUnlearnedPaths, "PS_SetHidePathsTip".Translate());
            CB(l, "PS_SetSkillFx".Translate(), ref s.skillFx, "PS_SetSkillFxTip".Translate());
            CB(l, "PS_SetMedBars".Translate(), ref s.medBars, "PS_SetMedBarsTip".Translate());

            Head(l, "PS_SetH_NoTakeBacks".Translate());
            CB(l, "PS_SetNoSkillReset".Translate(), ref s.disableSkillReset, "PS_SetNoSkillResetTip".Translate());
            CB(l, "PS_SetNoSpecReset".Translate(), ref s.disableSpecReset, "PS_SetNoSpecResetTip".Translate());

            Head(l, "PS_SetH_Speed".Translate());
            CB(l, "PS_SetPerfCache".Translate(), ref s.perfCaching, "PS_SetPerfCacheTip".Translate());
        }

        void TabAdvanced(Listing_Standard l)
        {
            var s = Settings;
            bool showCapRow = s.overrideVpeLevelCap;   // frame-start snapshot (see TabLevels)
            bool linksOff = s.disableSynergies;

            Head(l, "PS_SetH_LevelCap".Translate());
            CB(l, "PS_SetLevelCap".Translate(), ref s.overrideVpeLevelCap, "PS_SetLevelCapTip".Translate());
            if (showCapRow)
            {
                PushSub(l);
                IS(l, "PS_SetLevelCapVal".Translate(s.vpeLevelCap), ref s.vpeLevelCap, 30, 500, "PS_SetLevelCapValTip".Translate());
                PopSub(l);
            }
            ApplyVpeLevelCap();

            Head(l, "PS_SetH_OtherMods".Translate());
            CB(l, "PS_SetIsekai".Translate(), ref s.suppressIsekaiPsycastStats, "PS_SetIsekaiTip".Translate());

            Head(l, "PS_SetH_SynergyGraph".Translate());
            if (linksOff)
            {
                GUI.color = new Color(1f, 0.72f, 0.35f);
                l.Label("PS_SetSynDisabledNote".Translate());
                GUI.color = Color.white;
            }
            Hint(l, "PS_SetSynFrozen".Translate(), false);
            int tuned = PlayerTuning.Count;
            Hint(l, "PS_SetSynRetune".Translate()
                + (tuned > 0 ? " " + "PS_SetSynEditsActive".Translate(tuned) : new TaggedString("")), false);
            if (l.ButtonText("PS_SetSynRebuild".Translate()))
            {
                s.frozenSyn?.Clear();
                PsycastInfo.EnsureFrozen();
                Messages.Message("PS_MsgSynRebuilt".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            Head(l, "PS_SetH_BalanceEdits".Translate());
            Hint(l, "PS_SetBalanceInfo".Translate(), false);
            // OPEN BETA: the public button EXPORTS the player's edits as a shareable JSON (grouped per
            // tree and addon tree) to send the author. Non-destructive; nothing is baked or cleared.
            if (l.ButtonText(tuned > 0 ? "PS_SetShareN".Translate(tuned).ToString() : "PS_SetShare".Translate().ToString()))
            {
                if (tuned == 0)
                    Messages.Message("PS_MsgNoShare".Translate(), MessageTypeDefOf.RejectInput, false);
                else if (ManualBalance.ExportPlayerTuning(out string shareErr, out string sharePath, out int shared))
                    Find.WindowStack.Add(new Dialog_Confirm("PS_SetShareTitle".Translate(),
                        "PS_SetShareBody".Translate(shared, sharePath), () => { }));
                else
                    Messages.Message("PS_MsgShareFailed".Translate(shareErr), MessageTypeDefOf.RejectInput, false);
            }
            // Import a shared balance-edits JSON (from another player or a modpack creator) back into
            // the live overlay. Lists JSON files dropped into the SaveData/Psycasts2 folder.
            if (l.ButtonText("PS_SetImport".Translate()))
            {
                var files = ManualBalance.ListImportableFiles();
                if (files.Count == 0)
                    Messages.Message("PS_MsgNoImportFiles".Translate(ManualBalance.ImportFolder), MessageTypeDefOf.RejectInput, false);
                else
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (var file in files)
                    {
                        string fp = file;
                        opts.Add(new FloatMenuOption(Path.GetFileName(fp), () =>
                        {
                            if (ManualBalance.ImportPlayerTuning(fp, out string ierr, out int icount))
                            {
                                PsycastSynergiesMod.Instance?.WriteSettings();
                                Find.WindowStack.Add(new Dialog_Confirm("PS_SetImportTitle".Translate(),
                                    "PS_SetImportBody".Translate(icount, Path.GetFileName(fp)), () => { }));
                            }
                            else
                                Messages.Message("PS_MsgImportFailed".Translate(ierr), MessageTypeDefOf.RejectInput, false);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            }
            // Author flow, dev mode only: bake the edits into the mod's ManualBalance.json defaults.
            if (Prefs.DevMode && l.ButtonText(tuned > 0 ? "PS_SetBakeN".Translate(tuned).ToString() : "PS_SetBake".Translate().ToString()))
            {
                if (tuned == 0)
                    Messages.Message("PS_MsgNoBake".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                    Find.WindowStack.Add(new Dialog_Confirm("PS_SetBakeTitle".Translate(),
                        "PS_SetBakeBody".Translate(tuned),
                        () =>
                        {
                            if (ManualBalance.BakePlayerTuning(out string err, out int baked))
                                Messages.Message("PS_MsgBaked".Translate(baked), MessageTypeDefOf.TaskCompletion, false);
                            else
                                Messages.Message("PS_MsgBakeFailed".Translate(err), MessageTypeDefOf.RejectInput, false);
                        }));
            }
            if (l.ButtonText("PS_SetDiscard".Translate()))
            {
                if (tuned == 0)
                    Messages.Message("PS_MsgNoDiscard".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                    Find.WindowStack.Add(new Dialog_Confirm("PS_SetDiscardTitle".Translate(),
                        "PS_SetDiscardBody".Translate(tuned),
                        () => { PlayerTuning.ResetAll(); PsycastSynergiesMod.Instance?.WriteSettings(); }));
            }
        }

    }
}
