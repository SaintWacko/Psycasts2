#nullable disable
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using VanillaPsycastsExpanded;
using VanillaPsycastsExpanded.UI;
using Verse;

namespace PsycastSynergies
{
    // ============================== PSYCASTER ROSTER ==============================
    // A main-tab roster of every colonist (plus prisoners and slaves) that answers three questions in
    // one screen: who are my psycasters, what are their stats, and who is climbing toward Awakening /
    // a tier-up pilgrimage / Transcendence - and WHEN do they arrive.
    //
    // Replaces the old always-on psycast tab (Patch_DormantTab, removed): a dormant pawn had a whole
    // ITab to say "not a psycaster yet, here is one bar", which is a lot of chrome for one number and
    // could only ever show ONE pawn at a time. The roster shows the whole colony and can be sorted.
    //
    // The "expected" column is derived from MeditationData.medDayAvg - a rolling average of meditation
    // hours per day, folded in at the daily reset. When there is nothing to extrapolate from we say so
    // ("No estimate" / "Storyteller only") instead of inventing a date: an ETA nobody can meet reads
    // as a bug, and this system already has enough ways to stall quietly.
    //
    // Prisoners and slaves are DISPLAY ONLY. MeditationSystem accrues on FreeColonists*, which filters
    // on HostFaction == null, so neither accrues a tick - but a captured pawn may already BE a
    // psycaster, so their tier, level, paths and stats are all real and worth showing.
    internal enum MsKind { None, Awakening, Pilgrimage, Underway, Transcend, Blocked }

    internal class RosterRow
    {
        internal Pawn pawn;
        internal int group;                       // 0 psycasters, 1 on the path, 2 not progressing
        internal string name, sub, flag;
        internal Color flagCol;
        internal string tier, tierSub; internal Color tierCol;
        internal string level, paths;
        internal string med, medSub; internal Color medCol;
        internal MsKind kind;
        internal string msLabel, msValue; internal float msFrac; internal Color msCol;
        internal string eta, etaSub; internal Color etaCol;
        internal string tip;
        internal float sort;                      // days until arrival; float.MaxValue = unknown
        internal float height;
    }

    // Row model builder. Everything a row draws is a finished string built HERE, at most a few times a
    // second - Draw never translates, concatenates or probes a hediff set.
    internal static class PsycasterRoster
    {
        private static readonly List<RosterRow> rows = new List<RosterRow>();
        private static readonly List<Pawn> scratch = new List<Pawn>();
        private static readonly Dictionary<Pawn, int> pilgrimTicks = new Dictionary<Pawn, int>();
        private static int builtFrame = -999, dirtyFrame = -1;
        private static TimeAssignmentDef medAssign; private static bool medAssignDone;

        internal static List<RosterRow> Rows => rows;
        internal static int NPsycasters, NPath, NStalled;
        internal static float ColonyHoursToday;

        internal static void Invalidate() { dirtyFrame = Time.frameCount; builtFrame = -999; }

        internal static void EnsureBuilt(bool captives, bool hideIncapable)
        {
            // NEVER rebuild in the same frame an invalidation arrived. A checkbox flips on the MouseUp
            // pass, and OnGUI runs several passes per frame - rebuilding immediately would change the
            // ROW COUNT (and with it the number of ButtonInvisible controls) between two passes of one
            // frame, which is the same IMGUI state corruption that once blanked the settings page.
            if (Time.frameCount == dirtyFrame && rows.Count > 0) return;
            if (Time.frameCount - builtFrame < 30) return;   // ~0.5 s at 60 fps; the window redraws far more often
            builtFrame = Time.frameCount;
            Build(captives, hideIncapable);
        }

        private static TimeAssignmentDef MedAssign
        {
            get
            {
                if (!medAssignDone) { medAssignDone = true; medAssign = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("Meditate"); }
                return medAssign;
            }
        }

        private static bool Tracked(Pawn p, bool captives)
        {
            if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Humanlike) return false;
            if (ModsConfig.AnomalyActive && p.IsSubhuman) return false;
            if (p.IsPrisonerOfColony || p.IsSlaveOfColony) return captives;
            if (p.IsQuestLodger()) return false;             // guests are not ours to manage
            return p.Faction != null && p.Faction.IsPlayer && p.HostFaction == null;
        }

        private static void Collect(List<Pawn> src, bool captives)
        {
            if (src == null) return;
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i];
                if (!Tracked(p, captives) || scratch.Contains(p)) continue;
                scratch.Add(p);
            }
        }

        // Pilgrim -> ticks left on the tightest live site of their pilgrimage (-1 when unknown).
        private static void ScanQuests()
        {
            pilgrimTicks.Clear();
            var quests = Find.QuestManager?.QuestsListForReading;
            if (quests == null) return;
            for (int i = 0; i < quests.Count; i++)
            {
                var q = quests[i];
                if (q == null || (q.State != QuestState.Ongoing && q.State != QuestState.NotYetAccepted)) continue;
                var parts = q.PartsListForReading;
                if (parts == null) continue;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is QuestPart_PilgrimMeditation mp && mp.pilgrim != null)
                        pilgrimTicks[mp.pilgrim] = SiteTicks(mp.site, int.MaxValue);
                    else if (parts[j] is QuestPart_PilgrimJourney jp && jp.pilgrim != null)
                    {
                        int best = int.MaxValue;
                        if (jp.sites != null)
                            for (int k = 0; k < jp.sites.Count; k++) best = SiteTicks(jp.sites[k], best);
                        pilgrimTicks[jp.pilgrim] = best;
                    }
                }
            }
        }

        private static int SiteTicks(Site site, int best)
        {
            if (site == null || site.Destroyed) return best;
            var t = site.GetComponent<TimeoutComp>();
            if (t == null || !t.Active) return best;
            return t.TicksLeft < best ? t.TicksLeft : best;
        }

        private static void Build(bool captives, bool hideIncapable)
        {
            rows.Clear(); scratch.Clear();
            NPsycasters = 0; NPath = 0; NStalled = 0; ColonyHoursToday = 0f;
            var gc = GameComponent_PsycastSynergies.Instance;
            var s = PsycastSynergiesMod.Settings;
            if (gc == null || s == null) return;

            Collect(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonistsAndPrisoners, captives);
            if (captives) Collect(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_SlavesOfColony, true);
            ScanQuests();

            for (int i = 0; i < scratch.Count; i++)
            {
                var row = BuildRow(scratch[i], gc, s, hideIncapable);
                if (row == null) continue;
                rows.Add(row);
                if (row.group == 0) NPsycasters++; else if (row.group == 1) NPath++; else NStalled++;
            }
            rows.Sort(Compare);
        }

        private static int Compare(RosterRow a, RosterRow b)
        {
            if (a.group != b.group) return a.group - b.group;
            if (!Mathf.Approximately(a.sort, b.sort)) return a.sort < b.sort ? -1 : 1;
            return string.Compare(a.name, b.name, System.StringComparison.CurrentCulture);
        }

        private static RosterRow BuildRow(Pawn p, GameComponent_PsycastSynergies gc, PsycastSynergiesSettings s, bool hideIncapable)
        {
            var psy = p.Psycasts();
            var med = gc.GetMed(p, false);
            bool captive = p.IsPrisonerOfColony || p.IsSlaveOfColony;
            bool tooYoung = p.ageTracker != null && p.ageTracker.AgeBiologicalYears < 13;
            if (psy == null && (tooYoung || (hideIncapable && (captive || !MeditationRoute(s)))))
                return null;

            var r = new RosterRow { pawn = p, name = p.Name.ToStringShort };
            int tier = med?.tier ?? EnlightenmentTier.TierOf(p);
            ColonyHoursToday += (med?.todayTicks ?? 0) / 2500f;

            // ---- identity ----
            if (psy != null)
            {
                r.group = 0;
                var unlocked = psy.unlockedPaths;
                r.sub = unlocked != null && unlocked.Count > 0 ? unlocked[0].LabelCap : "PS_RosterNoPath".Translate().ToString();
                r.paths = PathList(unlocked);
                r.level = psy.level.ToString();
            }
            else
            {
                bool moving = (med?.awakenMeditationTicks ?? 0) > 0 || (med?.medDayAvg ?? 0f) > 0f;
                r.group = moving && !captive ? 1 : 2;
                r.sub = captive
                    ? (p.IsPrisonerOfColony ? "PS_RosterPrisoner".Translate().ToString() : "PS_RosterSlave".Translate().ToString())
                    : FocusLabel(med);
                r.paths = "-"; r.level = "-";
            }

            r.tier = tier > 0 ? EnlightenmentTier.Name(tier) : "PS_RosterDormant".Translate().ToString();
            r.tierSub = tier > 0 ? "PS_RosterTierRoman".Translate(RomanNumerals.ToRoman(tier)).ToString() : null;
            r.tierCol = tier <= 0 ? Palette.TextDim
                : tier == 1 ? Palette.Accent
                : tier == 2 ? Palette.Gold
                : tier == 3 ? new Color(0.94f, 0.84f, 0.60f)
                : new Color(0.72f, 0.5f, 0.95f);

            // Psyfocus / neural heat / sensitivity deliberately NOT shown here: they are live combat
            // numbers that belong on the pawn's own psycast tab, and they told you nothing about the
            // question this window exists to answer (who is climbing, and when do they arrive).

            // ---- meditation state ----
            BuildMedState(r, p, med, captive);

            // ---- milestone + arrival ----
            BuildMilestone(r, p, med, psy, s, tier, captive);

            // ---- attention flags ----
            if (med != null && med.pendingPick > 0)
            {
                r.flag = "PS_RosterFlagPick".Translate().ToString(); r.flagCol = Palette.Gold;
            }
            else if (psy != null)
            {
                int pts = gc.GetSpec(p, false)?.points ?? 0;
                if (pts > 0) { r.flag = "PS_RosterFlagPoints".Translate(pts).ToString(); r.flagCol = Palette.Gold; }
            }
            if (r.flag == null && ComaTicksLeft(p) > 0)
            {
                r.flag = "PS_RosterFlagComa".Translate(ComaTicksLeft(p).ToStringTicksToPeriod()).ToString();
                r.flagCol = Palette.Bad;
            }
            return r;
        }

        private static bool MeditationRoute(PsycastSynergiesSettings s)
            => s.enlightenmentEnabled && !TieringControl.MeditationAwakeningDisabled;

        private static string PathList(List<PsycasterPathDef> paths)
        {
            if (paths == null || paths.Count == 0) return "-";
            if (paths.Count == 1) return paths[0].LabelCap;
            if (paths.Count == 2) return paths[0].LabelCap + ", " + paths[1].LabelCap;
            return paths[0].LabelCap + ", " + paths[1].LabelCap + " +" + (paths.Count - 2);
        }

        private static string FocusLabel(MeditationData med)
        {
            if (med == null || string.IsNullOrEmpty(med.defaultFocus)) return "PS_RosterNoFocus".Translate().ToString();
            var d = DefDatabase<MeditationFocusDef>.GetNamedSilentFail(med.defaultFocus);
            return d == null ? "PS_RosterNoFocus".Translate().ToString() : "PS_RosterFocus".Translate(d.LabelCap).ToString();
        }

        private static int ComaTicksLeft(Pawn p)
        {
            var def = MeditationDefOf.PsychicComa;
            if (def == null || p.health?.hediffSet == null) return 0;
            var h = p.health.hediffSet.GetFirstHediffOfDef(def) as HediffWithComps;
            return h?.TryGetComp<HediffComp_Disappears>()?.ticksToDisappear ?? 0;
        }

        private static void BuildMedState(RosterRow r, Pawn p, MeditationData med, bool captive)
        {
            if (captive)
            {
                r.med = "PS_MedCannot".Translate().ToString(); r.medCol = Palette.TextDim;
                r.medSub = p.IsPrisonerOfColony ? "PS_RosterPrisoner".Translate().ToString() : "PS_RosterSlave".Translate().ToString();
                return;
            }
            if (ComaTicksLeft(p) > 0)
            {
                r.med = "PS_MedComa".Translate().ToString(); r.medCol = Palette.Bad;
            }
            else if (PilgrimRouting.IsPilgrimageMap(p.Map))
            {
                r.med = "PS_MedPilgrimage".Translate().ToString(); r.medCol = Palette.Gold;
            }
            else if (MeditationSystem.IsActivelyMeditating(p))
            {
                r.med = "PS_MedNow".Translate().ToString(); r.medCol = Palette.Good;
            }
            else if (!Scheduled(p))
            {
                r.med = "PS_MedNotScheduled".Translate().ToString(); r.medCol = Palette.TextDim;
            }
            else { r.med = "PS_MedIdle".Translate().ToString(); r.medCol = Palette.TextDim; }

            float perDay = (med?.medDayAvg ?? 0f) / 2500f;
            r.medSub = perDay > 0.05f
                ? "PS_MedPerDay".Translate(perDay.ToString("F1")).ToString()
                : "PS_MedToday".Translate(((med?.todayTicks ?? 0) / 2500f).ToString("F1")).ToString();
        }

        private static bool Scheduled(Pawn p)
        {
            var def = MedAssign;
            var tt = p.timetable;
            if (def == null || tt?.times == null) return false;
            for (int i = 0; i < tt.times.Count; i++) if (tt.times[i] == def) return true;
            return false;
        }

        private static void BuildMilestone(RosterRow r, Pawn p, MeditationData med, Hediff_PsycastAbilities psy,
                                           PsycastSynergiesSettings s, int tier, bool captive)
        {
            r.sort = float.MaxValue;
            r.msCol = Palette.Accent;
            float curTicks = 0f, needTicks = 0f;

            // A psylink our system never adopted (Empire, anima, another mod) climbs the SAME awakening ramp
            // as a pawn with no psylink at all, so it reports the same milestone rather than "none".
            if (psy == null || !MeditationSystem.IsAwakened(p, med))
            {
                if (!MeditationRoute(s))
                {
                    Set(r, MsKind.Blocked, "PS_MsAwakening".Translate(), "PS_MsOtherRoute".Translate(), 0f, Palette.TextDim);
                    r.eta = "PS_EtaOtherRoute".Translate(); r.etaCol = Palette.TextDim;
                    return;
                }
                curTicks = med?.awakenMeditationTicks ?? 0;
                needTicks = s.awakenGuaranteeHours * 2500f;
                r.kind = MsKind.Awakening;
                r.msLabel = "PS_MsAwakening".Translate();
                r.msCol = Palette.Accent;
            }
            else if (tier >= 3)
            {
                if (!s.transcendEnabled || TieringControl.TranscendenceDisabled)
                {
                    Set(r, MsKind.Blocked, "PS_MsTranscendence".Translate(), "PS_MsDisabled".Translate(), 0f, Palette.TextDim);
                    r.eta = "PS_EtaNone".Translate(); r.etaCol = Palette.TextDim;
                    return;
                }
                curTicks = med?.transcendTicks ?? 0;
                needTicks = MeditationSystem.TranscendThreshold(tier + 1, s);
                r.kind = MsKind.Transcend;
                r.msLabel = EnlightenmentTier.Name(tier + 1);
                r.msCol = new Color(0.72f, 0.5f, 0.95f);
            }
            else if (tier >= 1)
            {
                r.msCol = Palette.Gold;
                if (TieringControl.PilgrimagesDisabled)
                {
                    Set(r, MsKind.Blocked, "PS_MsPilgrimage".Translate(EnlightenmentTier.Name(tier + 1)),
                        "PS_MsDisabled".Translate(), 0f, Palette.TextDim);
                    r.eta = "PS_EtaNone".Translate(); r.etaCol = Palette.TextDim;
                    return;
                }
                if (MeditationSystem.PilgrimQuestOngoing(tier + 1))
                {
                    r.kind = MsKind.Underway;
                    r.msLabel = "PS_MsUnderway".Translate();
                    r.msValue = EnlightenmentTier.Name(tier + 1);
                    r.msFrac = 1f;
                    int left = pilgrimTicks.TryGetValue(p, out int tl) ? tl : -1;
                    if (left > 0 && left != int.MaxValue)
                    {
                        r.eta = "PS_EtaDays".Translate((left / 60000f).ToString("F0"));
                        r.etaSub = "PS_EtaSiteFades".Translate();
                        r.sort = left / 60000f;
                    }
                    else r.eta = "PS_EtaUnderway".Translate();
                    r.etaCol = Palette.Stat;
                    return;
                }
                curTicks = med?.pilgrimTicks ?? 0;
                needTicks = MeditationSystem.PilgrimPityThresholdTicks(tier, s);
                r.kind = MsKind.Pilgrimage;
                r.msLabel = "PS_MsPilgrimage".Translate(EnlightenmentTier.Name(tier + 1));
            }
            else
            {
                // A psycaster with no tier at all (an outside psylink our system has not adopted).
                Set(r, MsKind.None, "PS_MsNone".Translate(), null, 0f, Palette.TextDim);
                r.eta = "PS_EtaNone".Translate(); r.etaCol = Palette.TextDim;
                return;
            }

            // Guarantee switched off entirely: the climb has no finish line, only the storyteller.
            if (needTicks <= 0f)
            {
                r.msValue = "PS_MsChanceOnly".Translate();
                r.msFrac = 0f;
                r.eta = r.kind == MsKind.Awakening ? "PS_EtaChanceOnly".Translate() : "PS_EtaStoryteller".Translate();
                r.etaCol = Palette.TextDim;
                return;
            }

            r.msValue = "PS_BarHoursFmt".Translate((curTicks / 2500f).ToString("F1"), (needTicks / 2500f).ToString("F0"));
            r.msFrac = Mathf.Clamp01(curTicks / needTicks);

            if (captive)
            {
                r.eta = "PS_EtaNotAccruing".Translate(); r.etaCol = Palette.TextDim;
                r.tip = "PS_RosterCaptiveTip".Translate();
                return;
            }
            BuildEta(r, med, needTicks - curTicks);
        }

        private static void Set(RosterRow r, MsKind k, string label, string val, float frac, Color c)
        {
            r.kind = k; r.msLabel = label; r.msValue = val; r.msFrac = frac; r.msCol = c;
        }

        // Arrival estimate from the rolling meditation average. No rate, no estimate - we never guess.
        private static void BuildEta(RosterRow r, MeditationData med, float remainingTicks)
        {
            if (remainingTicks <= 0f)
            {
                r.eta = "PS_EtaImminent".Translate(); r.etaCol = Palette.Good; r.sort = 0f;
                return;
            }
            float ratePerDay = med?.medDayAvg ?? 0f;
            bool provisional = false;
            if (ratePerDay <= 0f && (med?.todayTicks ?? 0) > 0) { ratePerDay = med.todayTicks; provisional = true; }
            if (ratePerDay <= 0f)
            {
                r.eta = r.kind == MsKind.Awakening ? "PS_EtaNone".Translate() : "PS_EtaStoryteller".Translate();
                r.etaCol = Palette.TextDim;
                return;
            }

            float days = remainingTicks / ratePerDay;
            r.sort = days;
            r.eta = days < 1f
                ? "PS_EtaHours".Translate((days * 24f).ToString("F0"))
                : "PS_EtaDays".Translate(days < 10f ? days.ToString("F1") : days.ToString("F0"));
            r.etaCol = days <= 3f ? Palette.Good : Palette.Stat;
            r.etaSub = provisional
                ? "PS_EtaBasisToday".Translate()
                : "PS_EtaBasisRate".Translate((ratePerDay / 2500f).ToString("F1"));
            // The date is a nicety, and LongLatOf throws on a pawn with no valid tile (in a pod, mid-
            // transition). Never let the tooltip take the whole window down with it.
            try
            {
                long arrive = Find.TickManager.TicksAbs + (long)(days * 60000f);
                r.tip = "PS_EtaTip".Translate(GenDate.DateFullStringWithHourAt(arrive, Find.WorldGrid.LongLatOf(r.pawn.Tile)));
            }
            catch { r.tip = null; }
        }
    }

    // ============================== THE WINDOW ==============================
    public class MainTabWindow_Psycasters : MainTabWindow
    {
        private Pawn selected;
        private Vector2 scroll;
        private static readonly Color Hair = new Color(1f, 1f, 1f, 0.055f);
        private static readonly Color AltRow = new Color(1f, 1f, 1f, 0.02f);
        private static readonly Color SelRow = new Color(1f, 1f, 1f, 0.075f);

        private const float HeadH = 26f, GroupH = 24f, BarH = 6f, ActionH = 34f;
        private const float MinW = 700f, MinH = 360f;

        // Tiny is silently coerced to Small when tiny text is unsupported, so every height must be
        // measured off the font that will actually render (see ui lore).
        private static GameFont SubFont => Text.TinyFontSupported ? GameFont.Tiny : GameFont.Small;
        private static float SubH => Mathf.Ceil(Text.LineHeightOf(SubFont)) + 1f;
        private static float NameH => Mathf.Ceil(Text.LineHeightOf(GameFont.Small)) + 2f;

        public MainTabWindow_Psycasters()
        {
            // A main tab is normally welded to a screen edge. This one is a floating window: drag it by
            // any empty area, resize from the bottom-right corner, close with the X. The rect is kept in
            // settings, so it opens where the player last left it instead of snapping back every time.
            draggable = true;
            resizeable = true;
            doCloseX = true;
            preventCameraMotion = false;
        }

        public override Vector2 RequestedTabSize => new Vector2(
            Mathf.Min(1180f, UI.screenWidth - 40f),
            Mathf.Min(720f, UI.screenHeight - 120f));

        // Restore the remembered rect (clamped - the player may have changed resolution since), else
        // centre the default size. Deliberately does NOT call base: base re-anchors to a screen edge.
        protected override void SetInitialSizeAndPosition()
        {
            var s = PsycastSynergiesMod.Settings;
            Vector2 want = InitialSize;
            Rect r = s != null && s.rosterW > 0f
                ? new Rect(s.rosterX, s.rosterY, s.rosterW, s.rosterH)
                : new Rect((UI.screenWidth - want.x) / 2f, (UI.screenHeight - 35f - want.y) / 2f, want.x, want.y);
            r.width = Mathf.Clamp(r.width, MinW, UI.screenWidth);
            r.height = Mathf.Clamp(r.height, MinH, UI.screenHeight - 35f);
            r.x = Mathf.Clamp(r.x, 0f, Mathf.Max(0f, UI.screenWidth - r.width));
            r.y = Mathf.Clamp(r.y, 0f, Mathf.Max(0f, UI.screenHeight - 35f - r.height));
            windowRect = r.Rounded();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            PsycasterRoster.Invalidate();
            if (selected != null && (selected.Dead || selected.Destroyed)) selected = null;
        }

        // Remember where the player put it. Only writes when the rect actually moved.
        public override void PostClose()
        {
            base.PostClose();
            var s = PsycastSynergiesMod.Settings;
            if (s == null) return;
            if (Mathf.Approximately(s.rosterX, windowRect.x) && Mathf.Approximately(s.rosterY, windowRect.y)
                && Mathf.Approximately(s.rosterW, windowRect.width) && Mathf.Approximately(s.rosterH, windowRect.height))
                return;
            s.rosterX = windowRect.x; s.rosterY = windowRect.y;
            s.rosterW = windowRect.width; s.rosterH = windowRect.height;
            LoadedModManager.GetMod<PsycastSynergiesMod>()?.WriteSettings();
        }

        public override void DoWindowContents(Rect inRect)
        {
            var s = PsycastSynergiesMod.Settings;
            if (s == null) return;
            // Vanilla's resizer floor is 150x150, which this layout cannot honour. Clamping here is
            // idempotent (a floor, not an increment), so repeating it across a frame's passes is safe.
            if (windowRect.width < MinW || windowRect.height < MinH)
            {
                windowRect.width = Mathf.Max(windowRect.width, MinW);
                windowRect.height = Mathf.Max(windowRect.height, MinH);
            }
            var prevFont = Text.Font; var prevAnchor = Text.Anchor; var prevColor = GUI.color;
            try { Draw(inRect, s); }
            catch (System.Exception e)
            {
                Log.Warning("[Psycasts²] psycaster roster draw failed: " + e);
            }
            finally { Text.Font = prevFont; Text.Anchor = prevAnchor; GUI.color = prevColor; }
        }

        private void Draw(Rect inRect, PsycastSynergiesSettings s)
        {
            PsycasterRoster.EnsureBuilt(s.rosterIncludeCaptives, s.rosterHideIncapable);
            var rows = PsycasterRoster.Rows;
            float y = inRect.y;

            // ---- title + blurb ----
            Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 40f, 32f), "PS_RosterTitle".Translate());
            y += 32f;
            Text.Font = GameFont.Small; GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f),
                "PS_RosterBlurb".Translate(PsycasterRoster.NPsycasters, PsycasterRoster.NPath, PsycasterRoster.NStalled));
            GUI.color = Color.white;
            y += 24f;

            // ---- options ----
            bool captives = s.rosterIncludeCaptives, hide = s.rosterHideIncapable;
            var optRect = new Rect(inRect.x, y, Mathf.Min(300f, inRect.width * 0.3f), 24f);
            Widgets.CheckboxLabeled(optRect, "PS_RosterOptCaptives".Translate(), ref captives);
            var optRect2 = new Rect(optRect.xMax + 20f, y, Mathf.Min(320f, inRect.width * 0.32f), 24f);
            Widgets.CheckboxLabeled(optRect2, "PS_RosterOptHide".Translate(), ref hide);
            if (captives != s.rosterIncludeCaptives || hide != s.rosterHideIncapable)
            {
                s.rosterIncludeCaptives = captives; s.rosterHideIncapable = hide;
                PsycasterRoster.Invalidate();
                LoadedModManager.GetMod<PsycastSynergiesMod>()?.WriteSettings();
            }
            y += 28f;

            // ---- geometry ----
            float bottom = inRect.yMax - ActionH - 30f;
            var cols = new Cols(inRect.x, inRect.width);
            DrawHeader(new Rect(inRect.x, y, inRect.width, HeadH), cols);
            y += HeadH;

            var outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(60f, bottom - y));
            float viewH = 0f; int lastGroup = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].group != lastGroup) { lastGroup = rows[i].group; viewH += GroupH; }
                rows[i].height = RowHeight(rows[i]);
                viewH += rows[i].height;
            }
            var viewRect = new Rect(0f, 0f, outRect.width - 18f, viewH + 4f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            var scrollCols = new Cols(0f, viewRect.width);
            float ry = 0f; lastGroup = -1; int alt = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.group != lastGroup)
                {
                    lastGroup = row.group; alt = 0;
                    DrawGroup(new Rect(0f, ry, viewRect.width, GroupH), row.group);
                    ry += GroupH;
                }
                // Deliberately NOT culled to the visible band: the scroll position can move between
                // passes of a single frame, so a visibility test would register a different number of
                // controls per pass. Rows are labels and one invisible button; drawing them all is cheap.
                DrawRow(new Rect(0f, ry, viewRect.width, row.height), row, scrollCols, alt++ % 2 == 1);
                ry += row.height;
            }
            Widgets.EndScrollView();

            // ---- action bar ----
            DrawActions(new Rect(inRect.x, inRect.yMax - ActionH - 26f, inRect.width, ActionH));
            DrawFooter(new Rect(inRect.x, inRect.yMax - 22f, inRect.width, 20f), s);
        }

        private float RowHeight(RosterRow r) => NameH + SubH + (r.flag != null ? SubH : 0f) + 8f;

        // Column strip. Paths is the first thing dropped when the window is narrow.
        private struct Cols
        {
            public float pawn, tier, level, paths, med, ms, eta;
            public float pawnW, tierW, levelW, pathsW, medW, msW, etaW;
            public bool showPaths;

            public Cols(float x, float w)
            {
                showPaths = w >= 900f;
                pawnW = 200f; tierW = 100f; levelW = 56f; pathsW = showPaths ? 190f : 0f;
                medW = 120f; etaW = 126f;
                msW = Mathf.Max(150f, w - (pawnW + tierW + levelW + pathsW + medW + etaW) - 8f);
                pawn = x; tier = pawn + pawnW; level = tier + tierW; paths = level + levelW;
                med = paths + pathsW; ms = med + medW; eta = ms + msW;
            }
        }

        private void DrawHeader(Rect r, Cols c)
        {
            Widgets.DrawBoxSolid(r, Palette.BGD);
            GUI.color = Hair;
            Widgets.DrawLineHorizontal(r.x, r.y, r.width);
            Widgets.DrawLineHorizontal(r.x, r.yMax - 1f, r.width);
            GUI.color = Palette.TextDim;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;   // a wrapped header spills out of the band instead of clipping
            Head(r, c.pawn, c.pawnW, "PS_ColPawn".Translate(), false);
            Head(r, c.tier, c.tierW, "PS_ColTier".Translate(), false);
            Head(r, c.level, c.levelW, "PS_ColLevel".Translate(), true);
            if (c.showPaths) Head(r, c.paths, c.pathsW, "PS_ColPaths".Translate(), false);
            Head(r, c.med, c.medW, "PS_ColMeditation".Translate(), false);
            Head(r, c.ms, c.msW, "PS_ColMilestone".Translate(), false);
            Head(r, c.eta, c.etaW, "PS_ColExpected".Translate(), false);
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
        }

        private void Head(Rect band, float x, float w, string label, bool right)
        {
            if (w <= 0f) return;
            Text.Anchor = right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x + 6f, band.y, w - 12f, band.height), label);
        }

        private void DrawGroup(Rect r, int group)
        {
            Widgets.DrawBoxSolid(r, Palette.BGD);
            GUI.color = Hair;
            Widgets.DrawLineHorizontal(r.x, r.y, r.width);
            Widgets.DrawLineHorizontal(r.x, r.yMax - 1f, r.width);
            GUI.color = Palette.TextDim;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(r.x + 6f, r.y, r.width - 12f, r.height),
                group == 0 ? "PS_GrpPsycasters".Translate()
                : group == 1 ? "PS_GrpOnThePath".Translate()
                : "PS_GrpNotProgressing".Translate());
            if (group == 2)
            {
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(r.x + 6f, r.y, r.width - 12f, r.height), "PS_GrpNotProgressingWhy".Translate());
            }
            Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
        }

        private void DrawRow(Rect r, RosterRow row, Cols c, bool alt)
        {
            bool sel = ReferenceEquals(row.pawn, selected);
            if (sel) Widgets.DrawBoxSolid(r, SelRow);
            else if (alt) Widgets.DrawBoxSolid(r, AltRow);
            if (!sel && Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            GUI.color = Hair;
            Widgets.DrawLineHorizontal(r.x, r.yMax - 1f, r.width);
            GUI.color = Color.white;

            float top = r.y + 4f;
            float subY = top + NameH;

            // pawn
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(c.pawn + 6f, top, c.pawnW - 12f, NameH), row.name);
            Text.Font = SubFont; GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(c.pawn + 6f, subY, c.pawnW - 12f, SubH), row.sub);
            if (row.flag != null)
            {
                GUI.color = row.flagCol;
                Widgets.Label(new Rect(c.pawn + 6f, subY + SubH, c.pawnW - 12f, SubH), row.flag);
            }
            GUI.color = Color.white;

            // tier
            Text.Font = GameFont.Small; GUI.color = row.tierCol;
            Widgets.Label(new Rect(c.tier + 6f, top, c.tierW - 12f, NameH), row.tier);
            GUI.color = Palette.TextDim; Text.Font = SubFont;
            if (row.tierSub != null) Widgets.Label(new Rect(c.tier + 6f, subY, c.tierW - 12f, SubH), row.tierSub);
            GUI.color = Color.white;

            // level
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperRight;
            Num(c.level, c.levelW, top, row.level);
            Text.Anchor = TextAnchor.UpperLeft;
            if (c.showPaths)
            {
                GUI.color = Palette.TextDim; Text.Font = SubFont;
                Widgets.Label(new Rect(c.paths + 6f, top + 2f, c.pathsW - 12f, NameH + SubH), row.paths);
                GUI.color = Color.white;
            }

            // meditation
            Text.Font = GameFont.Small; GUI.color = row.medCol;
            Widgets.Label(new Rect(c.med + 6f, top, c.medW - 12f, NameH), row.med);
            GUI.color = Palette.TextDim; Text.Font = SubFont;
            if (row.medSub != null) Widgets.Label(new Rect(c.med + 6f, subY, c.medW - 12f, SubH), row.medSub);
            GUI.color = Color.white;

            // milestone: caption + value over a thin bar
            if (row.msLabel != null)
            {
                Text.Font = SubFont;
                Text.WordWrap = false;
                var cap = new Rect(c.ms + 6f, top + 1f, c.msW - 12f, SubH);
                // The caption and the value share one line, so the caption gets the width the value does
                // not need - otherwise a long milestone name ran straight under "0.0 / 60h".
                float valW = row.msValue != null ? Mathf.Ceil(Text.CalcSize(row.msValue).x) + 8f : 0f;
                float capW = Mathf.Max(24f, cap.width - valW);
                string lbl = row.msLabel;
                if (Text.CalcSize(lbl).x > capW) lbl = lbl.Truncate(capW);
                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(cap.x, cap.y, capW, cap.height), lbl);
                if (row.msValue != null)
                {
                    Text.Anchor = TextAnchor.UpperRight;
                    GUI.color = Palette.Stat;
                    Widgets.Label(cap, row.msValue);
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                Text.WordWrap = true;
                GUI.color = Color.white;
                var bar = new Rect(cap.x, cap.yMax + 2f, cap.width, BarH);
                Widgets.DrawBoxSolid(bar, Palette.BGD);
                if (row.msFrac > 0f)
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, Mathf.Max(3f, bar.width * row.msFrac), bar.height), row.msCol);
            }

            // expected
            Text.Font = GameFont.Small; GUI.color = row.etaCol;
            Widgets.Label(new Rect(c.eta + 6f, top, c.etaW - 12f, NameH), row.eta ?? "");
            if (row.etaSub != null)
            {
                Text.Font = SubFont; GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(c.eta + 6f, subY, c.etaW - 12f, SubH), row.etaSub);
            }
            GUI.color = Color.white;

            if (row.tip != null && Mouse.IsOver(r)) TooltipHandler.TipRegion(r, row.tip);
            if (Widgets.ButtonInvisible(r, false))
            {
                selected = row.pawn;
                if (Event.current.clickCount >= 2) OpenPsycastTab(row.pawn);
            }
        }

        private void Num(float x, float w, float y, string val)
        {
            if (w <= 0f) return;
            Widgets.Label(new Rect(x + 6f, y, w - 12f, NameH), val);
        }

        private void DrawActions(Rect r)
        {
            GUI.color = Hair;
            Widgets.DrawLineHorizontal(r.x, r.y, r.width);
            GUI.color = Color.white;
            var p = selected;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Palette.TextDim;
            float lblW = 200f;
            Widgets.Label(new Rect(r.x + 2f, r.y + 4f, lblW, r.height - 4f),
                p == null ? "PS_RosterSelectPrompt".Translate() : "PS_RosterSelected".Translate(p.Name.ToStringShort));
            GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft;

            float by = r.y + 5f, bh = r.height - 8f;
            bool live = p != null && !p.Dead;

            var jump = new Rect(r.xMax - 130f, by, 130f, bh);
            if (Btn(jump, "PS_BtnJump".Translate(), live)) CameraJumper.TryJumpAndSelect(p);

            // The left group is laid out in PRIORITY order and each button is drawn only if it still
            // fits before Jump. The window resizes down to 700px and four fixed-width buttons do not fit
            // there - the same squeeze that drops the Paths column below 900px. Leaving the least
            // important button out beats drawing two buttons on top of each other.
            float x = r.x + lblW + 6f, limit = jump.x - 6f;

            // A pick set aside with "Choose later" is the only urgent thing on this window, so it leads
            // the group - and it EXISTS only while there is one to reopen, rather than sitting there
            // permanently greyed out and eating width the other buttons need.
            if (PickWaiting(p, live) && x + 170f <= limit)
            {
                if (Btn(new Rect(x, by, 170f, bh), "PS_BtnPick".Translate(), true)) ReopenPick(p);
                x += 176f;
            }
            if (x + 150f <= limit)
            {
                if (Btn(new Rect(x, by, 150f, bh), "PS_BtnOpenTab".Translate(), live)) OpenPsycastTab(p);
                x += 156f;
            }
            if (x + 130f <= limit)
            {
                if (Btn(new Rect(x, by, 130f, bh), "PS_BtnConstellation".Translate(), live && p.Psycasts() != null)
                    && !Find.WindowStack.IsOpen(typeof(Window_Specializations)))
                    Find.WindowStack.Add(new Window_Specializations(p));
                x += 136f;
            }
            if (x + 170f <= limit && Btn(new Rect(x, by, 170f, bh), "PS_BtnFocus".Translate(), live))
            {
                var med = GameComponent_PsycastSynergies.Instance?.GetMed(p, true);
                if (med != null) CompSchoolFocus.OpenMenuFor(f => med.defaultFocus = f?.defName, "PS_FocusAnyDefer".Translate());
            }
        }

        // Whether the action bar shows the reopen button, ANSWERED ONCE PER FRAME AND HELD.
        // OnGUI runs several passes over one frame and the click lands on the MouseUp pass, where
        // ReopenPick clears pendingPick - so reading the live value would draw the button on the early
        // passes and skip it on the later ones, changing the control count inside a single frame. That
        // is the same IMGUI corruption that once blanked the settings page. Time.frameCount is constant
        // across the passes of one frame, which is exactly what makes it the right key.
        private int pickFrame = -1;
        private bool pickShown;
        private bool PickWaiting(Pawn p, bool live)
        {
            if (pickFrame == Time.frameCount) return pickShown;
            pickFrame = Time.frameCount;
            var med = live ? GameComponent_PsycastSynergies.Instance?.GetMed(p, false) : null;
            pickShown = med != null && med.pendingPick > 0;
            return pickShown;
        }

        // Reopen a pick the player set aside. Same handoff as Alert_PendingCardPick: clear the pending
        // marker, then hand the tier to MeditationSystem, which deals through the normal path. The hand
        // comes back as the one that was set aside rather than a fresh draw - nothing here moves the
        // card pool seed, and that is deliberate (a reopen must not be a free re-roll).
        private void ReopenPick(Pawn p)
        {
            var med = GameComponent_PsycastSynergies.Instance?.GetMed(p, false);
            if (med == null || med.pendingPick <= 0) return;
            int tier = med.pendingPick;
            med.pendingPick = 0;
            MeditationSystem.OpenPick(p, tier);
            PsycasterRoster.Invalidate();   // drops the row's "Path pick waiting" flag on the NEXT frame
        }

        private bool Btn(Rect r, string label, bool enabled)
        {
            if (!enabled)
            {
                MXStyle.Fill(r, new Color(0.1f, 0.12f, 0.14f));
                GUI.color = Hair; Widgets.DrawBox(r, 1); GUI.color = Palette.TextDim;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(r, label);
                Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
                return false;
            }
            return MXStyle.Button(r, label);
        }

        private void DrawFooter(Rect r, PsycastSynergiesSettings s)
        {
            Text.Font = SubFont; GUI.color = Palette.TextDim; Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(r, "PS_RosterFooter".Translate(
                PsycasterRoster.ColonyHoursToday.ToString("F1"),
                s.awakenGuaranteeHours.ToString("F0"),
                s.pilgrimGuaranteeHours.ToString("F0"),
                s.transcendBaseHours.ToString("F0")));
            Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white; Text.Font = GameFont.Small;
        }

        private void OpenPsycastTab(Pawn p)
        {
            if (p == null) return;
            CameraJumper.TryJumpAndSelect(p);
            InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Psycasts));
        }
    }
}
