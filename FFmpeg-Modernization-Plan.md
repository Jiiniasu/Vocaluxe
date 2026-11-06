# FFmpeg modernization — replace acinerella (REVISED v2)

Status: revised 2026-11-06 (v2) after reviewing upstream **Vocaluxe/Vocaluxe PR #946** ("Migrate
to .NET 10", merged to `develop` 2026-11-02), which already ships "Replace Acinerella with FFmpeg
8.1.3 (FFmpeg.AutoGen)" + "hardware accelerated video decoding (D3D11VA, DXVA2, VideoToolbox,
VAAPI, VDPAU) with software fallback", and after deciding to keep the managed-binding
architecture with a **bundled** FFmpeg for Linux (see Decisions). (v1 of this plan proposed a
custom C shim — superseded when the upstream implementation became the reference.)

## Decisions (final)

| Decision | Choice |
| --- | --- |
| Approach | **Adapt upstream's `CFFmpegDecoder` (FFmpeg.AutoGen, C#)** — no custom C shim, no per-platform FFmpeg build on our side beyond one published Linux build |
| FFmpeg | **8.1.3 shared GPL everywhere** (System233/ffmpeg-msvc-prebuilt for Windows, as re-hosted by VocaluxeDependencies; one GPL shared build for linux-x64 published the same way) — upstream's proven pairing, **one tested FFmpeg on every platform** |
| Bindings | **FFmpeg.AutoGen.Abstractions + FFmpeg.AutoGen.Bindings.DynamicallyLoaded 8.1.0** (Ruslan-B, nuget.org, **MIT**) — note: the binding pins the FFmpeg 8.x ABI, hence the bundle |
| HW accel | D3D11VA→DXVA2 (Win) / VideoToolbox (mac) / VAAPI→VDPAU (Linux), software fallback — upstream's implementation, `HardwareDecoding` config toggle |
| Windows CRT | Prebuilts are static-CRT ("zero runtime DLLs to install") → **drop the VC++ 2015-2022 redist check** (upstream did exactly this) |
| Linux | **Self-contained**: bundled FFmpeg 8.1.3 `.so` set staged to `libs/unmanaged/ffmpeg64/` (upstream's `CFFmpegDecoder._GetLibrariesPath` already probes that folder first) → **no system FFmpeg install, no local shim build** — simpler than today |
| macOS | Homebrew `ffmpeg@8` (upstream's approach; their library-path probe already includes the `ffmpeg@8` prefixes) — bundled dylib later if needed |
| Kept from our port | Glass-blur video path (`CDecoder`/`CVideoDecoderFFmpeg`/`CGlassBlur`), `CPortAudioStream` byte-accounted timing, x64-only, self-contained model |
| Dropped | `vldecode` C shim, FFmpeg self-build recipe for Windows, C↔C# ABI risk, "Linux flexibility" (today's per-user-compiled-against-different-FFmpeg behavior = per-distro variance, not a feature) |

## Upstream references (port sources, `develop` branch)

- `Vocaluxe/Lib/Video/FFmpeg/CFFmpegDecoder.cs` — the decoder core (~750 lines, `unsafe`).
- `Vocaluxe/Lib/Sound/Playback/Decoder/CAudioDecoderFFmpeg.cs` — thin audio wrapper (BitDepth 16, 10× EAGAIN retry).
- `Vocaluxe/Lib/Video/FFmpeg/CDecoderThread.cs` — ~95% identical to ours; use as merge base.
- `Vocaluxe/Vocaluxe.csproj` — `AllowUnsafeBlocks=true`, package versions, `ffmpegBeforeBuild`
  target (downloads `…/VocaluxeDependencies/raw/master/zips/dependencies/ffmpeg_x64.zip` →
  `libs\unmanaged\ffmpeg64\`, existence check on `avcodec-62.dll`).
- `Output/Licenses/FFmpeg/{LICENSE.txt,SourceAndVersion.txt}` + `Output/Licenses/FFmpeg.AutoGen/*`
  (GPL attribution: "FFmpeg 8.1.3 (shared, GPL)"; files avcodec-62/avformat-62/avutil-60/swscale-9/swresample-6/…).
- New config options (theirs): `Video.HardwareDecoding`, `Video.DecoderThreads`,
  `Video.VideoDownscaleResolution`, `Video.GpuColorConversion` (skip the last — needs their D3D11 GPU-YUV path).

## Work items

1. **Packages + project setup** — add the two AutoGen 8.1.0 packages; `AllowUnsafeBlocks=true`
   in `Vocaluxe/Vocaluxe.csproj`; drop `vocaluxe.dependencies.{acinerella,ffmpeg} 1.0.6`.
2. **Port `CFFmpegDecoder.cs`** → `Vocaluxe/Lib/Video/FFmpeg/CFFmpegDecoder.cs`. Adaptations:
   - **Pixel format: keep upstream's `AV_PIX_FMT_BGRA`.** Verified against the old stack:
     acinerella requested FFmpeg-4's `AV_PIX_FMT_RGB32` (native 32-bit layout), whose byte order in
     memory on little-endian is B,G,R,A (classic 0xAARRGGBB) — the same order FFmpeg 8's
     `AV_PIX_FMT_BGRA` produces (first-named byte at the lowest address) and the order our
     `COpenGL._WriteDataToTexture` uploads (`GL PixelFormat.Bgra`). So the upstream choice is
     already correct for our renderer — no enum change needed; the red-frame color-sanity check in
     the harness guards it anyway.
   - Strip the GPU-YUV branch: `OutputYuv`, `YuvToRgbRows`, the `av_image_copy_to_buffer`
     YUV420P path, and the `CDraw.SupportsYuvVideo`/`UpdateYuvTexture` references (they don't
     exist in our port). Keep the colorspace/range `sws_setColorspaceDetails` setup — it feeds the
     swscale path and is a real improvement over acinerella.
   - `_GetLibrariesPath`: keep the `libs/unmanaged/ffmpeg64` convention (already matches ours);
     keep the Linux/macOS probe candidates (homebrew `ffmpeg@8` paths already there).
   - Keep verbatim: HW device selection + process-wide device cache + negative cache,
     `get_format` callback with SW fallback, AV1 one-frame HW probe + rewind/flush,
     `av_hwframe_transfer_data`, `best_effort_timestamp` timecode, audio swr S16 path,
     seek (`av_seek_frame` + `avcodec_flush_buffers` + swr re-init), interleaved packet routing,
     `thread_count`/`FF_THREAD_FRAME|SLICE`, `MaxHeight` downscale, `av_log` QUIET.
3. **Merge `CDecoderThread.cs`** (ours → upstream shape): replace the `CAcinerella` IntPtr/struct
   plumbing with `_Decoder.*` calls (as upstream does); adopt their **`LoopLength` fix** (loop
   boundary when the last frame ends before nominal `Length`). Keep our class as the glass-twin
   producer: `CDecoder.GetFrame` still calls `CGlassBlur.Process(curFrame.Data, …)` on the RGBA
   byte[] — that logic lives in our `CDecoder`/`CVideoDecoderFFmpeg`, which upstream doesn't have,
   so those two files stay ours (only the `CDecoderThread` internals move).
4. **Replace `CAudioDecoderFFmpeg.cs`** with upstream's wrapper (`CFFmpegDecoder(Audio)`,
   BitDepth fixed 16, 10× `GetFrame` retry, `SetPosition` → `Seek`). `CPortAudioStream` stays
   byte-accounted (already proven; the new timecode is trustworthy but we keep our source of truth).
   The 32-bit `_FloatToPcm16` fallback in our file becomes dead → delete.
5. **Delete** `Vocaluxe/Lib/Video/Acinerella/` (CAcinerella.cs, acinerella.c, acinerella.h,
   makefile) + its `.gitignore` entries + root-makefile target + `_CheckLinuxNatives` acinerella
   line (keep the PitchTracker gate).
6. **FFmpeg staging (all platforms)** — csproj download targets (upstream's `ffmpegBeforeBuild`
   shape, .NET 10 SDK built-in `DownloadFile` + unzip):
   - **Windows**: existing URL `…/VocaluxeDependencies/raw/master/zips/dependencies/ffmpeg_x64.zip`
     (8.1.3, GPL, shared, static-CRT) → `Output/libs/unmanaged/ffmpeg64/`; hard-`Error` if
     `avcodec-62.dll` missing after download (never ship without it).
   - **Linux (NEW)**: one-time FFmpeg 8.1.3 GPL **shared** build for linux-x64 on an old-glibc
     base (Debian 11 / glibc 2.31 → portable across current distros):
     `./configure --enable-gpl --enable-shared --enable-vaapi --prefix=<staging>` (+ `nasm`),
     artifact = the `libav*.so.*/libsw*.so.*` set → publish as `ffmpeg_linux_x64.zip` to the
     **VocaluxeDependencies** repo (same place as the Windows zip; needs the maintainer's account)
     → csproj download target → same `libs/unmanaged/ffmpeg64/` folder (the C# probe already
     prefers it over system libs — no code change). Runtime is now fully self-contained.
   - **macOS**: Homebrew `ffmpeg@8` (documented); optional later: same bundle treatment with dylib
     + rpath fixups.
   - CI note: both downloads are network steps at build time — same category as the theme-video
     download already in the workflow.
7. **Config + UI** — add `CConfig` entries `Video.HardwareDecoding` (EOffOn, default ON),
   `Video.DecoderThreads` (int, default 0=auto), `Video.VideoDownscaleResolution` (px height,
   default 0=off) + their `CScreenOptionsVideo` UI rows (ported from upstream). Log the active HW
   backend once per stream (`CFFmpegDecoder.HardwareDeviceName`) so support reports can name it.
8. **Licensing** — commit `Output/Licenses/FFmpeg/{LICENSE.txt,SourceAndVersion.txt}` and
   `Output/Licenses/FFmpeg.AutoGen/{LICENSE.txt,SourceAndVersion.txt}` (upstream's files;
   FFmpeg GPL notice + MIT binding). The Linux `.so` set is the same GPL 8.1.3 build lineage —
   same license files cover it.
9. **Redist removal** — `Vocaluxe/CProgramHelper.cs`: delete the VC++ 2015-2022 check/install
   block (and its comments). Proof: `dumpbin /dependents` on the five FFmpeg DLLs shows no
   `VCRUNTIME140*`/`msvcp140*`.
10. **Docs/CI** — `HowToBuildLinux.md`: **no FFmpeg install or local build needed anymore**
    (staged download; build-time deps are just .NET 10 + the PitchTracker toolchain); runtime HW
    note: VAAPI driver (mesa `libva`) optional on the target machine, software fallback otherwise.
    `HowToBuildWin.md`: no redist needed. Root `makefile` (PitchTracker only).
    `.github/workflows/main.yml` completeness matrix (`acinerella`→drop, `avcodec-58`→`avcodec-62`,
    `libs/unmanaged/ffmpeg64/`). `NET10-Port-Plan.md` entry.

## What stays unchanged (and why)

- `CPortAudioStream` — byte-accounted timing is proven (2026-10-13 fix); new timecode is
  trustworthy but we keep our source of truth.
- `CDecoder` / `CVideoDecoderFFmpeg` / `CFramebuffer` / glass-blur path — our differentiator,
  upstream has none of it.
- `CVideoStream`, `CVideo`, `CSound`, `CBackground`, `CSongPlayer` — the seams don't move.
- PitchTracker, PortAudio (+ASIO swap), tests (105), CI shape.

## Verification (gates, same bar as before)

1. **Audio harness** (headless): "Ash Again.mp3" full decode (old 152.16 s freeze file) → PCM
   bytes ≈ duration × rate × 2 × ch, monotonic position, start@140 s clean continuation; one OGG,
   WAV, **WMA** each (WMA needs the GPL build — we have it); BitDepth=16 contract (no float path).
2. **Video harness**: theme MP4 — frame count ≈ duration × fps, dims, monotonic timecodes,
   `Skip`/`Seek` (both dirs)/`SkipFrames`, loop restart incl. the `LoopLength` edge. **Color
   sanity: decode a known-red frame, assert the byte order in the buffer** (guards the RGB32 fix).
3. **HW proof**: with a GPU, `HardwareDeviceName` = `d3d11va` (or `dxva2`) logged; HW-vs-SW
   decode CPU comparison; `HardwareDecoding` OFF → clean software path; headless/CI → software
   fallback without crash.
4. **Game launch** (self-contained `Output/`): song plays and ends itself; theme video
   loops/skips/pauses; glass twin renders with correct colors; no `DllNotFound`.
5. **CI gates**: build 0W/0E, `dotnet test` 105/105, `dotnet format Vocaluxe.slnx
   --verify-no-changes`, redist dependency scan.
6. **Linux**: fresh box **without any FFmpeg installed** → build (staged download) + both
   harnesses + launch; VAAPI driver present → `HardwareDeviceName=vaapi`; absent → clean software
   fallback. **macOS**: Homebrew `ffmpeg@8` → harness + launch.

## Phases

| Phase | Contents | Exit criteria |
| --- | --- | --- |
| **P0 — spike** (½ day) | AutoGen packages + unsafe; port `CFFmpegDecoder` (adapted); Windows 8.1.3 DLLs staged; headless smoke: one audio frame + one video frame through it | Decode proven on our toolchain; `avcodec-62` load verified |
| **P1 — swap** (1–2 d) | `CDecoderThread` merge + `CAudioDecoderFFmpeg` + acinerella deletion + staging targets + config options + licenses + docs; gates 1, 2, 4, 5 (partial) | Game runs end-to-end on Windows with the new decoder (HW or SW); 105 tests green |
| **P2 — Linux + HW proof** (1 d) | Linux FFmpeg 8.1.3 shared build + publish to VocaluxeDependencies + linux staging target; HW proof (gate 3); redist removal + scan (gate 5); macOS pass (gate 6) | No-FFmpeg Linux box decodes audio+video; `HardwareDeviceName` verified on a GPU box |
| **P3 — ship** (½ d) | CI matrix, final `dotnet format`, release tag | Self-contained zip ships with FFmpeg 8.1.3 + HW decode, no redist prompt; Linux tarball self-contained |

## Risks / mitigations

- **Red/blue swap** (byte-order mismatch with our GL upload) → ruled out at the port: the old
  acinerella "RGBA32" and the new `AV_PIX_FMT_BGRA` both land on B,G,R,A in memory (verified
  against `acinerella.c`'s `AV_PIX_FMT_RGB32` request + `COpenGL`'s `PixelFormat.Bgra` upload);
  red-frame color-sanity check in the video harness (gate 2) still guards it.
- **FFmpeg.AutoGen is a third-party managed dep** → MIT-licensed, upstream Vocaluxe depends on the
  same 8.1.0 version; `unsafe` scoped to `CFFmpegDecoder` only.
- **Bundled Linux `.so` glibc floor** → build on Debian 11 (glibc 2.31) → runs on every current
  distro; ~60 MB tarball growth (Windows already ships the equivalent — symmetric).
- **One more artifact to publish** (ffmpeg_linux_x64.zip in VocaluxeDependencies) → needs the
  maintainer's account once; afterwards it's a pinned URL like the Windows zip.
- **Upstream drift / FFmpeg 8.x EOL** → bump path is bounded: AutoGen 9.0.1.1 + a 9.x binary set
  (System233 publishes per-release) — package + DLL swap, not a rewrite.
- **VAAPI absent on target** → software fallback is the default path; only the driver is optional.

## Open (non-blocking)

- `DecoderThreads`/`VideoDownscaleResolution` — include in this pass (cheap; default: include).
- macOS bundling — defer unless a macOS playtest wants zero Homebrew dependency.
