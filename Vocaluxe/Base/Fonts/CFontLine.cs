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
using VocaluxeLib;
using VocaluxeLib.Draw;

namespace Vocaluxe.Base.Fonts
{
    /// <summary>
    ///     Standard font rendering: rasterizes a whole text line with the Skia font engine into ONE texture
    ///     at (at least) its target size, drawn with a single quad.
    ///     This replaces the legacy per-glyph pipeline (CGlyph + CFontStyle's glyph dictionaries), which
    ///     rasterized every character separately at a fixed maximum height, located its ink with an
    ///     INTEGER-pixel scan, cropped each character into its own texture and placed its own quad with
    ///     independently rounded corners. That stack has an irreducible per-glyph subpixel floor ("wavy
    ///     words"): the true anti-aliased ink edge sits at a fractional raster-pixel position the integer
    ///     scan cannot even see, so no amount of quad snapping can make the per-glyph baselines agree.
    ///     Rasterizing the line in one pass lets the font engine place every glyph at subpixel precision
    ///     relative to its neighbors (exactly what a browser/OS text renderer does, and what the game's
    ///     own cover generator already does via SKCanvas.DrawText). Drawing one texture with one quad
    ///     preserves all those relative positions exactly; the only remaining quantization is the single
    ///     quad's corner rounding in COpenGL._DrawTexture, which shifts the WHOLE line by half a pixel -
    ///     no relative drift between the letters, no wave.
    ///
    ///     Geometry convention (kept consistent with the legacy line boxes so theme layouts are
    ///     unchanged): at raster size B the line box is [advance + outline] wide and
    ///     [fullHeight + outline] tall (outline bleed of outline/2 per side), and the box is scaled so the
    ///     drawn height is exactly the requested font height - the same box the legacy per-glyph
    ///     GetTextWidth/GetTextHeight reported.
    ///
    ///     Textures are cached per (CFontStyle, height bucket, text); CFontStyle.Dispose (theme /
    ///     party-mode font unload) drops the style's entries, and <see cref="Close" /> drops everything.
    /// </summary>
    sealed class CFontLine
    {
        /// <summary>
        ///     Result of rasterizing one line: the pixel data plus the box metrics used for the quad mapping.
        /// </summary>
        internal sealed class SLineRasterData
        {
            internal byte[] Bgra;
            internal int W;
            internal int H;
            internal float Advance; // whole-text advance at raster size (px, kerning included)
            internal float BoxW;    // line box width at raster size (advance + outline)
            internal float BoxH;    // line box height at raster size (fullHeight + outline)
        }

        private CTextureRef _Texture;
        private readonly float _BoxW;
        private readonly float _BoxH;

        private CFontLine(CTextureRef texture, float boxW, float boxH)
        {
            _Texture = texture;
            _BoxW = boxW;
            _BoxH = boxH;
        }

        internal CTextureRef Texture
        {
            get { return _Texture; }
        }

        /// <summary>
        ///     Width of the drawn quad at the given font height. The line box scales uniformly, so this is
        ///     exactly the width CFonts.GetTextWidth reports for the line (centering and the karaoke
        ///     begin/end clip line up with the pixels actually drawn).
        /// </summary>
        internal float DrawWidth(float height)
        {
            if (height <= 0f)
                return 0f;
            return _BoxW * height / _BoxH;
        }

        public void Dispose()
        {
            CDraw.RemoveTexture(ref _Texture);
            _Texture = null;
        }

        #region cache
        // Cache and LRU bookkeeping. All access happens on the main thread (draw / text queries do),
        // exactly like the legacy per-glyph textures; the lock is belt and braces.
        private static readonly object _Lock = new object();
        private static readonly Dictionary<CFontStyle, Dictionary<SCacheKey, SCacheEntry>> _ByStyle = new Dictionary<CFontStyle, Dictionary<SCacheKey, SCacheEntry>>();
        private static int _Tick;
        private const int MaxCachedLines = 256;

        // LRU order is carried by a monotonically increasing tick. If it approaches int.MaxValue,
        // zero all ticks first so the relative order stays valid (we only hold MaxCachedLines entries).
        private static int _NextTick()
        {
            if (_Tick > int.MaxValue - MaxCachedLines - 1)
            {
                foreach (KeyValuePair<CFontStyle, Dictionary<SCacheKey, SCacheEntry>> styleEntry in _ByStyle)
                    foreach (KeyValuePair<SCacheKey, SCacheEntry> keyEntry in styleEntry.Value)
                        keyEntry.Value.Tick = 0;
                _Tick = 0;
            }
            return ++_Tick;
        }

        private struct SCacheKey
        {
            public int Bucket;
            public string Text;

            public bool Equals(SCacheKey other)
            {
                return Bucket == other.Bucket && Text == other.Text; // string ==: value equality
            }

            public override int GetHashCode()
            {
                return Bucket * 397 ^ Text.GetHashCode();
            }
        }

        private sealed class SCacheEntry
        {
            public CFontLine Line;
            public int Tick;
        }

        /// <summary>
        ///     Returns the cached (or newly rasterized) line for the given style/text/height.
        ///     Rasterizes at a height bucket >= the requested height so the texture is only ever
        ///     DOWN-sampled onto the quad - a uniform resample keeps every relative glyph position exact.
        /// </summary>
        public static CFontLine Get(CFontStyle style, string text, float height)
        {
            SCacheKey key = new SCacheKey { Bucket = style.GetRasterBucket(height), Text = text };
            lock (_Lock)
            {
                Dictionary<SCacheKey, SCacheEntry> lines;
                if (!_ByStyle.TryGetValue(style, out lines))
                    lines = _ByStyle[style] = new Dictionary<SCacheKey, SCacheEntry>();

                SCacheEntry entry;
                if (lines.TryGetValue(key, out entry))
                {
                    entry.Tick = _NextTick();
                    return entry.Line;
                }

                // Rasterizing and the texture upload must not happen while another thread could
                // mutate the cache (main-thread only in practice - the lock is belt and braces).
                SLineRasterData raster = _Rasterize(style.GetTypeface(), key.Bucket, text, style.Outline, style.OutlineColor);
                CTextureRef texture = CDraw.AddTexture(raster.W, raster.H, raster.Bgra);

                CFontLine line = new CFontLine(texture, raster.BoxW, raster.BoxH);
                entry = new SCacheEntry { Line = line, Tick = _NextTick() };
                lines.Add(key, entry);
                _EvictIfNeeded();
                return entry.Line;
            }
        }

        private static void _EvictIfNeeded()
        {
            int total = 0;
            foreach (Dictionary<SCacheKey, SCacheEntry> lines in _ByStyle.Values)
                total += lines.Count;

            // LRU: evict the least recently used line(s) until under the cap.
            while (total > MaxCachedLines)
            {
                Dictionary<SCacheKey, SCacheEntry> oldestOwner = null;
                SCacheKey oldestKey = new SCacheKey();
                SCacheEntry oldestEntry = null;
                int oldestTick = int.MaxValue;
                foreach (KeyValuePair<CFontStyle, Dictionary<SCacheKey, SCacheEntry>> styleEntry in _ByStyle)
                {
                    foreach (KeyValuePair<SCacheKey, SCacheEntry> keyEntry in styleEntry.Value)
                    {
                        if (keyEntry.Value.Tick < oldestTick)
                        {
                            oldestOwner = styleEntry.Value;
                            oldestKey = keyEntry.Key;
                            oldestEntry = keyEntry.Value;
                            oldestTick = keyEntry.Value.Tick;
                        }
                    }
                }
                if (oldestEntry == null) // nothing to evict (shouldn't happen)
                    break;

                oldestOwner.Remove(oldestKey);
                oldestEntry.Line.Dispose();
                total--;
            }
        }

        /// <summary>
        ///     Drops all cached lines of the given style. Called from CFontStyle.Dispose (theme /
        ///     party-mode font unload).
        /// </summary>
        public static void UnloadStyle(CFontStyle style)
        {
            lock (_Lock)
            {
                Dictionary<SCacheKey, SCacheEntry> lines;
                if (_ByStyle.TryGetValue(style, out lines))
                {
                    foreach (SCacheEntry entry in lines.Values)
                        entry.Line.Dispose();
                    _ByStyle.Remove(style);
                }
            }
        }

        /// <summary>
        ///     Drops all cached lines. Called from CFonts.Close.
        /// </summary>
        public static void Close()
        {
            lock (_Lock)
            {
                foreach (Dictionary<SCacheKey, SCacheEntry> lines in _ByStyle.Values)
                {
                    foreach (SCacheEntry entry in lines.Values)
                        entry.Line.Dispose();
                }
                _ByStyle.Clear();
            }
        }
        #endregion cache

        /// <summary>
        ///     Rasterizes the whole line with the Skia font engine (the standard way of rendering text):
        ///     one canvas, one DrawText, subpixel-precise glyph positioning by the font engine with the
        ///     same settings the legacy per-glyph rasters used (antialiasing, no grid-fit hinting, no
        ///     subpixel/ClearType coverage - both of which shift ink per glyph), outline stroke first,
        ///     white fill second (the draw color is a GPU tint, so one texture serves every color).
        ///     Pure function of (typeface, size, text, outline) - unit-testable without a draw context.
        /// </summary>
        /// <param name="typeface">The font face</param>
        /// <param name="size">The raster size in pixels (must be >= the height the line will be drawn at)</param>
        /// <param name="text">The text of the line</param>
        /// <param name="outline">Outline size as a fraction of size (0 = none)</param>
        /// <param name="outlineColor">Outline color</param>
        internal static SLineRasterData _Rasterize(SKTypeface typeface, float size, string text, float outline, SColorF outlineColor)
        {
            float outlineSize = outline * size;
            using (var font = new SKFont(typeface, size))
            {
                font.Edging = SKFontEdging.Antialias;
                font.Hinting = SKFontHinting.None;
                font.Subpixel = false;

                SKFontMetrics metrics = font.Metrics;
                float fullH = metrics.Descent - metrics.Ascent;
                SKRect tightBounds;
                float advance = font.MeasureText(text, out tightBounds);

                // Line box: advance + outline bleed (outline/2 per side) wide,
                // full line height + outline bleed tall - the same box the legacy per-glyph
                // GetTextWidth/GetTextHeight reported, so theme layouts stay put.
                float boxW = advance + outlineSize;
                float boxH = fullH + outlineSize;
                int w = Math.Max(1, (int)Math.Ceiling(boxW));
                int h = Math.Max(1, (int)Math.Ceiling(boxH));

                byte[] bgra;
                float x0 = outlineSize / 2f;
                float baselineY = -metrics.Ascent + outlineSize / 2f;
                using (var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul))
                {
                    using (var canvas = new SKCanvas(bmp))
                    {
                        canvas.Clear(SKColors.Transparent);

                        if (outlineSize > 0)
                        {
                            using (var outlinePaint = new SKPaint
                            {
                                IsAntialias = true,
                                Style = SKPaintStyle.Stroke,
                                StrokeWidth = outlineSize,
                                StrokeJoin = SKStrokeJoin.Round,
                                Color = _ToSkColor(outlineColor)
                            })
                                canvas.DrawText(text, x0, baselineY, font, outlinePaint);
                        }

                        using (var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White })
                            canvas.DrawText(text, x0, baselineY, font, fillPaint);
                    }

                    // SkiaSharp 3+: SKBitmap.Bytes is not guaranteed to be the pixel store - take a copy
                    // before the bitmap is disposed and the pixels handed to the texture upload.
                    bgra = (byte[])bmp.Bytes.Clone();
                }

                return new SLineRasterData
                {
                    Bgra = bgra,
                    W = w,
                    H = h,
                    Advance = advance,
                    BoxW = boxW,
                    BoxH = boxH
                };
            }
        }

        private static SKColor _ToSkColor(SColorF c)
        {
            return new SKColor((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255), (byte)(c.A * 255));
        }
    }
}