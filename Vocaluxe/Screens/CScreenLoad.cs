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

using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;

namespace Vocaluxe.Screens
{
    /// <summary>
    ///     The startup handover screen: by the time it shows, the splash has already finished all
    ///     loading (songs, covers, cloud sync), so its only jobs are to hand over to the main menu with
    ///     the usual fade and to do the bookkeeping the old load screen used to do (program state,
    ///     background music, playlists). It is never shown again after start-up.
    /// </summary>
    public class CScreenLoad : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 2; }
        }

        private const string _TextStatus = "TextStatus";
        private const string _TextProgramName = "TextProgramName";

        public override void Init()
        {
            base.Init();

            _ThemeTexts = new string[] { _TextStatus, _TextProgramName };
        }

        public override void OnShow()
        {
            base.OnShow();

            _Texts[_TextStatus].Text = CLanguage.Translate("TR_SCREENLOAD_TOTAL") + ": " + CSongs.NumAllSongs + " " +
                                       CLanguage.Translate("TR_SCREENLOAD_SONGS") + " (" + CSongs.NumSongsWithCoverLoaded + " " +
                                       CLanguage.Translate("TR_SCREENLOAD_LOADED") + ")";

            CSettings.ProgramState = EProgramState.Normal;

            CBackgroundMusic.OwnSongsAvailable = true;
            if (CConfig.Config.Video.VideoBackgrounds == EOffOn.TR_CONFIG_ON || CConfig.Config.Video.VideosToBackground == EOffOn.TR_CONFIG_ON)
                CBackgroundMusic.VideoEnabled = true;
            CBackgroundMusic.Play();

            CGraphics.FadeTo(EScreen.Main);
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