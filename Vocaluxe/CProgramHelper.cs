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

using System.IO;
using System.Reflection;
using Vocaluxe.Base;

namespace Vocaluxe
{
    /// <summary>
    ///     Some helper functions for the program
    /// </summary>
    static class CProgramHelper
    {
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
            // Nothing to check on any platform: the whole native stack (the FFmpeg 8.1.3 GPL shared
            // build, portaudio, PitchTracker) is linked against the static CRT or self-contained, so
            // the old VC++ 2015-2022 redist prompt and auto-download is gone. Kept as the single place
            // to add start-up prerequisites if they ever return.
            // TODO: check for dependencies on linux?
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