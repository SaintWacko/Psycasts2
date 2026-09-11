#nullable disable
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using VEF.Abilities;
using VanillaPsycastsExpanded;
using VanillaPsycastsExpanded.UI;
using AbilityDef = VEF.Abilities.AbilityDef;

namespace PsycastSynergies
{
    public static class UnlockControls
    {
        public static int SelectedAutoUnlockPathCount()
            => PsycastSynergiesMod.Settings?.autoUnlockPathDefs?.Count ?? 0;

        public static string SelectedAutoUnlockPathSummary()
        {
            var names = PsycastSynergiesMod.Settings?.autoUnlockPathDefs;
            if (names == null || names.Count == 0) return "PS_SetAutoUnlockPathsNone".Translate().ToString();
            var labels = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                var path = DefDatabase<PsycasterPathDef>.GetNamedSilentFail(names[i]);
                if (path != null && path.HasAbilities) labels.Add(path.LabelCap.ToString());
            }
            return labels.Count == 0 ? "PS_SetAutoUnlockPathsNone".Translate().ToString() : string.Join(", ", labels.OrderBy(x => x).ToArray());
        }

        public static void OpenAutoUnlockPathMenu()
        {
            var s = PsycastSynergiesMod.Settings;
            if (s == null) return;
            if (s.autoUnlockPathDefs == null) s.autoUnlockPathDefs = new List<string>();
            var opts = new List<FloatMenuOption>();
            foreach (var path in DefDatabase<PsycasterPathDef>.AllDefs.OrderBy(p => p.LabelCap.ToString()))
            {
                if (!path.HasAbilities) continue;
                var captured = path;
                bool on = s.autoUnlockPathDefs.Contains(captured.defName);
                string label = (on ? "\u2713 " : "") + captured.LabelCap;
                opts.Add(new FloatMenuOption(label, () =>
                {
                    if (s.autoUnlockPathDefs.Contains(captured.defName)) s.autoUnlockPathDefs.Remove(captured.defName);
                    else s.autoUnlockPathDefs.Add(captured.defName);
                }));
            }
            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }

        public static void EnsureAutoUnlocked(Pawn pawn, Hediff_PsycastAbilities psy = null)
        {
            var s = PsycastSynergiesMod.Settings;
            var names = s?.autoUnlockPathDefs;
            if (pawn == null || names == null || names.Count == 0) return;
            psy ??= pawn.Psycasts();
            if (psy?.unlockedPaths == null) return;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].NullOrEmpty()) continue;
                var path = DefDatabase<PsycasterPathDef>.GetNamedSilentFail(names[i]);
                if (path == null || !path.HasAbilities || psy.unlockedPaths.Contains(path)) continue;
                psy.UnlockPath(path);
            }
        }

        public static void SyncAutoUnlockedPaths()
        {
            if (SelectedAutoUnlockPathCount() <= 0) return;
            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                var pawns = maps[m].mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var p = pawns[i];
                    if (p == null || p.Dead) continue;
                    EnsureAutoUnlocked(p);
                }
            }
        }

        public static bool CanUnlockByPsyLevel(Pawn pawn, AbilityDef ability, bool viaPsytrainer, out string reason)
        {
            reason = null;
            var s = PsycastSynergiesMod.Settings;
            if (pawn == null || ability == null || s == null || !s.requirePsycasterLevelForUnlock) return true;
            if (viaPsytrainer && !s.requirePsycasterLevelForPsytrainers) return true;
            int psyLevel = pawn.Psycasts()?.level ?? 0;
            int req = SkillSystem.LevelReq(ability, 1);
            if (psyLevel >= req) return true;
            reason = viaPsytrainer
                ? "PS_MsgUnlockPsyLevelPsytrainer".Translate(ability.LabelCap, req, psyLevel).ToString()
                : "PS_MsgUnlockPsyLevel".Translate(req, psyLevel).ToString();
            return false;
        }

        public static bool BlocksTreeUnlock(Pawn pawn, Hediff_PsycastAbilities hediff, CompAbilities comp, AbilityDef ability, out string reason)
        {
            reason = null;
            if (pawn == null || hediff == null || comp == null || ability == null || comp.HasAbility(ability)) return false;
            if (ability.GetModExtension<AbilityExtension_Psycast>() == null) return false;
            EnsureAutoUnlocked(pawn, hediff);
            if (PsycastSynergiesMod.Settings?.disableTreePsycastUnlocks == true)
            {
                reason = "PS_MsgTreeUnlockDisabled".Translate().ToString();
                return true;
            }
            return !CanUnlockByPsyLevel(pawn, ability, false, out reason);
        }

        public static string DisabledTreeTooltip(Pawn pawn, AbilityDef ability)
        {
            if (pawn == null || ability == null) return null;
            if (PsycastSynergiesMod.Settings?.disableTreePsycastUnlocks == true)
                return "PS_TipTreeUnlocksDisabled".Translate().ToString();
            if (!CanUnlockByPsyLevel(pawn, ability, false, out _))
                return "PS_TipUnlockPsyLevel".Translate(SkillSystem.LevelReq(ability, 1), pawn.Psycasts()?.level ?? 0).ToString();
            return null;
        }
    }

    [HarmonyPatch(typeof(PsycastsUIUtility), nameof(PsycastsUIUtility.DrawAbility))]
    public static class Patch_BlockTreeUnlocks
    {
        static void Prefix(Rect inRect, AbilityDef ability)
        {
            var comp = PsycastsUIUtility.CompAbilities;
            var hediff = PsycastsUIUtility.Hediff;
            var pawn = hediff?.pawn;
            var ext = ability?.GetModExtension<AbilityExtension_Psycast>();
            if (pawn == null || comp == null || hediff == null || ext == null || comp.HasAbility(ability)) return;
            if (!ext.PrereqsCompleted(comp) || hediff.points < 1) return;
            if (!UnlockControls.BlocksTreeUnlock(pawn, hediff, comp, ability, out string reason)) return;
            if (Widgets.ButtonInvisible(inRect, false))
                Messages.Message(reason, pawn, MessageTypeDefOf.RejectInput, false);
        }
    }

    [HarmonyPatch(typeof(CompPsytrainer), nameof(CompPsytrainer.CanBeUsedBy))]
    public static class Patch_PsytrainerUseGate
    {
        static void Postfix(CompPsytrainer __instance, Pawn p, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || p == null) return;
            var ability = (__instance?.Props as CompProperties_UseEffect_Psytrainer)?.ability;
            if (!UnlockControls.CanUnlockByPsyLevel(p, ability, true, out string reason))
                __result = reason;
        }
    }

    [HarmonyPatch(typeof(CompPsytrainer), nameof(CompPsytrainer.DoEffect))]
    public static class Patch_PsytrainerUseGateEffect
    {
        static bool Prefix(CompPsytrainer __instance, Pawn usedBy)
        {
            var ability = (__instance?.Props as CompProperties_UseEffect_Psytrainer)?.ability;
            if (UnlockControls.CanUnlockByPsyLevel(usedBy, ability, true, out string _)) return true;
            if (usedBy?.Faction != null && usedBy.Faction.IsPlayer)
                Messages.Message("PS_MsgUnlockPsyLevelPsytrainer".Translate(ability.LabelCap, SkillSystem.LevelReq(ability, 1), usedBy.Psycasts()?.level ?? 0),
                    usedBy, MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
