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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using SkiaSharp;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Gives the GLFW window an application icon the standard, cross-platform way: OpenTK's
    ///     <see cref="NativeWindow.Icon" /> (one <see cref="WindowIcon" /> that GLFW translates per platform -
    ///     WM window icon on Windows, <c>_NET_WM_ICON</c> on X11; the macOS shell reads the app bundle).
    ///     The pixel source is a raster shipped next to the exe - <c>Vocaluxe.ico</c> (largest image inside,
    ///     256x256) or, as a fallback, <c>Vocaluxe.png</c> - one asset for every platform, no per-OS icon code.
    ///     Best-effort: a missing or undecodable file is a logged no-op so the app always starts.
    ///     On Windows the task-bar entry additionally reads the window-CLASS icon, which OpenTK never sets -
    ///     that is CWindowsIcon's job (it builds an HICON from the same RGBA).
    /// </summary>
    static class CAppIcon
    {
        private static readonly string[] _CandidateNames = { "Vocaluxe.ico", "Vocaluxe.png" };

        /// <summary>
        ///     Decodes the first decodable icon file that sits in <paramref name="appDirectory"/> to straight
        ///     (unpremultiplied) RGBA bytes, top-to-bottom - exactly what GLFW's <c>glfwSetWindowIcon</c> expects.
        ///     For an .ico with several sizes, Skia decodes the largest image. Returns false (and null pixels)
        ///     when nothing usable is found.
        /// </summary>
        public static bool TryLoadRgba(string appDirectory, out byte[] rgba, out int width, out int height)
        {
            rgba = null;
            width = 0;
            height = 0;

            foreach (string name in _CandidateNames)
            {
                byte[] pixels = null;
                int w = 0, h = 0;
                if (!TryLoadRgba(appDirectory, name, out pixels, out w, out h))
                    continue;
                rgba = pixels; width = w; height = h;
                return true;
            }
            return false;
        }

        private static bool TryLoadRgba(string appDirectory, string fileName, out byte[] rgba, out int width, out int height)
        {
            rgba = null;
            width = 0;
            height = 0;

            string path = Path.Combine(appDirectory, fileName);
            if (!File.Exists(path))
                return false;

            using (SKCodec codec = SKCodec.Create(path))
            {
                if (codec == null)
                    return false;

                int w = codec.Info.Width;
                int h = codec.Info.Height;
                if (w <= 0 || h <= 0)
                    return false;

                // Same decode pattern the texture/cover loaders use (CTextureProvider, CResourceDB).
                SKImageInfo info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using (SKBitmap bitmap = new SKBitmap(info))
                {
                    IntPtr pixelsPtr = bitmap.GetPixels();
                    SKCodecResult res = codec.GetPixels(info, pixelsPtr);
                    if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                        return false;

                    // RGBA8888 is 4 bytes/px with no row padding; copy out while the bitmap is alive.
                    byte[] pixels = new byte[w * h * 4];
                    Marshal.Copy(pixelsPtr, pixels, 0, pixels.Length);

                    rgba = pixels;
                    width = w;
                    height = h;
                    return true;
                }
            }
        }

        /// <summary>
        ///     Sets the given straight RGBA pixels as the window icon via OpenTK's standard cross-platform API.
        /// </summary>
        public static void SetWindowIcon(NativeWindow window, byte[] rgba, int width, int height)
        {
            window.Icon = new WindowIcon(new[] { new Image(width, height, rgba) });
            CLog.Information("CAppIcon: window icon set from " + width + "x" + height + " raster");
        }
    }
}
