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
using System.Diagnostics;
using System.IO;
using System.Threading;
using Vocaluxe.Base;
using Vocaluxe.Base.Fonts;
using Vocaluxe.Base.ThemeSystem;

using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Base class for graphic drivers
    ///     A few notes:
    ///     Writes to textures (TTextureType) should only be done in the main thread
    ///     Writes to the RefCount of existing textures should only be done in the main thread and guarded by _Textures lock
    /// </summary>
    /// <typeparam name="TTextureType"></typeparam>
    abstract class CDrawBase<TTextureType> : CTextureProvider<TTextureType> where TTextureType : CTextureBase, IDisposable
    {
        protected bool _Fullscreen;

        protected bool _Run;

        protected readonly CKeys _Keys = new CKeys();
        protected readonly CMouse _Mouse = new CMouse();

        protected int _H;
        protected int _W;
        protected int _Y;
        protected int _X;

        protected int _BorderLeft;
        protected int _BorderRight;
        protected int _BorderTop;
        protected int _BorderBottom;
        protected EGeneralAlignment _CurrentAlignment = EGeneralAlignment.Middle;

        /// <summary>
        ///     The panel currently being rendered, or -1 in single-window mode. In single-window mode (-1)
        ///     every draw is replicated/routed exactly as before; in multi-window mode (0..Count-1, plan v3 §4)
        ///     the draw loops only produce the copy/routing for this panel, whose own projection is set by
        ///     <c>_OnBeforeDraw(panel)</c>.
        /// </summary>
        protected int _PanelIndex = -1;

        protected abstract void _ClearScreen();
        protected abstract void _AdjustNewBorders(int panel);
        protected abstract void _LeaveFullScreen();

        protected abstract void _OnAfterDraw(int panel);

        protected abstract void _OnBeforeDraw(int panel);

        protected abstract void _DoResize();

        protected abstract void _EnterFullScreen();

        /// <summary>Flushes the per-window event queues and polls "should close" (called once per frame).</summary>
        protected virtual void _ProcessEvents() { }

        /// <summary>Number of render passes per frame: 1 in single-window mode, the window count in multi mode.</summary>
        protected virtual int _GetPanelCount() { return 1; }

        /// <summary>True in multi-window mode (one real window per panel); false means single giant window.</summary>
        protected virtual bool _MultiWindow() { return false; }

        /// <summary>The scene pass for the panel currently being rendered.</summary>
        protected virtual bool _DrawScene() { return CGraphics.Draw(); }

        /// <summary>True when a monitor hotplug asked for a layout rebuild (applied at the frame boundary).</summary>
        protected virtual bool _WantRelayout() { return false; }

        protected virtual void _ApplyRelayout() { }

        /// <summary>
        ///     The aspect ratio a window is letterboxed to: the whole canvas (RenderW*NumScreens) in
        ///     single-window mode, one panel in multi-window mode.
        /// </summary>
        protected virtual float _WindowAspect() { return CSettings.GetRenderAspect(); }

        protected void _AdjustAspect(bool reverse, int winW, int winH, out int x, out int y, out int vw, out int vh)
        {
            vw = winW;
            vh = winH;
            x = 0;
            y = 0;
            if (winW / (float)winH > _WindowAspect())
            {
                y = 0;
                //The windows width is too big
                vw = (int)Math.Round(winH * _WindowAspect());
                int diff = winW - vw;
                switch (_CurrentAlignment)
                {
                    case EGeneralAlignment.Start:
                        x = 0;
                        break;
                    case EGeneralAlignment.Middle:
                        x = diff / 2;
                        break;
                    case EGeneralAlignment.End:
                        x = diff;
                        break;
                }
            }
            else
            {
                x = 0;
                //The windows height is too big
                vh = (int)Math.Round(winW / _WindowAspect());
                int diff = winH - vh;
                switch (_CurrentAlignment)
                {
                    case EGeneralAlignment.Start:
                        y = reverse ? diff : 0;
                        break;
                    case EGeneralAlignment.Middle:
                        y = diff / 2;
                        break;
                    case EGeneralAlignment.End:
                        y = reverse ? 0 : diff;
                        break;
                }
            }
        }

        /// <summary>
        ///     Struct that contains texture and world coordinates for drawing
        /// </summary>
        protected struct SDrawCoords
        {
            /// <summary>
            ///     Texture coordinates
            /// </summary>
            public float Tx1, Tx2, Ty1, Ty2;
            /// <summary>
            ///     World coordinates
            /// </summary>
            public float Wx1, Wx2, Wy1, Wy2, Wz;
            public float Rotation;
        }

        /// <summary>
        ///     Adds a texture to the vertext buffer
        /// </summary>
        /// <param name="texture">Texture to draw</param>
        /// <param name="dc">Coordinates to draw on</param>
        /// <param name="color">Color to use</param>
        /// <param name="isReflection">If true, then color is faded out in y direction</param>
        /// <param name="fadeFrom">Relative alpha at the top of a reflection fade (default 1 = full alpha)</param>
        /// <param name="fadeTo">Relative alpha at the bottom of a reflection fade (default 0 = fully
        ///     transparent). Together fadeFrom/fadeTo let a reflection sample a shared gradient.
        ///     Ignored for non-reflections.</param>
        protected abstract void _DrawTexture(TTextureType texture, SDrawCoords dc, SColorF color, bool isReflection = false, float fadeFrom = 1f, float fadeTo = 0f);

        /// <summary>
        ///     Calculates the texture and world coordinates for drawing the texture in the rect cropping at the the bounds
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="rect">Rect to draw the texture (might stretch the texture)</param>
        /// <param name="bounds">Rect to stay in (might crop the texture)</param>
        /// <param name="drawCoords">Struct for the coordinates</param>
        /// <param name="mirrored">Mirror on y-axis</param>
        /// <returns>True if anything will be dawn</returns>
        protected bool _CalcDrawCoords(TTextureType texture, SRectF rect, SRectF bounds, out SDrawCoords drawCoords, bool mirrored = false)
        {
            drawCoords = new SDrawCoords();
            if (Math.Abs(rect.W) < 1 || Math.Abs(rect.H) < 1 || Math.Abs(bounds.H) < 1 || Math.Abs(bounds.W) < 1)
                return false;

            if (bounds.X >= rect.Right || bounds.Right <= rect.X)
                return false;

            if (bounds.Y >= rect.Bottom || bounds.Bottom <= rect.Y)
                return false;

            drawCoords.Tx1 = Math.Max(0, (bounds.X - rect.X) / rect.W * texture.WidthRatio);
            drawCoords.Wx1 = Math.Max(rect.X, bounds.X);
            drawCoords.Tx2 = Math.Min(1, (bounds.Right - rect.X) / rect.W) * texture.WidthRatio;
            drawCoords.Wx2 = Math.Min(rect.Right, bounds.Right);

            drawCoords.Ty1 = Math.Max(0, (bounds.Y - rect.Y) / rect.H * texture.HeightRatio);
            drawCoords.Wy1 = Math.Max(rect.Y, bounds.Y);
            drawCoords.Ty2 = Math.Min(1, (bounds.Bottom - rect.Y) / rect.H) * texture.HeightRatio;
            drawCoords.Wy2 = Math.Min(rect.Bottom, bounds.Bottom);

            if (mirrored)
            {
                float tmp = drawCoords.Ty1;
                drawCoords.Ty1 = drawCoords.Ty2;
                drawCoords.Ty2 = tmp;
            }

            drawCoords.Wz = rect.Z + CGraphics.ZOffset;
            drawCoords.Rotation = rect.Rotation;

            return true;
        }

        /// <summary>
        ///     Calculates the texture and world coordinates for drawing (a part of) the texture in the rect
        /// </summary>
        /// <param name="texture">Texture to draw</param>
        /// <param name="rect">Rect to draw in (might stretch the texture)</param>
        /// <param name="drawCoords">Struct for the coordinates</param>
        /// <param name="mirrored">Mirror on y axis</param>
        /// <param name="begin">Position of the texture to begin drawing (0 &lt;=x&lt;=1)</param>
        /// <param name="end">Position of the texture to end drawing (0 &lt;=x&lt;=1)</param>
        /// <returns>True if anything will be dawn</returns>
        protected bool _CalcDrawCoords(TTextureType texture, SRectF rect, out SDrawCoords drawCoords, bool mirrored = false, float begin = 0, float end = 1)
        {
            drawCoords = new SDrawCoords();
            if (Math.Abs(rect.W) < 1 || Math.Abs(rect.H) < 1)
                return false;
            if (begin >= 1 || begin >= end)
                return false;

            Debug.Assert(begin.IsInRange(0, 1) && end.IsInRange(0, 1));

            drawCoords.Tx1 = begin * texture.WidthRatio;
            drawCoords.Wx1 = rect.X + begin * rect.W;
            drawCoords.Tx2 = end * texture.WidthRatio;
            drawCoords.Wx2 = rect.X + end * rect.W;

            drawCoords.Ty1 = 0;
            drawCoords.Wy1 = rect.Y;
            drawCoords.Ty2 = texture.HeightRatio;
            drawCoords.Wy2 = rect.Bottom;

            if (mirrored)
            {
                float tmp = drawCoords.Ty1;
                drawCoords.Ty1 = drawCoords.Ty2;
                drawCoords.Ty2 = tmp;
            }

            drawCoords.Wz = rect.Z + CGraphics.ZOffset;
            drawCoords.Rotation = rect.Rotation;

            return true;
        }

        /// <summary>
        ///     Multi-window panel routing (plan v3 §4): true when a canvas-space draw belongs to the panel
        ///     currently being rendered. allMonitors draws arrive already shifted to their panel by the draw
        ///     loops; allMonitors=false draws arrive with absolute canvas coordinates (assigned once at
        ///     creation, e.g. X += screen * RenderW), and only their panel's window should draw them.
        ///     Single-window mode (_PanelIndex == -1): always visible, exactly as before.
        /// </summary>
        protected bool _PanelVisible(SRectF rect)
        {
            return PanelVisible(_PanelIndex, rect.X, rect.Right, CSettings.RenderW);
        }

        /// <summary>
        ///     Pure form of the panel guard: true when a canvas rect [rectX, rectRight) overlaps panel
        ///     <paramref name="panelIndex" /> (each panel spans renderW pixels). panelIndex &lt; 0 is
        ///     single-window mode: always visible. Extracted so the truth table is unit-testable without a
        ///     draw context.
        /// </summary>
        public static bool PanelVisible(int panelIndex, float rectX, float rectRight, int renderW)
        {
            if (panelIndex < 0)
                return true;
            float x0 = panelIndex * renderW;
            return rectRight > x0 && rectX < x0 + renderW;
        }

        public void DrawTexture(CTextureRef textureRef, SRectF rect, SColorF color, bool mirrored = false, bool allMonitors = true)
        {
            if (Math.Abs(color.A) < 0.01)
                return;
            SDrawCoords dc;
            TTextureType texture;
            if (!_GetTexture(textureRef, out texture))
                return;
            if (allMonitors)
            {
                for (int i = 0; i < CConfig.Config.Graphics.NumScreens; i++)
                {
                    SRectF newrect = rect;
                    newrect.X += CSettings.RenderW * i;
                    if (!_PanelVisible(newrect))
                        continue;
                    if (!_CalcDrawCoords(texture, newrect, out dc, mirrored))
                        return;
                    _DrawTexture(texture, dc, color);
                }
            }
            else
            {
                if (!_PanelVisible(rect))
                    return;
                if (!_CalcDrawCoords(texture, rect, out dc, mirrored))
                    return;
                _DrawTexture(texture, dc, color);
            }
        }

        /// <summary>
        ///     Draws a texture
        /// </summary>
        /// <param name="textureRef">The texture to be drawn</param>
        /// <param name="rect">A SRectF struct containing the destination coordinates</param>
        /// <param name="color">A SColorF struct containing a color which the texture will be colored in</param>
        /// <param name="bounds">A SRectF struct containing which part of the texture should be drawn</param>
        /// <param name="mirrored">True if the texture should be mirrored</param>
        public void DrawTexture(CTextureRef textureRef, SRectF rect, SColorF color, SRectF bounds, bool mirrored = false, bool allMonitors = true)
        {
            if (Math.Abs(color.A) < 0.01)
                return;
            SDrawCoords dc;
            TTextureType texture;
            if (!_GetTexture(textureRef, out texture))
                return;

            if (allMonitors)
            {
                for (int i = 0; i < CConfig.Config.Graphics.NumScreens; i++)
                {
                    SRectF newrect = rect;
                    SRectF newbounds = bounds;
                    newrect.X += CSettings.RenderW * i;
                    newbounds.X += CSettings.RenderW * i;
                    if (!_PanelVisible(newrect))
                        continue;
                    if (!_CalcDrawCoords(texture, newrect, newbounds, out dc, mirrored))
                        return;
                    _DrawTexture(texture, dc, color);
                }
            }
            else
            {
                if (!_PanelVisible(rect))
                    return;
                if (!_CalcDrawCoords(texture, rect, bounds, out dc, mirrored))
                    return;
                _DrawTexture(texture, dc, color);
            }
        }

        /// <summary>
        ///     Draws a texture
        /// </summary>
        /// <param name="textureRef">The texture to be drawn</param>
        /// <param name="rect">A SRectF struct containing the destination coordinates</param>
        /// <param name="color">A SColorF struct containing a color which the texture will be colored in</param>
        /// <param name="begin">A Value ranging from 0 to 1 containing the beginning of the texture</param>
        /// <param name="end">A Value ranging from 0 to 1 containing the ending of the texture</param>
        public void DrawTexture(CTextureRef textureRef, SRectF rect, SColorF color, float begin, float end, bool allMonitors = true)
        {
            if (Math.Abs(color.A) < 0.01)
                return;
            SDrawCoords dc;
            TTextureType texture;
            if (!_GetTexture(textureRef, out texture))
                return;

            if (allMonitors)
            {
                for (int i = 0; i < CConfig.Config.Graphics.NumScreens; i++)
                {
                    SRectF newrect = rect;
                    newrect.X += CSettings.RenderW * i;
                    if (!_PanelVisible(newrect))
                        continue;
                    if (!_CalcDrawCoords(texture, newrect, out dc, false, begin, end))
                        return;
                    _DrawTexture(texture, dc, color);
                }
            }
            else
            {
                if (!_PanelVisible(rect))
                    return;
                if (!_CalcDrawCoords(texture, rect, out dc, false, begin, end))
                    return;
                _DrawTexture(texture, dc, color);
            }
        }

        /// <summary>
        ///     Draws a reflection of a texture
        /// </summary>
        /// <param name="textureRef">The texture of which a reflection should be drawn</param>
        /// <param name="rect">A SRectF struct containing the destination coordinates</param>
        /// <param name="color">A SColorF struct containing a color which the texture will be colored in</param>
        /// <param name="bounds">A SRectF struct containing which part of the texture should be drawn</param>
        /// <param name="space">The space between the texture and the reflection</param>
        /// <param name="height">The height of the reflection</param>
        /// <param name="allMonitors">Render on all monitors</param>
        /// <param name="mirrorLine">World Y of a shared mirror line to mirror about (e.g. the
        ///     line the containing object's reflection mirrors about): every point at distance d
        ///     above it is drawn at distance d below it.
        ///     0 (default) keeps the classic behaviour: a strip of <paramref name="height"/> at
        ///     the rect bottom, fading to transparent.</param>
        /// <param name="fadeOrigin">World Y where the object's shared fade ramp starts (opacity
        ///     1), used with <paramref name="mirrorLine"/> &gt; 0. The whole reflection fades
        ///     1 -> 0 over the span <paramref name="fadeSpan"/> below it, so every element of
        ///     the object fades uniformly at the same depth.</param>
        /// <param name="fadeSpan">Length (pixels) of the object's shared fade ramp, used with
        ///     <paramref name="mirrorLine"/> &gt; 0.</param>
        public void DrawTextureReflection(CTextureRef textureRef, SRectF rect, SColorF color, SRectF bounds, float space, float height, bool allMonitors = true, float mirrorLine = 0f, float fadeOrigin = 0f, float fadeSpan = 0f)
        {
            Debug.Assert(height >= 0);

            if (Math.Abs(color.A) < 0.01 || height < 1)
                return;
            SDrawCoords dc;
            TTextureType texture;
            if (!_GetTexture(textureRef, out texture))
                return;

            for (int i = 0; i < (allMonitors ? CConfig.Config.Graphics.NumScreens : 1); i++)
            {
                SRectF newrect = rect;
                SRectF newbounds = bounds;
                newrect.X += CSettings.RenderW * i;
                newbounds.X += CSettings.RenderW * i;
                if (!_PanelVisible(newrect))
                    continue;
                if (!_CalcDrawCoords(texture, newrect, newbounds, out dc, true))
                    return;

                if (mirrorLine > 0f)
                {
                    // Mirror about the shared mirror line (world Y) - the same line the containing
                    // object's reflection mirrors about, so text and object share one mirror
                    // geometry: every point at distance d above the line is drawn at distance d
                    // below it. If the object defines a fade ramp ([fadeOrigin,
                    // fadeOrigin + fadeSpan], opacity 1 -> 0), this element is clipped at the
                    // ramp end and both its edges pick up the ramp value at their final
                    // position, so it fades exactly like the rest of the object at the same
                    // depth.
                    float top = 2f * mirrorLine - newrect.Bottom; // World Y of the mirrored content's top edge
                    float shown = newrect.H;
                    float a0 = 1f, a1 = 0f;
                    if (fadeSpan > 0f)
                    {
                        float rampEnd = fadeOrigin + fadeSpan;
                        if (top >= rampEnd)
                            continue; // Entirely past the object's fade: nothing visible
                        if (shown > rampEnd - top)
                            shown = rampEnd - top; // Clip the visible part at the object's fade end
                        a0 = Math.Min(1f, 1f - (top - fadeOrigin) / fadeSpan);
                        a1 = Math.Max(0f, 1f - (top + shown - fadeOrigin) / fadeSpan);
                    }
                    dc.Wy1 = top;
                    dc.Wy2 = top + shown;
                    dc.Ty2 = (1f - shown / newrect.H) * dc.Ty1; // Mirror coords: Ty1 is the content's bottom row
                    if (a0 <= 0f)
                        continue;
                    _DrawTexture(texture, dc, color, true, a0, a1);
                }
                else
                {
                    if (height > newrect.H)
                        height = newrect.H;
                    dc.Wy1 += newrect.H + space; // Move from start of rect to end of rect with spacing
                    dc.Wy2 += space + height; // Move from start of rect
                    dc.Ty2 += (newrect.H - height) / newrect.H; // Adjust so not all of the texture is drawn (mirrored--> Ty1>Ty2)
                    if (dc.Ty2 < dc.Ty1) // Make sure we actually draw something
                        _DrawTexture(texture, dc, color, true);
                }
            }
        }

        /// <summary>
        ///     Makes the render window visible. No-op for backends without a separate window object.
        /// </summary>
        protected virtual void _ShowWindow() { }

        private CTextureRef _SplashLogo;
        private CTextureRef _SplashWhite;
        private CTextureRef _SplashBg;
        private string _SplashStatus = "Loading…";

        /// <summary>
        ///     Shows the startup splash (program logo on a black background, with an empty progress bar)
        ///     in the render window, so the user sees branding immediately instead of nothing while the
        ///     rest of the (multi-second) initialization runs. Call once right after the draw backend is
        ///     initialized; drive the bar afterwards with <see cref="UpdateSplash" />.
        /// </summary>
        public void ShowSplash()
        {
            string logoPath = Path.Combine(CSettings.ProgramFolder, CSettings.FolderNameGraphics, CSettings.FileNameLogo);
            if (File.Exists(logoPath))
                _SplashLogo = AddTexture(logoPath);
            else
                CLog.Error("Splash logo not found: " + logoPath);

            // 1x1 white texture, tinted per-draw, used to paint the progress bar (DrawRect lives only in
            // the concrete backend, but DrawTexture is available here).
            _SplashWhite = AddTexture(1, 1, new byte[] { 255, 255, 255, 255 });

            _ShowWindow();
            _DoResize(); // set up viewport + projection so render-space coordinates map correctly
            _DrawSplashFrame(0f);
        }

        /// <summary>
        ///     Redraws the splash with the progress bar filled to <paramref name="progress" /> (0..1) and
        ///     an optional status caption (only rendered once the font system is ready - drawing text
        ///     before that is a no-op). Also pumps window events so the splash stays responsive.
        /// </summary>
        public void UpdateSplash(float progress, string status = null)
        {
            if (status != null)
                _SplashStatus = status;
            _DrawSplashFrame(progress < 0f ? 0f : (progress > 1f ? 1f : progress));
        }

        private void _DrawSplashFrame(float progress)
        {
            // Multi-window (plan v3 §11.4): every window shows the splash, matching today's behavior
            // where every panel of the giant window shows it.
            for (int panel = 0; panel < _GetPanelCount(); panel++)
            {
                _PanelIndex = _MultiWindow() ? panel : -1;
                _OnBeforeDraw(panel);
                _ClearScreen();
                _DrawSplashContent(progress);
                _OnAfterDraw(panel);
            }
            _PanelIndex = -1;
            _ProcessEvents();
        }

        private void _DrawSplashContent(float progress)
        {
            float renderW = CSettings.RenderW;
            float renderH = CSettings.RenderH;

            // Once the theme is loaded (not during the first few splash frames) use the loading screen's
            // background, which already includes the program logo, so the splash blends seamlessly into
            // the loading screen that follows. Before that, fall back to the logo centered on black.
            if (_SplashBg == null)
            {
                // Throws (KeyNotFoundException) until the theme/skin is loaded - that's expected for the
                // first few splash frames; swallow it and keep the logo-on-black fallback until then.
                try { _SplashBg = CThemes.GetSkinTexture("BG_Loading", -1); }
                catch { /* theme not loaded yet */ }
            }

            if (_SplashBg != null)
            {
                DrawTexture(_SplashBg, new SRectF(0f, 0f, renderW, renderH, 0f), new SColorF(1f, 1f, 1f, 1f));
            }
            else if (_SplashLogo != null)
            {
                // Center the logo, preserving aspect ratio and fitting it within 80% width / 50% height.
                float scale = Math.Min(renderW * 0.8f / _SplashLogo.OrigSize.Width, renderH * 0.5f / _SplashLogo.OrigSize.Height);
                float w = _SplashLogo.OrigSize.Width * scale;
                float h = _SplashLogo.OrigSize.Height * scale;
                DrawTexture(_SplashLogo, new SRectF((renderW - w) / 2f, (renderH - h) / 2f, w, h, 0f), new SColorF(1f, 1f, 1f, 1f));
            }

            // Progress bar near the bottom: faint track + brighter fill.
            if (_SplashWhite != null)
            {
                float barW = renderW * 0.5f;
                float barH = renderH * 0.012f;
                float barX = (renderW - barW) / 2f;
                float barY = renderH * 0.82f;
                DrawTexture(_SplashWhite, new SRectF(barX, barY, barW, barH, 0f), new SColorF(1f, 1f, 1f, 0.2f));
                if (progress > 0f)
                    DrawTexture(_SplashWhite, new SRectF(barX, barY, barW * progress, barH, 0f), new SColorF(1f, 1f, 1f, 0.9f));

                // Status caption below the bar - only once fonts are loaded (early in init they are not,
                // and CFonts.DrawText would be a no-op then).
                if (!string.IsNullOrEmpty(_SplashStatus) && CFonts.IsInitialized)
                {
                    var font = new CFont("Normal", EStyle.Normal, renderH * 0.028f);
                    float textW = CFonts.GetTextWidth(_SplashStatus, font);
                    CFonts.DrawText(_SplashStatus, font, (renderW - textW) / 2f, barY + barH + renderH * 0.02f, CSettings.ZNear,
                                    new SColorF(1f, 1f, 1f, 0.9f));
                }
            }

        }

        // Resharper doesn't get that this is used -.-
        // ReSharper disable UnusedMemberHiearchy.Global
        /// <summary>
        ///     Starts the rendering
        /// </summary>
        public virtual void MainLoop()
        {
            // ReSharper restore UnusedMemberHiearchy.Global
            _EnsureMainThread();
            _Run = true;

            _Fullscreen = false;
            if (CConfig.Config.Graphics.FullScreen == EOffOn.TR_CONFIG_ON)
                _EnterFullScreen();
            else
                _DoResize(); //Resize window if aspect ratio is incorrect

            while (_Run)
            {
                _CheckQueue();
                // The in-game web server (CVocaluxeServer) was removed in the .NET 10 port; nothing
                // else to process per frame here.

                //We want to begin drawing
                // One render pass per window (multi-window primary, plan v3 §4.4): each slot renders and
                // presents only its own panel; single-window mode is one pass with _PanelIndex == -1 (the
                // panel guards are inert, exactly as before).
                for (int panel = 0; panel < _GetPanelCount() && _Run; panel++)
                {
                    _PanelIndex = _MultiWindow() ? panel : -1;
                    _OnBeforeDraw(panel);

                    //Clear the previous frame of this slot's backbuffer
                    _ClearScreen();
                    if (!_DrawScene())
                        _Run = false;
                    _OnAfterDraw(panel);
                }
                _PanelIndex = -1;

                //Flush the window event queues once per frame (after all backbuffer swaps) and poll the
                //"should close" flag, so closing any window ends the loop.
                _ProcessEvents();

                if (!CGraphics.UpdateGameLogic(_Keys, _Mouse))
                    _Run = false;

                //Apply fullscreen mode
                if ((CConfig.Config.Graphics.FullScreen == EOffOn.TR_CONFIG_ON) != _Fullscreen)
                    _ToggleFullScreen();

                //Apply border changes
                if (_BorderLeft != CConfig.Config.Graphics.BorderLeft || _BorderRight != CConfig.Config.Graphics.BorderRight || _BorderTop != CConfig.Config.Graphics.BorderTop ||
                    _BorderBottom != CConfig.Config.Graphics.BorderBottom)
                {
                    _BorderLeft = CConfig.Config.Graphics.BorderLeft;
                    _BorderRight = CConfig.Config.Graphics.BorderRight;
                    _BorderTop = CConfig.Config.Graphics.BorderTop;
                    _BorderBottom = CConfig.Config.Graphics.BorderBottom;

                    for (int panel = 0; panel < _GetPanelCount(); panel++)
                        _AdjustNewBorders(panel);
                }

                if (_CurrentAlignment != CConfig.Config.Graphics.ScreenAlignment)
                    _DoResize();

                //Monitor hotplug (plan v3 §7): the window manager flagged a layout rebuild, applied at the
                //frame boundary (never mid-frame).
                if (_WantRelayout())
                    _ApplyRelayout();

                if (CConfig.Config.Graphics.VSync == EOffOn.TR_CONFIG_OFF)
                {
                    if (CTime.IsRunning())
                    {
                        int delay = (int)Math.Floor(CConfig.CalcCycleTime() - CTime.GetMilliseconds());

                        if (delay >= 1 && delay < 500)
                            Thread.Sleep(delay);
                    }
                }
                //Calculate the FPS Rate and restart the timer after a frame
                CTime.CalculateFPS();
                CTime.Restart();
            }
        }

        private void _ToggleFullScreen()
        {
            if (!_Fullscreen)
                _EnterFullScreen();
            else
                _LeaveFullScreen();
        }
    }
}