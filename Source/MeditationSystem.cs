#nullable disable
using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using HarmonyLib;
using VanillaPsycastsExpanded;

namespace PsycastSynergies
{
    [DefOf]
    public static class MeditationDefOf
    {
        public static HediffDef PsychicComa;
        public static ThoughtDef PS_InsightMemory;
        static MeditationDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(MeditationDefOf));
    }

    // Per-pawn meditation / awakening tracking (lives in the GameComponent save).
    public class MeditationData : IExposable
    {
        public int streakTicks;          // consecutive meditation (resets after a gap)
        public int todayTicks;           // meditation this day (drives the coma risk)
        public int lastMedTick = -99999;
        public int enlightenments;       // progress toward awakening
        public int awakenThreshold;      // HIDDEN, rolled 3-8 on first meditation
        public bool awakened;            // set when the awakening fires (Stage 2)
        public int tier;                 // enlightenment tier reached (mirrors the PS_Enlightenment hediff)
        public int pendingPick;          // a tier whose card pick was deferred to reroll via meditation (0 = none)
        public int rerollCount;          // deferred rerolls this pick - each raises psychic-coma risk
        // Immediate Tier I re-deals taken, ever. This exists ONLY to move the card-pool seed: the seed is
        // deliberately deterministic (see BuildPool) so a hand cannot be save-scummed, which also meant the
        // re-deal button rebuilt the pool from an unchanged seed and handed back the identical cards. It is
        // scribed and never reset, so re-loading cannot undo a re-deal and every later deal for this pawn
        // starts somewhere new.
        public int redeals;
        // NOT scribed, deliberately. Bumped every time a card window for this pawn closes, whatever the exit
        // (embraced, set aside, deferred, forgone). A queued pick records the value it was queued at, so a
        // duplicate request created BEFORE the player answered the hand is recognised as stale and dropped
        // rather than dealing a second one. A save/load empties the pick queue as well, so nothing that
        // reads this outlives the session that wrote it.
        public int pickResolves;
        public System.Collections.Generic.List<PsycasterPathDef> cardPaths;   // paths chosen via cards, in tier order (for path respec)
        public string focusType;         // VPE meditation-focus TYPE defName (or building defName) of the last focus meditated at
        public string defaultFocus;      // pawn's personal default focus type - used when meditating at an unattuned building
        public bool forcedMeditation;    // "meditate your ass off": keep meditating continuously (forces time assignment)
        public int pilgrimStyle;         // tier-up pilgrimage routing: 0 = Either, 1 = Combat (altar), 2 = Pacifist (anima)
        public int pilgrimTicks;         // cumulative tier 1-2 meditation toward a GUARANTEED pilgrimage offer (pity)
        public float medSaturation;      // anti-farming: builds with daily meditation, shrinks breakthrough chance
        public int awakenMeditationTicks; // cumulative meditation toward Awakening (non-psycasters) - feeds the ramp + pity
        public int transcendTicks;        // cumulative post-Illuminated meditation toward the next Transcendent tier (resets on tier-up)

        // Rolling meditation RATE, in ticks per day, folded in at the daily reset (EMA, ~6-day window).
        // Cumulative counters answer "how far along", this answers "how fast" - together they are the only
        // honest way to say WHEN a pawn arrives. medDays is the number of full days folded in: while it is
        // 0 there is nothing to extrapolate from, and the roster says "no estimate" rather than guessing.
        public float medDayAvg;
        public int medDays;

        // Non-retroactive, tier-scaled auto-stat accumulators (see AutoStats). Each psycaster level adds the
        // base per-level amount scaled by the enlightenment tier at THAT moment, so newer-tier levels are worth more.
        public float autoHeat, autoRecovery, autoSensitivity;
        public int autoLevel;   // highest psycaster level already folded into the accumulators
        public bool autoInit;   // seeded existing levels (at the tier-0 rate) yet?

        public void ExposeData()
        {
            Scribe_Values.Look(ref streakTicks, "streak", 0);
            Scribe_Values.Look(ref todayTicks, "today", 0);
            Scribe_Values.Look(ref lastMedTick, "lastMed", -99999);
            Scribe_Values.Look(ref enlightenments, "enl", 0);
            Scribe_Values.Look(ref awakenThreshold, "thresh", 0);
            Scribe_Values.Look(ref awakened, "awakened", false);
            Scribe_Values.Look(ref tier, "tier", 0);
            Scribe_Values.Look(ref pendingPick, "pendingPick", 0);
            Scribe_Values.Look(ref rerollCount, "rerollCount", 0);
            Scribe_Values.Look(ref redeals, "redeals", 0);
            Scribe_Collections.Look(ref cardPaths, "cardPaths", LookMode.Def);
            Scribe_Values.Look(ref focusType, "focus");
            Scribe_Values.Look(ref defaultFocus, "defaultFocus");
            Scribe_Values.Look(ref forcedMeditation, "forcedMeditation", false);
            Scribe_Values.Look(ref pilgrimStyle, "pilgrimStyle", 0);
            Scribe_Values.Look(ref pilgrimTicks, "pilgrimTicks", 0);
            Scribe_Values.Look(ref medSaturation, "medSaturation", 0f);
            Scribe_Values.Look(ref awakenMeditationTicks, "awakenMeditationTicks", 0);
            Scribe_Values.Look(ref transcendTicks, "transcendTicks", 0);
            Scribe_Values.Look(ref medDayAvg, "medDayAvg", 0f);
            Scribe_Values.Look(ref medDays, "medDays", 0);
            Scribe_Values.Look(ref autoHeat, "autoHeat", 0f);
            Scribe_Values.Look(ref autoRecovery, "autoRecovery", 0f);
            Scribe_Values.Look(ref autoSensitivity, "autoSensitivity", 0f);
            Scribe_Values.Look(ref autoLevel, "autoLevel", 0);
            Scribe_Values.Look(ref autoInit, "autoInit", false);
        }
    }

    // Meditation drives "a flow of ancient knowledge" breakthrough events toward Awakening (the awakening
    // chance ramps with cumulative meditation and is guaranteed within ~a week of dedication), while the
    // psychic-coma RISK rises with how much a pawn meditates in a day - so round-the-clock meditation
    // backfires. Psycasters instead get a full-level XP burst from each flow of ancient knowledge.
    public static class MeditationSystem
    {
        // Re-entrancy guard: true while OUR awakening creates the psylink, so the external-psylink Harmony
        // postfix doesn't mistake our own meditation awakening for an Empire/anima grant.
        internal static bool internalPsylinkChange;

        // The owning GameComponent passes itself in - no per-tick Instance resolve.
        public static void Tick(int t, GameComponent_PsycastSynergies gc)
        {
            var s = PsycastSynergiesMod.Settings;
            if (s == null || gc == null) return;

            if (t % 60000 == 0)
            {
                float satThreshold = (s.comaSafeHours > 0f ? s.comaSafeHours : 6f) * 2500f;   // only OVER-meditation counts
                foreach (var d in gc.MedDataValues)
                {
                    // Anti-farming saturation: only meditating PAST the daily safe window (6h by default) builds it
                    // up; any lighter day sheds it faster. So grinding many hours every day makes breakthroughs
                    // progressively rarer AND comas progressively longer, while normal meditation never accrues it.
                    d.medSaturation = d.todayTicks >= satThreshold
                        ? Mathf.Min(d.medSaturation + 1f, 6f)
                        : Mathf.Max(d.medSaturation - 1.5f, 0f);
                    // Fold the day into the rolling rate BEFORE the reset. A pawn who stops meditating
                    // decays toward zero over a couple of weeks of empty days, which is what we want:
                    // the roster's estimate should fade out rather than stay frozen at an old pace.
                    d.medDayAvg = d.medDays == 0 ? d.todayTicks : Mathf.Lerp(d.medDayAvg, d.todayTicks, 0.35f);
                    if (d.medDays < 9999) d.medDays++;
                    d.todayTicks = 0;   // daily reset
                }
            }
            if (t % 60 == 0) Accumulate(t, gc);
            if (s.enlightenmentEnabled && t % 2500 == 0) RollHourly(t, gc, s);
            if (t % 2500 == 0) AwakeningTrigger.HourlyScan(gc);   // XML trigger surfaces (thought/precept/surge), independent of the enlightenment toggle
        }

        [DebugAction("Psycasts²", "Force awakening (Tier I)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_ForceTier1(Pawn p)
        {
            var gc = GameComponent_PsycastSynergies.Instance;
            if (gc == null || p == null) return;
            var med = gc.GetMed(p, true);
            med.awakened = true;   // set BEFORE Awaken so the external-psylink postfix doesn't double-fire
            Awaken(p, med);
        }

        [DebugAction("Psycasts²", "Grant psycaster level (+1)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_GrantLevel(Pawn p)
        {
            if (p == null) return;
            EnsurePsycaster(p);
            var h = p.Psycasts();
            if (h != null) h.GainExperience(Hediff_PsycastAbilities.ExperienceRequiredForLevel(h.level + 1), false);
        }

        // One-click mastery: every learned psycast jumps to the ABSOLUTE cap (10 / 15 with
        // Convergence) so the mastered-tree gold glow + sparkles can be verified without
        // hundreds of psycaster levels. State-only (no invest bursts - those are player-path FX).
        [DebugAction("Psycasts²", "Max all psycast skills (absolute cap)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_MaxAllSkills(Pawn p)
        {
            var gc = GameComponent_PsycastSynergies.Instance;
            var comp = p?.GetComp<VEF.Abilities.CompAbilities>();
            if (gc == null || comp?.LearnedAbilities == null) return;
            var s = PsycastSynergiesMod.Settings;
            int absCap = (s?.maxSkillLevel ?? 10) + SpecEffects.LevelCapBonus(p);
            int n = 0;
            foreach (var ab in comp.LearnedAbilities)
            {
                if (ab?.def == null || ab.def.GetModExtension<AbilityExtension_Psycast>() == null) continue;
                gc.SetLevel(p, ab.def, absCap);
                n++;
            }
            Messages.Message(p.LabelShortCap + ": " + n + " psycast(s) set to level " + absCap + ".", p, MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Psycasts²", "Force Enlightened (Tier II)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_ForceTier2(Pawn p)
        {
            if (p == null) return;
            EnsurePsycaster(p);
            OpenPick(p, 2);
        }

        [DebugAction("Psycasts²", "Force Illuminated (Tier III)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_ForceTier3(Pawn p)
        {
            if (p == null) return;
            EnsurePsycaster(p);
            OpenPick(p, 3);
        }

        [DebugAction("Psycasts²", "Transcend (next tier IV+)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_Transcend(Pawn p)
        {
            if (p == null) return;
            EnsurePsycaster(p);
            var med = GameComponent_PsycastSynergies.Instance?.GetMed(p, true);
            int cur = EnlightenmentTier.TierOf(p);
            if (cur < 3) { EnlightenmentTier.SetTier(p, 3, false); cur = 3; }   // jump to Illuminated first
            if (med != null) med.transcendTicks = 0;
            Transcend(p, med, cur + 1);
        }

        [DebugAction("Psycasts²", "Reset to non-psycaster (wipe awakening)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_ResetPsycaster(Pawn p)
        {
            if (p?.health?.hediffSet == null) return;
            // Sweep off every status this mod adds (PS_*), the VPE psycast implant, and the psylink itself.
            var toRemove = new List<Hediff>();
            foreach (var h in p.health.hediffSet.hediffs)
            {
                var dn = h.def?.defName;
                if ((dn != null && dn.StartsWith("PS_")) || dn == "VPE_PsycastAbilityImplant" || h is Hediff_Psylink)
                    toRemove.Add(h);
            }
            internalPsylinkChange = true;   // suppress the external-psylink re-awaken postfix during removal
            try { foreach (var h in toRemove) p.health.RemoveHediff(h); }
            finally { internalPsylinkChange = false; }
            // Wipe our per-pawn bookkeeping: skill levels, specialization points/nodes, meditation + tier data.
            GameComponent_PsycastSynergies.Instance?.ResetPawn(p);
            Messages.Message(p.LabelShortCap + " reset to a non-psycaster, awakening wiped.", p, MessageTypeDefOf.NeutralEvent, false);
        }

        // Replays, for one pawn and in one click, exactly what the 2500-tick boundary does: the meditation
        // awakening, then the hourly trigger scan on the very next line. That adjacency IS the duplicate-pick
        // bug - the scan's path-less-psycaster repair sees a pawn who became a psycaster microseconds ago,
        // holds no path, has nothing pending and no cards recorded, and used to deal them a second hand.
        // Reaching it the honest way costs 120 in-game hours of meditation.
        [DebugAction("Psycasts²", "Awakening tick (duplicate-pick check)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_AwakeningTick(Pawn p)
        {
            var gc = GameComponent_PsycastSynergies.Instance;
            var s = PsycastSynergiesMod.Settings;
            if (p == null || gc == null || s == null) return;
            var med = gc.GetMed(p, true);
            int suppressedBefore = JoinAwaken.inFlightSuppressed, staleBefore = staleDropped;

            if (med.awakenThreshold == 0) med.awakenThreshold = Rand.RangeInclusive(5, 10);
            med.enlightenments = med.awakenThreshold;   // what the "awakening is certain after N hours" pity does
            Enlighten(p, med, s);                       // RollHourly's non-psycaster branch
            AwakeningTrigger.HourlyScan(gc);            // the next line of MeditationSystem.Tick, same tick

            int picks = PickCountFor(p);
            int suppressed = JoinAwaken.inFlightSuppressed - suppressedBefore;
            int stale = staleDropped - staleBefore;
            string verdict = picks == 1 ? "OK: one card window" : picks + " card windows - DUPLICATE";
            Log.Message("[Psycasts²] awakening tick for " + p.LabelShortCap + ": " + verdict
                + ". Second offers refused while a pick was in flight: " + suppressed
                + ". Stale queued requests dropped: " + stale + ".");
            Messages.Message(p.LabelShortCap + ": " + verdict + " (" + suppressed + " second offer(s) refused)",
                p, picks == 1 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
        }

        // Leaves the pawn exactly as an outside psylink source does - the Empire bestowing ceremony, an anima
        // tree, a scenario, or a mod that hands out psylinks: a VPE psycaster with no path, no tier and no
        // awakening. That state used to put them on the BREAKTHROUGH track, so they collected "A flow of
        // ancient knowledge" letters forever and never climbed the ramp. Follow this with the state report.
        [DebugAction("Psycasts²", "Give unadopted psylink (no awakening)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_UnadoptedPsylink(Pawn p)
        {
            var gc = GameComponent_PsycastSynergies.Instance;
            if (p == null || gc == null) return;
            EnsurePsycaster(p);   // sets internalPsylinkChange itself, so the external-psylink postfix stays out
            var med = gc.GetMed(p, true);
            med.awakened = false;
            med.enlightenments = 0;
            med.awakenMeditationTicks = 0;
            med.pendingPick = 0;
            med.cardPaths?.Clear();
            EnlightenmentTier.SetTier(p, 0, false);
            Messages.Message(p.LabelShortCap + " now holds a psylink this mod never adopted: no path, no tier.",
                p, MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Psycasts²", "Awakening state (report)", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_AwakeningState(Pawn p)
        {
            var gc = GameComponent_PsycastSynergies.Instance;
            var s = PsycastSynergiesMod.Settings;
            if (p == null || gc == null || s == null) return;
            var med = gc.GetMed(p, true);
            var psy = p.Psycasts();
            int paths = psy?.unlockedPaths?.Count ?? 0;
            bool awake = IsAwakened(p, med);
            string branch = !awake ? "AWAKENING RAMP (no breakthroughs)"
                : med.tier >= 3 ? "breakthroughs + Transcendence climb"
                : med.tier >= 1 ? "breakthroughs + pilgrimage climb"
                : "breakthroughs";
            Log.Message("[Psycasts²] " + p.LabelShortCap + " - awakening state"
                + "\n  psycaster: " + (psy == null ? "no" : "yes, level " + psy.level)
                + " | paths: " + paths + " | tier: " + med.tier + " | awakened flag: " + med.awakened
                + "\n  IsAwakened: " + (awake ? "yes" : "NO") + " -> meditation goes to " + branch
                + "\n  awakening meditation: " + (med.awakenMeditationTicks / 2500f).ToString("F1") + " h of "
                + s.awakenGuaranteeHours.ToString("F0") + " h guaranteed"
                + "\n  pendingPick: " + med.pendingPick + " | cards taken: " + (med.cardPaths?.Count ?? 0)
                + " | picks answered this session: " + med.pickResolves
                + "\n  picks in flight for them right now: " + PickCountFor(p));
            Messages.Message(p.LabelShortCap + ": " + (awake ? "awakened" : "not awakened") + ", on " + branch
                + ". Full report in the dev console.", p, MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Psycasts²", "Open 2 picks at once (queue test)", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Debug_TwoPicks()
        {
            var colonists = Find.CurrentMap?.mapPawns?.FreeColonists;
            if (colonists == null) return;
            for (int i = 0, n = 0; i < colonists.Count && n < 2; i++, n++)
                OpenPick(colonists[i], 1);   // two in a row: first opens, second queues, then chains on close
        }

        // Only count a pawn as meditating while they're actually in the meditation act - NOT while walking to
        // the spot (the Meditate job's goto toil still reports CurJobDef == Meditate). Without this, a pawn
        // whose schedule sends them to meditate accrues progress during the walk, before they ever sit down.
        internal static bool IsActivelyMeditating(Pawn p)
            => p?.CurJobDef == JobDefOf.Meditate && p.pather != null && !p.pather.MovingNow;

        private static void Accumulate(int t, GameComponent_PsycastSynergies gc)
        {
            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                var pawns = maps[m].mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var p = pawns[i];
                    if (!IsActivelyMeditating(p)) continue;
                    var med = gc.GetMed(p, true);
                    med.streakTicks += 60;
                    med.todayTicks += 60;
                    med.lastMedTick = t;
                    if (!IsAwakened(p, med)) med.awakenMeditationTicks += 60;    // cumulative meditation toward Awakening
                    else if (med.tier >= 3) med.transcendTicks += 60;             // Illuminated+ psycasters climb toward Transcendence
                    else if (med.tier >= 1) med.pilgrimTicks += 60;               // tier 1-2 climb toward the guaranteed pilgrimage offer
                    if (med.awakenThreshold == 0) med.awakenThreshold = Rand.RangeInclusive(5, 10);
                    // The meditate job stores the FOCUS object in targetC and the SPOT/seat in targetA
                    // (targetB is null). Priority chain: building's gizmo > pawn's personal default >
                    // building's NATIVE focus type (e.g. brazier -> Flame) > null (trait-weighted).
                    var focusThing = p.CurJob?.targetC.Thing;
                    var spotThing = p.CurJob?.targetA.Thing;
                    MeditationFocusDef pawnDefault = string.IsNullOrEmpty(med.defaultFocus) ? null
                        : DefDatabase<MeditationFocusDef>.GetNamedSilentFail(med.defaultFocus);
                    var ft = focusThing?.TryGetComp<CompSchoolFocus>()?.selectedFocus
                             ?? spotThing?.TryGetComp<CompSchoolFocus>()?.selectedFocus
                             ?? pawnDefault
                             ?? NativeFocus(focusThing) ?? NativeFocus(spotThing);
                    med.focusType = ft?.defName;
                }
            }
            foreach (var kv in gc.MedDataPairs)
                if (kv.Value.streakTicks > 0 && t - kv.Value.lastMedTick > 1250) kv.Value.streakTicks = 0;   // gap breaks streak
        }

        private static void RollHourly(int t, GameComponent_PsycastSynergies gc, PsycastSynergiesSettings s)
        {
            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                var pawns = maps[m].mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var p = pawns[i];
                    if (!IsActivelyMeditating(p)) continue;
                    var med = gc.GetMed(p, true);
                    float streakHours = med.streakTicks / 2500f;
                    float todayHours = med.todayTicks / 2500f;

                    // Over-meditation: psychic-coma risk past the daily safe window (+ a streak factor),
                    // raised further for each deferred card reroll. EXEMPT on pilgrimage site maps - the
                    // quest forces long daily meditation (well past the safe window), and a coma there would
                    // sabotage the pilgrimage (especially the pacifist anima chain, which must stay safe).
                    // s.ComaRiskOff (the safe window pushed to a full 24h) switches the whole roll off, not
                    // just the today-hours term: the streak and deferred-reroll terms below do not depend on
                    // the window and would otherwise keep producing comas on a setting the player has turned
                    // all the way up to mean "meditation is free".
                    if (!s.ComaRiskOff && !PilgrimRouting.IsPilgrimageMap(p.Map))
                    {
                        float comaRisk = Mathf.Max(0f, (todayHours - s.comaSafeHours) * s.comaRiskPerHour)
                                       + Mathf.Max(0f, (streakHours - s.comaSafeHours) * s.comaRiskPerHour * 0.5f)
                                       + med.rerollCount * 0.06f;
                        if (comaRisk > 0f && Rand.Chance(Mathf.Min(comaRisk, 0.75f)))
                        {
                            ApplyComa(p, med);
                            med.streakTicks = 0;
                            // The pending card pick SURVIVES a coma - forfeiting it here could permanently
                            // lose a tier-up (soft lock). Alert_PendingCardPick keeps it recoverable; the
                            // coma's downtime and saturation are punishment enough.
                            continue;
                        }
                    }

                    // Deferred reroll: re-open the card pick after the pawn meditated on it.
                    if (med.pendingPick > 0)
                    {
                        int tier = med.pendingPick;
                        med.pendingPick = 0;
                        OpenPick(p, tier);   // rerollCount feeds the pool seed for a fresh draw
                        continue;
                    }

                    // Transcendence: an Illuminated (tier 3+) psycaster who keeps meditating climbs into the
                    // open-ended Transcendent tiers. Each tier costs geometrically more, so the climb slows hard.
                    if (med.tier >= 3 && s.transcendEnabled && !TieringControl.TranscendenceDisabled
                        && med.transcendTicks >= TranscendThreshold(med.tier + 1, s))
                    {
                        med.transcendTicks = 0;
                        Transcend(p, med, med.tier + 1);
                        continue;
                    }

                    // Pilgrimage pity: an Awakened (tier 1-2) psycaster's cumulative meditation builds toward a
                    // GUARANTEED tier-up pilgrimage offer, so the storyteller's random rolls can never stall the
                    // climb. The storyteller may still offer one earlier on its own.
                    if ((med.tier == 1 || med.tier == 2) && TryFirePilgrimPity(p, med, s)) continue;

                    // Non-psycasters are on the AWAKENING ramp: the chance climbs with CUMULATIVE meditation so a
                    // dedicated colonist reaches Tier I within ~a week, with a hard guarantee at the end of the
                    // window. Psycasters use the saturation-scaled streak chance for their full-level breakthroughs.
                    if (!IsAwakened(p, med))
                    {
                        if (TieringControl.MeditationAwakeningDisabled) continue;   // a mod owns awakening now
                        float cumHours = med.awakenMeditationTicks / 2500f;
                        if (s.awakenGuaranteeHours > 0f && cumHours >= s.awakenGuaranteeHours)
                        {
                            med.enlightenments = med.awakenThreshold;   // pity: force the final insight -> Awakening
                            Enlighten(p, med, s);
                        }
                        else if (Rand.Chance(Mathf.Min((0.08f + cumHours * 0.02f) * AwakeningTrigger.SurgeMult(p.Map), 0.6f)))
                            Enlighten(p, med, s);
                        continue;
                    }
                    // Breakthroughs are rolled against a base hourly chance that the player sets separately for
                    // before and after Illuminated, so the late game can be made rarer or richer than the climb
                    // to it without touching the other half.
                    float baseChance = med.tier > 3 ? s.transcendChance : s.enlightenmentChance;
                    // Zero means OFF for this band, checked explicitly rather than left to the arithmetic: the
                    // streak term below is added to the base, so a base of 0 would still break through after a
                    // few unbroken hours - a slider dragged to nothing has to mean nothing.
                    if (baseChance <= 0f) continue;
                    // Saturation from habitual daily meditation scales the whole chance down (anti-farming).
                    float satMult = 1f / (1f + med.medSaturation * s.enlightenmentSaturationFactor);
                    // Above Illuminated, each Transcendent tier speeds breakthroughs further (leveling slows hard
                    // past 30) - but the 0.6 hard cap holds, so a breakthrough is never guaranteed.
                    float tierMult = med.tier > 3 ? 1f + (med.tier - 3) * s.transcendBreakthroughCurve : 1f;
                    float chance = Mathf.Min((baseChance + streakHours * s.enlightenmentStreakBonus) * satMult * tierMult * AwakeningTrigger.SurgeMult(p.Map), 0.6f);
                    if (Rand.Chance(chance)) Enlighten(p, med, s);
                }
            }
        }

        private static void Enlighten(Pawn p, MeditationData med, PsycastSynergiesSettings s)
        {
            var psy = p.Psycasts();
            // A breakthrough is a reward for a psycaster who has WALKED a path. A pawn holding a psylink they
            // never awakened into (see IsAwakened) goes on the awakening ramp below instead - handing them
            // "A flow of ancient knowledge" every hour gave free psycaster levels to a pawn the player was
            // still waiting to see awaken.
            if (psy != null && IsAwakened(p, med))
            {
                if (psy.level < (PsycastsMod.Settings?.maxLevel ?? 30))
                {
                    float need = Hediff_PsycastAbilities.ExperienceRequiredForLevel(psy.level + 1);
                    psy.GainExperience(need * s.enlightenmentFrac, false);
                    med.medSaturation += 2f;   // a full-level breakthrough sates the mind; the next one is much rarer
                    Fx(p);
                    Notify(p, "PS_LetterFlowLabel".Translate(), "PS_LetterFlowText".Translate(p.LabelShortCap));
                }
                return;
            }

            // Non-psycaster: progress toward awakening (threshold is hidden, so no count is shown).
            med.enlightenments++;
            var thought = MeditationDefOf.PS_InsightMemory;
            if (thought != null) p.needs?.mood?.thoughts?.memories?.TryGainMemory(thought);
            Fx(p);
            if (med.enlightenments >= med.awakenThreshold && !med.awakened)
            {
                med.awakened = true;
                Awaken(p, med);
            }
            else if (!med.awakened)
            {
                Messages.Message("PS_MsgStirring".Translate(p.LabelShortCap), p, MessageTypeDefOf.PositiveEvent, false);
            }
        }

        // "Awakened" in THIS mod's sense: the pawn has come through the awakening flow and holds a path.
        // A pawn can be a VPE psycaster and still not be awakened here - a psylink from the Empire bestowing
        // ceremony, an anima tree, a scenario or another mod leaves them with a psylink, no path and no tier.
        // The meditation branch used to be decided on p.Psycasts() alone, which put exactly those pawns on
        // the BREAKTHROUGH track: they rolled free psycaster levels hour after hour while the awakening ramp
        // - and the guarantee the player set in settings - never applied to them at all.
        // The unlockedPaths clause is what keeps old saves right: a pawn who awakened before this field
        // existed may have neither flag set, but they always hold the path their card gave them.
        internal static bool IsAwakened(Pawn p, MeditationData med)
        {
            if (med != null && (med.awakened || med.tier > 0)) return true;
            var psy = p?.Psycasts();
            return psy?.unlockedPaths != null && psy.unlockedPaths.Count > 0;
        }

        // Ensure the pawn is a VPE psycaster (creating the psylink + hediff if needed); returns the hediff.
        internal static Hediff_PsycastAbilities EnsurePsycaster(Pawn p)
        {
            var psy = p.Psycasts();
            if (psy != null) return psy;
            internalPsylinkChange = true;
            try { p.ChangePsylinkLevel(1, false); }    // creates the psylink; VPE attaches its psycast hediff
            finally { internalPsylinkChange = false; }
            psy = p.Psycasts();
            if (psy == null)
            {
                // Safety net: VPE didn't auto-attach - build its hediff straight from the psylink.
                var def = DefDatabase<HediffDef>.GetNamedSilentFail("VPE_PsycastAbilityImplant");
                var psylink = p.GetMainPsylinkSource();
                if (def != null && psylink != null)
                {
                    var h = (Hediff_PsycastAbilities)HediffMaker.MakeHediff(def, p);
                    h.InitializeFromPsylink(psylink);
                    p.health.AddHediff(h, p.health.hediffSet.GetBrain());
                    psy = h;
                }
            }
            return psy;
        }

        // Tier I: meditation turns a non-psycaster into a psycaster and offers the first path pick.
        private static void Awaken(Pawn p, MeditationData med)
        {
            var psy = EnsurePsycaster(p);
            Notify(p, "PS_LetterAwakeningLabel".Translate(), "PS_LetterAwakenMeditationText".Translate(p.LabelShortCap));
            if (psy == null) return;
            OpenPick(p, 1);
        }

        // Awaken a pawn who gained a psylink from OUTSIDE meditation (Empire bestowing/blinding ritual, or
        // anima-tree linking). They already HAVE the psylink, so attach the VPE implant from it WITHOUT
        // adding another level, then route them into our system with the first path pick.
        public static void AwakenExternal(Pawn p, MeditationData med)
        {
            if (p == null || med == null || med.awakened) return;
            med.awakened = true;
            var psy = p.Psycasts();
            if (psy == null)
            {
                var def = DefDatabase<HediffDef>.GetNamedSilentFail("VPE_PsycastAbilityImplant");
                var psylink = p.GetMainPsylinkSource();
                if (def != null && psylink != null)
                {
                    var h = (Hediff_PsycastAbilities)HediffMaker.MakeHediff(def, p);
                    h.InitializeFromPsylink(psylink);
                    p.health.AddHediff(h, p.health.hediffSet.GetBrain());
                    psy = h;
                }
            }
            if (psy == null) return;
            Notify(p, "PS_LetterAwakeningLabel".Translate(), "PS_LetterAwakenExternalText".Translate(p.LabelShortCap));
            OpenPick(p, 1);
        }

        // Opens the tiered card pick: Tier I = 3 themed, Tier II = 5 themed, Tier III = 3 any-roll
        // (the cards-per-tier-up setting, when >0, overrides those counts for every tier).
        // Pending card picks. Multiple pawns can awaken/ascend on the SAME tick (e.g. two non-psycasters hit
        // their threshold in one hourly roll, or a mass psylink grant). Opening every Window_Awakening at once
        // stacks them modally and one gets lost, so we QUEUE picks and show exactly one at a time - the next
        // opens when the current one closes (Window_Awakening.PostClose chains here).
        private struct PickRequest { public Pawn pawn; public int tier; public int stamp; public bool force; }
        private static readonly Queue<PickRequest> pickQueue = new Queue<PickRequest>();

        // thingIDNumber of the pawn whose window is on screen (or built and waiting for the deferred Add
        // below). An id rather than a Pawn reference: a static holding a Pawn roots the whole dead Game
        // graph after the player returns to the menu.
        private static int showingForId = -1;

        // A pick is "in flight" for a pawn while its window is up or a request for it sits in the queue.
        // Anything that OFFERS a pick must ask this first. RollHourly and AwakeningTrigger.HourlyScan run on
        // the SAME 2500-tick boundary, one straight after the other, so a pawn who awakens in the first is a
        // path-less psycaster by the time the second looks at them - which is precisely the signature the
        // JoinAwaken safety net exists to repair. It queued a second pick for the same pawn in the same tick,
        // and that second hand opened the moment the first window closed.
        internal static bool PickInFlight(Pawn p)
        {
            if (p == null) return false;
            if (showingForId == p.thingIDNumber) return true;
            foreach (var req in pickQueue) if (req.pawn == p) return true;
            return false;
        }

        // How many picks this pawn has waiting on them right now: the window on screen plus anything still
        // queued. Anything other than 0 or 1 is the duplicate bug. Kept apart from PickInFlight so that one
        // can early-out; this one has to count.
        internal static int PickCountFor(Pawn p)
        {
            if (p == null) return 0;
            int n = showingForId == p.thingIDNumber ? 1 : 0;
            foreach (var req in pickQueue) if (req.pawn == p) n++;
            return n;
        }

        // Requests dropped by the stale-stamp check in ShowNextPick, for the same debug tool.
        internal static int staleDropped;

        // False when the pawn already has a pick in flight, so the caller must not queue another. A queued
        // request at a LOWER tier is upgraded in place rather than dropped, so if two tiers ever do land on
        // one tick the pawn keeps the better one instead of losing it.
        private static bool AcceptPick(Pawn p, int tier)
        {
            if (showingForId == p.thingIDNumber) return false;
            bool found = false, upgrade = false;
            foreach (var req in pickQueue)
                if (req.pawn == p) { found = true; if (tier > req.tier) upgrade = true; }
            if (!found) return true;
            if (upgrade)
            {
                int n = pickQueue.Count;
                for (int i = 0; i < n; i++)
                {
                    var req = pickQueue.Dequeue();
                    if (req.pawn == p && tier > req.tier) req.tier = tier;
                    pickQueue.Enqueue(req);
                }
            }
            return false;
        }

        // Every exit from a card window lands here (Window_Awakening.PostClose) - embraced, set aside,
        // deferred for a re-roll, or forgone for points. Bumping the stamp is what makes any request queued
        // BEFORE this moment stale, so a duplicate cannot deal a second hand for a pick already answered.
        internal static void NotePickClosed(Pawn p)
        {
            showingForId = -1;
            var med = GameComponent_PsycastSynergies.Instance?.GetMed(p, true);
            if (med != null) med.pickResolves++;
        }

        // Called from GameComponent.FinalizeInit. Statics outlive a Game: a queue left holding pawns from
        // the previous session would both root that dead graph and open its windows in the new one.
        internal static void ResetPickQueue()
        {
            pickQueue.Clear();
            pickShowQueued = false;
            showingForId = -1;
        }

        // Pity threshold (ticks) for the guaranteed pilgrimage offer. 0 = the guarantee is disabled.
        // The tier 2 -> 3 climb runs half again longer than tier 1 -> 2 (mirrors the rarer T3 quests).
        internal static float PilgrimPityThresholdTicks(int curTier, PsycastSynergiesSettings s)
        {
            float h = s?.pilgrimGuaranteeHours ?? 60f;
            if (h <= 0f) return 0f;
            return h * 2500f * (curTier >= 2 ? 1.5f : 1f);
        }

        // True while any pilgrimage quest targeting `targetTier` is offered or underway (all four chains).
        internal static bool PilgrimQuestOngoing(int targetTier)
        {
            var quests = Find.QuestManager?.QuestsListForReading;
            if (quests == null) return false;
            for (int i = 0; i < quests.Count; i++)
            {
                var q = quests[i];
                if (q.State != QuestState.Ongoing && q.State != QuestState.NotYetAccepted) continue;
                if (QuestTargetTier(q.root) == targetTier) return true;
            }
            return false;
        }

        internal static int QuestTargetTier(QuestScriptDef def)
        {
            switch (def?.defName)
            {
                case "PS_TierIIPilgrimage": case "PS_T2AnimaPilgrimage": return 2;
                case "PS_TierIIIPilgrimage": case "PS_T3AnimaPilgrimage": return 3;
                default: return 0;
            }
        }

        // Fire the guaranteed pilgrimage offer once the pity threshold is met. Chain choice honors the
        // pawn's pilgrim style (Either picks randomly); if the chosen chain can't run right now (e.g. no
        // site tile), the other allowed chain is tried once, else we retry next hour. Progress is kept
        // when a pilgrimage fails - the offer simply re-fires. Reset on any tier change (SetTier).
        private static bool TryFirePilgrimPity(Pawn p, MeditationData med, PsycastSynergiesSettings s)
        {
            if (TieringControl.PilgrimagesDisabled) return false;
            float need = PilgrimPityThresholdTicks(med.tier, s);
            if (need <= 0f || med.pilgrimTicks < need) return false;
            int targetTier = med.tier + 1;
            if (PilgrimQuestOngoing(targetTier)) return false;   // one offer at a time; pity resumes after fail

            bool combat = PilgrimRouting.AllowsCombat(p), anima = PilgrimRouting.AllowsAnima(p);
            if (!combat && !anima) return false;
            bool useCombat = combat && (!anima || Rand.Bool);
            var target = (IIncidentTarget)p.Map ?? Find.World;
            float points = StorytellerUtility.DefaultThreatPointsNow(target);

            var def = PityQuestDef(targetTier, useCombat);
            if (def == null || !def.CanRun(points, target))
            {
                // Alternate chain, if the style allows both.
                def = combat && anima ? PityQuestDef(targetTier, !useCombat) : null;
                if (def == null || !def.CanRun(points, target)) return false;
            }

            var quest = QuestUtility.GenerateQuestAndMakeAvailable(def, points);
            if (quest == null) return false;
            QuestUtility.SendLetterQuestAvailable(quest);   // Patch_AutoAcceptPilgrimage accepts + announces
            // Do NOT clear pilgrimTicks here. The pity counter is discharged by SUCCESS, not by the
            // OFFER: EnlightenmentTier.SetTier zeroes it on the resulting tier change. Clearing it here
            // meant a player who missed their site had to re-earn the entire guarantee window (60h of
            // meditation by default) before another pilgrimage could ever be offered. Keeping it means a
            // FAILED pilgrimage refunds the climb and the offer re-fires on the next hourly roll, which
            // is what the PilgrimQuestOngoing comment above always claimed ("pity resumes after fail").
            return true;
        }

        private static QuestScriptDef PityQuestDef(int targetTier, bool combat)
            => DefDatabase<QuestScriptDef>.GetNamedSilentFail(
                targetTier == 2 ? (combat ? "PS_TierIIPilgrimage" : "PS_T2AnimaPilgrimage")
                                : (combat ? "PS_TierIIIPilgrimage" : "PS_T3AnimaPilgrimage"));

        // Geometric meditation cost (in ticks) to reach a given Transcendent tier (>=4). Diminishing returns:
        // each tier costs growth-x more than the last, so the climb beyond Illuminated slows dramatically.
        internal static float TranscendThreshold(int nextTier, PsycastSynergiesSettings s)
        {
            float baseTicks = Mathf.Max(1f, s?.transcendBaseHours ?? 48f) * 2500f;
            float growth = Mathf.Max(1.05f, s?.transcendGrowth ?? 1.6f);
            return baseTicks * Mathf.Pow(growth, Mathf.Max(0, nextTier - 4));
        }

        // Advance a pawn into the next Transcendent tier: grant the tier (+spec points) and open the animated
        // psycast-card pick for a bonus free path (its embrace unlocks the path; the SetTier there is a no-op).
        private static void Transcend(Pawn p, MeditationData med, int nextTier)
        {
            EnlightenmentTier.SetTier(p, nextTier, true);
            OpenPick(p, nextTier);
            if (PawnUtility.ShouldSendNotificationAbout(p))
                Find.LetterStack.ReceiveLetter("PS_LetterTranscendence".Translate(EnlightenmentTier.Name(nextTier)),
                    "PS_LetterTranscendText".Translate(p.LabelShortCap, EnlightenmentTier.Name(nextTier)),
                    LetterDefOf.PositiveEvent, p);
        }

        // `force` is for the one caller that legitimately re-opens a pick for a pawn who already has a window
        // up: the Tier I re-deal button, which enqueues the replacement before it closes its own window.
        // Everything else goes through the duplicate guard.
        public static void OpenPick(Pawn p, int tier, bool force = false)
        {
            if (p == null) return;
            if (!force && !AcceptPick(p, tier)) return;
            var med = GameComponent_PsycastSynergies.Instance?.GetMed(p, true);
            pickQueue.Enqueue(new PickRequest { pawn = p, tier = tier, stamp = med?.pickResolves ?? 0, force = force });
            ShowNextPick();
        }

        // A window has been built and handed to the deferred Add below but is not on the stack yet, so
        // IsOpen would still say false. Without this a second call in the same frame would deal twice.
        internal static bool pickShowQueued;

        // Opens the next queued pick, but only if no awakening window is currently up (one at a time).
        internal static void ShowNextPick()
        {
            if (Find.WindowStack == null || pickShowQueued || Find.WindowStack.IsOpen(typeof(Window_Awakening))) return;
            while (pickQueue.Count > 0)
            {
                var req = pickQueue.Dequeue();
                if (req.pawn == null || req.pawn.Dead) continue;
                var med = GameComponent_PsycastSynergies.Instance?.GetMed(req.pawn, true);
                // Stale request: the pawn answered a pick between this being queued and being dequeued, so
                // this is a duplicate of a hand they have already dealt with. Showing it hands out a second
                // free path.
                if (!req.force && med != null && med.pickResolves != req.stamp) { staleDropped++; continue; }
                var st = PsycastSynergiesMod.Settings;
                int count = st != null && st.cardPickCount > 0 ? st.cardPickCount : (req.tier == 2 ? 5 : 3);
                bool anyRoll = req.tier >= 3;
                var pool = BuildPool(req.pawn, med, count, !anyRoll, anyRoll);
                if (pool.Count == 0) continue;
                try
                {
                    var win = new Window_Awakening(req.pawn, pool, req.tier);
                    pickShowQueued = true;
                    showingForId = req.pawn.thingIDNumber;
                    // NEVER Add straight from here. One caller is Window_Awakening.PostClose, and vanilla's
                    // WindowStack.TryRemove runs PostClose AFTER windows.Remove but BEFORE it repairs
                    // focusedWindow - all while WindowStackOnGUI is walking that same list by index. Adding
                    // mid-removal corrupts the walk and surfaces as a root-level NRE in OnGUI with a
                    // truncated stack that names only vanilla. Deferring a frame puts the Add outside both.
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        pickShowQueued = false;
                        Find.WindowStack?.Add(win);
                    });
                    return;
                }
                catch (System.Exception e)
                {
                    pickShowQueued = false;
                    showingForId = -1;
                    Log.Warning("[PsycastSynergies] tier-" + req.tier + " pick window failed: " + e);
                }
            }
        }

        // Candidate paths for a pick: `count` unlocked-eligible paths, biased toward the meditation type
        // unless anyRoll (Tier III draws from EVERY path, rare ones included).
        private static List<PsycasterPathDef> BuildPool(Pawn p, MeditationData med, int count, bool theme, bool anyRoll)
        {
            var psy = p.Psycasts();
            var unlocked = psy?.unlockedPaths;
            var all = new List<PsycasterPathDef>();
            foreach (var path in DefDatabase<PsycasterPathDef>.AllDefs)
                if (path.HasAbilities && (unlocked == null || !unlocked.Contains(path))) all.Add(path);

            var pick = new List<PsycasterPathDef>();
            var rng = new System.Random(PoolSeed(p, med, count, anyRoll));

            if (theme && !anyRoll)
            {
                List<PsycasterPathDef> themed = null;
                // A focus TYPE (gizmo, or the focus building's native type) selects an EXCLUSIVE bucket.
                if (!string.IsNullOrEmpty(med?.focusType)
                    && DefDatabase<MeditationFocusDef>.GetNamedSilentFail(med.focusType) != null)
                {
                    themed = new List<PsycasterPathDef>();
                    foreach (var path in all) if (FocusForSchool(path) == med.focusType) themed.Add(path);
                }
                if (themed == null || themed.Count == 0)
                {
                    var kw = TraitKeywords(p);   // no focus type -> weight by the meditator's personality traits
                    if (kw != null)
                    {
                        themed = new List<PsycasterPathDef>();
                        foreach (var path in all)
                        {
                            string nm = ((path.defName ?? "") + " " + (path.label ?? "")).ToLowerInvariant();
                            for (int k = 0; k < kw.Length; k++) if (nm.Contains(kw[k])) { themed.Add(path); break; }
                        }
                    }
                }
                if (themed != null && themed.Count > 0) TakeRandom(themed, count - 1, pick, rng);   // mostly themed, leave room for variety
            }
            TakeRandom(all, count - pick.Count, pick, rng);   // fill to count
            return pick;
        }

        // The card hand is DETERMINISTIC on purpose: the same pawn, in the same state, always draws the same
        // cards, so reloading a save before an awakening cannot be used to shop for a better hand, and a pick
        // that was set aside with "Choose later" comes back as the hand the player set aside rather than a
        // free re-roll. Everything that is meant to earn a NEW hand therefore has to be a term in here:
        //   rerollCount - the paid deferred re-roll (costs a meditation cycle and coma risk)
        //   redeals     - the one free Tier I re-deal button
        //   cardPaths   - a pick already embraced, so the NEXT awakening deals fresh even when this pawn's
        //                 enlightenment count has not moved (dev-forced awakenings did repeat before this)
        // Terms are hash-combined rather than summed: the old additive form let unrelated terms cancel each
        // other out, so two different states could land on one seed. The mask keeps the seed non-negative,
        // because System.Random(int.MinValue) throws on Mono.
        private static int PoolSeed(Pawn p, MeditationData med, int count, bool anyRoll)
        {
            int seed = Gen.HashCombineInt(p?.thingIDNumber ?? 0, med?.enlightenments ?? 0);
            seed = Gen.HashCombineInt(seed, count);
            seed = Gen.HashCombineInt(seed, anyRoll ? 1 : 0);
            seed = Gen.HashCombineInt(seed, med?.rerollCount ?? 0);
            seed = Gen.HashCombineInt(seed, med?.redeals ?? 0);
            seed = Gen.HashCombineInt(seed, med?.cardPaths?.Count ?? 0);
            return seed & 0x7FFFFFFF;
        }

        private static void TakeRandom(List<PsycasterPathDef> from, int count, List<PsycasterPathDef> into, System.Random rng)
        {
            var avail = new List<PsycasterPathDef>();
            foreach (var x in from) if (!into.Contains(x)) avail.Add(x);
            while (count-- > 0 && avail.Count > 0)
            {
                int idx = rng.Next(avail.Count);
                into.Add(avail[idx]);
                avail.RemoveAt(idx);
            }
        }

        // EXCLUSIVE focus-type -> school assignment. Each school belongs to exactly ONE focus type
        // (first keyword match wins), so meditating at a focus type rolls ONLY that thematic group --
        // no duplicate rolls across types. Curated for the common VPE + addon schools; anything unmapped
        // lands in a deterministic even bucket. Edit a row to re-theme a school.
        private static readonly string[][] FocusThemes = new string[][]
        {
            new[]{ "Morbid",        "necropath","deadlife","hemosage","veincaster","deathmarch","destined death" },
            new[]{ "Flame",         "conflagrator","flameheart","luminis","civilight","shining","meteor" },
            new[]{ "Void",          "voidweaver","umbra","nightstalker","horaxian","lunacy","phase shift" },
            new[]{ "Natural",       "druid","empath","animancer","wildhunter","wildspeaker","bugmancer" },
            new[]{ "Artistic",      "harmonist","chronopath","fateweaver","tieweaver","skipmaster" },
            new[]{ "VPE_Science",   "technomancer","oripathy","biohazard","glitch","staticlord","biosoother" },
            new[]{ "VPE_Archotech", "archotechist","archon","enlightened","knowledge","gravcaster","saileach","neurophage" },
            new[]{ "VPE_Group",     "puppeteer","protector","warlord","amiya","kal'tsit","silence" },
            new[]{ "VPE_Wealth",    "geomancer","mudrock","frostshaper","hydromancer","aeromancer","ascalon" },
            new[]{ "Dignified",     "crownslayer","ines","wis'adel","blader","ranger" },
            new[]{ "Minimal",       "mechanitor" },
        };
        private static readonly string[] FocusKeys =
            { "Flame","Morbid","Void","Natural","Artistic","VPE_Science","VPE_Archotech","VPE_Group","VPE_Wealth","Dignified","Minimal" };

        // The one focus type (its defName) a school belongs to. First keyword match wins -> exclusive.
        public static string FocusForSchool(PsycasterPathDef path)
        {
            if (path?.defName == null) return null;
            string nm = ((path.defName ?? "") + " " + (path.label ?? "")).ToLowerInvariant();
            foreach (var t in FocusThemes)
                for (int i = 1; i < t.Length; i++)
                    if (nm.Contains(t[i])) return t[0];
            return FocusKeys[(int)(Hash(path.defName) % (uint)FocusKeys.Length)];   // even fallback for unmapped
        }

        private static uint Hash(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h & 0x7fffffffu;
        }

        // Public for the field manual + awakening: the schools steered toward by a focus TYPE (exclusive).
        public static System.Collections.Generic.List<PsycasterPathDef> FocusBiasedPaths(string focusType)
        {
            var result = new System.Collections.Generic.List<PsycasterPathDef>();
            if (string.IsNullOrEmpty(focusType)) return result;
            foreach (var path in DefDatabase<PsycasterPathDef>.AllDefs)
                if (path.HasAbilities && FocusForSchool(path) == focusType) result.Add(path);
            return result;
        }

        // The focus building's OWN native meditation-focus type (anima tree -> Natural, brazier -> Flame...).
        private static MeditationFocusDef NativeFocus(Thing t)
        {
            var props = t?.def?.GetCompProperties<CompProperties_MeditationFocus>();
            return (props?.focusTypes != null && props.focusTypes.Count > 0) ? props.focusTypes[0] : null;
        }

        // Rough meditation-type → theme keyword mapping (Stage 3 buildings will define exact pools).
        private static string[] FocusKeywords(string focus)
        {
            if (string.IsNullOrEmpty(focus)) return null;
            string f = focus.ToLowerInvariant();
            // VPE / vanilla meditation-focus TYPES picked via the focus gizmo. ("Flame"/"Morbid"/
            // "Artistic" are already caught by the building-name checks below; these add the rest.)
            if (f.Contains("natural") || f.Contains("nature"))
                return new[] { "wild", "nature", "druid", "gauranlen", "anima", "empath", "ranger", "wood", "harmon" };
            if (f.Contains("science") || f.Contains("techno"))
                return new[] { "techno", "mech", "machine", "nano", "bot", "circuit", "neuro", "oripathy", "scribe" };
            if (f.Contains("archotech"))
                return new[] { "archo", "ascend", "void", "celestial", "star", "empyrean", "chrono" };
            if (f.Contains("wealth"))
                return new[] { "greed", "wealth", "gold", "chrono", "harmon", "skip" };
            if (f.Contains("group"))
                return new[] { "consonance", "puppet", "group", "empath", "harmon", "ally", "anima" };

            if (f.Contains("anima") || f.Contains("tree") || f.Contains("plant") || f.Contains("gauranlen") || f.Contains("flower"))
                return new[] { "wild", "nature", "druid", "gauranlen", "anima", "empath", "ranger", "wood", "harmon" };
            if (f.Contains("brazier") || f.Contains("torch") || f.Contains("campfire") || f.Contains("fire") || f.Contains("flame") || f.Contains("lamp"))
                return new[] { "flame", "conflag", "luminis", "pyro", "fire", "ember", "sun", "warden" };
            if (f.Contains("grave") || f.Contains("sarcoph") || f.Contains("skull") || f.Contains("morbid") || f.Contains("tomb"))
                return new[] { "necro", "death", "deadlife", "hemo", "umbra", "blood", "void", "morbid", "bone" };
            if (f.Contains("sculpt") || f.Contains("art"))
                return new[] { "harmon", "chrono", "skip", "static", "arknight", "glitch", "time", "phase" };
            if (f.Contains("snow") || f.Contains("ice") || f.Contains("water"))
                return new[] { "hydro", "frost", "ice", "water", "aero", "storm" };
            return null;
        }

        // Non-specific meditation: bias the card pool by the pawn's personality traits.
        private static string[] TraitKeywords(Pawn p)
        {
            var traits = p?.story?.traits;
            if (traits == null) return null;
            var set = new System.Collections.Generic.HashSet<string>();
            foreach (var tr in traits.allTraits)
            {
                string d = (tr?.def?.defName ?? "").ToLowerInvariant();
                if (d.Contains("bloodlust") || d.Contains("cannibal") || d.Contains("abrasive"))
                    AddAll(set, "necro", "death", "blood", "hemo", "deadlife");
                if (d.Contains("pyromaniac"))
                    AddAll(set, "flame", "fire", "conflag", "pyro", "ember");
                if (d.Contains("psychopath"))
                    AddAll(set, "void", "umbra", "necro", "shadow", "night");
                if (d.Contains("nightowl") || d.Contains("night_owl"))
                    AddAll(set, "umbra", "void", "shadow", "night", "phase");
                if (d.Contains("kind"))
                    AddAll(set, "empath", "harmon", "wild", "nature", "anima");
                if (d.Contains("ascetic"))
                    AddAll(set, "wild", "nature", "empath", "anima");
                if (d.Contains("greedy") || d.Contains("jealous"))
                    AddAll(set, "harmon", "chrono", "skip", "glitch");
                if (d.Contains("brawler") || d.Contains("tough") || d.Contains("nimble"))
                    AddAll(set, "blader", "guard", "skip", "warden");
                if (d.Contains("transhumanist") || d.Contains("toosmart") || d.Contains("greatmemory"))
                    AddAll(set, "techno", "mechan", "chrono", "static", "arknight");
            }
            return set.Count > 0 ? System.Linq.Enumerable.ToArray(set) : null;
        }

        private static void AddAll(System.Collections.Generic.HashSet<string> set, params string[] vals)
        {
            foreach (var v in vals) set.Add(v);
        }

        private static void ApplyComa(Pawn p, MeditationData med)
        {
            var def = MeditationDefOf.PsychicComa;
            if (def == null || p.health?.hediffSet == null) return;
            if (p.health.hediffSet.GetFirstHediffOfDef(def) != null) return;
            var s = PsycastSynergiesMod.Settings;
            // Minimum 4 hours (10000 ticks), scaling with the pawn's level, how far past the safe window they
            // meditated today, AND accumulated over-meditation saturation (~0.5 day per point) - so a pawn pushed
            // past the safe window day after day faces escalating comas. Cap 8 days.
            int level = p.Psycasts()?.level ?? med.enlightenments;
            float excessHours = Mathf.Max(0f, med.todayTicks / 2500f - (s?.comaSafeHours ?? 6f));
            int dur = Mathf.Clamp(
                10000 + level * 2500 + Mathf.RoundToInt(excessHours * 2500f) + Mathf.RoundToInt(med.medSaturation * 30000f),
                10000, 480000);
            var h = HediffMaker.MakeHediff(def, p);
            p.health.AddHediff(h);
            var disappears = (h as HediffWithComps)?.TryGetComp<HediffComp_Disappears>();
            if (disappears != null) disappears.ticksToDisappear = dur;
            Notify(p, "PS_LetterOverloadLabel".Translate(), "PS_LetterOverloadText".Translate(p.LabelShortCap, dur.ToStringTicksToPeriod()));
        }

        private static void Fx(Pawn p)
        {
            if (p.Spawned && p.Map != null)
                FleckMaker.Static(p.DrawPos, p.Map, FleckDefOf.PsycastAreaEffect, 1.4f);
        }

        private static void Notify(Pawn p, string label, string text)
        {
            if (PawnUtility.ShouldSendNotificationAbout(p))
                Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, new LookTargets(p));
        }
    }

    // Integrate external psylink grants into our Awakening. Hediff_Psylink.PostAdd is the universal
    // chokepoint: the PsychicAmplifier hediff is created by EVERY psylink source - the Empire bestowing
    // ceremony, the blinding ritual, anima-tree linking, and the dev "add psylink" tool. The first time a
    // spawned player colonist gains a psylink outside our own meditation flow, route them into the
    // Enlightenment system (awaken + first path pick) instead of leaving them a path-less psycaster.
    [HarmonyPatch(typeof(Hediff_Psylink), "PostAdd")]
    public static class Patch_ExternalPsylinkAwaken
    {
        public static void Postfix(Hediff_Psylink __instance)
        {
            if (MeditationSystem.internalPsylinkChange) return;        // our own awakening created it
            var pawn = __instance?.pawn;
            if (pawn?.Faction == null || !pawn.Faction.IsPlayer) return;
            if (!pawn.Spawned || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return;   // not at gen time
            if (PsycastSynergiesMod.Settings?.empirePsylinkIntegrate != true) return;
            if (TieringControl.ExternalPsylinkAwakeningDisabled) return;   // a mod owns awakening now
            var med = GameComponent_PsycastSynergies.Instance?.GetMed(pawn, true);
            if (med == null || med.awakened) return;
            MeditationSystem.AwakenExternal(pawn, med);
        }
    }
}
