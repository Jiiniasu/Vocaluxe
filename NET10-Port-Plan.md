# .NET 10 Port Plan — SSIS-Remote

Status: **AGREED 2026-10-10** — see "Decision log" below.
Reference: upstream PR [#881](https://github.com/Vocaluxe/Vocaluxe/pull/881) (byte55, `feature/768-net10-crossplatform`, tip `80eccef`) — used as **API/reference only**, never merged as history. Fetched locally as `FETCH_HEAD`; re-fetch from `https://github.com/byte55/Vocaluxe feature/768-net10-crossplatform` on demand.

## 1. Strategic decisions (final)

| Topic | Decision |
|---|---|
| Strategy | Port the SSIS-Remote codebase in place; PR = reference implementation; **no upstream merge**; SSIS product decisions win everywhere |
| Keep | multiplayer/"Normal"/duets/multi-voice; CCloud + COutbox (HTTPS, **public TLS — zero cert work**); all screens/themes/languages; local tests; local logging (Serilog 4) |
| Drop | party **minigames** (TicTacToe/Challenge + CodeDom runtime compiler — so **no Roslyn needed**); in-game **webserver** (WCF `CVocaluxeServer` et al. + QR popup); **gamepad/WiiMote** (hidapi); **webcam** (AForge → null backend); **D3D + software renderer**; **DirectSound/GStreamer/OpenAL**; **CReporter** (crash → log-only); WinForms host/splash/`Keys`; installer/AppVeyor/x86 |
| Renderer | OpenTK 4 (GLFW `NativeWindow`) — OpenGL only (PR pattern, incl. `#if WIN` fullscreen-present fix) |
| Audio | PortAudioSharp2 only; **local `Rescan()` mic feature + per-mic player-channel mapping re-implemented on the new API** |
| Imaging | SkiaSharp replaces GDI+ (`System.Drawing.Common` gone; `System.Drawing.Primitives` OK) |
| CCloud | public-cert HTTPS = works natively; drop `ServicePointManager.Expect100Continue`; avatar `Bitmap` → `byte[]`/`SKCodec`; `CProfiles` image save → `File.WriteAllBytes` + `Path.Combine` |
| Natives | **Local pair stays**: `PitchTracker` C++ (x64, local sources), local `acinerella.c` + FFmpeg 4.x binaries, SQLite. **PR's acinerella C NOT adopted.** |
| Deliverable | Self-contained, **x64 only**, GitHub Actions, zip artifact; unit-test matrix on win/mac/linux (compile) |
| Versioning | Standard semver tags (`1.0.0`) + PR's `_StampGitVersion` target; informational version carries `git describe` |
| Tests | Existing local NUnit suite → **NUnit 4.3.2** (minus party tests); + PR's `CImagingTest`; + new headless tests: COutbox persistence, CCloud JSON round-trips |
| Cross-platform | Keep code Linux/macOS-compile-capable (PR define machinery) — tested on Windows only |
| Config stability | `Config.xml` schema unchanged; `Server`/`Webcam`/`Gamepad` sections become inert |

## 2. Disposition table

### A — Adopt from PR (no product decisions inside)
- `VocaluxeLib/Keys.cs` (custom keys enum)
- GLFW host (`CDrawBaseGlfw` replacing `CDrawBaseWindows`) + `#if WIN` fullscreen fix
- OpenTK 4 `COpenGL` as API baseline (see B: your delta re-applied on top)
- `CPortAudioHandle` / `CPortAudioStream` wrappers as baseline (see B: `CPortAudioRecord`)
- SkiaSharp: `CFontStyle`, `CGlyph`, `CTextureProvider`, `CBase64Image`; `CCoverGenerator`/`CCover` with local delta re-applied
- `CNullWebcam` / `CV4l2Webcam`; `CWebcam` factory → null
- `CLog.ShowLogAssistant` null-safe patch
- `Program` main-loop + splash-with-progress structure (local cloud init/teardown re-applied)
- `_StampGitVersion` MSBuild target; CI/build-script patterns; `CImagingTest`
- `Community.CsharpSqlite` SDK conversion + WMI exclusion patch

### B — Local delta re-based onto new API (main effort)
- `COpenGL` — `allMonitors` reflection loops on OpenTK 4 (S0 verifies local 90-line delta scope)
- `CPortAudioRecord` — `Rescan()` + per-mic `CConfig.GetPlayerFromMicConfig` mapping on PortAudioSharp2 streams (highest risk item)
- `IRecord`/`CRecord` — keep `Rescan` contract
- `CDraw`/`IDraw`/`CDrawBase` — `allMonitors` signatures
- `CCloud`/`CProfiles` — see CCloud row above
- `CParty` — delete `_LoadPartyModes`/CodeDom; keep Normal mode + local edits
- `CMain`/`Program` — remove `Application.DoEvents` pumps

### C — Surgical net10 fixes in local files
- `CPopupNewPlayer.cs`, `VocaluxeLib/Menu/SongMenu/CSongMenuDetails.cs`, `CScreenPrepare.cs` — WinForms `Keys` → `VocaluxeLib.Keys`
- `app.config` runtime probes → PR-style native filename loading

### Delete / archive (archived on tag `pre-net10` before deletion)
- `PartyModes/` (both projects), `CScreenParty`, `CPartyScreenTicTacToe*`, `CPartyScreenChallenge*`, `CMenuPartyNameSelection`, `CMenuPartySongSelection` (usage-checked), `Tests/PartyModeChallenge/`
- `Base/Server/CVocaluxeServer`, `CWebservice`, `ICWebservice`, `Session`, `SessionControl`, `CPopupScreenServerQR`
- `CDirect3D`, `CDrawWinForm`, `CGstreamerSharp*`, `COpenAL*`, `CAForgeNet`, `CDirectSoundRecord`, `CSoundCardSource`
- `CGamePad`, `CHIDAPI`, `CWiiMote`, `WiiMoteLib`
- `Reporting/Reporter.cs`
- `WebserverInitalConfig/`, `Installer/`, `.appveyor/`, `appveyor.yml`, `packages.config`, both `app.config`, `Packages/` (after migration proven)

### Keep untouched
- All other `VocaluxeLib` + `Base` + `Screens` + `Lib` (multi-voice, cloud, sparkles, menus, themes)
- `PitchTracker/` C++; `acinerella.c` + `CAcinerella`/`CDecoderThread` (local 32-line delta to be glanced-verified, not a feature)
- `Output/` (themes, languages, data — incl. inert `Output/PartyModes/`, `Output/Website/`)

## 3. New solution & packages (final, S1)

**`Vocaluxe.slnx`** (`.NET 10` slnx format — the `dotnet` CLI default; VS 2026 reads it): Vocaluxe (WinExe net10.0) · VocaluxeLib (lib) · Community.CsharpSqlite (lib) · Tests. **PitchTracker (vcxproj) is NOT in the slnx** — `dotnet sln add` crashes on C++ projects; build it separately (VS, or `msbuild /p:Platform=x64` as its own CI step).

**PackageReference matrix (replaces `packages.config`):**
| Project | Packages |
|---|---|
| Vocaluxe | OpenTK 4.9.4 · PortAudioSharp2 1.0.6 · SkiaSharp 3.119.4 (+NativeAssets.Linux, +macOS cond.) · Newtonsoft.Json 13.0.4 · Microsoft.Extensions.Http 8.0.0 · Microsoft.Extensions.Http.Polly 8.0.0 · Polly 8.2.0 (only `CCloud.cs` consumes this stack) · natives: `vocaluxe.dependencies.{acinerella 1.0.6, ffmpeg 1.0.6, csharpsqlite 1.0.1, sqlite 1.0.1}` |
| VocaluxeLib | Serilog 4.2.0 + Enrichers.Thread 4.0.0 + Sinks.Console 6.1.1 + Sinks.File 7.0.0 + Sinks.TextWriter 3.0.0 · **temporary**: System.Drawing.Common 8.0.0 (S1 bridge for the local bitmap pipeline; removed with the S4 SkiaSharp port) |
| Community.CsharpSqlite | none (WMI path guarded `#if VOCALUXE_USE_WMI`, PR approach; SQLITE native comes from the Vocaluxe project's package refs) |
| Tests | Microsoft.NET.Test.Sdk 17.11.1 · **NUnit 3.14.0** + NUnit3TestAdapter 4.6.0 (follow PR — not NUnit 4; lower migration risk, existing tests run as-is) |

**Other S1-final choices** (deviations from the initial draft, all per PR reference or local facts):
- **No `global.json`** (PR has none; local SDK 10.0.401 suffices).
- **No Microsoft.Data.Sqlite / SQLitePCLRaw** (local `CDataBase` uses the Community.CsharpSqlite P/Invoke wrapper + `sqlite3.dll` native; the PR's dual-sqlite stack is not needed).
- **No QRCoder** (verified: only the deleted `CPopupScreenServerQR` used it).
- **`OutputPath` = `..\Output\`** on the app project only — legacy layout: exe + managed assemblies land next to the game data (Themes/Languages/Fonts) in `Output/`. Lib/test projects use default `bin/` (their outputs ride along via project references).
- `Vocaluxe/Properties/AssemblyInfo.cs`: hardcoded Title/Version attributes removed; the PR's `_StampGitVersion` MSBuild target (git describe → WriteCodeFragment) generates them; `GenerateAssemblyInfo=false` on all projects (keeps the 4 existing AssemblyInfo.cs files).
- **Input architecture** (decided S0/S1): keyboard/mouse path = `CKeys`/`CMouse` (`Vocaluxe/Base/CUtility.cs`) + `CBcontrollers` (in `CMain.cs`) behind `IControllers` (`VocaluxeLib/IBasic.cs`, rumble-only) — **kept**; `WiiMote`/`GamePad`/`Server` device pool (`Base/CController.cs` + all of `Lib/Input/`) — **deleted**; `SetRumble` kept as no-op for API stability. `PR VocaluxeLib/Keys.cs` adopted (replaces WinForms `Keys` across ~35 files — the usings are stripped, `Keys.*` resolves to `VocaluxeLib.Keys`).
- Webcam: PR's `CWebcam.cs` + `CNullWebcam.cs` + `CV4l2Webcam.cs` adopted (null backend on Windows, per scope).
- `CScreenScore._UpdateBackground` (photo slide-show from the deleted webserver) → inert, returns false. `CDrawBase` per-frame `CVocaluxeServer.ProcessServerTasks()` → removed. `CSound`/`CRecord` keep the PortAudio case; OpenAL/GStreamerSharp/DirectSound cases + usings deleted.

## 4. Phases & gates

| # | Phase | Gate |
|---|---|---|
| S0 | Prep & inventory: tag `pre-net10`; branch `net10-port`; final file list; native DLL version audit; verify COpenGL/CDecoderThread local deltas; csproj/sln/CI drafts | table above locked; native matrix known |
| S1 | SDK-style projects + packages + deletes | `dotnet build`: VocaluxeLib clean; main project errors confined to known backend files |
| S2 | Host + render + input (GLFW, main loop, splash, COpenGL+allMonitors, Keys swap, reporter removal) | window opens; theme+menus render via GL; keyboard nav; windowed↔fullscreen |
| S3 | Audio (PortAudioSharp2 in/out; `Rescan` + mic config ported) | preview plays; mic records; **live mic-plug → Rescan → records** |
| S4 | Imaging (SkiaSharp) + CCloud/CProfiles net10 fixes | fonts/text/covers/screenshots/B64 correct; avatar saved; zero GDI+/WinForms refs |
| S5 | Tests | `dotnet test` green (local suite −party, +imaging, +CCloud/Outbox round-trips) |
| S6 | Native x64 | PitchTracker builds (MSVC x64); app runs from publish folder |
| S7 | CI + packaging | GH Actions: build + tests + self-contained x64 zip w/ `Output/`+natives; version string from tag; artifact launches clean |
| S8 | Handover | side-by-side behavior parity session (sing/multi-voice/duets/cloud/mic); docs; known-omissions note |

## 5. Risks
1. `Rescan()` on PortAudioSharp2 (stream/callback model) — CConfigTest + live-plug gate.
2. Glyph fidelity SkiaSharp vs GDI+ — PR font fixes + imaging tests + visual diff.
3. `Application.DoEvents` → GLFW pump (frame pacing) — S2 gate + playtest.
4. COpenGL re-apply assumes local delta = multi-monitor only — S0 verifies.
5. CCloud runtime subtleties (Expect-100, socket teardown, TLS) — new headless tests + live smoke against public-TLS server.

## 6. Known omissions after the port (user-visible)
No webcam · no gamepad/WiiMote · no party minigames · no phone-remote webserver · crash errors logged only (no send) · `Config.xml` Server/Webcam/Gamepad sections inert.

## 7. Decision log
- 2026-10-10: strategy = port-in-place w/ PR as reference (user), after rejecting 2-stage upstream sync (directional divergence).
- 2026-10-10: drops & scope (user): minigames, webserver, gamepad/WiiMote, webcam, D3D, DSound/GStreamer/OpenAL, reporter, x86, installer; x64 self-contained; GH Actions; semver tags; public TLS; local natives kept (user: "I have not modified acinerella myself").
- 2026-10-10: plan written and confirmed (user: "Scope drop sounds right … start the process").
- 2026-10-11: S0 inventory completed: orphan traps verified (`Vocaluxe/Animations/`, `Vocaluxe/Base/Font/` — not in legacy csproj, PR deleted both → deleted); CController cluster = WiiMote/GamePad/Server only (keyboard path separate) → cluster deleted, `IControllers`/`CBasic`/`CBcontrollers` kept; `CSongMenuRemote` kept (used by the factory, server-independent); `CMenuParty` base kept (party-mode framework, Normal mode), `CMenuParty{Name,Song}Selection` deleted (minigame-only).
- 2026-10-11: S1 scaffold done. `dotnet restore` OK; **VocaluxeLib + Community.CsharpSqlite build green on net10.0**; Vocaluxe red errors confined to 8 files = the S2/S3 work surface: `CDrawBaseWindows.cs`, `COpenGL.cs`, `CUtility.cs` (S2 host/input), `CPortAudioHandle.cs`/`CPortAudioStream.cs`/`CPortAudioRecord.cs` (S3 PortAudioSharp2 API), `CNullWebcam.cs` (IWebcam surface → S2/S4).
- 2026-10-11: imaging re-confirmed (user, question): **SkiaSharp per plan** over keeping local GDI+ (System.Drawing.Common 8.0 stays as a temporary S1 bridge in VocaluxeLib only).
- 2026-10-11 (S2+S3 code complete): **full solution builds green — 0 errors / 0 warnings** on net10.0 (Vocaluxe→`Output/net10.0`, plus VocaluxeLib, Community.CsharpSqlite, **Tests**). Done: GLFW host (`CDrawBaseGlfw`), OpenTK4 `COpenGL` + local `allMonitors` reflection re-applied, `CKeys`/`CMouse` explicit-modifier input API, splash (`ShowSplash`/`UpdateSplash`) wired into `Program`, PortAudioSharp2 in/out (`CPortAudioHandle`/`Stream`/`Record` + `Rescan` + per-mic `GetPlayerFromMicConfig`, 48 kHz), WinForms remnants removed (`CSettings.Application` → informational-version attr; `CConfig.Screen` → `1`/`"unknown"`; `Program` `path` #if). Dropped screens excised: `CScreenParty` (`EScreen.Party`), `CPopupScreenServerQR` (`EPopupScreens.PopupServerQR`, `PopupNewPlayer` renumbered 3→2) from `CGraphics`/`CScreenMain`/`CScreenOptionsGame`/`Enums`. `CCloud`: dropped obsolete `ServicePointManager.Expect100Continue` (net10 `HttpClient` never sends 100-continue).
- 2026-10-11 (imaging = interim GDI+, flip in S4): webhook/screenshot left as GDI+ `Bitmap` **only because the whole imaging pipeline (`CTextureProvider`/`CGlyph`/`CScreenProfiles`…) is still the temporary S1 bridge** — kept type-consistent so S2 builds. Webcam (`CWebcam`/`CNullWebcam`/`CV4l2Webcam`) + `COpenGL.MakeScreenShot` will be converted to SkiaSharp **with** the S4 pipeline + `System.Drawing.Common` removed. Do NOT flip them mid-S2.
- 2026-10-11 (test baseline): `dotnet test` runs (was previously blocked on Vocaluxe): **91 pass / 11 fail** (102). 5 `TestRealFiles` fixed via output-relative data path (net48 `..\..` artifact). Remaining 11 = **logging follow-up, not S2**: 10× `CLog *WithData(True)` (reporter `lastError` passes the *rendered* message; test asserts the *raw* template — CLog/net10 behavior decision) + 1× `TestRealFiles(CConfig.SConfig)` (stale fixture missing `CloudServerURL`).
