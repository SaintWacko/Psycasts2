#!/usr/bin/env bash
# Builds texture-compare.html with every PNG inlined as a data: URI.
# The mockup sandbox blocks local file loads, so relative <img src> renders nothing.
# Run from the mod root:  bash .circinus/mockups/build-compare.sh
set -u
OUT=.circinus/mockups/texture-compare.html
T=Textures
# Rejected generated candidates. They live OUTSIDE Textures/ on purpose: Textures/ is a shipped
# content directory, so anything left in it would upload to the Workshop.
C=.circinus/texture-candidates

# emit an <img> with the file's bytes base64'd inline
img() {
  if [ -f "$1" ]; then
    printf '<img src="data:image/png;base64,%s" alt="">' "$(base64 -w0 "$1")"
  else
    printf '<span class="miss">missing</span>'
  fi
}
tile()  { printf '<div class="tile %s">' "$2"; img "$1"; printf '</div>'; }
empty() { printf '<div class="empty">%s</div>' "$1"; }

dims() {
  [ -f "$1" ] || { printf '?'; return; }
  od -An -tu4 -j16 -N8 --endian=big "$1" 2>/dev/null | tr -s ' ' | sed 's/^ //;s/ / × /'
}

# ---------- head ----------
cat > "$OUT" <<'HEAD'
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Psycasts² — texture comparison</title>
<style>
  :root{--bg:#0e1013;--panel:#1b1f23;--band:#151a1e;--border:#2f3337;--hair:#22272b;
        --fg:#e3e3e3;--dim:#9ea6b3;--dim2:#69727e;--accent:#73bfff;--gold:#d9b859;--good:#66d966;--bad:#e65959;}
  *{box-sizing:border-box}
  body{margin:0;padding:20px;background:#08090b;font-family:"Segoe UI",Tahoma,Verdana,sans-serif;font-size:13px;color:var(--fg)}
  h1{font-size:22px;font-weight:400;margin:0 0 2px}
  .blurb{color:var(--dim);font-size:12px;margin:0 0 16px}
  .wrap{max-width:1400px}
  .notice{background:var(--band);border:1px solid var(--border);padding:10px 12px;margin-bottom:18px;font-size:12px;color:var(--dim);line-height:1.55}
  .notice b{color:var(--fg);font-weight:600}
  .notice code{color:var(--accent);font-family:Consolas,monospace}
  .sect{font-size:15px;color:var(--fg);margin:26px 0 3px}
  .sectsub{color:var(--dim2);font-size:11px;margin:0 0 10px;line-height:1.5}
  .row{display:flex;gap:14px;background:var(--panel);border:1px solid var(--border);padding:12px;margin-bottom:10px}
  .row.warn{border-color:#5a3a3a}
  .meta{width:230px;flex:none}
  .meta .nm{font-weight:600;font-size:13px;word-break:break-all}
  .meta .px{color:var(--dim2);font-size:11px;margin-top:2px}
  .meta .use{font-size:11px;margin-top:7px;line-height:1.55;color:var(--dim)}
  .meta .use.good{color:var(--good)} .meta .use.bad{color:var(--bad)}
  .slot{flex:1;min-width:0}
  .slotlbl{font-size:11px;color:var(--dim2);margin-bottom:5px;display:flex;justify-content:space-between}
  .pair{display:flex;gap:8px;flex-wrap:wrap}
  .tile{width:140px;height:140px;flex:none;display:flex;align-items:center;justify-content:center;border:1px solid var(--hair);overflow:hidden}
  .tile.dark{background:#20252a}
  .tile.check{background-color:#8a8a8a;
    background-image:linear-gradient(45deg,#6f6f6f 25%,transparent 25%,transparent 75%,#6f6f6f 75%),
                     linear-gradient(45deg,#6f6f6f 25%,transparent 25%,transparent 75%,#6f6f6f 75%);
    background-size:16px 16px;background-position:0 0,8px 8px}
  .tile.tiny{width:56px;height:56px}
  .tile img{max-width:100%;max-height:100%}
  .empty{flex:1;min-width:140px;min-height:140px;border:1px dashed #3a4048;display:flex;align-items:center;
         justify-content:center;color:var(--dim2);font-size:11px;text-align:center;padding:8px;line-height:1.5}
  .miss{color:var(--bad);font-size:10px}
  .grid{display:flex;flex-wrap:wrap;gap:8px}
  .cell{width:112px;background:var(--panel);border:1px solid var(--border);padding:6px}
  .cell .t{width:98px;height:98px;display:flex;align-items:center;justify-content:center;background:#20252a;border:1px solid var(--hair)}
  .cell .t img{max-width:92px;max-height:92px}
  .cell .n{font-size:10px;color:var(--dim);margin-top:4px;word-break:break-all;line-height:1.3}
  .cell .d{font-size:10px;color:var(--dim2)}
  .foot{margin-top:26px;padding-top:12px;border-top:1px solid var(--border);color:var(--dim2);font-size:11px;line-height:1.6}
  .k{display:inline-block;padding:1px 6px;border:1px solid var(--border);color:var(--dim);font-size:11px;margin-right:4px}
</style>
</head>
<body><div class="wrap">
<h1>Texture comparison</h1>
<p class="blurb">Psycasts² ships 71 textures. Nothing is ever overwritten — the generator refuses to replace a file and writes a new name, so every original survives.</p>
<div class="notice">
  Every image on this page is <b>embedded directly in the file</b>, so it renders with no file access at all.
  Each piece is shown on a <b>dark panel</b> (how the mod's own UI shows it) and, where alpha matters, on a
  <b>light checkerboard</b> that reveals white-on-transparent art and alpha fringing.
</div>
HEAD

# ---------- pilot ----------
cat >> "$OUT" <<'P1'
<p class="sect" style="color:var(--gold)">Pilot results — 4 subjects, 2 variants each</p>
<p class="sectsub">Generated against <b>vanilla-core-authored</b> via OpenAI (gpt-image-2); Gemini and xAI have no key configured.
Transparency is honoured. <b style="color:var(--bad)">Every result came back 128 × 128</b> despite requesting 1K — the profile's own
size convention ("typically 128×128 px, about 128 px per tile") is written into the prompt and the output obeyed it.</p>
P1

pilot_row() { # name pxnote verdictclass verdict cur varA varB checkfile
  printf '<div class="row%s"><div class="meta"><div class="nm">%s</div><div class="px">%s</div><div class="use %s">%s</div></div>' \
    "$( [ "$3" = bad ] && echo ' warn' )" "$1" "$2" "$3" "$4" >> "$OUT"
  printf '<div class="slot"><div class="slotlbl"><span>current · variant A · variant B</span><span>dark · checker</span></div><div class="pair">' >> "$OUT"
  tile "$5" dark >> "$OUT"; tile "$6" dark >> "$OUT"; tile "$7" dark >> "$OUT"; tile "$8" check >> "$OUT"
  printf '</div></div></div>\n' >> "$OUT"
}

pilot_row "Things/PS_PilgrimAltar.png" "current 256² → candidate 128²" good \
  "Style is a clear win: mossy cel-shaded stone with a dark outline, reads as a vanilla building next to the flat vector disc. But it is half the resolution of what it replaces, and the scattered rubble ring will look like clutter on a floor tile." \
  "$T/Things/PS_PilgrimAltar.png" "$C/PS_PilgrimAltar_a.png" "$C/PS_PilgrimAltar_b.png" "$C/PS_PilgrimAltar_a.png"

pilot_row "UI/Pilgrim_Altar.png" "current 128² → candidate 128²" good \
  "Works. Tiered stone and a crystal, strong silhouette, holds up as a dropdown icon. Variant B is the stronger of the two." \
  "$T/UI/Pilgrim_Altar.png" "$C/ui_pilgrim_altar_a.png" "$C/ui_pilgrim_altar_b.png" "$C/ui_pilgrim_altar_b.png"

pilot_row "UI/Specs/maelstrom.png" "drawn at 24–48 px, tinted at runtime" bad \
  "The predicted failure. The current glyph is one flat colour so GUI.color can tint it per node state. The candidate is a full-colour painted swirl with rocks: tinting yields mud, and at 24–48 px it collapses into a blob. Fifty of these would also not match each other." \
  "$T/UI/Specs/maelstrom.png" "$C/spec_maelstrom_a.png" "$C/spec_maelstrom_b.png" "$C/spec_maelstrom_a.png"

pilot_row "UI/Sparkle.png" "additive blend, tinted per effect" bad \
  "Same problem. The current sprite is a white alpha ramp: additive blending turns it into light. The candidate is baked gold with a dark outline and debris specks — additively blended, a dark outline adds grey haze instead of glow, and the baked colour fights every tint." \
  "$T/UI/Sparkle.png" "$C/fx_sparkle_a.png" "$C/fx_sparkle_b.png" "$C/fx_sparkle_a.png"

# ---------- group A remaining ----------
cat >> "$OUT" <<'P2'
<p class="sect">Drawn art — still to generate</p>
<p class="sectsub">The remaining pieces the image model is right for, once the resolution question is settled.</p>
P2

a_row() { # file note
  printf '<div class="row"><div class="meta"><div class="nm">%s</div><div class="px">%s</div><div class="use">%s</div></div>' \
    "${1#$T/}" "$(dims "$1")" "$3" >> "$OUT"
  printf '<div class="slot"><div class="slotlbl"><span>current</span><span>dark · checker</span></div><div class="pair">' >> "$OUT"
  tile "$1" dark >> "$OUT"; tile "$1" check >> "$OUT"; empty "candidate goes here" >> "$OUT"
  printf '</div></div></div>\n' >> "$OUT"
}

a_row "$T/UI/CardBack.png" "" "Back of the awakening cards — the largest asset and the most visible, filling the screen at every path pick. At 128² this would be destroyed."
a_row "$T/UI/SpecTreeMap.png" "" "Backdrop behind the constellation. Must stay dark and low-contrast; nodes and links draw on top of it."
a_row "$T/World/Expanding/PilgrimSite.png" "" "World-map site icon, drawn small over the planet, so it needs a hard silhouette."
a_row "$T/UI/SchoolFocus.png" "" "Meditation-focus icon on the focus picker."
a_row "$T/UI/Pilgrim_Anima.png" "" "Journey-type icon. Must match Pilgrim_Altar and Pilgrim_Unbound as a set of three."
a_row "$T/UI/Pilgrim_Unbound.png" "" "Journey-type icon. Must match the other two."

# ---------- grids ----------
grid_open() { printf '<p class="sect">%s</p><p class="sectsub">%s</p><div class="grid">\n' "$1" "$2" >> "$OUT"; }
grid_cell() {
  printf '<div class="cell"><div class="t">' >> "$OUT"; img "$1" >> "$OUT"
  printf '</div><div class="n">%s</div><div class="d">%s</div></div>\n' "$(basename "$1" .png)" "$(dims "$1")" >> "$OUT"
}
grid_close() { printf '</div>\n' >> "$OUT"; }

grid_open "Specialization glyphs — 50 icons" \
  "Single-colour, drawn at roughly 24–48 px, recoloured at runtime through GUI.color. The pilot shows what the model returns for these. SVG redraw keeps them flat, sharp and consistent as a set."
for f in "$T"/UI/Specs/*.png; do grid_cell "$f"; done
grid_close

grid_open "Particle and UI primitives — 12 sprites" \
  "Additive FX sprites and button glyphs. Not pictures of anything: alpha ramps that get tinted and blended. Painted art visibly breaks the ascension effects and the card-reveal bursts."
for f in "$T"/FX/Ascension/*.png "$T/UI/Sparkle.png" "$T/UI/Glow.png" "$T/UI/Accept.png" "$T/UI/Gizmos/aura_toggle.png" "$T/UI/MainButtons/Psycasters.png"; do grid_cell "$f"; done
grid_close

# ---------- foot ----------
cat >> "$OUT" <<'FOOT'
<div class="foot">
  <span class="k">71</span>textures total &nbsp;·&nbsp;
  <span class="k">8</span>suited to the image model &nbsp;·&nbsp;
  <span class="k">50</span>glyphs better served by SVG &nbsp;·&nbsp;
  <span class="k">13</span>primitives to leave alone
  <br>
  Style: <b>vanilla-core-authored</b> — desaturated browns and warm greys, 2 px near-black outline, flat cel shading,
  hard-edged transparency, 128 px per tile. <b>Described, not measured</b> (n = 0): Core art lives inside the Unity
  archives, and this machine has no unpacker, so the numbers are written from knowledge rather than sampled.
  <br>
  Candidates live in <b>.circinus/texture-candidates/</b>, deliberately outside <b>Textures/</b> — that is a shipped content
  directory, and anything left in it would upload to the Workshop. Verdict: rejected, too far from vanilla. See Pyxis card #189.
</div>
</div></body></html>
FOOT

echo "wrote $OUT ($(du -h "$OUT" | cut -f1))"
