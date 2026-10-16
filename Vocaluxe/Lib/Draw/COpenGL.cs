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

using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using ClearBufferMask = OpenTK.Graphics.OpenGL.ClearBufferMask;
using DepthFunction = OpenTK.Graphics.OpenGL.DepthFunction;
using EnableCap = OpenTK.Graphics.OpenGL.EnableCap;
using GenerateMipmapTarget = OpenTK.Graphics.OpenGL.GenerateMipmapTarget;
using GL = OpenTK.Graphics.OpenGL.GL;
using MatrixMode = OpenTK.Graphics.OpenGL.MatrixMode;
using PixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;
using PixelInternalFormat = OpenTK.Graphics.OpenGL.PixelInternalFormat;
using PixelStoreParameter = OpenTK.Graphics.OpenGL.PixelStoreParameter;
using PixelType = OpenTK.Graphics.OpenGL.PixelType;
using TextureMagFilter = OpenTK.Graphics.OpenGL.TextureMagFilter;
using TextureMinFilter = OpenTK.Graphics.OpenGL.TextureMinFilter;
using TextureParameterName = OpenTK.Graphics.OpenGL.TextureParameterName;
using TextureTarget = OpenTK.Graphics.OpenGL.TextureTarget;

namespace Vocaluxe.Lib.Draw
{
    class COGLTexture : CTextureBase
    {
        //The texture "name" according to the specs
        public readonly int Name;

        public COGLTexture(int name, Size dataSize, int texWidth = 0, int texHeight = 0) : base(dataSize, new Size(texWidth, texHeight))
        {
            Name = name;
            if (name == 0)
                return;
            GL.BindTexture(TextureTarget.Texture2D, Name);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, texWidth, texHeight, 0, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public override bool IsLoaded
        {
            get { return Name != 0; }
        }

        public override void Dispose()
        {
            base.Dispose();
            if (Name != 0)
                GL.DeleteTexture(Name);
        }
    }

    class COpenGL : CDrawBaseGlfw<COGLTexture>, IDraw
    {
        private int _FBO;

        public COpenGL()
        {
            int panels = CConfig.Config.Graphics.NumScreens;
            bool multi = CConfig.Config.Graphics.MultiWindow == EOffOn.TR_CONFIG_ON;
            int panelW = CConfig.Config.Graphics.ScreenW;
            int panelH = CConfig.Config.Graphics.ScreenH;

            // Multi-window primary (plan v3 §4): N windows of one panel each, all sharing a single GL
            // context (textures live once in VRAM). Single-window mode (MultiWindow = OFF, or after the
            // F1 mixed-GPU downgrade below): today's one giant window of ScreenW * NumScreens.
            int w = multi ? panelW : panelW * panels;
            int h = panelH;

            var settings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(w, h),
                Title = CSettings.GetFullVersionText(),
                // GL 2.1 with ContextProfile.Any -> a legacy/compatibility context that keeps the
                // fixed-function pipeline (immediate mode) the renderer relies on. GLFW only allows an
                // explicit Core/Compatibility profile for GL >= 3.2, so we must not set one here.
                Profile = ContextProfile.Any,
                APIVersion = new Version(2, 1),
                Flags = ContextFlags.Default,
                NumberOfSamples = (int)CConfig.Config.Graphics.AAMode,
                StartVisible = false,
                Vsync = CConfig.Config.Graphics.VSync == EOffOn.TR_CONFIG_ON ? VSyncMode.On : VSyncMode.Off
            };

            // OpenTK's default GLFW error handler turns EVERY GLFW error into an exception -
            // including harmless "feature not available on this platform" reports. On Wayland,
            // querying the window position is such a case (the protocol deliberately does not
            // expose it to clients), which killed startup during "Init Draw". Log instead of throw.
            GLFWProvider.SetErrorCallback((errorCode, description) =>
                CLog.Error("GLFW: {ErrorCode} - {Description}",
                    CLog.Params(errorCode, description)));

            _Window = new NativeWindow(settings);
            _Window.MakeCurrent();

            if (multi)
            {
                try
                {
                    _Windows = new CWindowManager(_Window, panels, settings);

                    // F1 (plan v3 §5): a shared context cannot span two GPUs - on a mixed-GPU machine the
                    // session runs as the legacy single giant window instead.
                    if (_Windows.ProbeRenderers())
                        _DowngradeToSingleWindow("mixed GPUs detected (shared contexts cannot span GPUs)");
                }
                catch (Exception e)
                {
                    // Shared-context creation failures (ICD quirks) must never break startup (plan v3 §12
                    // R1): fall back to today's single giant window session.
                    CLog.Error("Multi-window setup failed, falling back to the single giant window: " + e);
                    _DowngradeToSingleWindow("shared-context window creation failed");
                }
            }

            _NonPowerOf2TextureSupported = false;
        }

        /// <summary>
        ///     Undoes a multi-window attempt and continues the session in single-window mode
        ///     (today's MultiWindow = OFF path).
        /// </summary>
        private void _DowngradeToSingleWindow(string reason)
        {
            if (_Windows != null)
            {
                // The primary (slot 0) becomes the giant window - only the shared-context children close.
                _Windows.CloseAll(keepPrimary: true);
                _Windows = null;
            }

            _Window.MakeCurrent();
            _Window.ClientSize = new Vector2i(CConfig.Config.Graphics.ScreenW * CConfig.Config.Graphics.NumScreens, CConfig.Config.Graphics.ScreenH);
            CLog.Warning("Multi-window is disabled for this session (" + reason + "). Running the single giant window. " +
                         "Set Graphics.MultiWindow = OFF to silence this message.");
            CGraphics.SetSessionNotice("Multi-window disabled for this session (" + reason + "). Running the single giant window.");
        }

        /*
        private int _CheckColorDepth(int setValue)
        {
            int result = 16;

            if (setValue > 32)
                setValue = 32;

            while (result <= setValue)
            {
                GraphicsMode mode;
                try
                {
                    mode = new GraphicsMode(result, 0, 0, 0);
                }
                catch (Exception)
                {
                    break;
                }
                if (mode.ColorFormat != result)
                    break;
                result += 8;
            }

            return result - 8;
        }
*/

        protected override void _DoResize()
        {
            _CurrentAlignment = CConfig.Config.Graphics.ScreenAlignment;

            if (_MultiWindow())
            {
                // Per-slot letterbox (plan v3 §4.3/§4.5): each window shows one panel, so it letterboxes to
                // the panel aspect. Each panel's viewport/projection itself is applied per pass in
                // _OnBeforeDraw(panel) (all windows share one GL context and one state).
                for (int i = 0; i < _Windows.Count; i++)
                {
                    CWindowManager.Slot s = _Windows.Slots[i];
                    int winW = s.Window.ClientSize.X;
                    int winH = s.Window.ClientSize.Y;
                    int x = 0, y = 0, vw = winW, vh = winH;
                    if (CConfig.Config.Graphics.Stretch != EOffOn.TR_CONFIG_ON)
                        _AdjustAspect(true, winW, winH, out x, out y, out vw, out vh);

                    // Slot is a class: this mutates the stored slot in place (no list write-back, which would
                    // bump the list version and invalidate any in-flight enumeration - see CWindowManager).
                    s.LetterX = x;
                    s.LetterY = y;
                    s.LetterW = vw;
                    s.LetterH = vh;
                }
                return;
            }

            _H = _Window.ClientSize.Y;
            _W = _Window.ClientSize.X;
            if (CConfig.Config.Graphics.Stretch != EOffOn.TR_CONFIG_ON)
            {
                _AdjustAspect(true, _W, _H, out _X, out _Y, out _W, out _H);
            }

            _AdjustNewBorders(0);
            GL.Viewport(_X, _Y, _W, _H);
        }

        public override bool Init()
        {
            if (!base.Init())
                return false;

            // The fixed-function base state (enables, blend func, pixel store, depth, clear color) is
            // PER-CONTEXT state: context sharing shares objects (textures, FBOs, ...) but NOT the current
            // state (verified empirically: the primary window's context had Texture2D/DepthTest disabled
            // while the shared child's context had them enabled, so the primary rendered every textured
            // quad as a white color quad). Every context that renders a panel must receive the base
            // state - applying it once to the "current" context is not enough once children exist.
            if (_MultiWindow())
            {
                for (int i = 0; i < _Windows.Count; i++)
                    _ApplyBaseGLState(_Windows.Slots[i].Window);
                _Window.MakeCurrent(); // Leave the primary current, as in single-window startup.
            }
            else
                _ApplyBaseGLState(_Window);

            GL.GenFramebuffers(1, out _FBO);

            return true;
        }

        /// <summary>
        ///     Applies the one-time fixed-function base state to the given window's context (plan v3 §4.4).
        /// </summary>
        private void _ApplyBaseGLState(NativeWindow win)
        {
            win.MakeCurrent();

            // Init Texturing
            GL.Enable(EnableCap.Texture2D);

            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            GL.DepthRange(CSettings.ZFar, CSettings.ZNear);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.DepthTest);
            GL.ClearColor(0f, 0f, 0f, 1f);
        }

        public override void Close()
        {
            base.Close();
            GL.DeleteFramebuffers(1, ref _FBO);
        }

        protected override void _OnBeforeDraw(int panel)
        {
            if (!_MultiWindow())
            {
                // Single-window mode: today's behavior - the viewport/projecton are applied by
                // _DoResize and the border-change poll.
                return;
            }

            // Multi-window primary (plan v3 §4.4): this pass must be fully self-contained because all
            // windows share one GL context and therefore one GL state (viewport, matrices). Make the
            // slot current, set its (letterboxed) viewport, the panel's canvas-strip projection, and the
            // identity modelview.
            CWindowManager.Slot s = _Windows.Slots[panel];
            s.Window.MakeCurrent();
            GL.Viewport(s.LetterX, s.LetterY, s.LetterW, s.LetterH);
            _AdjustNewBorders(panel);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();
        }

        protected override void _OnAfterDraw(int panel)
        {
            if (_MultiWindow())
                _Windows.Slots[panel].Window.Context.SwapBuffers();
            else
                _Window.Context.SwapBuffers();
            // Window event flushing + the "should close" poll live in CDrawBaseGlfw._ProcessEvents,
            // called once per frame after the last swap - the same sequence as before this change.
        }

        public int GetScreenWidth()
        {
            // Single-window: the window width (today). Multi-window: the whole canvas
            // (RenderW * NumScreens), so the CUtility._Add mouse remap becomes the identity for the
            // panel-local coordinates emitted by the per-window handlers (plan v3 §6).
            if (_MultiWindow())
                return CSettings.RenderW * CConfig.Config.Graphics.NumScreens;
            return _Window.ClientSize.X;
        }

        public int GetScreenHeight()
        {
            if (_MultiWindow())
                return CSettings.RenderH;
            return _Window.ClientSize.Y;
        }

        protected override void _ClearScreen()
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }

        protected override void _AdjustNewBorders(int panel)
        {
            int screens = CConfig.Config.Graphics.NumScreens;
            float left, right, bottom, top;
            PanelOrtho(_MultiWindow(), panel, screens,
                       CSettings.RenderW, CSettings.RenderH,
                       CConfig.Config.Graphics.BorderLeft, CConfig.Config.Graphics.BorderRight,
                       CConfig.Config.Graphics.BorderTop, CConfig.Config.Graphics.BorderBottom,
                       out left, out right, out bottom, out top);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(left, right, bottom, top, CSettings.ZNear, CSettings.ZFar);
        }

        /// <summary>
        ///     Pure panel-strip projection math (plan v3 §4.3), extracted from <c>_AdjustNewBorders</c> so it
        ///     is unit-testable without a GL context. Multi-window with more than one panel: the strip of
        ///     panel <paramref name="multiPanel" /> with the borders only on the canvas' outer edges (the
        ///     union of all panels reconstructs exactly the giant window's ortho). Single-window mode - or
        ///     multi with one panel: the whole canvas incl. the border margins, exactly today's ortho.
        ///     The Y span is common to all cases.
        /// </summary>
        /// <param name="multiWindow">true when the multi-window path is active (panel routing on).</param>
        /// <param name="panel">Panel index being projected (0..screens-1; ignored in the giant-window case).</param>
        /// <param name="screens">Panel count (NumScreens).</param>
        /// <param name="renderW">One panel's canvas width.</param>
        /// <param name="renderH">One panel's canvas height.</param>
        public static void PanelOrtho(bool multiWindow, int panel, int screens,
                                      float renderW, float renderH,
                                      float borderL, float borderR, float borderT, float borderB,
                                      out float left, out float right, out float bottom, out float top)
        {
            if (multiWindow && screens > 1)
            {
                left = panel * renderW - (panel == 0 ? borderL : 0);
                right = (panel + 1) * renderW + (panel == screens - 1 ? borderR : 0);
            }
            else
            {
                left = -borderL;
                right = borderR + renderW * screens;
            }
            bottom = borderB + renderH;
            top = -borderT;
        }

        /// <summary>
        ///     Captures the whole logical canvas (all panels) as bottom-up BGRA pixels (GL orientation):
        ///     single-window mode reads the (giant) window exactly as before; multi-window mode reads one
        ///     panel per window and stitches the panels side by side (plan v3 §7).
        /// </summary>
        private byte[] _CaptureCanvas(out int width, out int height)
        {
            if (!_MultiWindow())
            {
                width = _Window.ClientSize.X;
                height = _Window.ClientSize.Y;
                byte[] pixels = new byte[4 * width * height];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                return pixels;
            }

            int count = _Windows.Count;

            // Each slot is read at its own client area (monitors may differ in size in fullscreen);
            // the canvas is stitched bottom-aligned (GL orientation).
            int canvasW = 0;
            int canvasH = 0;
            for (int i = 0; i < count; i++)
            {
                canvasW += _Windows.Slots[i].Window.ClientSize.X;
                canvasH = Math.Max(canvasH, _Windows.Slots[i].Window.ClientSize.Y);
            }

            width = canvasW;
            height = canvasH;
            byte[] canvas = new byte[4 * canvasW * canvasH];
            int offsetX = 0;
            for (int i = 0; i < count; i++)
            {
                CWindowManager.Slot s = _Windows.Slots[i];
                int w = s.Window.ClientSize.X;
                int h = s.Window.ClientSize.Y;
                offsetX += w;
                if (w == 0 || h == 0)
                    continue;

                // A captured backbuffer is only guaranteed to hold valid content at the moment it was
                // just rendered. _CaptureCanvas is normally called between MainLoop frames - i.e. AFTER
                // this slot's SwapBuffers - where the backbuffer content is undefined (observed: a pure
                // black canvas). Render exactly the per-panel pass the MainLoop runs for this slot and
                // read before the present; the present then shows the content that was just captured,
                // so it is a visual no-op.
                _PanelIndex = i;
                _OnBeforeDraw(i);
                _ClearScreen();
                _DrawScene();
                byte[] panel = new byte[4 * w * h];
                GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, panel);
                _OnAfterDraw(i);
                _PanelIndex = -1;

                // Stitch bottom-up rows verbatim; only the x offset differs.
                for (int row = 0; row < h && row < canvasH; row++)
                    System.Buffer.BlockCopy(panel, row * w * 4, canvas, row * canvasW * 4 + (offsetX - w) * 4, w * 4);
            }

            return canvas;
        }

        public void MakeScreenShot()
        {
            string file = CHelper.GetUniqueFileName(Path.Combine(CSettings.DataFolder, CSettings.FolderNameScreenshots), "Screenshot.png");

            int width, height;
            byte[] screen = _CaptureCanvas(out width, out height);

            // OpenGL's origin is bottom-left, so flip vertically before saving.
            // The pixel buffer is seeded with SKImage.FromPixels: in SkiaSharp 3, SKBitmap.Bytes is no
            // longer the bitmap's pixel store, so the pre-3.0 "BlockCopy into SKBitmap.Bytes +
            // DrawBitmap" idiom seeds a blank bitmap (verified: produced an all-transparent PNG).
            using (var flipped = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(0, height);
                canvas.Scale(1, -1);

                using (var data = SKData.CreateCopy(screen))
                using (var source = SKImage.FromPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul), data, width * 4))
                    canvas.DrawImage(source, 0, 0);

                using (var encoded = SKImage.FromBitmap(flipped).Encode(SKEncodedImageFormat.Png, 100))
                    File.WriteAllBytes(file, encoded.ToArray());
            }
        }

        public CTextureRef CopyScreen()
        {
            // A CPU read-back + write is used instead of CopyTexSubImage2D so the semantics are identical
            // in single-window and multi-window mode (the "screen" captured is always the whole canvas).
            int width, height;
            byte[] data = _CaptureCanvas(out width, out height);

            //TODO: Check if _W,_H needs to be used or not
            Size size = new Size(width, height);
            COGLTexture texture = _CreateTexture(size);
            _WriteDataToTexture(texture, data);

            return _GetTextureReference(size, texture);
        }

        public void CopyScreen(ref CTextureRef textureRef)
        {
            int width, height;
            byte[] data = _CaptureCanvas(out width, out height);

            COGLTexture texture;
            //Check for actual texture sizes as it may be downsized compared to OrigSize
            if (!_GetTexture(textureRef, out texture) || texture.DataSize.Width != width || texture.DataSize.Height != height)
            {
                RemoveTexture(ref textureRef);
                textureRef = CopyScreen();
            }
            else
            {
                _WriteDataToTexture(texture, data);
            }
        }

        public void DrawRect(SColorF color, SRectF rect, bool allMonitors = true)
        {
            int loops = 1;
            if (allMonitors)
                loops = CConfig.Config.Graphics.NumScreens;
            for (int i = 0; i < loops; i++)
            {
                SRectF newrect = rect;
                newrect.X += CSettings.RenderW * i;
                if (!_PanelVisible(newrect))
                    continue; // multi-window: this copy belongs to another panel's window

                GL.Enable(EnableCap.Blend);
                GL.Color4(color.R, color.G, color.B, color.A * CGraphics.GlobalAlpha);

                GL.Begin(PrimitiveType.Quads);
                GL.MatrixMode(MatrixMode.Color);
                GL.PushMatrix();
                if (Math.Abs(newrect.Rotation) > 0.001)
                {
                    GL.Translate(0.5f, 0.5f, 0);
                    GL.Rotate(-newrect.Rotation, 0f, 0f, 1f);
                    GL.Translate(-0.5f, -0.5f, 0);
                }
                GL.Vertex3(newrect.X, newrect.Y, newrect.Z + CGraphics.ZOffset);
                GL.Vertex3(newrect.X, newrect.Y + newrect.H, newrect.Z + CGraphics.ZOffset);
                GL.Vertex3(newrect.X + newrect.W, newrect.Y + newrect.H, newrect.Z + CGraphics.ZOffset);
                GL.Vertex3(newrect.X + newrect.W, newrect.Y, newrect.Z + CGraphics.ZOffset);
                GL.End();
                GL.PopMatrix();
                GL.Disable(EnableCap.Blend);
            }
        }

        public void DrawRectReflection(SColorF color, SRectF rect, float space, float height, bool allMonitors = true)
        {
            for (int i = 0; i < (allMonitors ? CConfig.Config.Graphics.NumScreens : 1); i++)
            {
                SRectF newrect = rect;
                newrect.X += CSettings.RenderW * i;
                if (!_PanelVisible(newrect))
                    continue; // multi-window: this copy belongs to another panel's window

                if (newrect.H < height)
                    height = newrect.H;

                float rx1 = newrect.X;
                float rx2 = newrect.X + newrect.W;
                float ry1 = newrect.Y + newrect.H + space;
                float ry2 = newrect.Y + newrect.H + space + height;

                if (rx1 < newrect.X)
                    rx1 = newrect.X;

                if (rx2 > newrect.X + newrect.W)
                    rx2 = newrect.X + newrect.W;

                if (ry1 < newrect.Y + space)
                    ry1 = newrect.Y + space;

                if (ry2 > newrect.Y + newrect.H + space + height)
                    ry2 = newrect.Y + newrect.H + space + height;

                GL.Enable(EnableCap.Blend);
                GL.MatrixMode(MatrixMode.Color);
                GL.PushMatrix();
                if (Math.Abs(newrect.Rotation) > 0.001)
                {
                    GL.Translate(0.5f, 0.5f, 0);
                    GL.Rotate(-newrect.Rotation, 0f, 0f, 1f);
                    GL.Translate(-0.5f, -0.5f, 0);
                }

                GL.Begin(PrimitiveType.Quads);

                GL.Color4(color.R, color.G, color.B, color.A * CGraphics.GlobalAlpha);
                GL.Vertex3(rx2, ry1, newrect.Z + CGraphics.ZOffset);

                GL.Color4(color.R, color.G, color.B, 0f);
                GL.Vertex3(rx2, ry2, newrect.Z + CGraphics.ZOffset);
                GL.Vertex3(rx1, ry2, newrect.Z + CGraphics.ZOffset);

                GL.Color4(color.R, color.G, color.B, color.A * CGraphics.GlobalAlpha);
                GL.Vertex3(rx1, ry1, newrect.Z + CGraphics.ZOffset);

                GL.End();
                GL.PopMatrix();
                GL.Disable(EnableCap.Blend);
            }
        }

        protected override COGLTexture _CreateTexture(Size dataSize)
        {
            if (dataSize.Width < 0)
                return new COGLTexture(0, dataSize);
            COGLTexture texture = new COGLTexture(GL.GenTexture(), dataSize, _CheckForNextPowerOf2(dataSize.Width), _CheckForNextPowerOf2(dataSize.Height));

            return texture;
        }

        private void _ClearTexture(COGLTexture texture)
        {
            if (texture.DataSize.Equals(texture.Size))
                return;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _FBO);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
                                    texture.Name, 0);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        protected override void _WriteDataToTexture(COGLTexture texture, byte[] data)
        {
            Debug.Assert(texture.Name > 0);
            _ClearTexture(texture);
            GL.BindTexture(TextureTarget.Texture2D, texture.Name);

            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.DataSize.Width, texture.DataSize.Height, PixelFormat.Bgra, PixelType.UnsignedByte, data);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        protected override void _WriteDataToTexture(COGLTexture texture, IntPtr data)
        {
            Debug.Assert(texture.Name > 0);
            _ClearTexture(texture);
            GL.BindTexture(TextureTarget.Texture2D, texture.Name);

            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, texture.DataSize.Width, texture.DataSize.Height, PixelFormat.Bgra, PixelType.UnsignedByte, data);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        protected override void _DrawTexture(COGLTexture texture, SDrawCoords dc, SColorF color, bool isReflection = false, float fadeFrom = 1f, float fadeTo = 0f)
        {
            // Align textures to full pixels to reduce artefacts
            dc.Wx1 = (float)Math.Round(dc.Wx1);
            dc.Wy1 = (float)Math.Round(dc.Wy1);
            dc.Wx2 = (float)Math.Round(dc.Wx2);
            dc.Wy2 = (float)Math.Round(dc.Wy2);

            GL.BindTexture(TextureTarget.Texture2D, texture.Name);

            GL.Enable(EnableCap.Blend);

            GL.MatrixMode(MatrixMode.Texture);
            GL.PushMatrix();

            if (Math.Abs(dc.Rotation) > float.Epsilon)
            {
                GL.Translate(0.5f, 0.5f, 0);
                GL.Rotate(-dc.Rotation, 0f, 0f, 1f);
                GL.Translate(-0.5f, -0.5f, 0);
            }

            GL.Begin(PrimitiveType.Quads);

            GL.Color4(color.R, color.G, color.B, color.A * fadeFrom * CGraphics.GlobalAlpha);
            GL.TexCoord2(dc.Tx1, dc.Ty1);
            GL.Vertex3(dc.Wx1, dc.Wy1, dc.Wz);

            if (isReflection)
                GL.Color4(color.R, color.G, color.B, color.A * fadeTo * CGraphics.GlobalAlpha);
            GL.TexCoord2(dc.Tx1, dc.Ty2);
            GL.Vertex3(dc.Wx1, dc.Wy2, dc.Wz);

            GL.TexCoord2(dc.Tx2, dc.Ty2);
            GL.Vertex3(dc.Wx2, dc.Wy2, dc.Wz);

            if (isReflection)
                GL.Color4(color.R, color.G, color.B, color.A * fadeFrom * CGraphics.GlobalAlpha);
            GL.TexCoord2(dc.Tx2, dc.Ty1);
            GL.Vertex3(dc.Wx2, dc.Wy1, dc.Wz);

            GL.End();

            GL.PopMatrix();

            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
    }
}