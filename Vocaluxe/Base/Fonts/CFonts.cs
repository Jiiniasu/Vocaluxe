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

using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;
using VocaluxeLib.Xml;

namespace Vocaluxe.Base.Fonts
{
    static class CFonts
    {
        private static bool _IsInitialized;
        private static readonly List<SFontFamily> _FontFamilies = new List<SFontFamily>();
        private static readonly List<String> _LoggedMissingFonts = new List<string>();

        public static int PartyModeID { get; set; }

        public static bool IsInitialized
        {
            get { return _IsInitialized; }
        }

        public static bool Init()
        {
            if (_IsInitialized)
                return false;
            PartyModeID = -1;
            bool ok = _LoadDefaultFonts();
            _IsInitialized = ok;
            return ok;
        }

        public static void Close()
        {
            foreach (SFontFamily font in _FontFamilies)
                font.Dispose(); // CFontStyle.Dispose drops the style's cached CFontLine textures
            _FontFamilies.Clear();
            CFontLine.Close();
            _IsInitialized = false;
        }

        #region DrawText
        /// <summary>
        ///     Draws a black text
        /// </summary>
        /// <param name="text">The text to be drawn</param>
        /// <param name="font">The texts font</param>
        /// <param name="x">The texts x-position</param>
        /// <param name="y">The texts y-position</param>
        /// <param name="z">The texts z-position</param>
        public static void DrawText(string text, CFont font, float x, float y, float z)
        {
            DrawText(text, font, x, y, z, new SColorF(0f, 0f, 0f, 1f));
        }

        /// <summary>
        ///     Draws a text
        /// </summary>
        /// <param name="text">The text to be drawn</param>
        /// <param name="font">The texts font</param>
        /// <param name="x">The texts x-position</param>
        /// <param name="y">The texts y-position</param>
        /// <param name="z">The texts z-position</param>
        /// <param name="color">The text color</param>
        public static void DrawText(string text, CFont font, float x, float y, float z, SColorF color, bool allMonitors = true)
        {
            if (text == null || text == "" || font.Height <= 0f)
                return;

            CFontStyle fontStyle = _GetFontStyle(font);
            CFontLine line = CFontLine.Get(fontStyle, text, font.Height);
            CDraw.DrawTexture(line.Texture, new SRectF(x, y, line.DrawWidth(font.Height), font.Height, z), color, false, allMonitors);
        }

        public static void DrawTextReflection(string text, CFont font, float x, float y, float z, SColorF color, float rspace, float rheight, float mirrorLine = 0f, float fadeOrigin = 0f, float fadeSpan = 0f)
        {
            if (text == null || text == "" || font.Height <= 0f)
                return;

            CFontStyle fontStyle = _GetFontStyle(font);
            CFontLine line = CFontLine.Get(fontStyle, text, font.Height);
            SRectF rect = new SRectF(x, y, line.DrawWidth(font.Height), font.Height, z);

            // The single line texture mirrors as one object (uniform distance to the mirror line, one
            // fade ramp) instead of mirroring each glyph's own ink box at a different position - which
            // is also what CText's mirrorLine math (the line box bottom) assumed all along.
            CDraw.DrawTextureReflection(line.Texture, rect, color, rect, rspace, rheight, true, mirrorLine, fadeOrigin, fadeSpan);
        }

        public static void DrawText(string text, CFont font, float x, float y, float z, SColorF color, float begin, float end)
        {
            if (text == null || text == "" || font.Height <= 0f)
                return;
            if (begin >= end)
                return;

            CFontStyle fontStyle = _GetFontStyle(font);
            CFontLine line = CFontLine.Get(fontStyle, text, font.Height);
            // The karaoke reveal is a plain X-clip of the one line texture (fraction of the whole width,
            // same mapping the old per-glyph slice used over the same total width) - no per-glyph steps.
            CDraw.DrawTexture(line.Texture, new SRectF(x, y, line.DrawWidth(font.Height), font.Height, z), color, begin, end);
        }
        #endregion DrawText

        /// <summary>
        ///     Calculates the bounds for a CText object
        /// </summary>
        /// <param name="text">The CText object of which the bounds should be calculated for</param>
        /// <returns>RectangleF object containing the bounds</returns>
        public static RectangleF GetTextBounds(CText text)
        {
            return new RectangleF(text.X, text.Y, GetTextWidth(text.TranslatedText, text.CalculatedFont), GetTextHeight(text.TranslatedText, text.CalculatedFont));
        }

        public static SkiaSharp.SKTypeface GetTypeface(CFont font)
        {
            return _GetFontStyle(font).GetTypeface();
        }

        public static float GetOutlineSize(CFont font)
        {
            return _FontFamilies[_GetFontIndex(font.Name)].Outline;
        }

        public static SColorF GetOutlineColor(CFont font)
        {
            return _FontFamilies[_GetFontIndex(font.Name)].OutlineColor;
        }

        private static CFontStyle _GetFontStyle(CFont font)
        {
            int index = _GetFontIndex(font.Name);

            switch (font.Style)
            {
                case EStyle.Normal:
                    return _FontFamilies[index].Normal;
                case EStyle.Italic:
                    return _FontFamilies[index].Italic;
                case EStyle.Bold:
                    return _FontFamilies[index].Bold;
                case EStyle.BoldItalic:
                    return _FontFamilies[index].BoldItalic;
            }
            throw new ArgumentException("Invalid Style: " + font.Style);
        }

        public static float GetTextWidth(string text, CFont font)
        {
            if (text == null || text == "" || font.Height <= 0f)
                return 0f;

            CFontStyle fontStyle = _GetFontStyle(font);
            int bucket = fontStyle.GetRasterBucket(font.Height);
            // Same box convention as CFontLine uses for the texture/quad: the whole-line advance
            // (kerning included - the font engine measures the string, not a per-character sum) plus
            // the outline bleed, scaled to exactly the height the line will be drawn at. Keeping the
            // two in lock-step means centering and the karaoke clip line up with the drawn pixels.
            using (var skFont = new SKFont(fontStyle.GetTypeface(), bucket))
            {
                float outlineSize = fontStyle.Outline * bucket;
                SKFontMetrics metrics = skFont.Metrics;
                float fullH = metrics.Descent - metrics.Ascent;
                SKRect tightBounds;
                float advance = skFont.MeasureText(text, out tightBounds);
                return (advance + outlineSize) * font.Height / (fullH + outlineSize);
            }
        }

        public static float GetTextHeight(string text, CFont font)
        {
            // The line box height is exactly the requested font height for every non-empty line (the
            // same the legacy per-glyph box reported: all glyphs of a line shared the ascent->descent height).
            if (text == null || text == "" || font.Height <= 0f)
                return 0f;
            return font.Height;
        }

        private static int _GetFontIndex(string fontName)
        {
            if (fontName != "")
            {
                int index = _GetPartyFontIndex(fontName, PartyModeID);
                if (index < 0)
                    index = _GetThemeFontIndex(fontName, CConfig.Config.Theme.Theme);
                if (index >= 0)
                    return index;
                for (int i = 0; i < _FontFamilies.Count; i++)
                {
                    if (_FontFamilies[i].Name == fontName)
                        return i;
                }
                if (!_LoggedMissingFonts.Contains(fontName))
                {
                    _LoggedMissingFonts.Add(fontName);
                    CLog.Error("Font \"" + fontName + "\" not found!");
                }
            }
            else
                CLog.Error("Empty fontName requested");

            if (_FontFamilies.Count == 0)
                CLog.Fatal("No fonts found!");

            return 0;
        }

        private static int _GetThemeFontIndex(string fontName, string themeName)
        {
            Debug.Assert(fontName != "" && themeName != "");

            for (int i = 0; i < _FontFamilies.Count; i++)
            {
                if (_FontFamilies[i].Name == fontName && _FontFamilies[i].ThemeName == themeName)
                    return i;
            }

            return -1;
        }

        private static int _GetPartyFontIndex(string fontName, int partyModeID)
        {
            Debug.Assert(fontName != "");

            for (int i = 0; i < _FontFamilies.Count; i++)
            {
                if (_FontFamilies[i].PartyModeID == partyModeID && _FontFamilies[i].Name == fontName)
                    return i;
            }

            return -1;
        }

        /// <summary>
        ///     Load default fonts
        /// </summary>
        /// <returns></returns>
        private static bool _LoadDefaultFonts()
        {
            SDefaultFonts defaultFonts;
            try
            {
                var xml = new CXmlDeserializer();
                defaultFonts = xml.Deserialize<SDefaultFonts>(Path.Combine(CSettings.ProgramFolder, CSettings.FolderNameFonts, CSettings.FileNameFonts));
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error loading default Fonts");
                return false;
            }

            return LoadThemeFonts(defaultFonts.Fonts, Path.Combine(CSettings.ProgramFolder, CSettings.FolderNameFonts), "", -1);
        }

        /// <summary>
        ///     Loads theme fonts
        /// </summary>
        public static bool LoadThemeFonts(IEnumerable<SFontFamily> fontFamilies, string fontFolder, string themeName, int partyModeId)
        {
            if (fontFamilies == null)
                return true;
            foreach (SFontFamily fontFamily in fontFamilies)
            {
                if (!_LoadFont(fontFamily, fontFolder, themeName, partyModeId))
                {
                    CLog.Error("Error loading fonts for Theme " + themeName + "(" + partyModeId + ")");
                    return false;
                }
            }

            return true;
        }

        private static bool _LoadFont(SFontFamily fontFamily, string fontFolder, string themeName, int partyModeId)
        {
            fontFamily.ThemeName = themeName;
            fontFamily.PartyModeID = partyModeId;
            fontFamily.Normal = new CFontStyle(Path.Combine(fontFolder, fontFamily.Folder, fontFamily.FileNormal), EStyle.Normal, fontFamily.Outline, fontFamily.OutlineColor);
            fontFamily.Italic = new CFontStyle(Path.Combine(fontFolder, fontFamily.Folder, fontFamily.FileItalic), EStyle.Italic, fontFamily.Outline, fontFamily.OutlineColor);
            fontFamily.Bold = new CFontStyle(Path.Combine(fontFolder, fontFamily.Folder, fontFamily.FileBold), EStyle.Bold, fontFamily.Outline, fontFamily.OutlineColor);
            fontFamily.BoldItalic = new CFontStyle(Path.Combine(fontFolder, fontFamily.Folder, fontFamily.FileBoldItalic), EStyle.BoldItalic, fontFamily.Outline,
                                                   fontFamily.OutlineColor);
            _FontFamilies.Add(fontFamily);
            return true;
        }

        public static void UnloadThemeFonts(string themeName)
        {
            int index = 0;
            while (index < _FontFamilies.Count)
            {
                if (_FontFamilies[index].ThemeName == themeName)
                {
                    _FontFamilies[index].Dispose();
                    _FontFamilies.RemoveAt(index);
                }
                else
                    index++;
            }
        }

        public static void UnloadPartyModeFonts(int partyModeID)
        {
            Debug.Assert(partyModeID >= 0);
            int index = 0;
            while (index < _FontFamilies.Count)
            {
                if (_FontFamilies[index].PartyModeID == partyModeID)
                {
                    _FontFamilies[index].Dispose();
                    _FontFamilies.RemoveAt(index);
                }
                else
                    index++;
            }
        }

    }
}