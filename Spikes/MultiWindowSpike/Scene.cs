using System;
using OpenTK.Graphics.OpenGL;
using GLX = OpenTK.Graphics.OpenGL;

namespace MultiWindowSpike
{
    /// <summary>
    ///     Synthetic benchmark scene in the exact immediate-mode shape of the Vocaluxe renderer
    ///     (plan §11): per-draw-call 1x1 white texture bind (the _SplashWhite pattern), per-quad
    ///     Blend on/off, per-vertex Color4 + TexCoord2 + Vertex.
    ///     The scene is N panels of 1280x720. DrawPanel(panel) draws ONE panel - the equivalent of
    ///     the _PanelIndex restrictor (plan §8); the VFB/giant paths call it for all N panels.
    ///     Panel 0's background color is a deterministic per-panel hue, which the probe uses to
    ///     verify that each slot really presents its own slice of the shared VFB.
    /// </summary>
    static class Scene
    {
        public static int White;

        public static void EnsureTextures()
        {
            if (White != 0)
                return;
            White = GL.GenTexture();
            GL.BindTexture(GLX.TextureTarget.Texture2D, White);
            GL.TexImage2D(GLX.TextureTarget.Texture2D, 0, GLX.PixelInternalFormat.Rgba, 1, 1, 0,
                GLX.PixelFormat.Bgra, GLX.PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureMagFilter, (int)GLX.TextureMagFilter.Linear);
            GL.TexParameter(GLX.TextureTarget.Texture2D, GLX.TextureParameterName.TextureMinFilter, (int)GLX.TextureMinFilter.Linear);
            GL.BindTexture(GLX.TextureTarget.Texture2D, 0);
        }

        public static (float R, float G, float B) PanelHue(int i)
        {
            const double phase = 2.3;
            double f = i * phase;
            return (
                (float)(0.5 + 0.5 * Math.Sin(f)),
                (float)(0.5 + 0.5 * Math.Sin(f + 2.0943951)),
                (float)(0.5 + 0.5 * Math.Sin(f + 4.1887902)));
        }

        static void Quad(float x, float y, float w, float h, float r, float g, float b)
        {
            GL.Enable(GLX.EnableCap.Blend);
            GL.Color4(r, g, b, 1f);
            GL.BindTexture(GLX.TextureTarget.Texture2D, White);
            GL.Begin(GLX.PrimitiveType.Quads);
            GL.TexCoord2(0f, 0f);
            GL.Vertex2(x, y);
            GL.TexCoord2(0f, 1f);
            GL.Vertex2(x, y + h);
            GL.TexCoord2(1f, 1f);
            GL.Vertex2(x + w, y + h);
            GL.TexCoord2(1f, 0f);
            GL.Vertex2(x + w, y);
            GL.End();
            GL.Disable(GLX.EnableCap.Blend);
        }

        public static void DrawPanel(int panel, int quads, double t, int frame)
        {
            int pw = panel * 1280;
            var hue = PanelHue(panel);
            Quad(pw, 0, 1280, 720, hue.R, hue.G, hue.B);   // background: unique per panel (probe verifies)

            for (int q = 0; q < quads; q++)
            {
                double a = t * (0.6 + 0.13 * (q % 7)) + q * 1.7;
                float x = pw + 40 + (float)(500 * (0.5 + 0.5 * Math.Cos(a)));
                float y = 40 + (float)(300 * (0.5 + 0.5 * Math.Sin(a * 1.31 + q * 0.7)));
                float s = 40f + (q % 5) * 16f;
                var c = PanelHue(q);
                Quad(x, y, s, s, c.R, c.G, c.B);
            }

            // Frame marker: color encodes the frame index, so a frozen present shows a static quad.
            float f = ((frame * 97) % 251) / 251f;
            Quad(pw + 40, 600, 96, 60, f, 1f - f, 0.5f);
        }
    }
}
