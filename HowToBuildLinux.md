# Build Vocaluxe - HowTo (Linux)

Vocaluxe on Linux runs on the **.NET 10 SDK** (no Mono, no xbuild). The game data ships in the
repository (`Output/`), the one small native sub-project (pitch tracking) builds with plain gcc/g++,
and everything else comes from NuGet or a build-time download.

## Prerequisites

Build-time:

- .NET 10 SDK
- gcc, g++, make

(No FFmpeg development libraries are needed: the FFmpeg 8.1.3 binaries are staged next to the game
by the build - see `Output/libs/unmanaged/ffmpeg64`.)

Runtime (on the machine that *runs* the game; a dev box has all of this from the build-time
packages):

- .NET 10 runtime - unless you use the self-contained build below
- OpenGL + windowing: `libgl1 libegl1 libgbm1` plus X11 (`libx11-6 libxrandr2 libxinerama1
  libxcursor1 libxi6 libxkbcommon0`) or a Wayland compositor
- Audio: `libasound2t64` (ALSA); PulseAudio/JACK are optional
- FFmpeg 8.x shared libraries - either the staged bundle in `Output/libs/unmanaged/ffmpeg64`
  (once the pinned `ffmpeg_linux_x64.zip` artifact has been published, see
  `FFmpeg-Modernization-Plan.md` P2) or a system install (e.g. `ffmpeg@8` on macOS-style setups)
- Optional (webcam): a V4L2 capture device

## Build

```sh
git clone https://github.com/Vocaluxe/Vocaluxe.git
cd Vocaluxe
make          # = PitchTracker (g++) + dotnet build -c Release
```

Or, step by step:

```sh
dotnet restore Vocaluxe.slnx
make -C PitchTracker                          # -> Output/libPitchTracker.dll.so
dotnet build Vocaluxe.slnx                    # Debug is fine for a dev loop
```

The build refuses to succeed while `libPitchTracker.dll.so` is missing (it points at the makefile
above), so a broken native setup can never masquerade as a working game.

## Run

```sh
./Output/Vocaluxe
```

(Or `dotnet Output/Vocaluxe.dll`. Logs land in `Output/Logs/`.)

- User data (`Config.xml`, profiles, highscore/cover DBs, playlists, screenshots) lives in
  `~/.config/Vocaluxe/`.
- Songs go into `~/.config/Vocaluxe/Songs` (or point `Config.xml` at another folder).
- Game data (themes, languages, fonts, default music) lives in `Output/` next to the executable.

## Self-contained copy (no .NET runtime needed on the target machine)

```sh
dotnet publish Vocaluxe/Vocaluxe.csproj -c Release -r linux-x64 --self-contained true -o Output
```

The resulting `Output/` folder is portable - copy or tar it as a whole.

## Package install (optional)

```sh
make install INSTALLPATH=/usr/share/vocaluxe     # expects Output/ to hold the desired layout
make install_icon                                # .desktop entry -> /usr/share/applications
```

For the installed game, a self-contained `Output/` (see above) is the sensible layout.

## Notes

- The Genius theme's screen videos (`BG_Video`, `IntroIn/Mid/Out.mp4`) are deliberately not in
  the repository (upstream CI has always downloaded them). The game boots and runs fine without
  them - it just reports them as missing. Optional:
  `https://raw.githubusercontent.com/lukeIam/VocaluxeDependencies/master/zips/themes/vocaluxe.themes.genius.video.zip`
- Under Wayland, GLFW cannot set the window icon or the window position; Vocaluxe logs those two
  `FeatureUnavailable` messages at startup. They are harmless - under X11 the calls succeed.
- PortAudio probes every audio backend (ALSA/Pulse/JACK) on startup; without a running JACK
  server you will see `jack server is not running` noise on the console. Harmless.
- The webcam uses the V4L2 backend on Linux; without the `ffmpeg` CLI or a capture device it
  degrades to "no device" instead of failing.
