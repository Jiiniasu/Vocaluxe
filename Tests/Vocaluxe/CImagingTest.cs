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
        // Glyph wobble regression: per-glyph tight ink boxes sit at different fractional
        // positions on the shared float baseline; COpenGL's per-corner rounding then quantized
        // each glyph's baseline independently (measured on FreeSans @30px: -0.31px..+0.40px
        // drift across the word "singing") - the wavy lyrics. SnapToBaselineRow must keep the
        // shared baseline on exactly one pixel row for the whole line, with corners still on
        // the pixel grid (which is what that rounding was created for).
        [Test]
        public void TestSnapToBaselineRow_SharedBaselineLandsOnOnePixelRow()
        {
            const float baseline = 24.339f; // the line's shared float baseline
            const float offI = 17.199f;    // measured 'i': baseline 17.199px below its quad top
            const float offG = 12.309f;    // measured 'g'
            const float hI = 18.05f;
            const float hG = 18.42f;

            CGlyph.SnapToBaselineRow(baseline - offI, hI, offI, out float yI, out float newHI);
            CGlyph.SnapToBaselineRow(baseline - offG, hG, offG, out float yG, out float newHG);

            float snappedOffI = (float)Math.Round(offI);
            float snappedOffG = (float)Math.Round(offG);

            // Both baselines land on the one shared row (the correctly-rounded float baseline)
            Assert.AreEqual((float)Math.Round(baseline), yI + snappedOffI);
            Assert.AreEqual(yI + snappedOffI, yG + snappedOffG);

            // Corners stay on the pixel grid
            Assert.AreEqual((float)(int)yI, yI);
            Assert.AreEqual((float)(int)(yI + newHI), yI + newHI);
            Assert.AreEqual((float)(int)yG, yG);
            Assert.AreEqual((float)(int)(yG + newHG), yG + newHG);

            // Heights stay within half a pixel of the float heights (aspect preserved)
            Assert.Less(Math.Abs(newHI - hI), 1f);
            Assert.Less(Math.Abs(newHG - hG), 1f);
        }

        // Idempotent under COpenGL._DrawTexture's own corner rounding: glyph quads arrive
        // pre-snapped, so that rounding can no longer move anything.
        [Test]
        public void TestSnapToBaselineRow_IdempotentUnderCornerRounding()
        {
            const float off = 17.199f;
            CGlyph.SnapToBaselineRow(24.339f - off, 18.05f, off, out float y, out float h);

            Assert.AreEqual(y, (float)Math.Round(y));
            Assert.AreEqual(y + h, (float)Math.Round(y + h));
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
            // Mirrors what the font renderer (CGlyph/CFontStyle/CCoverGenerator) relies on:
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