# Build Vocaluxe - HowTo (Linux)

Vocaluxe on Linux runs on the **.NET 10 SDK** (no Mono, no xbuild). The game data ships in the
repository (`Output/`), the two small native sub-projects (pitch tracking, video/audio decoding)
build with plain gcc/g++, and everything else comes from NuGet.

## Prerequisites

Build-time:

- .NET 10 SDK
- gcc, g++, make
- FFmpeg development libraries (`libacinerella.so` links against the system FFmpeg):
  Debian/Ubuntu:
  ```sh
  sudo apt install pkg-config libavformat-dev libavcodec-dev libavutil-dev libswscale-dev libswresample-dev
  ```

Runtime (on the machine that *runs* the game; a dev box has all of this from the build-time
packages):

- .NET 10 runtime - unless you use the self-contained build below
- OpenGL + windowing: `libgl1 libegl1 libgbm1` plus X11 (`libx11-6 libxrandr2 libxinerama1
  libxcursor1 libxi6 libxkbcommon0`) or a Wayland compositor
- Audio: `libasound2t64` (ALSA); PulseAudio/JACK are optional
- FFmpeg shared libraries, e.g. on Debian 13: `libavformat63 libavcodec63 libavutil61
  libswscale10 libswresample7` (numbers follow your distribution's FFmpeg)
- Optional (webcam): `ffmpeg` CLI + a V4L2 capture device

## Build

```sh
git clone https://github.com/Vocaluxe/Vocaluxe.git
cd Vocaluxe
make          # = the two native sub-projects (g++) + dotnet build -c Release
```

Or, step by step:

```sh
dotnet restore Vocaluxe.slnx
make -C PitchTracker                          # -> Output/libPitchTracker.dll.so
make -C Vocaluxe/Lib/Video/Acinerella         # -> Output/libacinerella.so (system FFmpeg)
dotnet build Vocaluxe.slnx                    # Debug is fine for a dev loop
```

The build refuses to succeed while `libPitchTracker.dll.so` or `libacinerella.so` are missing
(it points at the makefile above), so a broken native setup can never masquerade as a working
game.

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
