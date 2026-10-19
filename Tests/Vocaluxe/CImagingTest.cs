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

using NUnit.Framework;
using SkiaSharp;
using System;
using System.IO;
using Vocaluxe.Base.Fonts;
using VocaluxeLib;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Headless coverage for the GDI+ -> SkiaSharp imaging migration (S4): image decode,
    ///     texture-quality resize, PNG encode and font rasterization. These run on every OS in
    ///     CI and also prove the native libSkiaSharp loads and rasterizes there - the unit suite
    ///     otherwise only exercises platform-independent VocaluxeLib logic, none of the ported
    ///     imaging subsystems.
    /// </summary>
    [TestFixture]
    public class CImagingTest
    {
        // Wavy-word regression (see the 2026-10-16 plan log): the legacy per-glyph pipeline
        // rasterized each character separately (integer ink scan, own texture, own quad) so the
        // shared baseline of a line landed up to 0.4px apart per glyph - the wavy words. Text is
        // now rasterized as ONE line per string (CFontLine._Rasterize) and drawn with a single quad,
        // which makes the per-glyph drift impossible by construction. These tests pin the properties
        // of that standard path that the quad mapping relies on: the box geometry, one shared
        // baseline for the whole line, and consistent placement of repeated glyphs.
        [Test]
        public void CFontLineRaster_WholeLine_BoxGeometryAndInk()
        {
            SKTypeface typeface = SKTypeface.Default; // shared instance: no dispose
            CFontLine.SLineRasterData raster = CFontLine._Rasterize(typeface, 60f, "Vocaluxe", 0f, new SColorF(1f, 1f, 1f, 1f));
            using (var font = new SKFont(typeface, 60f))
            {
                SKFontMetrics metrics = font.Metrics;
                SKRect tightBounds;
                float advance = font.MeasureText("Vocaluxe", out tightBounds);

                // Box = exactly the ink-extent the quad mapping assumes (uniform scale, no per-glyph stretch)
                Assert.AreEqual(Math.Max(1, (int)Math.Ceiling(advance)), raster.W);
                Assert.AreEqual(Math.Max(1, (int)Math.Ceiling(metrics.Descent - metrics.Ascent)), raster.H);
            }

            // The line must actually be inked, and the ink must fit inside the box.
            Assert.Greater(_CountInkedPixels(raster.Bgra, raster.W, raster.H), raster.W * raster.H / 20, "the line should be visibly inked");
            (int minX, int maxX, int minY, int maxY) = _InkBBox(raster.Bgra, raster.W, raster.H, 0, raster.W);
            Assert.GreaterOrEqual(minX, 0);
            Assert.LessOrEqual(maxX + 1, raster.W);
            Assert.GreaterOrEqual(minY, 0);
            Assert.LessOrEqual(maxY + 1, raster.H);
        }

        // The property that kills the wave: identical repeated glyphs of a line must sit on the
        // SAME baseline row. The rasterizer draws the whole line in one pass on one shared
        // baseline, so every copy of a glyph lands on the identical row; the legacy per-glyph
        // design let each glyph's drawn baseline drift by up to 0.4px (the "singing" wobble
        // measured on FreeSans). Different glyphs may differ by up to ~1-2px - that is the font's
        // own optical overshoot (round bowls like s/a/e dip a hair below the baseline in ANY
        // renderer, even a browser), not a position drift.
        [Test]
        public void CFontLineRaster_SharedBaseline_BottomInkRowsAgreeAcrossGlyphs()
        {
            const string text = "singing";
            SKTypeface typeface = SKTypeface.Default; // shared instance: no dispose
            CFontLine.SLineRasterData raster = CFontLine._Rasterize(typeface, 60f, text, 0f, new SColorF(1f, 1f, 1f, 1f));
            using (var font = new SKFont(typeface, 60f))
            {
                // Per-character regions from the font engine's own advances (kerning inclusive).
                float[] x = new float[text.Length + 1];
                for (int i = 0; i <= text.Length; i++)
                {
                    SKRect tightBounds;
                    x[i] = font.MeasureText(text.Substring(0, i), out tightBounds);
                }

                int bottom(int i)
                {
                    (_, _, _, int b) = _InkBBox(raster.Bgra, raster.W, raster.H, (int)Math.Ceiling(x[i]) + 1, (int)Math.Floor(x[i + 1]) - 1);
                    return b;
                }

                // Strong invariant (the anti-wave regression): the two copies of each repeated glyph
                // must land on the IDENTICAL baseline row. In one shared-baseline pass they always do;
                // in the legacy per-glyph pipeline independent corner rounding let copies drift apart.
                Assert.AreEqual(bottom(1), bottom(4), "both 'i' copies must sit on the same baseline row");
                Assert.AreEqual(bottom(2), bottom(5), "both 'n' copies must sit on the same baseline row");
                Assert.AreEqual(bottom(3), bottom(6), "both 'g' copies must sit on the same baseline row");

                // Different x-height glyphs stay within the font's own overshoot tolerance (~2px). This
                // is glyph design (round bowls dip a hair below the baseline in any renderer), not drift.
                int lowest = Math.Min(bottom(0), Math.Min(bottom(1), bottom(2)));
                int highest = Math.Max(bottom(0), Math.Max(bottom(1), bottom(2)));
                Assert.LessOrEqual(highest - lowest, 2, "x-height glyph bases sit within font-overshoot tolerance");

                // And the descender 'g' really does extend below that shared x-height row.
                Assert.Greater(bottom(3), bottom(2), "'g' must descend below the shared baseline");
            }
        }

        // Repeated glyphs of a line sit at the same relative position (the font engine places
        // them at subpixel precision consistently) - so the single-quad downsample to the screen
        // size shifts each one by the same amount: no relative drift remains at any size.
        [Test]
        public void CFontLineRaster_RepeatedGlyphs_SitAtSameRelativePosition()
        {
            const string text = "singing";
            SKTypeface typeface = SKTypeface.Default; // shared instance: no dispose
            CFontLine.SLineRasterData raster = CFontLine._Rasterize(typeface, 60f, text, 0f, new SColorF(1f, 1f, 1f, 1f));
            using (var font = new SKFont(typeface, 60f))
            {
                float[] x = new float[text.Length + 1];
                for (int i = 0; i <= text.Length; i++)
                {
                    SKRect tightBounds;
                    x[i] = font.MeasureText(text.Substring(0, i), out tightBounds);
                }

                (float cx, float cy) com1 = _CenterOfMass(raster, x[1], x[2]); // 'i' #1
                (float cx, float cy) com2 = _CenterOfMass(raster, x[4], x[5]); // 'i' #2
                // The two cells start at different fractional x positions, so their stems hit the
                // pixel grid with different subpixel phases and the quantized center of mass may
                // differ by up to one pixel - that is raster quantization, not glyph drift. The
                // regression under test (per-glyph baseline wobble) shows up on the y axis, which
                // is independent of the x phase and stays at the tighter bound.
                Assert.Less(Math.Abs(com1.cx - com2.cx), 1.5f, "repeated 'i' x-position must agree up to one pixel of raster quantization");
                Assert.Less(Math.Abs(com1.cy - com2.cy), 0.5f, "repeated 'i' y-position must agree");
            }
        }

        // Outline: the rasterized line's ink must grow by the full stroke width (half per side)
        // when a stroke is added - which proves the bleed is drawn and the box is large enough to
        // hold it (a too-small box would clip it and hide the growth). We measure the same glyph
        // with and without stroke and compare ink extents (bbox size, not position, so the per-line
        // origin shift is irrelevant). 'L' is used because it is flat on all four extremes, so the
        // stroke extends exactly half-width beyond the ink on each side.
        [Test]
        public void CFontLineRaster_Outline_BleedsByTheStrokeWidth()
        {
            const float outline = 0.10f; // stroke = outline * size = 6px at size 60
            const float size = 60f;
            const string glyph = "L";
            SKTypeface typeface = SKTypeface.Default; // shared instance: no dispose

            CFontLine.SLineRasterData fill = CFontLine._Rasterize(typeface, size, glyph, 0f, new SColorF(1f, 1f, 1f, 1f));
            CFontLine.SLineRasterData outlined = CFontLine._Rasterize(typeface, size, glyph, outline, new SColorF(0f, 0f, 0f, 1f));

            (int fx0, int fx1, int fy0, int fy1) = _InkBBox(fill.Bgra, fill.W, fill.H, 0, fill.W);
            (int ox0, int ox1, int oy0, int oy1) = _InkBBox(outlined.Bgra, outlined.W, outlined.H, 0, outlined.W);

            float strokeWidth = outline * size; // 6px: half per side
            float fillW = fx1 - fx0, fillH = fy1 - fy0;
            float outW = ox1 - ox0, outH = oy1 - oy0;

            // The outlined ink is strictly larger than the fill ink (the bleed exists), and grows by
            // roughly the full stroke width (an antialiased edge may fall short by a pixel or two).
            Assert.Greater(outW, fillW, "a stroke must add visible ink to the glyph width");
            Assert.Greater(outH, fillH, "a stroke must add visible ink to the glyph height");
            Assert.Less(Math.Abs(outW - fillW - strokeWidth), 2.0f, "outline grows the ink width by the stroke width");
            Assert.Less(Math.Abs(outH - fillH - strokeWidth), 2.0f, "outline grows the ink height by the stroke width");
        }

        private static int _CountInkedPixels(byte[] bgra, int w, int h)
        {
            int count = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    if (bgra[(y * w + x) * 4 + 3] > 8)
                        count++;
            }
            return count;
        }

        private static (int minX, int maxX, int minY, int maxY) _InkBBox(byte[] bgra, int w, int h, int x0, int x1)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    if (bgra[(y * w + x) * 4 + 3] > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < minX)
                return (0, -1, 0, -1);
            return (minX, maxX, minY, maxY);
        }

        private static (float cx, float cy) _CenterOfMass(CFontLine.SLineRasterData raster, float x0, float x1)
        {
            float sumX = 0f, sumY = 0f, sumA = 0f;
            for (int y = 0; y < raster.H; y++)
            {
                for (int x = (int)x0; x < (int)x1 && x < raster.W; x++)
                {
                    float a = raster.Bgra[(y * raster.W + x) * 4 + 3] / 255f;
                    if (a > 0.03f)
                    {
                        sumX += (x - x0) * a;
                        sumY += y * a;
                        sumA += a;
                    }
                }
            }
            return (sumX / sumA, sumY / sumA);
        }

        private static byte[] _EncodePng(int width, int height, SKColor fill)
        {
            using (var bmp = new SKBitmap(width, height))
            {
                bmp.Erase(fill);
                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                    return data.ToArray();
            }
        }

        [Test]
        public void SkiaSharp_DecodeAndResize_MatchesRequestedSize()
        {
            // Mirrors CTextureProvider._LoadImageBgra/_ResampleBgra: decode a PNG to BGRA bytes
            // and downscale it the way the texture-quality path does.
            byte[] png = _EncodePng(64, 48, SKColors.Red);
            using (var bmp = SKBitmap.Decode(png))
            {
                Assert.IsNotNull(bmp, "decoded PNG should not be null");
                int w = bmp.Width;
                int h = bmp.Height;
                Assert.AreEqual(64, w);
                Assert.AreEqual(48, h);

                // Downscale (like CTextureProvider._GetNewTextureSize for large textures).
                using (var scaled = bmp.Resize(new SKImageInfo(32, 24, SKColorType.Bgra8888, SKAlphaType.Unpremul), SKSamplingOptions.Default))
                {
                    Assert.IsNotNull(scaled, "resize should produce a bitmap");
                    Assert.AreEqual(32, scaled.Width);
                    Assert.AreEqual(24, scaled.Height);
                    // PNG is lossless and the fill covers the whole bitmap, so the colour
                    // must survive decode + downscale.
                    Assert.AreEqual(SKColors.Red, scaled.GetPixel(16, 12));
                }
            }
        }

        [Test]
        public void SkiaSharp_PngEncodeRoundTrip_PreservesPixels()
        {
            // Mirrors CScreenProfiles._OnSaveSnapshot / COpenGL.MakeScreenShot: bitmap -> PNG bytes.
            string file = Path.Combine(Path.GetTempPath(), "voc_s4_roundtrip_" + TestContext.CurrentContext.Test.ID + ".png");
            try
            {
                byte[] png = _EncodePng(8, 6, SKColors.Green);
                File.WriteAllBytes(file, png);

                using (var reloaded = SKBitmap.Decode(file))
                {
                    Assert.IsNotNull(reloaded, "decoded image should not be null");
                    Assert.AreEqual(8, reloaded.Width);
                    Assert.AreEqual(6, reloaded.Height);
                    Assert.AreEqual(SKColors.Green, reloaded.GetPixel(0, 0));
                }
            }
            finally
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        [Test]
        public void SkiaSharp_RasterizesText_ProducesVisiblePixels()
        {
            // Mirrors what the font renderer (CFontLine/CCoverGenerator) relies on:
            // rasterizing text via SkiaSharp. A successful render proves the SkiaSharp font
            // stack works on this OS (freetype/fontconfig/CoreText).
            using (var bmp = new SKBitmap(160, 48))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
                using (var font = new SKFont(SKTypeface.Default, 28))
                    canvas.DrawText("Vocaluxe", 4, 34, SKTextAlign.Left, font, paint);

                bool drewSomething = false;
                for (int y = 0; y < bmp.Height && !drewSomething; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y) != SKColors.White)
                        {
                            drewSomething = true;
                            break;
                        }

                Assert.IsTrue(drewSomething, "drawing text should produce non-background pixels");
            }
        }
    }
}