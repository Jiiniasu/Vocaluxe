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

using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Vocaluxe
{
    /// <summary>
    ///     Some helper functions for the programm
    /// </summary>
    static class CProgrammHelper
    {
        [SupportedOSPlatform("windows")]
        private static bool _CheckUninstallKey(string name, string key)
        {
            using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(key))
            {
                if (rk == null)
                    throw new SecurityException();
                foreach (string skName in rk.GetSubKeyNames())
                {
                    using (RegistryKey sk = rk.OpenSubKey(skName))
                    {
                        if (sk == null || sk.GetValue("DisplayName") == null)
                            continue;
                        string displayName = sk.GetValue("DisplayName").ToString().ToLower();
                        if (displayName.Equals(name) || displayName.StartsWith(name) || name.StartsWith(displayName))
                            return true;
                    }
                }
            }
            return false;
        }

        [SupportedOSPlatform("windows")]
        private static bool _KeyExists(string key)
        {
            using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(key))
            {
                if (rk != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        ///     Checks if a given program is installed by checking the Uninstall registry key.
        /// </summary>
        /// <param name="name">Name of the programm. Got to equal the displayname in the key or one needs to be the start of the other one. NOT case sensitive.</param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        private static bool _IsProgramInstalled(string name)
        {
            const string baseKey = @"SOFTWARE\";
            const string baseKey64 = @"SOFTWARE\WoW6432Node\";
            const string uninstallKey = @"Microsoft\Windows\CurrentVersion\Uninstall";
            name = name.ToLower();
            return _CheckUninstallKey(name, baseKey + uninstallKey) || (_KeyExists(baseKey64) && _CheckUninstallKey(name, baseKey64 + uninstallKey));
        }

        private static bool _SystemDllExists(string dllName)
        {
            const string sysDir = "%windir%\\system32\\";
            return File.Exists(Environment.ExpandEnvironmentVariables(sysDir + dllName + ".dll"));
        }

        private static bool _IsVC2012Installed()
        {
            return _SystemDllExists("msvcr110") && _SystemDllExists("msvcp110");
        }

        [SupportedOSPlatform("windows")]
        private static bool _IsVC2008Installed()
        {
            return _IsProgramInstalled("Microsoft Visual C++ 2008 Redistributable");
        }

        [SupportedOSPlatform("windows")]
        private static bool _IsVC2010Installed()
        {
            //Note: Maybe check for x64 or x86
            return _IsProgramInstalled("Microsoft Visual C++ 2010");
        }

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
            if (OperatingSystem.IsWindows() && !_IsVC2010Installed())
            {
                CLog.Fatal(
                    "VC++ 2010 Redistributables are missing. Please install them first. \r\nDownload(x86): https://www.microsoft.com/de-de/download/details.aspx?id=5555 \r\nDownload(x64): https://www.microsoft.com/de-de/download/details.aspx?id=14632");
                return false;
            }
            /*
            if (!_IsVC2012Installed())
            {
                CLog.Fatal(
                    "VC++ 2012 Redistributables are missing. Please install them first.\r\nDownload: http://www.microsoft.com/de-de/download/details.aspx?id=30679");
                return false;
            }
            bool vc2008Installed = _IsVC2008Installed();
            if (!vc2008Installed)
            {
                CLog.Fatal(
                    "VC++ 2008 Redistributables are missing. Portaudio might not be working.\r\nDownload: http://www.microsoft.com/de-de/download/details.aspx?id=29");
            }
            if (!vc2008Installed && !_IsVC2010Installed())
            {
                CLog.Fatal(
                    "VC++ 2010 and 2008 Redistributables are missing. Please install them first. VC++ 2008 is preferred as Portaudio doesn't work with VC++ 2010.\r\nDownload(2008): http://www.microsoft.com/de-de/download/details.aspx?id=29 \r\nDownload(2010): http://www.microsoft.com/de-de/download/details.aspx?id=5555");
                return false;
            }
            */
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