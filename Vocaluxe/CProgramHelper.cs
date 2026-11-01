#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Vocaluxe
{
    /// <summary>
    ///     Some helper functions for the program
    /// </summary>
    static class CProgramHelper
    {
#if WIN
        private static class CWindowsFunctions
        {
            [SupportedOSPlatform("windows")]
            [DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
            public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, int uType);
        }
#endif

#if WIN
        private const int _MBYesNo = 0x0004;
        private const int _MBIconQuestion = 0x00000020;
        private const int _MBYes = 6;

        // Stable Microsoft permalinks for the latest VC++ 2015-2022 Redistributable (aka.ms -> download.visualstudio.microsoft.com).
        // The 2015-2022 runtime (VCRUNTIME140.dll) is the one external dependency of the native stack:
        // the bundled FFmpeg decoder (acinerella.dll) which decodes every song is linked against it.
        private const string _VcRedistUrlX86 = "https://aka.ms/vc14/vc_redist.x86.exe";
        private const string _VcRedistUrlX64 = "https://aka.ms/vc14/vc_redist.x64.exe";

        private static readonly HttpClient _VcRedistHttp = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
#endif

        /// <summary>
        ///     True when the VC++ 2015-2022 runtime is usable by this process: VCRUNTIME140.dll in the
        ///     system directory that matches the process architecture. (Checking the DLL mirrors what the
        ///     loader actually does - unlike an uninstall entry in the registry, a missing file fails at
        ///     load time, and a present file is enough even if a redist package is uninstalled in the
        ///     registry.)
        /// </summary>
        private static bool _IsVcRedist2015PlusInstalled()
        {
            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(systemDir, "VCRUNTIME140.dll"));
        }

#if WIN
        /// <summary>
        ///     Asks the user whether the missing VC++ Redistributable may be downloaded from Microsoft
        ///     and installed automatically.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static bool _AskInstallVcRedist()
        {
            string text =
                "Vocaluxe needs the Microsoft Visual C++ 2015-2022 runtime (it is used to play songs)\r\n" +
                "which is not installed on this computer.\r\n\r\n" +
                "Download and install it automatically now?";
            int answer = CWindowsFunctions.MessageBoxW(IntPtr.Zero, text, "Vocaluxe - Missing component", _MBYesNo | _MBIconQuestion);
            return answer == _MBYes;
        }

        /// <summary>
        ///     Downloads the official VC++ 2015-2022 Redistributable from Microsoft and installs it
        ///     silently (elevated). Never throws - failures are logged and reported to the caller.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static bool _InstallVcRedist()
        {
            bool is64Bit = Environment.Is64BitProcess;
            string url = is64Bit ? _VcRedistUrlX64 : _VcRedistUrlX86;
            string installerPath = Path.Combine(Path.GetTempPath(), "Vocaluxe", (is64Bit ? "vcredist_x64" : "vcredist_x86") + ".exe");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installerPath));

                CLog.Information("Downloading VC++ Redistributable from {Url}", CLog.Params(url));
                using (HttpResponseMessage response = _VcRedistHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream file = File.Create(installerPath))
                    {
                        response.Content.ReadAsStream().CopyTo(file);
                    }
                }

                CLog.Information("Installing VC++ Redistributable: {Path}", CLog.Params(installerPath));
                // /quiet /norestart: unattended install, the installer exits as soon as the files are staged.
                // Verb=runas elevates via UAC; a denied UAC prompt throws and is handled by the catch below.
                using (Process install = Process.Start(new ProcessStartInfo(installerPath, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                }))
                {
                    if (install == null)
                        throw new InvalidOperationException("Could not start the VC++ Redistributable installer.");
                    if (!install.WaitForExit(TimeSpan.FromMinutes(10)))
                    {
                        try
                        {
                            install.Kill();
                        }
                        catch (Exception)
                        {
                            // the installer already left
                        }
                        throw new TimeoutException("VC++ Redistributable installation timed out.");
                    }
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Automatic installation of the VC++ 2015-2022 Redistributable failed: {Error}", CLog.Params(e.Message));
                return false;
            }

            if (_IsVcRedist2015PlusInstalled())
            {
                CLog.Information("VC++ 2015-2022 Redistributable was installed successfully.");
                return true;
            }
            CLog.Error("VC++ Redistributable installer finished, but VCRUNTIME140.dll is still missing.");
            return false;
        }
#endif

        private static void _EnsureDataFolderExists()
        {
            // Program.cs creates the data folder early (before the log is initialized), so
            // "!Directory.Exists(DataFolder)" can never detect a first start. Seed the default
            // profiles when the profiles folder itself is still missing instead - they show up
            // but can still be edited or removed (an emptied folder is left alone on purpose).
            string profilePath = Path.Combine(CSettings.DataFolder, CSettings.FolderNameProfiles);
            if (!Directory.Exists(profilePath))
            {
                Directory.CreateDirectory(CSettings.DataFolder);
                // copy default profiles to DataFolder instead of adding ProgramFolder to CConfig.ProfileFolders
                // because we want to be able to edit them, but might not have permission to write to ProgramFolder
                Directory.CreateDirectory(profilePath);
                DirectoryInfo defaultProfileDir = new DirectoryInfo(Path.Combine(CSettings.ProgramFolder, CSettings.FolderNameProfiles));
                FileInfo[] files = defaultProfileDir.GetFiles();
                foreach (FileInfo file in files)
                {
                    string newPath = Path.Combine(profilePath, file.Name);
                    file.CopyTo(newPath, false);
                }
            }
        }

        public static bool CheckRequirements()
        {
#if WIN
            // OperatingSystem.IsWindows(): CA1416 (the platform-compat analyzer) does not understand
            // our '#if WIN' define, so the call to the [SupportedOSPlatform("windows")] members below
            // is guarded at runtime as well. A build made on Windows (WIN is only defined there) can
            // only ever run on Windows, so this never changes behavior.
            if (OperatingSystem.IsWindows() && !_IsVcRedist2015PlusInstalled())
            {
                // The old VC++ 2010 registry check was a leftover from the .NET 4.0 era: none of the
                // bundled binaries needs it (portaudio.dll and PitchTracker.dll are built with the
                // static CRT, the FFmpeg av*.dll files only rely on msvcrt.dll, which every Windows
                // ships), yet it blocked the start-up on machines without it.
                CLog.Warning("VC++ 2015-2022 Redistributable (VCRUNTIME140.dll) is missing - it is needed by the bundled FFmpeg decoder (acinerella.dll) to play songs.");
                if (_AskInstallVcRedist() && _InstallVcRedist())
                    return true;

                CLog.Fatal(
                    "VC++ 2015-2022 Redistributables are missing. Please install them first. \r\n" +
                    "Download(x86): https://aka.ms/vc14/vc_redist.x86.exe \r\n" +
                    "Download(x64): https://aka.ms/vc14/vc_redist.x64.exe");
                return false;
            }
#endif //TODO: check for dependencies on linux?
            return true;
        }

        public static void Init()
        {
#if ARCH_X86
            string path = "x86";
#endif
#if ARCH_X64
            string path = "x64";
#endif
            path = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location) + Path.DirectorySeparatorChar + path;
            COSFunctions.AddEnvironmentPath(path);
#if LINUX
            _EnsureDataFolderExists();
#endif
        }
    }
}