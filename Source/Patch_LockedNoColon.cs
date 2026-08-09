#nullable disable
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using VanillaPsycastsExpanded.UI;
using Verse;

namespace PsycastSynergies
{
    // VPE's psycast tab hardcodes the locked-path button label as "VPE.Locked".Translate() + ": "
    // + def.lockedReason. Under our card-based unlock scheme (lockPathsToEnlightenment) every path
    // is locked and most reasons are nulled, which leaves a dangling "Locked: " - and the few
    // reasons that survive ("Imperials only", "Tribals only") are misleading anyway, since unlocks
    // come from awakening cards rather than this button. Retarget the concat to a helper:
    //   - our progression ON: the button reads "Undiscovered" (the tree is not locked, it simply
    //     has not revealed itself yet - awakening cards are the only way in);
    //   - our progression OFF: VPE's own "Locked" + genuine reasons still show, only the dangling
    //     colon is dropped.
    // Modern Psycasts UI used to draw a plain "VPE.Locked" label on locked tree tiles. Its P5/R5 pass
    // moved off that key entirely (see TryWireModernUI), so we now override the composed caption at
    // its memoised source instead of transpiling a literal.
    [HarmonyPatch(typeof(ITab_Pawn_Psycasts), "DoPaths")]
    public static class Patch_LockedNoColon
    {
        private static readonly PerfCache.LangCache LblUndiscovered = new PerfCache.LangCache("PS_Undiscovered");

        // True while paths unlock ONLY through the awakening cards - i.e. "locked" is the wrong word.
        private static bool Undiscovered => PsycastSynergiesMod.Settings?.lockPathsToEnlightenment == true;

        // Signature must exactly mirror string.Concat(string, string, string) so the retargeted
        // call site keeps a valid stack transition.
        public static string LockedLabel(string locked, string sep, string reason)
        {
            if (Undiscovered) return LblUndiscovered.Value;
            if (string.IsNullOrWhiteSpace(reason)) return locked;
            return locked + sep + reason;
        }

        // Key fed to Translate() at Modern Psycasts UI's narrow-tile fallback label site. When our
        // progression is off we hand back Modern UI's OWN key, so its wording is preserved verbatim.
        public static string LockedKey() => Undiscovered ? "PS_Undiscovered" : "MPUI_LockedShort";

        // Anchor on the ldstr ": " and retarget the next 3-string Concat call. Operand-only swap
        // (same opcode, same static string->string shape) keeps labels and blocks intact. If VPE
        // reshapes the method, log and return the IL untouched - a dangling colon is not worth a
        // failed PatchAll.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var concat3 = AccessTools.Method(typeof(string), nameof(string.Concat),
                new[] { typeof(string), typeof(string), typeof(string) });
            var helper = AccessTools.Method(typeof(Patch_LockedNoColon), nameof(LockedLabel));
            bool armed = false, done = false;
            for (int i = 0; i < list.Count && !done; i++)
            {
                var ci = list[i];
                if (!armed)
                {
                    if (ci.opcode == OpCodes.Ldstr && (ci.operand as string) == ": ") armed = true;
                }
                else if (ci.Calls(concat3))
                {
                    ci.operand = helper;
                    done = true;
                }
            }
            if (!done)
                Log.Warning("[Psycasts²] Locked-label patch: VPE's DoPaths IL did not match; keeping the vanilla \"Locked:\" label.");
            return list;
        }

        // ---- Modern Psycasts UI (soft, reflection only) ----------------------------------------
        // Modern Psycasts UI no longer references VPE's "VPE.Locked" key anywhere: its P5/R5 pass moved
        // the locked-tile caption onto its own MPUI_LockedShort / MPUI_LockedNeeds keys, composed inside
        // DrawCache.LockedChip and MEMOISED per path def. So we postfix that single chokepoint rather
        // than chasing string literals: BOTH the classic tile grid (ModernPsycastsDrawer.DrawTreeTile)
        // and the adaptive shelf layout (LayoutAdaptive) funnel their caption through it, and a postfix
        // also wraps the dictionary-hit early return - patching the builder lambda would miss every
        // cached call and only ever fire once per def.
        //
        // NOTE: Modern UI's PartnerApi lists DrawTreeTile's signature as frozen, but the guarantee only
        // ever covered the SIGNATURE; the string inside it was never part of the contract. Prefer
        // overriding composed output over pattern-matching a partner's literals.
        public static void TryWireModernUI(Harmony harmony)
        {
            try
            {
                var cache = AccessTools.TypeByName("ModernPsycastsUI.DrawCache");
                var chip = cache == null ? null : AccessTools.Method(cache, "LockedChip");
                if (chip == null) return;   // Modern Psycasts UI absent or reshaped
                harmony.Patch(chip, postfix: new HarmonyMethod(typeof(Patch_LockedNoColon), nameof(LockedChipPostfix)));

                // DrawTreeTile keeps ONE independent literal: the LabelFit fallback used when the
                // caption is too wide for the tile. "Undiscovered" is longer than "Locked", so that
                // fallback is genuinely reachable on compact tiles - swap it too.
                var drawer = AccessTools.TypeByName("ModernPsycastsUI.ModernPsycastsDrawer");
                var tile = drawer == null ? null : AccessTools.Method(drawer, "DrawTreeTile");
                if (tile != null)
                    harmony.Patch(tile, transpiler: new HarmonyMethod(typeof(Patch_LockedNoColon), nameof(ModernTranspiler)));
            }
            catch (System.Exception e)
            {
                Log.Warning("[Psycasts²] Undiscovered label: could not wire Modern Psycasts UI: " + e.Message);
            }
        }

        // Overriding the RESULT keeps us independent of which key or format string Modern UI used to
        // build it. Runs on cache hits too, so a mid-game settings flip is reflected immediately.
        public static void LockedChipPostfix(ref string __result)
        {
            if (Undiscovered) __result = LblUndiscovered.Value;
        }

        public static IEnumerable<CodeInstruction> ModernTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var helper = AccessTools.Method(typeof(Patch_LockedNoColon), nameof(LockedKey));
            int swapped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldstr || (list[i].operand as string) != "MPUI_LockedShort") continue;
                list[i].opcode = OpCodes.Call;   // in-place: any labels/blocks on this instruction survive
                list[i].operand = helper;
                swapped++;
            }
            if (swapped == 0)
                Log.Warning("[Psycasts²] Undiscovered label: Modern Psycasts UI's narrow-tile fallback label "
                          + "did not match; an over-wide caption can still fall back to \"Locked\".");
            return list;
        }
    }
}
