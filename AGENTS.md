# AGENTS.md

Instructions for AI agents (and humans) working on this repo. Read this before editing code.

## Formatting gate (MANDATORY before every commit)

`.editorconfig` is the single source of style truth. CI runs
`dotnet format Vocaluxe.slnx --verify-no-changes` and fails on the first finding, so a
commit that does not pass locally will fail the pipeline.

**After any code change, and before committing, run:**

```
dotnet format Vocaluxe.slnx --verify-no-changes
```

and fix every finding (or run `dotnet format Vocaluxe.slnx` to apply the fixes, then re-verify).

Counterintuitive pins that agents and editors routinely get wrong (these have broken CI):

- `end_of_line = crlf` — C# sources must use CRLF (pinned for `*.cs` and friends in
  `.gitattributes` as well).
- `insert_final_newline = false` — **files must NOT end in a newline**: the final `}` is the
  last byte. Every editor/LLM defaults to adding a trailing newline; if you create or rewrite
  a file, delete the final newline (e.g. strip the last CRLF) before verifying.
- Usings: one flat, alphabetically sorted group with `System.*` in the mix, OUTSIDE the
  namespace declaration (`dotnet_separate_import_directive_groups = false`, etc.).
- `EnforceCodeStyleInBuild` + the `dotnet_diagnostic.*.severity` entries in `.editorconfig`
  are enforced at build time. The "Enforced diagnostics" section of `.editorconfig` documents
  each hard rule and the deliberately-disabled ones — do not re-litigate a disabled rule.

Repo policy (`Directory.Build.props`): **`NoWarn` is not used anywhere.** A warning means the
code (or the rule configuration) is wrong — fix one of them instead of suppressing. On CI,
`TreatWarningsAsErrors` (set when `CI=true`) turns every warning into a failed build; treat a
local warning as "will fail CI" even if the local build passes.

## Build, test, run (Windows dev box)

.NET 10 SDK required. Order matters:

```
msbuild PitchTracker\PitchTracker.vcxproj /p:Configuration=Release /p:Platform=x64 /m /nologo
dotnet build Vocaluxe\Vocaluxe.csproj
dotnet test  Tests\Tests.csproj
```

- `PitchTracker.dll` must be built first: `_StageUnmanagedNatives` copies it from
  `PitchTracker\bin\Release.x64` into `Output\libs\unmanaged` and hard-errors if it is missing.
- Output is pinned to the repo's `Output/` folder (legacy SSIS-Remote layout, see
  `AppendTargetFrameworkToOutputPath=false` / publish settings).
- Tests are pure-logic and run headless; integration (audio, GL, display) is verified on a
  real machine.

## Cross-platform code

The same sources compile on Windows, Linux and macOS: OpenGL via OpenTK everywhere (no
WinForms/WPF), PortAudio for audio. Per-OS define set (`Directory.Build.props`):

- Windows → `WIN;ARCH_X64`
- Linux → `LINUX;ARCH_X64`
- macOS → `LINUX;ARCH_X64;MACOS` (macOS rides the shared Unix path; opt out with
  `#if LINUX && !MACOS` for genuinely Linux-only code)

Do not call Windows-only APIs unguarded (CA1416 is enforced).

## Commits

Conventional Commits with a scope, matching existing history, e.g.
`fix(window): ...`, `feat(sing): ...`, `fix(render): ...`, `docs: ...`, `ci: ...`.
Keep logical changes in separate commits; do not commit scratch/spike files unless asked.
