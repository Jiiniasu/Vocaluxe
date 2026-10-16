#region license
// This file is part of Vocaluxe.
//
// Vocaluxe is free software: you can redistribute it or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See
// the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using NUnit.Framework;
using OpenTK.Mathematics;
using Vocaluxe.Base;
using Vocaluxe.Lib.Draw;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Multi-window pure-logic coverage (plan v3 §8): panel/letterbox rect math, mouse panel-local
    ///     mapping with the GetScreenWidth/Height contract, monitor→slot assignment incl. stacking, the
    ///     _PanelIndex guard truth table, and the F1 (mixed-GPU) decision table. All of these were
    ///     extracted into pure statics so they run headless on CI.
    /// </summary>
    [TestFixture]
    public class CMultiWindowTest
    {
        private const int RenderW = 1280;
        private const int RenderH = 720;

        #region PanelVisible truth table (CDrawBase._PanelIndex guard)

        [Test]
        [TestCase(true, -1, 0, 640, 1280)]            // single mode (_PanelIndex < 0): always visible
        [TestCase(true, -1, 9000, 9500, 1280)]
        [TestCase(true, -1, -100, 50, 1280)]
        [TestCase(true, 0, 0, 640, 1280)]             // rect fully in panel 0 -> visible on panel 0
        [TestCase(false, 1, 0, 640, 1280)]            // ... not on panel 1
        [TestCase(true, 1, 1280, 1920, 1280)]         // rect fully in panel 1 -> visible on panel 1
        [TestCase(false, 0, 1280, 1920, 1280)]        // ... not on panel 0
        [TestCase(true, 0, 1000, 1400, 1280)]         // straddling the panel boundary -> visible on BOTH
        [TestCase(true, 1, 1000, 1400, 1280)]
        [TestCase(true, 0, 0, 1280, 1280)]            // rect exactly [0, 1280) belongs to panel 0...
        [TestCase(false, 1, 0, 1280, 1280)]           // ...not panel 1 (Right == x0: no overlap)
        [TestCase(false, 0, 1280, 1280, 1280)]        // zero-width rect at the boundary -> invisible everywhere
        [TestCase(false, 1, 1280, 1280, 1280)]
        [TestCase(true, 0, -100, 50, 1280)]           // border overflow (x < 0) still touches panel 0
        [TestCase(false, 1, -100, 50, 1280)]
        public void PanelVisible_Guard_TruthTable(bool expected, int panelIndex, float rectX, float rectRight, int renderW)
        {
            Assert.AreEqual(
                expected,
                CDrawBase<COGLTexture>.PanelVisible(panelIndex, rectX, rectRight, renderW));
        }

        #endregion

        #region Panel ortho / letterbox projection math (COpenGL._AdjustNewBorders)

        [Test]
        // single-window mode: today's giant ortho, whatever the screen count
        [TestCase(false, 1, 0, -8, 1286, 722, -4)]
        [TestCase(false, 2, 0, -8, 2566, 722, -4)]
        // multi-window, one panel: same giant ortho as single mode
        [TestCase(true, 1, 0, -8, 1286, 722, -4)]
        // multi-window strips: borders only on the canvas' outer edges, strips are contiguous
        [TestCase(true, 2, 0, -8, 1280, 722, -4)]
        [TestCase(true, 2, 1, 1280, 2566, 722, -4)]
        [TestCase(true, 3, 0, -8, 1280, 722, -4)]
        [TestCase(true, 3, 1, 1280, 2560, 722, -4)]
        [TestCase(true, 3, 2, 2560, 3846, 722, -4)]
        public void PanelOrtho_PanelStrips(bool multi, int screens, int panel, float left, float right, float bottom, float top)
        {
            COpenGL.PanelOrtho(multi, panel, screens, RenderW, RenderH,
                               8, 6, 4, 2, // BL BR BT BB
                               out float l, out float r, out float b, out float t);
            Assert.AreEqual(left, l);
            Assert.AreEqual(right, r);
            Assert.AreEqual(bottom, b);
            Assert.AreEqual(top, t);
        }

        [Test]
        public void PanelOrtho_MultiStrips_ReconstructGiantOrtho()
        {
            // The union of the N panel strips must cover exactly the giant-window projection
            // (same outer bounds, contiguous inner edges) - so per-panel rendering tiles the canvas.
            int screens = 4;
            float left0, right0;
            COpenGL.PanelOrtho(true, 0, screens, RenderW, RenderH, 8, 6, 4, 2,
                               out left0, out right0, out _, out _);
            float leftN, rightN;
            COpenGL.PanelOrtho(true, screens - 1, screens, RenderW, RenderH, 8, 6, 4, 2,
                               out leftN, out rightN, out _, out _);

            float gl, gr;
            COpenGL.PanelOrtho(false, 0, screens, RenderW, RenderH, 8, 6, 4, 2,
                               out gl, out gr, out _, out _);

            Assert.AreEqual(gl, left0, "outer left == giant ortho left");
            Assert.AreEqual(gr, rightN, "outer right == giant ortho right");

            float end = right0;
            for (int i = 1; i < screens; i++)
            {
                COpenGL.PanelOrtho(true, i, screens, RenderW, RenderH, 8, 6, 4, 2,
                                   out float l, out float r, out _, out _);
                Assert.AreEqual(end, l, "strip " + i + " starts where strip " + (i - 1) + " ends");
                end = r;
            }
        }

        #endregion

        #region Monitor -> slot assignment incl. stacking (CWindowManager.ApplyLayout math)

        [Test]
        [TestCase(0, 1, 0)]
        [TestCase(0, 4, 0)]
        [TestCase(3, 4, 3)]
        [TestCase(4, 4, 0)]   // 5th panel wraps back to monitor 0 (stacked)
        [TestCase(5, 4, 1)]
        [TestCase(5, 1, 0)]   // one monitor: everything stacks on it
        [TestCase(0, 0, -1)]  // headless: no monitor
        [TestCase(3, 0, -1)]
        public void MonitorForSlot_RoundRobin(int slot, int monitors, int expected)
        {
            Assert.AreEqual(expected, CWindowManager.MonitorForSlot(slot, monitors));
        }

        [Test]
        [TestCase(0, 4, 0, 0)]
        [TestCase(4, 4, 64, 64)]
        [TestCase(8, 4, 128, 128)]
        [TestCase(12, 4, 192, 192)]
        [TestCase(28, 4, 448, 384)]  // clamped so the panel stays on the monitor
        [TestCase(32, 4, 448, 384)]
        [TestCase(5, 0, 0, 0)]       // headless: no offset
        public void StackOffset_CascadeClamped(int slot, int monitors, int x, int y)
        {
            Vector2i o = CWindowManager.StackOffset(slot, monitors);
            Assert.AreEqual(x, o.X);
            Assert.AreEqual(y, o.Y);
        }

        [Test]
        public void CenteredPanelLocation_CentersClampsOffsets()
        {
            // Monitor @(-2560, 0) with a 2560x1440 client area, 1280x720 panel
            Vector2i loc = CWindowManager.CenteredPanelLocation(
                new Vector2i(-2560, 0), new Vector2i(2560, 1440),
                new Vector2i(1280, 720), Vector2i.Zero);
            Assert.AreEqual(-1920, loc.X, "centered x");
            Assert.AreEqual(360, loc.Y, "centered y");

            // Cascade offset shifts the stacked panel
            loc = CWindowManager.CenteredPanelLocation(
                new Vector2i(-2560, 0), new Vector2i(2560, 1440),
                new Vector2i(1280, 720), new Vector2i(64, 64));
            Assert.AreEqual(-1856, loc.X);
            Assert.AreEqual(424, loc.Y);

            // Panel wider than the client area: x clamped to the client origin, y still centered
            loc = CWindowManager.CenteredPanelLocation(
                new Vector2i(0, 0), new Vector2i(1000, 800),
                new Vector2i(1280, 720), Vector2i.Zero);
            Assert.AreEqual(0, loc.X);
            Assert.AreEqual(40, loc.Y);

            // Negative client origin: clamped to the (negative) origin, not to 0
            loc = CWindowManager.CenteredPanelLocation(
                new Vector2i(-1000, -500), new Vector2i(800, 600),
                new Vector2i(1280, 720), Vector2i.Zero);
            Assert.AreEqual(-1000, loc.X);
            Assert.AreEqual(-500, loc.Y);
        }

        #endregion

        #region F1 mixed-GPU decision (CWindowManager.ProbeRenderers math)

        [Test]
        [TestCase(false, "")]                                            // a lone renderer is never mixed
        [TestCase(false, "AMD Radeon RX 6900 XT")]
        [TestCase(false, "AMD Radeon RX 6900 XT", "AMD Radeon RX 6900 XT")]
        [TestCase(false, "amd radeon rx 6900 xt", "AMD RADEON RX 6900 XT")]  // case-insensitive
        [TestCase(false, "  amd radeon  ", "AMD Radeon")]                    // trimmed
        [TestCase(false, "", "")]
        [TestCase(true, "AMD Radeon RX 6900 XT", "NVIDIA GeForce RTX 4090")]
        [TestCase(true, "AMD Radeon RX 6900 XT", "AMD Radeon RX 6900 XT", "Intel UHD")]
        public void RequiresSingleWindow_F1DecisionTable(bool expected, params string[] renderers)
        {
            Assert.AreEqual(expected, CWindowManager.RequiresSingleWindow(renderers));
        }

        [Test]
        public void RequiresSingleWindow_NullEntriesTreatedAsEmpty()
        {
            Assert.IsFalse(CWindowManager.RequiresSingleWindow(new string[] { null, null }));
            // GL.GetString can return null on a half-initialized context; it must compare as "",
            // which only ever downgrades when paired with a non-empty renderer.
            Assert.IsTrue(CWindowManager.RequiresSingleWindow(new string[] { null, "AMD" }));
        }

        #endregion

        #region Mouse panel-local mapping (CMouse remap + letterbox-undo)

        [Test]
        [TestCase(0, 0, 2560, 2)]        // panel-local 0 passes through the multi contract unchanged
        [TestCase(640, 640, 2560, 2)]
        [TestCase(1279, 1279, 2560, 2)]
        [TestCase(1280, 1280, 2560, 2)]  // boundary: not wrapped (remap keeps [0, RenderW])
        [TestCase(2000, 720, 2560, 2)]   // beyond one panel wraps into the first panel
        [TestCase(2559, 1279, 2560, 2)]
        [TestCase(310, 310, 1280, 1)]    // single panel: identity
        [TestCase(1920, 1280, 3840, 2)]  // legacy (scaled) window: scaled, then not wrapped
        [TestCase(2000, 53, 3840, 2)]    // legacy (scaled) window: scaled, then wrapped
        public void RemapMouseX_Contract(int xIn, int xOut, int screenWidth, int screens)
        {
            Assert.AreEqual(xOut, CMouse.RemapMouseX(xIn, screenWidth, RenderW, screens));
        }

        [Test]
        public void RemapMouseX_MultiWindowIdentity()
        {
            // The multi-window contract (plan v3 §6): GetScreenWidth == RenderW * NumScreens, so the
            // remap is the identity for every panel-local coordinate the per-window handlers emit.
            int screens = 3;
            int screenWidth = RenderW * screens;
            for (int x = 0; x < RenderW; x += 97)
                Assert.AreEqual(x, CMouse.RemapMouseX(x, screenWidth, RenderW, screens), "x=" + x);
        }

        [Test]
        [TestCase(0, 0, 720, 720)]     // multi-window contract: identity
        [TestCase(360, 360, 720, 720)]
        [TestCase(719, 719, 720, 720)]
        [TestCase(540, 360, 1080, 720)]  // legacy: scaled to the canvas height
        [TestCase(1080, 720, 1080, 720)]
        public void RemapMouseY_Contract(int yIn, int yOut, int screenHeight, int renderH)
        {
            Assert.AreEqual(yOut, CMouse.RemapMouseY(yIn, screenHeight, renderH));
        }

        [Test]
        [TestCase(192, 100, 1280, 1280, 92)]    // offset removed, scale applied
        [TestCase(0, 100, 1280, 1280, -100)]    // left of the letterbox maps into the border strip
        [TestCase(640, 0, 640, 1280, 1280)]     // right edge of a 640-wide viewport -> full panel width
        [TestCase(200, 160, 640, 720, 45)]
        [TestCase(33, 0, 0, 720, 33)]           // degenerate letterbox (single window): identity
        public void Unletterbox_PanelLocalMapping(int pixel, int letterX, int letterW, int renderW, int expected)
        {
            Assert.AreEqual(expected, CDrawBaseGlfw<COGLTexture>.Unletterbox(pixel, letterX, letterW, renderW));
        }

        #endregion
    }
}