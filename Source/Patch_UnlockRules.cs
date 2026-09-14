#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using VEF.Abilities;
using VanillaPsycastsExpanded;
using AbilityDef = VEF.Abilities.AbilityDef;

namespace PsycastSynergies
{
    internal static class PsycastUnlockRules
    {
        internal static IEnumerable<PsycasterPathDef> SelectablePaths
            => DefDatabase<PsycasterPathDef>.AllDefs
                .Where(p => p != null && p.HasAbilities && !string.IsNullOrEmpty(p.defName))
                .OrderBy(p => p.tab ?? string.Empty)
                .ThenBy(p => p.order)
                .ThenBy(p => p.label ?? string.Empty);

        internal static int AutoUnlockedPathCount
            => PsycastSynergiesMod.Settings?.autoUnlockedPaths?.Count ?? 0;

        private static bool AppliesTo(Pawn pawn)
            => pawn != null && pawn.RaceProps?.Humanlike == true
            && (pawn.Faction?.IsPlayer == true || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony);

        internal static void SyncAllAutoUnlockedPaths()
        {
            if (Current.ProgramState != ProgramState.Playing) return;
            var seen = new HashSet<Pawn>();
            SyncSet(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonistsAndPrisoners, seen);
            SyncSet(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_SlavesOfColony, seen);
        }

        private static void SyncSet(IEnumerable<Pawn> pawns, HashSet<Pawn> seen)
        {
            if (pawns == null) return;
            foreach (var pawn in pawns)
                if (pawn != null && seen.Add(pawn))
                    SyncAutoUnlockedPaths(pawn.Psycasts());
        }

        internal static void SyncAutoUnlockedPaths(Hediff_PsycastAbilities psy)
        {
            var pawn = psy?.pawn;
            var settings = PsycastSynergiesMod.Settings;
            if (psy == null || pawn == null || settings?.autoUnlockedPaths == null || settings.autoUnlockedPaths.Count == 0) return;
            if (!AppliesTo(pawn)) return;
            psy.unlockedPaths ??= new List<PsycasterPathDef>();
            bool changed = false;
            for (int i = 0; i < settings.autoUnlockedPaths.Count; i++)
            {
                string defName = settings.autoUnlockedPaths[i];
                if (string.IsNullOrEmpty(defName)) continue;
                var path = DefDatabase<PsycasterPathDef>.GetNamedSilentFail(defName);
                if (path == null || !path.HasAbilities || psy.unlockedPaths.Contains(path)) continue;
                psy.unlockedPaths.Add(path);
                changed = true;
            }
            if (changed) PerfCache.Bump();
        }

        private static bool NeedsPsyLevel(Pawn pawn, AbilityDef ability, out int requiredLevel)
        {
            requiredLevel = 0;
            if (pawn == null || ability == null) return false;
            var s = PsycastSynergiesMod.Settings;
            if (s?.restrictUnlocksByPsyLevel != true) return false;
            requiredLevel = Mathf.Max(0, SkillSystem.LevelReq(ability, 1));
            return pawn.Psycasts()?.level < requiredLevel;
        }

        internal static bool TreeUnlockBlocked(Pawn pawn, AbilityDef ability)
        {
            var s = PsycastSynergiesMod.Settings;
            if (pawn == null || ability == null || s == null) return false;
            if (s.disableTreeAbilityUnlocks) return true;
            return NeedsPsyLevel(pawn, ability, out _);
        }

        internal static bool PsytrainerBlocked(Pawn pawn, AbilityDef ability, out string reason)
        {
            reason = null;
            var s = PsycastSynergiesMod.Settings;
            if (pawn == null || ability == null || s?.restrictUnlocksByPsyLevel != true) return false;
            if (s.allowPsytrainerBypassLevelRequirement) return false;
            if (!NeedsPsyLevel(pawn, ability, out int requiredLevel)) return false;
            reason = "PS_MsgPsytrainerLevelRequired".Translate(ability.LabelCap, requiredLevel).ToString();
            return true;
        }
    }

    [HarmonyPatch(typeof(Hediff_PsycastAbilities), nameof(Hediff_PsycastAbilities.InitializeFromPsylink))]
    public static class Patch_AutoUnlockedPaths_Init
    {
        static void Postfix(Hediff_PsycastAbilities __instance)
            => PsycastUnlockRules.SyncAutoUnlockedPaths(__instance);
    }

[HarmonyPatch(typeof(Hediff_PsycastAbilities), nameof(Hediff_PsycastAbilities.ChangeLevel), new Type[] { typeof(int), typeof(bool) })]
    public static class Patch_AutoUnlockedPaths_Level
    {
        static void Postfix(Hediff_PsycastAbilities __instance)
            => PsycastUnlockRules.SyncAutoUnlockedPaths(__instance);
    }

    [HarmonyPatch(typeof(Hediff_PsycastAbilities), nameof(Hediff_PsycastAbilities.ExposeData))]
    public static class Patch_AutoUnlockedPaths_Load
    {
        static void Postfix(Hediff_PsycastAbilities __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                PsycastUnlockRules.SyncAutoUnlockedPaths(__instance);
        }
    }

    [HarmonyPatch(typeof(AbilityExtension_Psycast), nameof(AbilityExtension_Psycast.PrereqsCompleted), new Type[] { typeof(CompAbilities) })]
    public static class Patch_AbilityUnlockRestrictions
    {
        static void Postfix(AbilityExtension_Psycast __instance, CompAbilities compAbilities, ref bool __result)
        {
            if (!__result || __instance?.abilityDef == null) return;
            var pawn = compAbilities?.parent as Pawn;
            if (PsycastUnlockRules.TreeUnlockBlocked(pawn, __instance.abilityDef)) __result = false;
        }
    }

    [HarmonyPatch(typeof(CompPsytrainer), nameof(CompPsytrainer.CanBeUsedBy))]
    public static class Patch_PsytrainerRestrictions_CanUse
    {
        static void Postfix(CompPsytrainer __instance, Pawn p, ref AcceptanceReport __result)
        {
            if (!__result.Accepted) return;
            var ability = (__instance?.props as CompProperties_UseEffectGiveAbility)?.ability;
            if (PsycastUnlockRules.PsytrainerBlocked(p, ability, out string reason)) __result = reason;
        }
    }

    [HarmonyPatch(typeof(CompPsytrainer), nameof(CompPsytrainer.DoEffect))]
    public static class Patch_PsytrainerRestrictions_DoEffect
    {
        static bool Prefix(CompPsytrainer __instance, Pawn usedBy)
        {
            var ability = (__instance?.props as CompProperties_UseEffectGiveAbility)?.ability;
            if (!PsycastUnlockRules.PsytrainerBlocked(usedBy, ability, out string reason)) return true;
            Messages.Message(reason, usedBy, MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
