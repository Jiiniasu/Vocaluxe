# Rebuilds win-x64\portaudio.dll from PortAudio v19.7.0-RC2 + ASIO SDK 2.3.4.
# Requires: a Visual Studio with the C++ workload (VS 2022/2026) and its bundled CMake,
# or any CMake 3.30+/4.x on PATH. Writes to .\win-x64\portaudio.dll.
# See BUILD.md for details.
param(
    [string]$VsRoot = "C:\Program Files\Microsoft Visual Studio\18\Community",
    [string]$WorkRoot = "$env:TEMP\vocaluxe-pa-asio"
)
$ErrorActionPreference = "Stop"
$paCommit = "147dd722548358763a8b649b3e4b41dfffbcfbb6"
$pa = Join-Path $WorkRoot "portaudio"
$cmake = Join-Path $VsRoot "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }

if (-not (Test-Path (Join-Path $pa ".git"))) {
    if (Test-Path $pa) { Remove-Item -Recurse -Force $pa }
    New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
    git clone --quiet https://github.com/PortAudio/PortAudio $pa
}
Set-Location $pa
git fetch --quiet origin $paCommit
git checkout --quiet $paCommit

# --- ASIO SDK -------------------------------------------------------------
$sdk = Join-Path $pa "src\hostapi\asio\ASIOSDK"
if (-not (Test-Path (Join-Path $sdk "host\pc\asiolist.cpp"))) {
    $zip = Join-Path $pa "asiosdk.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Invoke-WebRequest "https://www.steinberg.net/asiosdk" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath (Join-Path $pa "src\hostapi\asio") -Force
    $inner = Get-ChildItem (Join-Path $pa "src\hostapi\asio") -Directory |
        Where-Object { $_.Name -like "asiosdk_*" } | Select-Object -First 1
    if ($inner -and $inner.Name -ne "ASIOSDK") { Move-Item $inner.FullName $sdk }
    if (-not (Test-Path (Join-Path $sdk "host\pc\asiolist.cpp"))) {
        throw "ASIO SDK layout not found under $sdk - cannot continue."
    }
}

# --- add the missing Pa_GetVersionInfo export -----------------------------
$def = Join-Path $pa "cmake_support\template_portaudio.def"
$dc = [System.IO.File]::ReadAllText($def)
if ($dc -notmatch "Pa_GetVersionInfo\s+@35") {
    $dc = $dc -replace "Pa_GetVersionText\s+@2", "Pa_GetVersionText                   @2`nPa_GetVersionInfo                   @35"
    [System.IO.File]::WriteAllText($def, $dc)
    Write-Host "patched def: += Pa_GetVersionInfo @35"
}

# --- configure + build -----------------------------------------------------
$buildDir = Join-Path $pa "build"
& $cmake -S $pa -B $buildDir -A x64 `
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5" `
    -DPA_USE_ASIO=ON `
    "-DASIOSDK_ROOT_DIR=$sdk" `
    "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
& $cmake --build $buildDir --config Release --target portaudio --parallel 8
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Join-Path $buildDir "Release\portaudio_x64.dll"
if (-not (Test-Path $dll)) { throw "expected output not found: $dll" }
$dst = Join-Path $PSScriptRoot "win-x64\portaudio.dll"
New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
Copy-Item $dll $dst -Force
Write-Host ("OK -> " + $dst + "  ({0:N0} bytes)" -f (Get-Item $dst).Length)
