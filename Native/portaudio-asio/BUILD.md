# PortAudio + ASIO host (win-x64)

`win-x64\portaudio.dll` replaces the stock upstream PortAudio shipped by the
PortAudioSharp2 runtime package (`org.k2fsa.portaudio.runtime.win-x64 1.0.6`).
The standard hosts (MME, WASAPI, DirectSound) behave exactly as before; the
difference is the **ASIO host** compiled in, which adds ASIO clients as
additional devices (multichannel interfaces as single clients + fixed
64-frame buffers ≈ 1.3 ms latency for input *and* output).

## Why this exact build

- **Base revision**: PortAudio `147dd722548358763a8b649b3e4b41dfffbcfbb6`
  (`v19.7.0-RC2`) — the *same* revision k2fsa built the stock runtime from
  (version string in the shipped dll: `PortAudio V19.7.0-devel, revision
  147dd722548358763a8b649b3e4b41dfffbcfbb6`). Same core ⇒ same C API/ABI ⇒
  **the PortAudioSharp2 managed wrapper needs no changes** (every entry point
  it binds is exported; verified: 56/56).
- **ASIO**: `PA_USE_ASIO=ON` with Steinberg ASIO SDK 2.3.4. Client discovery
  follows the classic registry flow:
  `HKLM\SOFTWARE\ASIO\<client>` → `clsid` value →
  `HKLM\SOFTWARE\Classes\CLSID\{…}\InprocServer32` → `CoCreateInstance`.
  Important: the ASIO client instantiates on the *calling thread*, and RME
  plugins are Apartment (STA) COM servers — which is why the game's
  `[STAThread]` main matters (and why a console probe needs it too).
- **`Pa_GetVersionInfo` export added**: the function exists in the PA 19.7
  source (`pa_front.c`) but the CMake-generated export def omits it; the
  wrapper's `PortAudio.VersionInfo` property binds it lazily, so a missing
  export would crash on first use. Patched into
  `cmake_support/template_portaudio.def` (ordinal `@35`).
- **Static MSVC runtime** (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`): the
  dll has no `vcruntime140*.dll` dependency, so the self-contained release
  zip stays dependency-free.

## Rebuild recipe (tested: VS 18 Community / MSVC 14.51, CMake 4.3.1)

```powershell
git clone https://github.com/PortAudio/PortAudio
cd PortAudio
git checkout 147dd722548358763a8b649b3e4b41dfffbcfbb6

# ASIO SDK (auto-download URL is unstable; the direct zip works)
Invoke-WebRequest https://www.steinberg.net/asiosdk -OutFile asiosdk.zip
Expand-Archive asiosdk.zip -DestinationPath src\hostapi\asio
Move-Item src\hostapi\asio\asiosdk_*.*.*_* src\hostapi\asio\ASIOSDK   # -> src\hostapi\asio\ASIOSDK

# add the missing export (def file, after "Pa_GetVersionText @2"):
#   Pa_GetVersionInfo                   @35
#   (or run patch-def in build.ps1)

$cmake = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
& $cmake -S . -B build -A x64 `
  "-DCMAKE_POLICY_VERSION_MINIMUM=3.5" `
  -DPA_USE_ASIO=ON `
  -DASIOSDK_ROOT_DIR="src\hostapi\asio\ASIOSDK" `
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
& $cmake --build build --config Release --target portaudio

# result: build\Release\portaudio_x64.dll  ->  copy here as win-x64\portaudio.dll
```

Or simply run `.\build.ps1` from this folder (writes to `win-x64\portaudio.dll`).

## Verification that was done before shipping this binary

- Export table diff vs the stock dll: all 10+10 wrapper entry points present
  (plus the `PaAsio_*` extensions the stock k2fsa build doesn't even have).
- In-process probe under net10 (STA main, `[STAThread]`), RME Fireface
  client registered as `ASIO Fireface USB` (18 in / 18 out):
  - input stream @ 48 kHz, 64-frame driver buffer: ~48.0 kHz delivered,
    zero >500 ms gaps over 2.5 s, live signal (RMS > 0);
  - **second simultaneous input stream on the same client** (duet case): OK;
  - output stream on the client (silence): OK.
- The game's normal (non-ASIO) devices keep working — standard hosts are
  unchanged in the dll.

## Notes / constraints for users

- The pitch tracker assumes **exactly 48 kHz**. ASIO runs at the interface
  clock; if the interface is at e.g. 44.1 kHz the driver may switch or
  SRC per client (observed: RME Fireface honored 48 kHz requests). The game
  logs a warning when an ASIO device reports a non-48 kHz default rate.
- One ASIO client can serve both players at once (per-channel mapping),
  which is the net48-era workflow (`ASIO MADIface USB` channel 10, etc.).
