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
using System.Drawing;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;

namespace Vocaluxe.Lib.Draw
{
    /// <summary>
    ///     Frosted-glass backdrops for theme elements (Static Glass="true"): draws a darkened, blurred
    ///     copy of the current background behind the element, shaped by a mask texture, so the video
    ///     doesn't shine through the panels.
    /// 
    ///     The background is registered with <see cref="SetBackground" />: either the video's per-frame
    ///     glass twin (pre-blurred by the decoder, owned by the video stream) or an image texture, for
    ///     which a blurred "twin" is generated once (256 px long edge, see CGlassBlur) and cached.
    ///     Only the generated textures (blurred twins + white-alpha masks) are owned here - the
    ///     background source and the video twin are borrowed and never disposed.
    /// </summary>
    public class CGlass : IGlass
    {
        private struct SBlurEntry
        {
            public CTextureRef Twin;
            public int Strength;
        }

        private CTextureRef _Source;
        private CTextureRef _VideoTwin;
        private EAspect _BackgroundAspect = EAspect.Crop;
        private readonly Dictionary<string, SBlurEntry> _BlurredBackgrounds = new Dictionary<string, SBlurEntry>();
        private readonly Dictionary<string, CTextureRef> _Masks = new Dictionary<string, CTextureRef>();
        private readonly HashSet<string> _UnresolvedMasks = new HashSet<string>();
        private readonly HashSet<string> _Diagnosed = new HashSet<string>();

        public bool IsEnabled()
        {
            return CConfig.Config.Graphics.GlassBlur == EOffOn.TR_CONFIG_ON;
        }

        public void SetBackground(CTextureRef source, CTextureRef videoTwin, EAspect aspect)
        {
            _BackgroundAspect = aspect;

            if (videoTwin != null)
            {
                if (_VideoTwin == null || !ReferenceEquals(_VideoTwin, videoTwin))
                    CLog.Debug("[Glass] background: video twin " + (_VideoTwin == null ? "null" : _VideoTwin.ID.ToString()) + " -> " + videoTwin.ID + " (" + videoTwin.OrigSize.Width + "x" + videoTwin.OrigSize.Height + ")");
                _VideoTwin = videoTwin;
                return;
            }

            //The background is no longer a video: drop the twin so a later screen (image or none)
            //does not sample a frozen video frame
            _VideoTwin = null;

            if (source == _Source)
                return;
            CLog.Debug("[Glass] background: image -> " + (source == null ? "null" : source.TexturePath));

            //New background image: the cached blurred copies of the old one are stale
            foreach (SBlurEntry entry in _BlurredBackgrounds.Values)
                entry.Twin.Dispose();
            _BlurredBackgrounds.Clear();
            _Source = source;
        }

        public void OnSettingsChanged()
        {
            //The skin was reloaded: the generated masks (derived from skin PNGs) are stale
            foreach (CTextureRef mask in _Masks.Values)
                mask.Dispose();
            _Masks.Clear();

            //The background texture may point into the reloaded skin content: drop the blurred twins
            foreach (SBlurEntry entry in _BlurredBackgrounds.Values)
                entry.Twin.Dispose();
            _BlurredBackgrounds.Clear();
        }

        public void DrawGlass(CStatic cStatic)
        {
            if (!IsEnabled() || !cStatic.Glass || !cStatic.Visible)
            {
                _DiagnoseGate(cStatic);
                return;
            }

            CTextureRef texture = cStatic.Texture;
            //A glass static with neither texture nor mask is a plain rectangular glass (e.g. a text bar),
            //and a mask-only glass (e.g. the lyrics bar) is fine as well. Only a declared mask that does
            //not resolve in the skin's texture list silently changes the shape (to a plain rectangle),
            //so warn once per static name about that
            if (texture == null && cStatic.GlassMask == null)
            {
                SThemeStatic theme = (SThemeStatic)cStatic.GetTheme();
                if (!String.IsNullOrEmpty(theme.GlassMask))
                {
                    //Copied per-screen player statics (CStatic copy-ctor) lose their theme and have no name
                    string name = cStatic.GetThemeName() ?? "(unnamed)";
                    if (_UnresolvedMasks.Add(name))
                        CLog.Warning("Glass static \"" + name + "\" declares glass mask \"" + theme.GlassMask + "\" but it does not resolve; the glass is drawn as a plain rectangle. Check that the mask is declared in the skin's texture list.");
                }
            }

            SRectF rect = _GlassRect(cStatic);
            //The static's fill color does not shape the backdrop: a mask-only glass static (e.g. the
            //lyrics bar) has an "Invisible" fill by design, so only the runtime fade alpha modulates it
            float alpha = cStatic.Alpha;
            if (alpha < 0.01f)
                return;

            float darken = 1f - (float)CConfig.Config.Graphics.GlassDarken / 100f;
            SColorF color = new SColorF(darken, darken, darken, alpha);

            CTextureRef twin = _GetTwin();
            if (twin != null)
            {
                CTextureRef mask = _GetMask(cStatic, texture);
                //The twin is a full-frame copy of the background: only the part behind the panel is
                //sampled, otherwise the whole frame is squashed into the panel
                SRectF uv = _TwinUv(rect, twin);
                _DiagnoseDraw(cStatic, rect, uv, twin, mask, alpha);
                CBase.Drawing.DrawGlassTexture(twin, rect, color, mask, rect, uv, cStatic.AllMonitors);
            }
            else
            {
                //No blur source available (e.g. a plain color background): tint-only fallback,
                //shaped by the mask when there is one
                CTextureRef mask = _GetMask(cStatic, texture);
                string name = (cStatic.GetThemeName() ?? "(unnamed)") + "@" + cStatic.Rect.X.ToString("F0") + "," + cStatic.Rect.Y.ToString("F0");
                if (_Diagnosed.Add("tint:" + name))
                    CLog.Debug("[Glass] tint fallback " + name + ": twin=null source=" + (_Source == null ? "null" : _Source.TexturePath) + " mask=" + (mask == null ? "null" : mask.ID.ToString()));
                if (mask != null)
                    CBase.Drawing.DrawTexture(mask, rect, color, cStatic.AllMonitors);
                else
                    CBase.Drawing.DrawRect(color, rect, cStatic.AllMonitors);
            }
        }

        /// <summary>
        ///     The destination rect of the glass quad - exactly the rect math of CStatic.Draw
        ///     (scale = 1, zModify = 0)
        /// </summary>
        private static SRectF _GlassRect(CStatic cStatic)
        {
            CTextureRef texture = cStatic.Texture;
            SRectF bounds = cStatic.Rect;

            //Change bounds if rect size should be modified without adjusting texture
            if (cStatic.ModH != -1)
                bounds.H = cStatic.ModH;
            if (cStatic.ModW != -1)
                bounds.W = cStatic.ModW;

            SRectF rect = texture == null ? bounds : CHelper.FitInBounds(bounds, texture.OrigAspect, cStatic.Aspect);

            //Use original rect
            if (cStatic.ModH != -1 || cStatic.ModW != -1)
                rect = cStatic.Rect;

            return rect;
        }

        /// <summary>
        ///     The sub-rect of the twin texture (normalized 0..1) that lies behind the glass panel.
        ///     The background is drawn per panel as FitInBounds(panel window, twin aspect, aspect),
        ///     exactly like the regular background draws (CBackground, CScreenSing): the twin quad is
        ///     that fitted rect and the panel window clips it, so the panel's rect maps to a UV
        ///     sub-rect of the twin. The per-panel geometry is identical (everything is shifted by
        ///     panel * RenderW), so the panel-0 mapping is valid for every panel.
        /// </summary>
        private SRectF _TwinUv(SRectF rect, CTextureRef twin)
        {
            float panelX = (float)Math.Floor(rect.X / CSettings.RenderW) * CSettings.RenderW;
            SRectF window = new SRectF(panelX, 0f, CSettings.RenderW, CSettings.RenderH, 0f);
            SRectF twinRect = CHelper.FitInBounds(window, twin.OrigAspect, _BackgroundAspect);

            float u1 = (rect.X - twinRect.X) / twinRect.W;
            float v1 = (rect.Y - twinRect.Y) / twinRect.H;
            float u2 = (rect.Right - twinRect.X) / twinRect.W;
            float v2 = (rect.Bottom - twinRect.Y) / twinRect.H;

            u1 = Math.Min(1f, Math.Max(0f, u1));
            v1 = Math.Min(1f, Math.Max(0f, v1));
            u2 = Math.Min(1f, Math.Max(0f, u2));
            v2 = Math.Min(1f, Math.Max(0f, v2));

            return new SRectF(u1, v1, u2 - u1, v2 - v1, 0f);
        }

        /// <summary>
        ///     The blurred copy of the current background: the video stream's pre-blurred twin if the
        ///     background is a video, otherwise a cached blurred copy of the background image (generated
        ///     on demand, regenerated when the blur strength changes). File-backed sources are decoded
        ///     from the file; generated textures (e.g. the cover DB's resized bytes, which carry no path)
        ///     are read back from the texture itself. Null if no blur source is available.
        /// </summary>
        private CTextureRef _GetTwin()
        {
            if (_VideoTwin != null)
                return _VideoTwin;
            if (_Source == null)
                return null; //no background source at all: tint-only fallback

            int strength = (int)CConfig.Config.Graphics.GlassStrength;
            bool hasFile = !String.IsNullOrEmpty(_Source.TexturePath);
            string key = hasFile ? _Source.TexturePath : "id" + _Source.ID;

            SBlurEntry entry;
            if (_BlurredBackgrounds.TryGetValue(key, out entry) && entry.Strength == strength)
                return entry.Twin;

            if (entry.Twin != null)
            {
                //Strength changed: the cached twin was blurred with the old strength
                entry.Twin.Dispose();
            }

            int width, height;
            byte[] bgra = hasFile ? _LoadBgra(_Source.TexturePath, out width, out height) : CDraw.CopyTextureData(_Source, out width, out height);
            if (bgra == null)
                return null;

            byte[] blurred = CGlassBlur.Process(bgra, width, height, strength);
            if (blurred == null)
                return null;

            Size size = CGlassBlur.CalcTargetSize(width, height);
            CTextureRef twin = CDraw.AddTexture(size.Width, size.Height, blurred);
            if (twin == null)
                return null;

            _BlurredBackgrounds[key] = new SBlurEntry { Twin = twin, Strength = strength };
            return twin;
        }

        /// <summary>
        ///     The mask texture shaping the glass: the static's GlassMask if set, otherwise the static's
        ///     own texture (its alpha defines the shape). The mask is a generated white-alpha copy of
        ///     the source PNG (using the colored skin directly would tint the glass), cached per file.
        ///     Null if the mask cannot be generated (unshaped, rectangular glass).
        /// </summary>
        private CTextureRef _GetMask(CStatic cStatic, CTextureRef texture)
        {
            CTextureRef maskSource = cStatic.GlassMask ?? texture;
            if (maskSource == null)
                return null; //no texture and no mask: unshaped, rectangular glass

            string key;
            if (!String.IsNullOrEmpty(maskSource.TexturePath))
                key = maskSource.TexturePath;
            else
                key = "id" + maskSource.ID; //generated texture: key by the stable reference

            CTextureRef mask;
            if (_Masks.TryGetValue(key, out mask))
                return mask;

            if (String.IsNullOrEmpty(maskSource.TexturePath))
                return null; //cannot decode generated textures

            int width, height;
            byte[] bgra = _LoadBgra(maskSource.TexturePath, out width, out height);
            if (bgra == null)
                return null;

            byte[] white = new byte[bgra.Length];
            for (int i = 3; i < bgra.Length; i += 4)
            {
                white[i - 3] = 255;
                white[i - 2] = 255;
                white[i - 1] = 255;
                white[i] = bgra[i];
            }

            mask = CDraw.AddTexture(width, height, white);
            if (mask == null)
                return null;

            _Masks[key] = mask;
            return mask;
        }

        // Log the gate decision and the drawn quad once per static name so the debug log shows
        // which path a glass static takes
        private void _DiagnoseGate(CStatic cStatic)
        {
            string name = (cStatic.GetThemeName() ?? "(unnamed)") + "@" + cStatic.Rect.X.ToString("F0") + "," + cStatic.Rect.Y.ToString("F0");
            if (!_Diagnosed.Add("gate:" + name))
                return;
            CLog.Debug("[Glass] gate skip " + name + ": enabled=" + IsEnabled() + " glass=" + cStatic.Glass + " visible=" + cStatic.Visible + " alpha=" + cStatic.Alpha.ToString("F3"));
        }

        private void _DiagnoseDraw(CStatic cStatic, SRectF rect, SRectF uv, CTextureRef twin, CTextureRef mask, float alpha)
        {
            string name = (cStatic.GetThemeName() ?? "(unnamed)") + "@" + cStatic.Rect.X.ToString("F0") + "," + cStatic.Rect.Y.ToString("F0");
            if (!_Diagnosed.Add("draw:" + name))
                return;
            CLog.Debug("[Glass] draw " + name + ": twin=" + twin.ID + " (" + twin.OrigSize.Width + "x" + twin.OrigSize.Height + ") mask=" + (mask == null ? "null" : mask.ID.ToString()) + " rect=(" + rect.X.ToString("F0") + "," + rect.Y.ToString("F0") + "," + rect.W.ToString("F0") + "," + rect.H.ToString("F0") + ") uv=(" + uv.X.ToString("F3") + "," + uv.Y.ToString("F3") + "," + uv.W.ToString("F3") + "," + uv.H.ToString("F3") + ") alpha=" + alpha.ToString("F3") + " allMonitors=" + cStatic.AllMonitors);
        }

        /// <summary>
        ///     Decodes an image file to a tightly packed, straight-alpha BGRA byte array using SkiaSharp
        ///     (same idiom as CTextureProvider._LoadImageBgra). Null (and logged) on failure.
        /// </summary>
        private static byte[] _LoadBgra(string texturePath, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var codec = SKCodec.Create(texturePath))
                {
                    if (codec == null)
                    {
                        CLog.Error("Error loading glass image: " + texturePath);
                        return null;
                    }
                    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    using (var bmp = new SKBitmap(info))
                    {
                        SKCodecResult res = codec.GetPixels(info, bmp.GetPixels());
                        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                        {
                            CLog.Error("Error loading glass image: " + texturePath);
                            return null;
                        }
                        width = info.Width;
                        height = info.Height;
                        return bmp.Bytes;
                    }
                }
            }
            catch (Exception)
            {
                CLog.Error("Error loading glass image: " + texturePath);
                return null;
            }
        }
    }
}