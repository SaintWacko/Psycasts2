#nullable disable
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using VanillaPsycastsExpanded;

namespace PsycastSynergies
{
    public class CompProperties_SchoolFocus : CompProperties
    {
        public CompProperties_SchoolFocus() { compClass = typeof(CompSchoolFocus); }
    }

    // Added (via Patches/SchoolFocus.xml) to every vanilla/modded meditation-focus building. Lets the
    // player attune that focus to a VPE/vanilla meditation-focus TYPE (Flame, Morbid, Natural, Science,
    // Wealth, Group, Archotech, ...). A non-psycaster who meditates FACING it is then steered toward
    // that type's psycast schools among the Enlightenment awakening cards (MeditationSystem.FocusKeywords).
    // Storing the VPE MeditationFocusDef keeps the naming interoperable as VPE/addons add focus types.
    // Holds static Texture2D fields, so Verse requires the attribute or it warns that assets must be
    // loaded on the main thread. Same rule as SkillFx and Patch_AuraToggleGizmo.
    [StaticConstructorOnStartup]
    public class CompSchoolFocus : ThingComp
    {
        public MeditationFocusDef selectedFocus;

        private static Texture2D iconTex;
        internal static Texture2D GizmoIcon => iconTex != null ? iconTex : (iconTex = ContentFinder<Texture2D>.Get("UI/SchoolFocus", false));

        private static Texture2D[] pilgrimIcons;
        internal static Texture2D PilgrimIcon(int style)
        {
            if (pilgrimIcons == null)
                pilgrimIcons = new[]
                {
                    ContentFinder<Texture2D>.Get("UI/Pilgrim_Unbound", false),
                    ContentFinder<Texture2D>.Get("UI/Pilgrim_Altar", false),
                    ContentFinder<Texture2D>.Get("UI/Pilgrim_Anima", false),
                };
            int i = (style >= 0 && style < 3) ? style : 0;
            return pilgrimIcons[i] ?? GizmoIcon;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref selectedFocus, "ps_focusType");
        }

        // Persistent meditation-focus readout. Vanilla CompMeditationFocus only prints the strength line
        // while a pawn is ACTIVELY meditating (its LastUser returns null 5 ticks after the last use), so the
        // number flickers in and out and is hard to read. We mirror it persistently: cache the live per-user
        // value while it's shown, and keep displaying it (or the intrinsic strength) once vanilla hides it.
        private string cachedFocusStr;

        // Vanilla walls (and natural rock) ship a CompProperties_MeditationFocus (Minimal, 22%), so our
        // comp-add patch picks them up. Don't decorate every wall segment with our focus readout / gizmo.
        private bool ParentIsWall => parent.def.graphicData != null && parent.def.graphicData.linkFlags.HasFlag(LinkFlags.Wall);

        public override string CompInspectStringExtra()
        {
            if (ParentIsWall) return null;
            // The per-thing attunement is no longer settable (the gizmo was removed in favour of the
            // pawn's own default focus), so only saves that already attuned something show the line.
            string typeLine = selectedFocus != null
                ? "PS_FocusTypeLabel".Translate(selectedFocus.LabelCap.ToString()).ToString()
                : null;
            var med = parent.TryGetComp<CompMeditationFocus>();
            if (med == null) return typeLine;
            var user = med.LastUser;
            if (user != null)
                cachedFocusStr = "PS_FocusStrengthFor".Translate(user.LabelShort,
                    parent.GetStatValueForPawn(StatDefOf.MeditationFocusStrength, user).ToStringPercent());
            else if (cachedFocusStr.NullOrEmpty())
            {
                float v = parent.GetStatValue(StatDefOf.MeditationFocusStrength);
                if (v > 0f) cachedFocusStr = "PS_FocusStrength".Translate(v.ToStringPercent());
            }
            // While vanilla is showing its live line (user != null) don't duplicate the strength.
            if (user != null || cachedFocusStr.NullOrEmpty()) return typeLine;
            return typeLine == null ? cachedFocusStr : typeLine + "\n" + cachedFocusStr;
        }

        // NO GIZMO. Every meditation focus in the colony used to carry a "Focus type: ..." button,
        // which meant one on every sculpture, brazier, throne and meditation spot. The same choice
        // now lives on the PAWN (Psycasters main tab, the psycast tab's focus row, and Modern
        // Psycasts UI's focus tiles), so a colonist carries their preference to whatever they sit
        // at. selectedFocus is kept and still scribed so saves that attuned a building keep working -
        // MeditationSystem still reads it first in the focus priority chain.

        internal static Texture2D FocusIcon(MeditationFocusDef def)
        {
            string p = def?.GetModExtension<MeditationFocusExtension>()?.icon;
            return string.IsNullOrEmpty(p) ? null : ContentFinder<Texture2D>.Get(p, false);
        }

        internal static void OpenMenuFor(System.Action<MeditationFocusDef> setter, string nullLabel)
        {
            var opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption(nullLabel, () => setter(null)));
            foreach (var def in DefDatabase<MeditationFocusDef>.AllDefs.OrderBy(d => d.LabelCap.ToString()))
            {
                var fd = def;
                opts.Add(new FloatMenuOption(fd.LabelCap, () => setter(fd), FocusIcon(fd), Color.white));
            }
            if (opts.Count > 1) Find.WindowStack.Add(new FloatMenu(opts));
        }
    }

    // Pawn gizmos. The old per-pawn "Default focus" and "Pilgrim's Path" gizmos moved into the
    // psycast tab (focus = clickable focus-type tiles via Modern Psycasts UI's PawnFocusHooks,
    // with a dropdown fallback; path = a dropdown row above the respec buttons - see
    // Patch_PsycastTabRespec). Only the "Stop meditating" button remains on the pawn itself.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_PawnDefaultFocusGizmo
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
        {
            foreach (var v in values) yield return v;
            if (__instance?.Faction == null || !__instance.Faction.IsPlayer) yield break;
            if (__instance.RaceProps == null || !__instance.RaceProps.Humanlike) yield break;
            if (__instance.GetMainPsylinkSource() == null) yield break;   // psycasters only

            // Stop button for "meditate your ass off" forced meditation (always reachable on the pawn).
            if (ForcedMeditation.On(__instance))
            {
                yield return new Command_Action
                {
                    defaultLabel = "PS_StopMeditating".Translate(),
                    defaultDesc = "PS_StopMeditatingDesc".Translate(),
                    icon = CompSchoolFocus.GizmoIcon,
                    action = () => ForcedMeditation.Stop(__instance)
                };
            }
        }
    }
}
