#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.IO;
using Vocaluxe.Base;
using Vocaluxe.Base.Fonts;
using Vocaluxe.Base.ThemeSystem;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;

namespace Vocaluxe.Screens
{
    /// <summary>
    ///     The startup handover screen: by the time it shows, the splash has already finished all
    ///     loading (songs, covers, cloud sync), so its only jobs are to hand over to the main menu with
    ///     the usual fade and to do the bookkeeping the old load screen used to do (program state,
    ///     background music, playlists). It draws a pixel copy of the splash's final frame (background,
    ///     full progress bar, "Starting.") so the fade is a seamless crossfade into the menu instead of
    ///     a flash of the theme's load-screen design. It is never shown again after start-up.
    /// </summary>
    public class CScreenLoad : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 2; }
        }

        private const string _StartingText = "Starting.";

        // The skin owns _Background (never dispose it); _Logo is only created as a fallback and is
        // left to the refcount/finalizer, exactly like the splash's own texture references.
        private CTextureRef _Background;
        private CTextureRef _Logo;

        public override void OnShow()
        {
            base.OnShow();

            // The theme is loaded by now, so the load-screen background is available; fall back to the
            // program logo on black exactly like the splash does when a theme has no BG_Loading.
            try
            {
                _Background = CThemes.GetSkinTexture("BG_Loading", -1);
            }
            catch
            {
                _Background = null;
            }

            if (_Background == null)
            {
                string logoPath = Path.Combine(CSettings.ProgramFolder, CSettings.FolderNameGraphics, CSettings.FileNameLogo);
                if (File.Exists(logoPath))
                    _Logo = CDraw.AddTexture(logoPath);
            }

            CSettings.ProgramState = EProgramState.Normal;

            CBackgroundMusic.OwnSongsAvailable = true;
            if (CConfig.Config.Video.VideoBackgrounds == EOffOn.TR_CONFIG_ON || CConfig.Config.Video.VideosToBackground == EOffOn.TR_CONFIG_ON)
                CBackgroundMusic.VideoEnabled = true;
            CBackgroundMusic.Play();

            CGraphics.FadeTo(EScreen.Main);
        }

        /// <summary>
        ///     Replicates the splash's final frame: the same background, the same bar position and
        ///     colors (fully filled, since the splash reached 1.0 before the loop started) and the same
        ///     status caption. Deliberately does not draw the theme's load-screen elements.
        /// </summary>
        public override void Draw()
        {
            float renderW = CSettings.RenderW;
            float renderH = CSettings.RenderH;

            if (_Background != null)
            {
                CDraw.DrawTexture(_Background, new SRectF(0f, 0f, renderW, renderH, 0f), new SColorF(1f, 1f, 1f, 1f));
            }
            else if (_Logo != null)
            {
                float scale = Math.Min(renderW * 0.8f / _Logo.OrigSize.Width, renderH * 0.5f / _Logo.OrigSize.Height);
                float w = _Logo.OrigSize.Width * scale;
                float h = _Logo.OrigSize.Height * scale;
                CDraw.DrawTexture(_Logo, new SRectF((renderW - w) / 2f, (renderH - h) / 2f, w, h, 0f), new SColorF(1f, 1f, 1f, 1f));
            }

            float barW = renderW * 0.5f;
            float barH = renderH * 0.012f;
            float barX = (renderW - barW) / 2f;
            float barY = renderH * 0.82f;
            CDraw.DrawRect(new SColorF(1f, 1f, 1f, 0.2f), new SRectF(barX, barY, barW, barH, CSettings.ZNear));
            CDraw.DrawRect(new SColorF(1f, 1f, 1f, 0.9f), new SRectF(barX, barY, barW, barH, CSettings.ZNear));

            CFont font = new CFont("Normal", EStyle.Normal, renderH * 0.028f);
            float textW = CFonts.GetTextWidth(_StartingText, font);
            CFonts.DrawText(_StartingText, font, (renderW - textW) / 2f, barY + barH + renderH * 0.02f, CSettings.ZNear,
                            new SColorF(1f, 1f, 1f, 0.9f));
        }

        public override bool UpdateGame()
        {
            return true;
        }

        public override void OnClose()
        {
            base.OnClose();

            // The screen is only ever shown at start-up, so this runs exactly once, when the main menu
            // takes over (or on an aborted start-up).
            using (CBenchmark.Time("Init Playlists"))
            {
                CPlaylists.Init();
            }
        }
    }
}