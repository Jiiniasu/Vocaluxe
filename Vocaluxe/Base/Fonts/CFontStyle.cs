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
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Base.Fonts
{
    /// <summary>
    ///     One font style of a font family: the font file, the outline settings and the shared typeface.
    ///     Text lines are rasterized on demand per style/text/height by <see cref="CFontLine" /> (the
    ///     standard single-texture-per-line path) which caches the textures and disposes them here.
    /// </summary>
    class CFontStyle : IDisposable
    {
        private readonly string _FilePath;
        private readonly EStyle _Style;
        public readonly float Outline; //0..1, 0=not outline 1=100% outline
        public readonly SColorF OutlineColor;
        private readonly float _MaxGlyphHeight;
        private SKTypeface _Typeface;
        private bool _Disposed;

        public CFontStyle(string file, EStyle style, float outline, SColorF outlineColor)
        {
            _FilePath = file;
            _Style = style;
            Outline = outline;
            OutlineColor = outlineColor;

            switch (CConfig.Config.Graphics.TextureQuality)
            {
                case ETextureQuality.TR_CONFIG_TEXTURE_LOWEST:
                    _MaxGlyphHeight = 20f;
                    break;
                case ETextureQuality.TR_CONFIG_TEXTURE_LOW:
                    _MaxGlyphHeight = 40f;
                    break;
                case ETextureQuality.TR_CONFIG_TEXTURE_MEDIUM:
                    _MaxGlyphHeight = 60f;
                    break;
                case ETextureQuality.TR_CONFIG_TEXTURE_HIGH:
                    _MaxGlyphHeight = 80f;
                    break;
                case ETextureQuality.TR_CONFIG_TEXTURE_HIGHEST:
                    _MaxGlyphHeight = 100f;
                    break;
                default:
                    _MaxGlyphHeight = 60f;
                    break;
            }
        }

        /// <summary>
        ///     Height bucket (px) to rasterize a line drawn at <paramref name="height" /> at: always at
        ///     least the requested height (so the texture is only ever downsampled onto the quad - a
        ///     uniform resample keeps every relative glyph position exact) and quantized so that
        ///     resizing the text by a few pixels reuses the same texture (theme editor).
        /// </summary>
        internal int GetRasterBucket(float height)
        {
            if (height < _MaxGlyphHeight + 50f)
                return (int)_MaxGlyphHeight;
            return (int)(Math.Ceiling(height / 50f) * 50f);
        }

        /// <summary>
        ///     Loads (once) and returns the SkiaSharp typeface for this font file.
        ///     Bold/italic are NOT synthesized (Embolden/SkewX): the loaded face file already IS the
        ///     requested style (FreeSans / FreeSansBold / FreeSansOblique / FreeSansBoldOblique) and
        ///     synthesizing would double-apply both, which over-weighted the bold text and over-slanted
        ///     the italic glyphs until their ink overhung the advance and got clipped by neighbors.
        /// </summary>
        public SKTypeface GetTypeface()
        {
            if (_Typeface == null)
            {
                try
                {
                    _Typeface = SKTypeface.FromFile(_FilePath);
                }
                catch (Exception e)
                {
                    CLog.Error("Error opening font file " + _FilePath + ": " + e.Message);
                }
                if (_Typeface == null)
                    _Typeface = SKTypeface.Default;
            }
            return _Typeface;
        }

        public void Dispose()
        {
            if (!_Disposed)
            {
                CFontLine.UnloadStyle(this);
                if (_Typeface != null)
                {
                    _Typeface.Dispose();
                    _Typeface = null;
                }
                _Disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}