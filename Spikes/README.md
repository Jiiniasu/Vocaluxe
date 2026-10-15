# MultiWindowSpike

Go/No‑Go harness for the multi‑monitor refactor (plan `~/.opencode/plan/multi-window-multi-monitor-plan.md` §11):
proves which per‑monitor‑window architecture works on the exact production stack (OpenTK 4.9.4 / GLFW,
GL 2.1 compatibility) before the main solution is touched:

- `giant` — today's single giant fullscreen window (the true baseline),
- `vfb` — one shared‑context VFB render + N slice blits (the original primary),
- `rerender` — N independent contexts, each re‑rendering its own panel (the fallback),
- `sharedrender` — **hybrid**: shared context (textures live once in VRAM) + each window re‑renders
  its own panel directly into its backbuffer (no VFB, no blit).

Kept **outside** `Vocaluxe.sln` on purpose (no `Solution` item, references the same
`OpenTK 4.9.4` NuGet package the game uses, net10.0 like `Vocaluxe.csproj`).

## Files
| file | role |
|---|---|
| `Program.cs` | args (`--probe` / `--matrix` / `--mode giant\|vfb\|rerender\|sharedrender --panels N --quads Q --vsync 0\|1 --seconds S`) |
| `Rig.cs` | N per‑monitor `NativeWindow` slots; shared context via `NativeWindowSettings.SharedContext`; monitor assignment; Windows +1px DWM fullscreen workaround (mirrors `CDrawBaseGlfw`) |
| `Vfb.cs` | POT FBO texture + `BlitPanel` — the copy‑paste donor for the production `COpenGL` implementation |
| `Scene.cs` | synthetic 1280×720 N‑panel scene in Vocaluxe's immediate‑mode shape (per‑quad 1×1 white texture bind, Blend on/off, `Color4`+`TexCoord2`+`Vertex`) |
| `Stats.cs` | per‑op µs stats (mean/p99) + JSON + GATE‑PERF / GATE‑SHARED evaluation |
| `Bench.cs` | probe + bench drivers, `Spikes/results/*.json` output; `sharedrender` runs include a per‑slot content (pixel) PASS/FAIL check |
| `results/` | per‑run JSON + `summary.txt` (git‑ignored) |

## Run (from the repo root)
```powershell
dotnet build Spikes\MultiWindowSpike\MultiWindowSpike.csproj -c Release
$exe = 'Spikes\MultiWindowSpike\bin\Release\net10.0\MultiWindowSpike.exe'

# GATE-API (+ fullscreen/minimize round-trips + per-panel pixel verification)
& $exe --probe --panels 6

# one bench cell
& $exe --mode vfb --panels 6 --quads 1000 --vsync 0 --seconds 20

# full matrix (24 runs ≈ 10–12 min) including the 60 s vsync-ed vfb + sharedrender endurance runs
& $exe --matrix
```

## Gates (plan §11.3)
- **GATE‑API** (probe): shared texture visible from every slot (`GL.IsTexture`); N windows present;
  `Monitors.GetMonitors()`; fullscreen in/out ×3 with content readback (catches the exclusive‑present
  freeze); minimize/restore ×2; **each slot presents its own panel slice of the shared VFB**
  (pixel check vs the panel's hue, scene content verified pre‑blit as well).
- **GATE‑SMOKE**: the 60 s `vfb n=6 q=1000 vs=1` endurance cell (plus the matrix surviving as a whole).
  The 300 s / manual‑Alt‑Tab / toggle‑while‑in‑app forms belong to the **in‑app** matrix (§11.4) because
  they need the real UI.
- **GATE‑PERF** (n=6, q=1000, vs=0):
  1. `vfb.frameMs ≤ giant.frameMs × 1.05 + 2 ms`
  2. per‑slot `rebind+blit+swap` mean ` < 500 µs`
  3. `vfb.fps ≥ 0.95 × rerender.fps`
- **GATE‑SHARED** (n=6, q=1000, vs=0) — same shape for the hybrid challenger. Gate‑2 is *relative*
  instead of the absolute 500 µs, because on this rig **every** multi‑window mode (vfb, rerender AND
  sharedrender) pays ~0.8–1 ms of DWM surface activation per `MakeCurrent`; the meaningful claim is
  that sharing adds no extra per‑slot cost:
  1. `sharedrender.frameMs ≤ giant.frameMs × 1.05 + 2 ms`
  2. per‑slot `rebind+render+swap` ≤ `1.25 ×` rerender's own per‑slot cost
  3. `sharedrender.fps ≥ 0.95 × rerender.fps`

## Results (2026‑10‑15, dev rig: 4 monitors 2560×1440/1920×1080, AMD RX 6900 XT)
- **GATE‑API: PASS** — 6 shared‑context slots; slot‑0 texture visible in all slots; each slot presents
  its own panel slice (pixel‑verified); fullscreen toggle ×3 and minimize/restore ×2 stay presentable.
- **GATE-SMOKE: PASS** - all 24 runs completed clean, including the 60 s vsync-on endurance cells
  (vfb 43.9 fps; sharedrender 59.93 fps - level with giant 59.98 and rerender 59.92).
- **GATE-PERF (vfb): FAIL x3** (n=6 q=1000 vs=0) - vfb 18.97 ms / 52.7 fps vs giant 6.70 ms / 149.2 fps
  and rerender 6.95 ms / 143.9 fps. The whole penalty is the present path: `swap` after a VFB slice blit
  is ~1.86 ms mean (p99 ~12.6 ms) per slot, vs ~24 us for the same present in every other mode.
- **GATE-SHARED (sharedrender): PASS x3** (n=6 q=1000 vs=0) - 6.95 ms / 143.8 fps
  (giant 6.70 ms / 149.2 fps; rerender 6.95 ms / 143.9 fps); swap 23.8 us per slot from a shared
  context; per-slot 1065 us <= 1.25 x rerender per-slot 1073 us; CONTENT PASS on every run
  (each slot renders + presents its own panel).
- => **Verdict: sharedrender (shared context + per-window own-panel render) is the shipping
  architecture.** vfb's numbers stay in the record as evidence of what not to do on this stack
  (see finding #4). Full data: 24 JSONs + `results/summary.txt`.

### Spike findings that changed the spec (plan §3 / §4.4 updated)
1. **Per‑context GL state** (viewport, matrix stacks, **`GL.Color4`**, caps) is NOT shared — only the
   object namespace is. The blit pass must be fully self‑contained **including `GL.Color4(1,1,1,1)`**;
   without it the primary panel renders as `sceneColor × texture` (probe caught: `(90,70,8)` =
   `hue0 × markerColor` elementwise) while child slots look fine (default white color).
2. The VFB scene pass must own projection/modelview/clear (`BeginRender`) — blit passes leave
   `Ortho(0..1)` in the shared matrix stack, and stale texture content survives without a clear.
3. `NativeWindow.FramebufferSize` (live, DPI‑aware) is the clean blit‑viewport source
   (the size event is named `FramebufferResize`, single‑arg `Action<...>` delegates;
   `FocusedChanged` likewise). `NativeWindow.Context` is already `IGLFWGraphicsContext`.

4. The VFB stall is **FBO-read->present** pipeline sync, not shared-context present serialization:
   `swap` after a VFB slice blit averages ~1.96 ms (p99 ~13 ms) per slot, while `swap` after a
   direct backbuffer render costs ~25 us per slot - **even from the same shared context**
   (sharedrender: 24.3 us). Render-once-blit-many is the expensive pattern on this Windows/DWM stack.
