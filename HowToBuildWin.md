# Build Vocaluxe - HowTo (Windows)

Vocaluxe on Windows runs on the **.NET 10 SDK**. The game data ships in the repository
(`Output/`), the one native sub-project (pitch tracking, C++) builds with the Visual Studio
C++ toolchain, and everything else comes from NuGet or a build-time download:

- **FFmpeg 8.1.3** (the audio/video decoder) is downloaded automatically by the build when it is
  not yet in `Output/libs/unmanaged/ffmpeg64` - no manual install, and **no VC++ Redistributable
  needed**: every bundled native (FFmpeg, PortAudio, PitchTracker) is linked against the static
  C runtime.
- PortAudio (with ASIO) and the SQLite engine ship as NuGet assets.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Visual Studio 2022 (or Build Tools) with the "Desktop development with C++" workload** -
  only for building `PitchTracker.vcxproj` (the build locates VS MSBuild via `vswhere` itself)

## Build

```bat
git clone https://github.com/Vocaluxe/Vocaluxe.git
cd Vocaluxe
dotnet build Vocaluxe.slnx
```

That is it - the build automatically:

1. builds `PitchTracker` (Release x64) with the Visual Studio MSBuild it finds,
2. downloads + stages the FFmpeg 8.1.3 binaries into `Output\libs\unmanaged\ffmpeg64`,
3. stages the ASIO PortAudio build and the managed assemblies into `Output\`,

so `Output\` holds the finished game right after.

## Run

- Open `Output\` and run `Vocaluxe.exe` (or copy the whole folder to a location of your choice)
- Songs go into the user data folder (`%LOCALAPPDATA%\Vocaluxe\Songs`, or point `Config.xml` at
  another folder)

## Self-contained copy (no .NET runtime needed on the target machine)

```bat
dotnet publish Vocaluxe\Vocaluxe.csproj -c Release -r win-x64 --self-contained true -o Output /p:AppendRuntimeIdentifierToOutputPath=false
```

The resulting `Output\` folder is portable - copy or zip it as a whole (the CI pipeline ships
exactly this as the release zip).

## Notes

- The Genius theme's screen videos (`BG_Video`, `IntroIn/Mid/Out.mp4`) are deliberately not in
  the repository (upstream CI has always downloaded them). The game boots and runs fine without
  them - it just reports them as missing. Optional:
  `https://raw.githubusercontent.com/lukeIam/VocaluxeDependencies/master/zips/themes/vocaluxe.themes.genius.video.zip`
- Tests: `dotnet test Tests\Tests.csproj`
- Code style is enforced by `.editorconfig`; run `dotnet format Vocaluxe.slnx` before committing
  (see `AGENTS.md`).
