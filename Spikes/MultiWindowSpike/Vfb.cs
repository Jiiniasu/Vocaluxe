using System;
using OpenTK.Graphics.OpenGL;
using GLX = OpenTK.Graphics.OpenGL;

namespace MultiWindowSpike
{
    /// <summary>
    ///     The VFB (plan §4.3): POT-safe render-target texture + FBO + one textured-quad blit per
    ///     panel. This file is the copy-paste donor for the production COpenGL implementation: same
    ///     names, same call order, same GL 2.1 compatibility constraints (fixed-function blit, no
    ///     mipmaps on the render target).
    /// </summary>
    static class Vfb
    {
        public static int Tex;
        public static int Fbo;
        public static int TexW;
        public static int TexH;
        public static int CanvasW;
        public static int CanvasH;

        static int NextPowerOfTwo(int v)
        {
            int p = 1;
            while (p < v)
                p *= 2;
            return p;
        }

        public static void Create(int canvasW, int canvasH)
        {
            CanvasW = canvasW;
            CanvasH = canvasH;
            TexW = NextPowerOfTwo(canvasW);
            TexH = NextPowerOfTwo(canvasH);

            Tex = GL.GenTexture();
            GL.BindTexture(GLX.TextureTarget.Texture2D, Tex);
            GL.TexImage2D(GLX.TextureTarget.Texture2D, 0, GLX.PixelInternalFormat.Rgba, TexW, TexH, 0,
                GLX.PixelFormat.Bgra, GLX.PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureWrapS, (int)GLX.TextureWrapMode.ClampToEdge);
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureWrapT, (int)GLX.TextureWrapMode.ClampToEdge);
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureMagFilter, (int)GLX.TextureMagFilter.Linear);
            // Linear/Linear, deliberately NO mipmaps: a render target whose mip levels would bleed
            // across panel boundaries (plan §4.3).
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureMinFilter, (int)GLX.TextureMinFilter.Linear);
            GL.BindTexture(GLX.TextureTarget.Texture2D, 0);

            Fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(GLX.FramebufferTarget.Framebuffer, Fbo);
            GL.FramebufferTexture2D(GLX.FramebufferTarget.Framebuffer, GLX.FramebufferAttachment.ColorAttachment0,
                GLX.TextureTarget.Texture2D, Tex, 0);
            // Completeness is a dedicated query in GL (not a GetInteger):
            var status = GL.CheckFramebufferStatus(GLX.FramebufferTarget.Framebuffer);
            if (status != GLX.FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("VFB FBO incomplete: " + status);
            GL.BindFramebuffer(GLX.FramebufferTarget.Framebuffer, 0);
        }

        public static void Delete()
        {
            if (Fbo != 0)
                GL.DeleteFramebuffers(1, ref Fbo);
            if (Tex != 0)
                GL.DeleteTexture(Tex);
            Fbo = 0;
            Tex = 0;
        }

        /// <summary>
        ///     Called on the primary context before the scene pass. Ownership of GL state is explicit:
        ///     shared contexts keep ONE matrix stack / texture state across all windows, and the blit
        ///     passes leave Ortho(0..1) behind — so the scene pass must set up its own projection,
        ///     modelview and clear, or it inherits the previous pass's state.
        /// </summary>
        public static void BeginRender()
        {
            GL.BindFramebuffer(GLX.FramebufferTarget.Framebuffer, Fbo);
            GL.Viewport(0, 0, CanvasW, CanvasH);
            GL.MatrixMode(GLX.MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0f, CanvasW, 0f, CanvasH, -1f, 1f);
            GL.MatrixMode(GLX.MatrixMode.Modelview);
            GL.LoadIdentity();
            GL.Disable(GLX.EnableCap.DepthTest);
            GL.Disable(GLX.EnableCap.Blend);
            GL.Enable(GLX.EnableCap.Texture2D);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(GLX.ClearBufferMask.ColorBufferBit);
        }

        /// <summary>Called after the scene pass, before the N blit passes.</summary>
        public static void EndRender()
        {
            GL.BindFramebuffer(GLX.FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>
        ///     Draws panel <paramref name="panel"/>'s slice as one textured quad, aspect-fit with a
        ///     centered letterbox (plan §4.4/§4.5). Caller must have made that slot current.
        /// </summary>
        public static void BlitPanel(Slot slot, int panel)
        {
            int fbW = slot.FbW;
            int fbH = slot.FbH;
            if (fbW < 1 || fbH < 1)
                throw new InvalidOperationException("slot has no framebuffer size");

            GL.Viewport(0, 0, fbW, fbH);
            GL.MatrixMode(GLX.MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0f, 1f, 0f, 1f, -1f, 1f);
            GL.MatrixMode(GLX.MatrixMode.Modelview);
            GL.LoadIdentity();
            GL.Disable(GLX.EnableCap.DepthTest);
            GL.Disable(GLX.EnableCap.Blend);
            GL.Enable(GLX.EnableCap.Texture2D);
            // GL state such as the current RGBA color is PER-CONTEXT, not shared: the primary
            // context carries whatever color the scene pass last set (modulation would tint the
            // whole blit), so the blit pass must be fully self-contained.
            GL.Color4(1f, 1f, 1f, 1f);
            GL.BindTexture(GLX.TextureTarget.Texture2D, Tex);

            // Letterbox the 16:9 panel into the (possibly non-16:9) framebuffer, centered.
            float vw = Math.Min(fbW, fbH * 1280f / 720f);
            float vh = Math.Min(fbH, fbW * 720f / 1280f);
            float ox = (fbW - vw) / 2f;
            float oy = (fbH - vh) / 2f;
            float x0 = 2f * ox / fbW - 1f;
            float x1 = 2f * (ox + vw) / fbW - 1f;
            float y0 = 1f - 2f * (oy + vh) / fbH;
            float y1 = 1f - 2f * oy / fbH;

            // UVs: panel i of the N-panel canvas anchored at (0,0) inside the POT texture,
            // scaled by canvas/POT (plan §4.3 k-factor).
            float u0 = panel * 1280f / TexW;
            float u1 = (panel + 1) * 1280f / TexW;
            float v0 = 0f;
            float v1 = 720f / TexH;

            GL.Begin(GLX.PrimitiveType.Quads);
            GL.TexCoord2(u0, v1);
            GL.Vertex2(x0, y0);
            GL.TexCoord2(u1, v1);
            GL.Vertex2(x1, y0);
            GL.TexCoord2(u1, v0);
            GL.Vertex2(x1, y1);
            GL.TexCoord2(u0, v0);
            GL.Vertex2(x0, y1);
            GL.End();

            GL.Disable(GLX.EnableCap.Texture2D);
            GL.BindTexture(GLX.TextureTarget.Texture2D, 0);
        }
    }
}
