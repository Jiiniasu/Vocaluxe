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
        private readonly Dictionary<string, SBlurEntry> _BlurredBackgrounds = new Dictionary<string, SBlurEntry>();
        private readonly Dictionary<string, CTextureRef> _Masks = new Dictionary<string, CTextureRef>();
        private readonly HashSet<string> _UnresolvedMasks = new HashSet<string>();

        public bool IsEnabled()
        {
            return CConfig.Config.Graphics.GlassBlur == EOffOn.TR_CONFIG_ON;
        }

        public void SetBackground(CTextureRef source, CTextureRef videoTwin)
        {
            if (videoTwin != null)
            {
                _VideoTwin = videoTwin;
                return;
            }

            if (source == _Source)
                return;

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
                return;

            CTextureRef texture = cStatic.Texture;
            //A glass static may be shaped by a mask alone, without a texture of its own (e.g. the lyrics bar)
            if (texture == null && cStatic.GlassMask == null)
            {
                //A mask name that is not declared in the skin's texture list resolves to null - without a
                //warning the glass would simply never draw (the mask-only lyrics bars hit this when their
                //mask was missing from the skin XML)
                if (_UnresolvedMasks.Add(cStatic.GetThemeName()))
                    CLog.Warning("Glass static \"" + cStatic.GetThemeName() + "\" has no resolvable texture or glass mask; the glass is not drawn. Check that the mask is declared in the skin's texture list.");
                return;
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
                CBase.Drawing.DrawGlassTexture(twin, rect, color, mask, rect, cStatic.AllMonitors);
            }
            else
            {
                //No blur source available (e.g. a plain color background): tint-only fallback,
                //shaped by the mask when there is one
                CTextureRef mask = _GetMask(cStatic, texture);
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
        ///     The blurred copy of the current background: the video stream's pre-blurred twin if the
        ///     background is a video, otherwise a cached blurred copy of the background image (generated
        ///     on demand, regenerated when the blur strength changes).
        ///     Null if no blur source is available.
        /// </summary>
        private CTextureRef _GetTwin()
        {
            if (_VideoTwin != null)
                return _VideoTwin;
            if (_Source == null || String.IsNullOrEmpty(_Source.TexturePath))
                return null; //generated texture, no file to decode: tint-only fallback

            int strength = (int)CConfig.Config.Graphics.GlassStrength;
            string key = _Source.TexturePath;

            SBlurEntry entry;
            if (_BlurredBackgrounds.TryGetValue(key, out entry) && entry.Strength == strength)
                return entry.Twin;

            if (entry.Twin != null)
            {
                //Strength changed: the cached twin was blurred with the old strength
                entry.Twin.Dispose();
            }

            int width, height;
            byte[] bgra = _LoadBgra(key, out width, out height);
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