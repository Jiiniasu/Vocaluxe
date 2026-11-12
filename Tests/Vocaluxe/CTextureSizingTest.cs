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
using Vocaluxe.Base;
using Vocaluxe.Lib.Draw;
using VocaluxeLib;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Tests the pure automatic texture-sizing functions in CTextureProvider (TR_CONFIG_TEXTURE_AUTO): the
    ///     fixed per-level areas, the VRAM safety-valve ladder, and the display-density scaling for fullscreen
    ///     textures and covers. These are pure functions so they run headless (no GL context).
    /// </summary>
    [TestFixture]
    public class CTextureSizingTest
    {
        private const long MiB = 1024L * 1024L;
        private const long GiB = 1024L * MiB;

        [Test]
        public void TestFixedMaxTextureArea()
        {
            Assert.AreEqual(128 * 128, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_LOWEST));
            Assert.AreEqual(256 * 128, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_LOW));
            Assert.AreEqual(512 * 256, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_MEDIUM));
            Assert.AreEqual(1024 * 512, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_HIGH));
            Assert.AreEqual(CSettings.RenderW * CSettings.RenderH, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_HIGHEST));
            //AUTO falls back to the render canvas when there is no display information.
            Assert.AreEqual(CSettings.RenderW * CSettings.RenderH, CTextureProvider<COGLTexture>.FixedMaxTextureArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO));
        }

        [Test]
        public void TestVramLadderCap()
        {
            //Unknown (0) means no backoff.
            Assert.AreEqual(long.MaxValue, CTextureProvider<COGLTexture>.VramLadderCap(0));
            //Below the ladder thresholds the cap steps down to the MEDIUM / HIGH tier area.
            Assert.AreEqual(512L * 256L, CTextureProvider<COGLTexture>.VramLadderCap(512 * MiB));
            Assert.AreEqual(1024L * 512L, CTextureProvider<COGLTexture>.VramLadderCap(1536 * MiB));
            //Comfortable VRAM -> no backoff.
            Assert.AreEqual(long.MaxValue, CTextureProvider<COGLTexture>.VramLadderCap(4 * GiB));
        }

        [Test]
        public void TestResolveGeneralMaxArea_PinnedIgnoresDisplay()
        {
            //A pinned level always returns its fixed area, regardless of display/VRAM.
            Assert.AreEqual(512 * 256, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_MEDIUM, 1920, 1080, 0));
            Assert.AreEqual(512 * 256, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_MEDIUM, 1920, 1080, 512 * MiB));
        }

        [Test]
        public void TestResolveGeneralMaxArea_AutoScalesWithDisplay()
        {
            //At density 1.0 (a 1280x720 display) AUTO equals the render canvas.
            Assert.AreEqual(1280 * 720, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 1280, 720, 0));
            //1080p -> 1.5x density -> 1920x1080.
            Assert.AreEqual(1920 * 1080, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 1920, 1080, 0));
            //A small display (960x540) -> 0.75x density -> 960x540.
            Assert.AreEqual(960 * 540, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 960, 540, 0));
            //No display information -> density falls back to 1.0 (the canvas).
            Assert.AreEqual(1280 * 720, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 0, 0, 0));
        }

        [Test]
        public void TestResolveGeneralMaxArea_AutoRespectsVramValve()
        {
            //A 1080p display would want 1920x1080, but 512 MiB of VRAM caps it to the MEDIUM tier area.
            Assert.AreEqual(512 * 256, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 1920, 1080, 512 * MiB));
            //Ample VRAM -> the full display size.
            Assert.AreEqual(1920 * 1080, CTextureProvider<COGLTexture>.ResolveGeneralMaxArea(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 1920, 1080, 4 * GiB));
        }

        [Test]
        public void TestResolveCoverMaxSide_PinnedReturnsKnob()
        {
            //A pinned level returns the CoverSize knob unchanged (display/VRAM ignored).
            Assert.AreEqual(512, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_MEDIUM, 512, 1920, 1080, 0));
            Assert.AreEqual(256, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_HIGHEST, 256, 1920, 1080, 0));
        }

        [Test]
        public void TestResolveCoverMaxSide_AutoScalesWithDisplay()
        {
            //Density 1.0 -> the knob as-is; 1.5x (1080p) -> 512*1.5; 3x (4K) -> 512*3.
            Assert.AreEqual(512, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 1280, 720, 0));
            Assert.AreEqual(768, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 1920, 1080, 0));
            Assert.AreEqual(1536, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 3840, 2160, 0));
            //Small display -> shrinks proportionally.
            Assert.AreEqual(384, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 960, 540, 0));
        }

        [Test]
        public void TestResolveCoverMaxSide_AutoRespectsVramValve()
        {
            //512 MiB of VRAM -> tier area 512*256 -> sqrt ~= 362, which caps the 768 the 1080p display would give.
            Assert.AreEqual(362, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 1920, 1080, 512 * MiB));
            //Ample VRAM -> the full display-scaled side.
            Assert.AreEqual(768, CTextureProvider<COGLTexture>.ResolveCoverMaxSide(ETextureQuality.TR_CONFIG_TEXTURE_AUTO, 512, 1920, 1080, 4 * GiB));
        }
    }
}