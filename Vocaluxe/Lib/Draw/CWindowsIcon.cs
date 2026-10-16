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
#if WIN

using System;
using System.IO;
using System.Runtime.InteropServices;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Forces the application icon onto the top-level GLFW window so it appears crisply in BOTH the
    ///     Windows task bar (window-CLASS icon) and the title bar (window icon). GLFW never sets a class
    ///     icon, so a fresh window shows the generic IDI_APPLICATION (a blank/generic icon) there - while
    ///     <c>NativeWindow.Icon</c> (see CAppIcon) only issues WM_SETICON and does not reliably reach the
    ///     task-bar entry. This restores the original look of the .NET Framework (WinForms) host.
    ///
    ///     The HICON is built with <c>CreateIconFromResourceEx</c> directly from the largest BMP image in
    ///     the <c>Vocaluxe.ico</c> shipped next to the exe (256x256 -> a crisp task-bar source at any DPI);
    ///     if that file is missing or unparsable, the shell's own icon for the executable is used
    ///     (<c>SHGetFileInfo</c>). Note: <c>CreateIconIndirect</c> (DIB sections) is avoided on purpose -
    ///     it access-violates on some machines, and <c>ExtractIconEx</c> is avoided too because it returns
    ///     nothing for .NET app-host PEs even though the shell can read the icon. Strictly best-effort:
    ///     any failure is a logged no-op and the window still has the CAppIcon title-bar icon.
    /// </summary>
    static class CWindowsIcon
    {
        // Icon slot indices (identical values for class and window; kept separate for readability).
        private const int GCLP_HICON = -34;
        private const int GCLP_HICONSM = -35;
        private const int GWLP_HICON = -34;
        private const int GWLP_HICONSM = -35;

        // SetWindowPos flags: nothing moves, just "the frame changed" -> the shell re-queries the icons.
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>
        ///     Gives the top-level window whose caption is <paramref name="windowTitle" /> the application
        ///     icon as both its class icon (task bar) and window icon (title bar). Safe to call while the
        ///     window is still hidden. <paramref name="appDirectory" /> is the folder holding the exe and -
        ///     shipped with the build - the application icon file.
        /// </summary>
        public static void Apply(string windowTitle, string appDirectory)
        {
            if (string.IsNullOrEmpty(windowTitle))
                return;

            IntPtr hIcon = IntPtr.Zero;
            string how = "";

            string icoPath = Path.Combine(appDirectory, "Vocaluxe.ico");
            hIcon = TryFromIcoFile(icoPath);
            how = "Vocaluxe.ico (CreateIconFromResourceEx)";

            if (hIcon == IntPtr.Zero)
            {
                // Shell's own icon for the executable - what Explorer shows for it (32x32, but valid).
                try
                {
                    hIcon = ShellIcon(Environment.ProcessPath);
                    how = "shell icon of " + Environment.ProcessPath;
                }
                catch (Exception e)
                {
                    CLog.Warning(e, "CWindowsIcon: SHGetFileInfo failed");
                }
            }

            if (hIcon == IntPtr.Zero)
            {
                CLog.Warning("CWindowsIcon: no icon could be created; the window keeps the default task-bar icon (title-bar icon is still set by CAppIcon)");
                return;
            }

            IntPtr hwnd = FindWindowW(null, windowTitle);
            if (hwnd == IntPtr.Zero)
            {
                CLog.Information("CWindowsIcon: window '" + windowTitle + "' not found; class icon not applied");
                DestroyIcon(hIcon);
                return;
            }

            // Class icon = what Explorer/the task bar read; window icon = what the title bar reads.
            SetClassLongPtrW(hwnd, GCLP_HICON, hIcon);
            SetClassLongPtrW(hwnd, GCLP_HICONSM, hIcon);
            SetWindowLongPtrW(hwnd, GWLP_HICON, hIcon);
            SetWindowLongPtrW(hwnd, GWLP_HICONSM, hIcon);

            // Ask the shell to refresh the (still hidden) task-bar entry now that the window has icons.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);

            // NOTE: the icon is deliberately NOT DestroyIcon'ed here. Once attached to a live
            // window/class the system resolves it from the slot, and destroying it invalidates the
            // attached icon (observed: GetIconInfo fails on the slot afterwards). One HICON per
            // process lifetime is the standard pattern for window/task-bar icons.

            CLog.Information("CWindowsIcon: task-bar (class) + title-bar (window) icon set from " + how);
        }

        /// <summary>
        ///     Reads <paramref name="icoPath" />, finds the largest BMP-format image entry (a 256x256 modern
        ///     ico stores these as 32bpp BITMAPINFOHEADER + pixel data - exactly the ICONRESOURCE layout) and
        ///     turns it into an HICON. PNG-compressed entries are skipped (CreateIconFromResourceEx parses
        ///     the BMP layout); returning NULL when nothing suitable is found.
        /// </summary>
        private static IntPtr TryFromIcoFile(string icoPath)
        {
            try
            {
                if (!File.Exists(icoPath))
                    return IntPtr.Zero;

                byte[] file = File.ReadAllBytes(icoPath);
                if (file.Length < 6)
                    return IntPtr.Zero;

                int count = BitConverter.ToInt16(file, 4);
                if (count <= 0 || 6 + count * 16 > file.Length)
                    return IntPtr.Zero;

                int bestLen = 0, bestOff = 0, bestW = 0, bestH = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + i * 16;
                    int w = file[e] == 0 ? 256 : file[e];
                    int h = file[e + 1] == 0 ? 256 : file[e + 1];
                    int len = BitConverter.ToInt32(file, e + 8);
                    int off = BitConverter.ToInt32(file, e + 12);
                    if (len <= 0 || off < 0 || off + Math.Max(4, len) > file.Length)
                        continue;
                    if (BitConverter.ToUInt32(file, off) != 40) // not a BITMAPINFOHEADER (e.g. a PNG entry)
                        continue;
                    if (len > bestLen)
                    {
                        bestLen = len; bestOff = off; bestW = w; bestH = h;
                    }
                }

                if (bestLen <= 0)
                    return IntPtr.Zero;

                byte[] entry = new byte[bestLen];
                Array.Copy(file, bestOff, entry, 0, bestLen);

                // winuser.h: ... cxDesired(int), cyDesired(int), Flags(UINT). With explicit sizes and
                // no LR_DEFAULTSIZE flag, the resource's actual dimensions are used (no resampling).
                IntPtr hIcon = CreateIconFromResourceEx(entry, (uint)bestLen, true, 0x00030000 /*v3.0*/, bestW, bestH, 0 /*LR_DEFAULTCOLOR*/);
                if (hIcon != IntPtr.Zero)
                    return hIcon;

                // Some Windows builds only implement the classic API.
                hIcon = CreateIconFromResource(entry, (uint)bestLen, true, 0x00030000 /*v3.0*/);
                return hIcon;
            }
            catch (Exception e)
            {
                CLog.Warning(e, "CWindowsIcon: failed to build the icon from " + icoPath);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        ///     Asks the shell for the icon it displays for <paramref name="file" /> (the same one Explorer
        ///     shows). The caller must <c>DestroyIcon</c> the result.
        /// </summary>
        private static IntPtr ShellIcon(string file)
        {
            if (string.IsNullOrEmpty(file))
                return IntPtr.Zero;

            SHFILEINFO info = new SHFILEINFO();
            SHGetFileInfo(file, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x00000100 /*SHGFI_ICON*/);
            if (info.hIcon == IntPtr.Zero)
                return IntPtr.Zero;
            return info.hIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        // The documented winuser.h signature passes the size as TWO int arguments (cxDesired, cyDesired),
        // NOT as a struct pointer - a "ref struct" form silently fails (NULL) on real user32.dll.
        [DllImport("user32.dll", EntryPoint = "CreateIconFromResourceEx")]
        private static extern IntPtr CreateIconFromResourceEx(byte[] presBits, uint dwResSize, bool fIcon, int dwVersion, int cxDesired, int cyDesired, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "CreateIconFromResource")]
        private static extern IntPtr CreateIconFromResource(byte[] presBits, uint dwResSize, bool fIcon, int dwVersion);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        private static extern IntPtr SetClassLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
#endif